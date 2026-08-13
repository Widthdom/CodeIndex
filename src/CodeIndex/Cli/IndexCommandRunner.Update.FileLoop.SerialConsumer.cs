using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class UpdateFileLoopSession
    {
        private void ConsumeSerialUpdateTarget(
            UpdateFileTarget target,
            int targetIndex,
            LazyDisposable<SymbolExtractionWorkerClient> symbolExtractionWorker)
        {
            ThrowIfUpdateCancelled();
            updateProgress.Start();
            var relPath = target.RelativePath;
            currentUpdatePath = relPath;
            currentUpdatePhase = "preparing";
            var absPath = target.FilePath;
            var dbPath = target.IndexPath;
            var fileBatchMarked = false;
            string? knownLanguage = null;
            CSharpStaticInterfacePrepass.FileStatSnapshot csharpWorkspaceSnapshot = default;
            var hasCSharpWorkspaceSnapshot = csharpWorkspaceSnapshots != null
                && csharpWorkspaceSnapshots.TryGetValue(dbPath, out csharpWorkspaceSnapshot);
            try
            {
                if (hasCSharpWorkspaceSnapshot
                    && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                        absPath,
                        dbPath,
                        relPath,
                        csharpWorkspaceSnapshot.Size,
                        csharpWorkspaceSnapshot.ModifiedUtc,
                        csharpWorkspaceSnapshots!,
                        out _,
                        cancellationToken))
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "The C# file changed before its authoritative update pass.");
                    skipped++;
                    return;
                }

                if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                {
                    if (hasCSharpWorkspaceSnapshot)
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "The C# file disappeared after contract preflight.");
                        skipped++;
                        return;
                    }

                    using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing target");
                    if (writer.DeleteFileByPath(dbPath))
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                        updateProgress.WriteVerbose($"  [DEL ] {relPath}");
                    }
                    else
                    {
                        skipped++;
                        updateProgress.WriteVerbose($"  [SKIP] {relPath} (not in DB)");
                    }
                    return;
                }

                var pathFilter = indexer.EvaluatePathFilter(absPath);
                RecordScanErrors(pathFilter.Errors);
                if (pathFilter.ShouldSkip)
                {
                    if (!pathFilter.ShouldDeleteExisting)
                    {
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            updateProgress.Resume();
                        }
                        return;
                    }

                    using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete skipped path");
                    if (writer.DeleteFileByPath(dbPath))
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [DEL ] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            updateProgress.Resume();
                        }
                    }
                    else
                    {
                        skipped++;
                        if (options.Verbose && !options.Json)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            updateProgress.Resume();
                        }
                    }
                    return;
                }

                var indexability = indexer.GetFileIndexabilityForIndexing(absPath);
                var detection = indexer.TryDetectLanguageForIndexing(absPath, knownIndexability: indexability);
                if (hasCSharpWorkspaceSnapshot
                    && (indexability != FileIndexer.FileProbeStatus.Supported
                        || detection.Status != FileIndexer.FileProbeStatus.Supported
                        || detection.Language != "csharp"))
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "The C# file changed language or indexability after contract preflight.");
                    skipped++;
                    return;
                }
                if (!hasCSharpWorkspaceSnapshot
                    && csharpWorkspaceSnapshots != null
                    && indexability == FileIndexer.FileProbeStatus.Supported
                    && detection.Status == FileIndexer.FileProbeStatus.Supported
                    && detection.Language == "csharp")
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "A C# target appeared after the authoritative workspace target set was captured.");
                    skipped++;
                    return;
                }
                if (indexability == FileIndexer.FileProbeStatus.Missing || detection.Status == FileIndexer.FileProbeStatus.Missing)
                {
                    var message = $"{relPath}: skipped because it was deleted during indexing.";
                    warnings++;
                    warningList.Add(new CliJsonMessage(relPath, message));
                    if (!options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        ConsoleUi.PrintWarning(message);
                        updateProgress.Resume();
                    }

                    using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing during probe");
                    if (writer.DeleteFileByPath(dbPath))
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                    }
                    else
                    {
                        skipped++;
                    }
                    return;
                }

                if (indexability == FileIndexer.FileProbeStatus.ProbeFailed || detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                {
                    DemoteReadinessOnce();

                    errors++;
                    errorList.Add(new CliJsonMessage(relPath, "Could not probe file for indexability/language."));
                    if (fileErrorList.Count < PartialIndexFileErrorLimit)
                    {
                        fileErrorList.Add(new StatusIndexFileError
                        {
                            File = FileIndexer.NormalizePathSeparators(relPath),
                            Category = "file_read_error",
                            Phase = "reading",
                            Detail = "Could not probe file for indexability/language.",
                        });
                    }
                    if (!options.Json)
                    {
                        updateProgress.Pause();
                        if (options.Verbose)
                            CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                        else
                            CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                        updateProgress.Resume();
                    }
                    return;
                }

                if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                {
                    if (!writer.HasFileAtPath(dbPath))
                    {
                        using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported renamed target");
                        var purged = PurgeStaleUpdateCleanupPaths(
                            dbPath,
                            checksum: null,
                            includeDirectoryAndStem: persistenceOperations.IsProjectRootWritten());
                        if (purged > 0)
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            purgeTxn.Commit();
                            removed += purged;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [DEL ] {relPath} (unsupported renamed target)");
                                updateProgress.Resume();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                                updateProgress.Resume();
                            }
                        }
                        return;
                    }

                    DemoteReadinessOnce();
                    using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete unsupported target");
                    if (writer.DeleteFileByPath(dbPath))
                    {
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [DEL ] {relPath} (no longer indexable)");
                            updateProgress.Resume();
                        }
                    }
                    else
                    {
                        skipped++;
                        if (options.Verbose && !options.Json)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                            updateProgress.Resume();
                        }
                    }
                    return;
                }

                if (FileIndexer.TryGetFileIdentity(absPath, out var identity, out var linkCount)
                    && linkCount > 1
                    && !visitedFileIdentities.Add(identity))
                {
                    var message = "Skipped hardlinked file because the same file content was already indexed from another path.";
                    warnings++;
                    warningList.Add(new CliJsonMessage(relPath, message));
                    if (!options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        ConsoleUi.PrintWarning($"{relPath}: {message}");
                        updateProgress.Resume();
                    }

                    using var deleteTxn = writer.BeginTransaction();
                    if (writer.DeleteFileByPath(dbPath))
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                    }
                    else
                    {
                        skipped++;
                    }
                    return;
                }

                var statReusableLanguage = GetStatReusableLanguage(absPath, detection);
                var generatedExtractionSuppressed = indexer.IsGeneratedCodeExtractionSuppressed(dbPath);
                var statMatchedFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    writer,
                    absPath,
                    dbPath,
                    statReusableLanguage,
                    options.MaxFileSizeBytes ?? FileIndexer.DefaultMaxFileSizeBytes,
                    options.MaxSymbolsPerFile,
                    options.MaxReferencesPerFile,
                    generatedExtractionSuppressed,
                    allowReuse: symbolKindFilterMatchesPrior
                        && (statReusableLanguage != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (statReusableLanguage != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (statReusableLanguage != "sql" || sqlGraphContractMatchesCurrent)
                        && (statReusableLanguage is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent));
                if (statMatchedFile != null)
                {
                    skipped++;
                    readableFileBytes.Remember(targetIndex, statMatchedFile.Value.Size);
                    if (options.Verbose && !options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unchanged)");
                        updateProgress.Resume();
                    }
                    return;
                }

                knownLanguage = scannedUpdateLanguages == null
                    ? statReusableLanguage
                    : FileIndexer.GetReusableDetectedLanguage(absPath, scannedUpdateLanguages);

                currentUpdatePhase = "reading";
                UpdateFileContentLoadForTesting?.Invoke(relPath);
                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                    absPath,
                    relPath,
                    knownLanguage,
                    cancellationToken);
                var record = loaded.Record;
                if (hasCSharpWorkspaceSnapshot
                    && (record.Lang != "csharp"
                        || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                            absPath,
                            dbPath,
                            relPath,
                            record.Size,
                            record.Modified,
                            csharpWorkspaceSnapshots!,
                            out _,
                            cancellationToken)))
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "The C# file changed while the authoritative update pass was reading it.");
                    skipped++;
                    return;
                }
                readableFileBytes.Remember(targetIndex, record.Size);
                var warning = loaded.Warning;
                var generatedSuppressionIssue = generatedExtractionSuppressed
                    ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                    : null;

                if (warning != null && !options.Json && !options.Quiet)
                {
                    updateProgress.Pause();
                    ConsoleUi.PrintWarning(warning);
                    updateProgress.Resume();
                }

                var existingId = writer.GetReusableUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    size: record.Size,
                    lines: record.Lines,
                    language: record.Lang,
                    generated: record.Generated,
                    maxSymbolsPerFile: options.MaxSymbolsPerFile,
                    maxReferencesPerFile: options.MaxReferencesPerFile,
                    generatedExtractionSuppressed: generatedExtractionSuppressed,
                    allowReuse: symbolKindFilterMatchesPrior
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                        && (record.Lang is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent));
                if (existingId != null)
                {
                    using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unchanged stale paths");
                    var purged = PurgeStaleUpdateCleanupPaths(
                        record.Path,
                        record.Checksum,
                        includeDirectoryAndStem: persistenceOperations.IsProjectRootWritten());
                    if (purged > 0)
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                        purgeTxn.Commit();
                        removed += purged;
                        ftsMutated = true;
                        mutualRecursionRefreshNeeded = true;
                    }
                    skipped++;
                    if (options.Verbose && !options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        CommandOutputWriter.WriteLine(purged > 0
                            ? $"  [SKIP] {relPath} (unchanged; purged {purged:N0} stale renamed path(s))"
                            : $"  [SKIP] {relPath} (unchanged)");
                        updateProgress.Resume();
                    }
                    return;
                }

                DemoteReadinessOnce();
                if (record.Lang == "csharp")
                    csharpMetadataTargetsNeedRefresh = true;
                _ = postExtractionHooks.Value;
                var persistence = PersistSerialUpdateFile(
                    relPath,
                    absPath,
                    record,
                    loaded,
                    generatedSuppressionIssue,
                    symbolExtractionWorker.Value,
                    persistenceOperations.IsProjectRootWritten(),
                    ref fileBatchMarked);
                symbolsDroppedByKindFilter += persistence.SymbolsDroppedByKindFilter;
                mutualRecursionRefreshNeeded |= persistence.MutualRecursionRefreshNeeded;
                updated++;
                ftsMutated = true;
                UpdateFileCommittedForTesting?.Invoke(updated + removed, targetPaths.Count);
                ThrowIfUpdateCancelled();
                updateProgress.WriteVerbose(persistence.VerboseMessage);
            }
            catch (IndexExtractionStalledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ex is CSharpWorkspaceChangedException)
                {
                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();
                    RecordCSharpWorkspaceDrift(relPath, ex.Message);
                    skipped++;
                    return;
                }

                if (ex is FileIndexer.BinaryFileSkippedException
                    or FileIndexer.FileTooLargeSkippedException)
                {
                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();

                    var skippedFile = HandleSkippedUpdateFile(
                        new SkippedUpdateFileHandlingContext
                        {
                            Writer = writer,
                            Indexer = indexer,
                            Options = options,
                            AbsolutePath = absPath,
                            RelativePath = relPath,
                            IndexPath = dbPath,
                            KnownLanguage = knownLanguage,
                            ProjectRootWritten = persistenceOperations.IsProjectRootWritten(),
                            TargetIndex = targetIndex,
                            ReadableFileBytes = readableFileBytes,
                            HasCSharpWorkspaceSnapshot =
                                hasCSharpWorkspaceSnapshot,
                            CSharpWorkspaceSnapshot =
                                csharpWorkspaceSnapshot,
                            CSharpWorkspaceSnapshots =
                                csharpWorkspaceSnapshots,
                            WarningList = warningList,
                            UpdateProgress = updateProgress,
                            CancellationToken = cancellationToken,
                            DemoteReadinessOnce = DemoteReadinessOnce,
                            SetCurrentUpdatePhase =
                                phase => currentUpdatePhase = phase,
                            RecordCSharpWorkspaceDrift =
                                RecordCSharpWorkspaceDrift,
                            RecordUpdateFileFailure =
                                RecordUpdateFileFailure,
                            PurgeStaleUpdateCleanupPaths =
                                PurgeStaleUpdateCleanupPaths,
                            RequireTypeScriptAugmentationRefresh =
                                RequireTypeScriptAugmentationRefresh,
                            WriteProjectRootOnce = WriteProjectRootOnce,
                            RecordDynamicGraphFileRefresh =
                                RecordDynamicGraphFileRefresh,
                        },
                        ex);
                    updated += skippedFile.Updated;
                    skipped += skippedFile.Skipped;
                    warnings += skippedFile.Warnings;
                    mutualRecursionRefreshNeeded |=
                        skippedFile.MutualRecursionRefreshNeeded;
                    if (skippedFile.Updated > 0)
                    {
                        ftsMutated = true;
                    }
                    return;
                }

                if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();

                    if (hasCSharpWorkspaceSnapshot)
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "The C# file disappeared during its authoritative update pass.");
                        skipped++;
                        return;
                    }

                    var message = $"{relPath}: skipped because it was deleted during indexing.";
                    warnings++;
                    warningList.Add(new CliJsonMessage(relPath, message));
                    if (!options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        ConsoleUi.PrintWarning(message);
                        updateProgress.Resume();
                    }

                    if (writer.HasFileAtPath(dbPath))
                    {
                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing during write");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                    return;
                }

                if (fileBatchMarked)
                    writer.ClearBatchInProgress();
                RecordUpdateFileFailure(relPath, currentUpdatePhase, ex);
            }
        }
    }
}
