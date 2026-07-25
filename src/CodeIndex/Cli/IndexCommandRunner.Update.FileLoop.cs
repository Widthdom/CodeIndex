using System.Diagnostics;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateFileLoopContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required Stopwatch Stopwatch { get; init; }
        internal required string ProjectRoot { get; init; }
        internal List<string>? IndexRunDiagnostics { get; init; }
        internal required IReadOnlyCollection<string> TargetPaths { get; init; }
        internal required IndexProgressReporter UpdateProgress { get; init; }
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal required int Updated { get; init; }
        internal required int Removed { get; init; }
        internal required int Skipped { get; init; }
        internal required bool FtsMutated { get; init; }
        internal required bool MutualRecursionRefreshNeeded { get; init; }
        internal required bool CSharpMetadataTargetsNeedRefresh { get; init; }
        internal required int SymbolsDroppedByKindFilter { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace { get; init; }
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? CSharpWorkspaceSnapshots { get; init; }
        internal IReadOnlyDictionary<string, string>? ScannedUpdateLanguages { get; init; }
        internal required bool SymbolKindFilterMatchesPrior { get; init; }
        internal required bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal required bool SqlGraphContractMatchesCurrent { get; init; }
        internal required bool HdlGraphContractMatchesCurrent { get; init; }
        internal required LazyDisposable<PostExtractionHookRunner> PostExtractionHooks { get; init; }
        internal required HashSet<FileIndexer.FileIdentity> VisitedFileIdentities { get; init; }
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action<IEnumerable<FileIndexer.ScanError>, string> RecordScanErrors { get; init; }
        internal required Action<string, string, string> RecordCSharpWorkspaceDrift { get; init; }
        internal required Action DemoteReadinessOnce { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required Func<string, string?, bool, int> PurgeStaleUpdateCleanupPaths { get; init; }
        internal required Action<string?> RecordDynamicGraphFileRefresh { get; init; }
        internal required Action<string, string, Exception> RecordUpdateFileFailure { get; init; }
        internal required Func<bool> IsProjectRootWritten { get; init; }
    }

    private sealed record UpdateFileLoopResult(
        int Updated,
        int Removed,
        int Skipped,
        int Warnings,
        int Errors,
        bool FtsMutated,
        bool MutualRecursionRefreshNeeded,
        bool CSharpMetadataTargetsNeedRefresh,
        int SymbolsDroppedByKindFilter,
        ReadableFileByteTracker ReadableFileBytes);

    private static UpdateFileLoopResult RunUpdateFileLoop(UpdateFileLoopContext context)
    {
        var writer = context.Writer;
        var indexer = context.Indexer;
        var options = context.Options;
        var stopwatch = context.Stopwatch;
        var projectRoot = context.ProjectRoot;
        var indexRunDiagnostics = context.IndexRunDiagnostics;
        var targetPaths = context.TargetPaths;
        var updateProgress = context.UpdateProgress;
        var memorySamples = context.MemorySamples;
        var updated = context.Updated;
        var removed = context.Removed;
        var skipped = context.Skipped;
        var warnings = 0;
        var errors = 0;
        var ftsMutated = context.FtsMutated;
        var mutualRecursionRefreshNeeded = context.MutualRecursionRefreshNeeded;
        var csharpMetadataTargetsNeedRefresh = context.CSharpMetadataTargetsNeedRefresh;
        var symbolsDroppedByKindFilter = context.SymbolsDroppedByKindFilter;
        var csharpWorkspace = context.CSharpWorkspace;
        var csharpWorkspaceSnapshots = context.CSharpWorkspaceSnapshots;
        var scannedUpdateLanguages = context.ScannedUpdateLanguages;
        var symbolKindFilterMatchesPrior = context.SymbolKindFilterMatchesPrior;
        var csharpSymbolNameContractMatchesCurrent =
            context.CSharpSymbolNameContractMatchesCurrent;
        var sqlGraphContractMatchesCurrent = context.SqlGraphContractMatchesCurrent;
        var hdlGraphContractMatchesCurrent = context.HdlGraphContractMatchesCurrent;
        var postExtractionHooks = context.PostExtractionHooks;
        var visitedFileIdentities = context.VisitedFileIdentities;
        var errorList = context.ErrorList;
        var fileErrorList = context.FileErrorList;
        var warningList = context.WarningList;
        var cancellationToken = context.CancellationToken;

        void RecordScanErrors(
            IEnumerable<FileIndexer.ScanError> scanErrors,
            string fatalPhase = "discovery")
            => context.RecordScanErrors(scanErrors, fatalPhase);

        void RecordCSharpWorkspaceDrift(
            string relativePath,
            string detail,
            string fatalPhase = "reading")
            => context.RecordCSharpWorkspaceDrift(relativePath, detail, fatalPhase);

        void DemoteReadinessOnce() => context.DemoteReadinessOnce();
        void WriteProjectRootOnce() => context.WriteProjectRootOnce();
        void RequireTypeScriptAugmentationRefresh()
            => context.RequireTypeScriptAugmentationRefresh();

        int PurgeStaleUpdateCleanupPaths(
            string retainedRelativePath,
            string? checksum,
            bool includeDirectoryAndStem)
            => context.PurgeStaleUpdateCleanupPaths(
                retainedRelativePath,
                checksum,
                includeDirectoryAndStem);

        void RecordDynamicGraphFileRefresh(string? language)
            => context.RecordDynamicGraphFileRefresh(language);

        void RecordUpdateFileFailure(
            string relativePath,
            string phase,
            Exception exception)
            => context.RecordUpdateFileFailure(relativePath, phase, exception);

        void ThrowIfUpdateCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            updateProgress.Pause();
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }

        updateProgress.Start();

        var updateTargets = new UpdateFileTarget[targetPaths.Count];
        var updateTargetIndex = 0;
        foreach (var targetPath in targetPaths)
            updateTargets[updateTargetIndex++] = UpdateFileTarget.Create(projectRoot, targetPath);
        var readableFileBytes = new ReadableFileByteTracker(
            updateTargets.Length,
            targetIndex => updateTargets[targetIndex].FilePath,
            projectRoot,
            indexRunDiagnostics);

        WriteIndexJsonLiveness(options, $"updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        string? currentUpdatePath = null;
        var currentUpdatePhase = "preparing";
        var updateHeartbeat = StartIndexJsonPhaseHeartbeat(
            options,
            "updating index",
            () => currentUpdatePath == null
                ? $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed"
                : $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed, current {currentUpdatePath}");
        using var symbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(() =>
        {
            UpdateExtractionWorkStartedForTesting?.Invoke();
            return new SymbolExtractionWorkerClient(options.MaxFileSizeBytes);
        });
        try
        {
            for (var targetIndex = 0; targetIndex < updateTargets.Length; targetIndex++)
            {
                var target = updateTargets[targetIndex];
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
                        continue;
                    }

                    if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                    {
                        if (hasCSharpWorkspaceSnapshot)
                        {
                            RecordCSharpWorkspaceDrift(
                                relPath,
                                "The C# file disappeared after contract preflight.");
                            skipped++;
                            continue;
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
                        continue;
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
                            continue;
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
                        continue;
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
                        continue;
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
                        continue;
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
                        continue;
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
                        continue;
                    }

                    if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                    {
                        if (!writer.HasFileAtPath(dbPath))
                        {
                            using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported renamed target");
                            var purged = PurgeStaleUpdateCleanupPaths(
                                dbPath,
                                checksum: null,
                                includeDirectoryAndStem: context.IsProjectRootWritten());
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
                            continue;
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
                        continue;
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
                        continue;
                    }

                    var statReusableLanguage = GetStatReusableLanguage(absPath, detection);
                    var generatedExtractionSuppressed = indexer.IsGeneratedCodeExtractionSuppressed(dbPath);
                    var statMatchedFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        writer,
                        absPath,
                        dbPath,
                        statReusableLanguage,
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
                        continue;
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
                        continue;
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
                            includeDirectoryAndStem: context.IsProjectRootWritten());
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
                        continue;
                    }

                    DemoteReadinessOnce();
                    if (record.Lang == "csharp")
                        csharpMetadataTargetsNeedRefresh = true;
                    var persistence = PersistUpdateFile(new UpdateFilePersistenceContext
                    {
                        Writer = writer,
                        Indexer = indexer,
                        Options = options,
                        ProjectRoot = projectRoot,
                        RelativePath = relPath,
                        AbsolutePath = absPath,
                        Record = record,
                        Loaded = loaded,
                        GeneratedSuppressionIssue = generatedSuppressionIssue,
                        CSharpWorkspace = csharpWorkspace,
                        PostExtractionHooks = postExtractionHooks.Value,
                        SymbolExtractionWorker = symbolExtractionWorker.Value,
                        ProjectRootWritten = context.IsProjectRootWritten(),
                        CancellationToken = cancellationToken,
                        RequireTypeScriptAugmentationRefresh = RequireTypeScriptAugmentationRefresh,
                        PurgeStaleUpdateCleanupPaths = PurgeStaleUpdateCleanupPaths,
                        WriteProjectRootOnce = WriteProjectRootOnce,
                        RecordDynamicGraphFileRefresh = RecordDynamicGraphFileRefresh,
                        SetBatchMarkerOwned = owned => fileBatchMarked = owned,
                        SetPhase = (path, phase) =>
                        {
                            currentUpdatePath = path;
                            currentUpdatePhase = phase;
                        },
                    });
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
                        continue;
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
                                ProjectRootWritten = context.IsProjectRootWritten(),
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
                        continue;
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
                            continue;
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
                        continue;
                    }

                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();
                    RecordUpdateFileFailure(relPath, currentUpdatePhase, ex);
                }
            }
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(updateHeartbeat);
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));
        return new UpdateFileLoopResult(
            updated,
            removed,
            skipped,
            warnings,
            errors,
            ftsMutated,
            mutualRecursionRefreshNeeded,
            csharpMetadataTargetsNeedRefresh,
            symbolsDroppedByKindFilter,
            readableFileBytes);
    }
}
