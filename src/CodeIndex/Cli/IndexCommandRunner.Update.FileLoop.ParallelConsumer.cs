using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class UpdateFileLoopSession
    {
        private void ConsumeParallelUpdateResult(UpdateParallelExtractionResult item)
        {
            var target = item.Target;
            if (cancellationToken.IsCancellationRequested && item.Record != null)
            {
                DemoteReadinessOnce();
                csharpMetadataTargetsNeedRefresh = true;
            }
            ThrowIfUpdateCancelled();
            updateProgress.Start();
            var relPath = target.RelativePath;
            currentUpdatePath = relPath;
            currentUpdatePhase = item.FailurePhase ?? "preparing";
            var absPath = target.FilePath;
            var dbPath = target.IndexPath;
            var fileBatchMarked = false;
            var csharpWorkspaceSnapshot = csharpWorkspaceSnapshots![dbPath];
            try
            {
                if (item.Record != null)
                {
                    readableFileBytes.Remember(item.TargetIndex, item.Record.Size);
                    if (item.Warning != null && !options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        ConsoleUi.PrintWarning(item.Warning);
                        updateProgress.Resume();
                    }
                    DemoteReadinessOnce();
                    csharpMetadataTargetsNeedRefresh = true;
                }
                var sourceContractSeenBeforeObservation =
                    postExtractionHooks.Value.SawCSharpStaticInterfaceSourceContract;
                postExtractionHooks.Value.ObserveCSharpStaticInterfaceSourceContractEvidence(
                    item.HasCSharpStaticInterfaceSourceContract);
                if (!csharpWorkspace.HasSourceStaticInterfaceContracts
                    && !sourceContractSeenBeforeObservation
                    && postExtractionHooks.Value.SawCSharpStaticInterfaceSourceContract)
                {
                    parallelSourceWorkspaceDriftDetected = true;
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "A C# static-interface contract appeared after workspace preflight.");
                    skipped++;
                    return;
                }
                if (item.Exception is IndexExtractionStalledException stalledException)
                {
                    if (!string.Equals(
                            item.FailurePhase,
                            "reading",
                            StringComparison.Ordinal))
                    {
                        DemoteReadinessOnce();
                        csharpMetadataTargetsNeedRefresh = true;
                        writer.MarkBatchInProgress();
                        fileBatchMarked = true;
                    }
                    RethrowPreservingStackTrace(
                        new IndexExtractionStalledException(
                            updated + removed,
                            targetPaths.Count,
                            stalledException.Timeout,
                            stalledException.ActivePath,
                            stalledException.WorkerError));
                }
                if (item.Exception is CSharpWorkspaceChangedException
                    or CSharpWorkspaceSnapshotDriftException)
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        item.Exception.Message,
                        "reading");
                    skipped++;
                    return;
                }
                if (item.Exception is FileIndexer.BinaryFileSkippedException
                    or FileIndexer.FileTooLargeSkippedException)
                {
                    var skippedFile = HandleSkippedUpdateFile(
                        new SkippedUpdateFileHandlingContext
                        {
                            Writer = writer,
                            Indexer = indexer,
                            Options = options,
                            AbsolutePath = absPath,
                            RelativePath = relPath,
                            IndexPath = dbPath,
                            KnownLanguage = item.KnownLanguage,
                            ProjectRootWritten = persistenceOperations.IsProjectRootWritten(),
                            TargetIndex = item.TargetIndex,
                            ReadableFileBytes = readableFileBytes,
                            HasCSharpWorkspaceSnapshot = true,
                            CSharpWorkspaceSnapshot = csharpWorkspaceSnapshot,
                            CSharpWorkspaceSnapshots = csharpWorkspaceSnapshots,
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
                        item.Exception);
                    updated += skippedFile.Updated;
                    skipped += skippedFile.Skipped;
                    warnings += skippedFile.Warnings;
                    mutualRecursionRefreshNeeded |=
                        skippedFile.MutualRecursionRefreshNeeded;
                    if (skippedFile.Updated > 0)
                    {
                        ftsMutated = true;
                        parallelExtractionEventForTesting?.Invoke(
                            new UpdateParallelExtractionTestEvent(
                                UpdateParallelExtractionEventKind.PersistenceCompleted,
                                item.TargetIndex,
                                target.DisplayRelativePath,
                                WorkerIndex: -1));
                    }
                    return;
                }
                if (item.Exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "The C# file disappeared during its authoritative update pass.");
                    skipped++;
                    return;
                }
                if (item.Exception != null)
                {
                    if (item.Exception is OperationCanceledException)
                        ThrowIfUpdateCancelled();
                    if (!string.Equals(
                            item.FailurePhase,
                            "reading",
                            StringComparison.Ordinal))
                    {
                        csharpMetadataTargetsNeedRefresh = true;
                    }
                    RecordUpdateFileFailure(
                        relPath,
                        item.FailurePhase ?? "reading",
                        item.Exception);
                    return;
                }

                var record = item.Record!;
                currentUpdatePhase = "validating";
                if (record.Lang != "csharp"
                    || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                        absPath,
                        dbPath,
                        target.DisplayRelativePath,
                        record.Size,
                        record.Modified,
                        csharpWorkspaceSnapshots,
                        out _,
                        cancellationToken))
                {
                    RecordCSharpWorkspaceDrift(
                        relPath,
                        "The C# file changed after extraction and before its authoritative update was persisted.",
                        "reading");
                    skipped++;
                    return;
                }

                currentUpdatePhase = "reading";
                parallelExtractionEventForTesting?.Invoke(
                    new UpdateParallelExtractionTestEvent(
                        UpdateParallelExtractionEventKind.PersistenceStarted,
                        item.TargetIndex,
                        target.DisplayRelativePath,
                        WorkerIndex: -1));
                var persistence = PersistPrecomputedUpdateFile(
                    item,
                    persistenceOperations.IsProjectRootWritten(),
                    ref fileBatchMarked);
                symbolsDroppedByKindFilter +=
                    persistence.SymbolsDroppedByKindFilter;
                mutualRecursionRefreshNeeded |=
                    persistence.MutualRecursionRefreshNeeded;
                updated++;
                ftsMutated = true;
                UpdateFileCommittedForTesting?.Invoke(
                    updated + removed,
                    targetPaths.Count);
                parallelExtractionEventForTesting?.Invoke(
                    new UpdateParallelExtractionTestEvent(
                        UpdateParallelExtractionEventKind.PersistenceCompleted,
                        item.TargetIndex,
                        target.DisplayRelativePath,
                        WorkerIndex: -1));
                ThrowIfUpdateCancelled();
                updateProgress.WriteVerbose(persistence.VerboseMessage);
            }
            catch (IndexExtractionStalledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();
                if (ex is CSharpWorkspaceChangedException)
                {
                    RecordCSharpWorkspaceDrift(relPath, ex.Message);
                    skipped++;
                    return;
                }
                if (ex is OperationCanceledException)
                    ThrowIfUpdateCancelled();
                RecordUpdateFileFailure(relPath, currentUpdatePhase, ex);
            }
        }

        private void SetUpdatePhase(string path, string phase)
        {
            currentUpdatePath = path;
            currentUpdatePhase = phase;
        }

        private UpdateFilePersistenceResult PersistPrecomputedUpdateFile(
            UpdateParallelExtractionResult item,
            bool projectRootWritten,
            ref bool fileBatchMarked)
        {
            var record = item.Record!;
            var mutualRecursionRefreshNeeded = false;

            writer.MarkBatchInProgress();
            fileBatchMarked = true;
            using var txn = writer.BeginTransaction(
                cancellationToken,
                "update precomputed file");
            var stalePurged = PurgeStaleUpdateCleanupPaths(
                record.Path,
                record.Checksum,
                projectRootWritten);
            if (stalePurged > 0 && !options.SymbolsOnly)
                mutualRecursionRefreshNeeded = true;
            if (stalePurged > 0)
                RequireTypeScriptAugmentationRefresh();
            WriteProjectRootOnce();
            var fileId = writer.UpsertFile(record, out var referenceIdentityChanged);
            if (!options.SymbolsOnly && referenceIdentityChanged)
                mutualRecursionRefreshNeeded = true;

            SetUpdatePhase(
                FormatIndexPhasePath(item.Target.DisplayRelativePath, "chunking"),
                "chunking");
            var chunks = ReassignChunkFileIds(item.Chunks!, fileId);
            if (item.GeneratedSuppressionIssue != null)
            {
                writer.InsertChunks(chunks, cancellationToken);
                writer.InsertSymbols([], cancellationToken);
                writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    cancellationToken);
                SetUpdatePhase(
                    FormatIndexPhasePath(item.Target.DisplayRelativePath, "validating"),
                    "validating");
                writer.InsertIssues(fileId, item.Issues!);
                SetUpdatePhase(
                    FormatIndexPhasePath(item.Target.DisplayRelativePath, "committing"),
                    "committing");
                writer.ClearBatchInProgress();
                txn.Commit();
                fileBatchMarked = false;
                RecordDynamicGraphFileRefresh(record.Lang);
                return new UpdateFilePersistenceResult(
                    0,
                    mutualRecursionRefreshNeeded,
                    $"  [OK  ] {item.Target.RelativePath} ({chunks.Count} chunks, generated-code extraction skipped)");
            }

            SetUpdatePhase(
                FormatIndexPhasePath(item.Target.DisplayRelativePath, "symbols"),
                "symbols");
            var symbols = ReassignSymbolFileIds(item.Symbols!, fileId);
            if (item.SymbolCapExceeded)
            {
                writer.InsertSymbols([], cancellationToken);
                writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    cancellationToken);
                writer.InsertIssues(fileId, item.Issues!);
                writer.ClearBatchInProgress();
                txn.Commit();
                fileBatchMarked = false;
                RecordDynamicGraphFileRefresh(record.Lang);
                return new UpdateFilePersistenceResult(
                    0,
                    mutualRecursionRefreshNeeded,
                    $"  [SKIP] {item.Target.RelativePath} ({item.Issues![^1].Message})");
            }

            writer.InsertChunks(chunks, cancellationToken);
            FileIndexer.ValidateSymbolLineRanges(record, symbols);
            writer.InsertSymbols(symbols, cancellationToken);
            SetUpdatePhase(
                FormatIndexPhasePath(item.Target.DisplayRelativePath, "references"),
                "references");
            var references = ReassignReferenceFileIds(item.References!, fileId);
            writer.InsertReferencesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                cancellationToken);
            SetUpdatePhase(
                FormatIndexPhasePath(item.Target.DisplayRelativePath, "validating"),
                "validating");
            writer.InsertIssues(fileId, item.Issues!);
            SetUpdatePhase(
                FormatIndexPhasePath(item.Target.DisplayRelativePath, "committing"),
                "committing");
            writer.ClearBatchInProgress();
            txn.Commit();
            fileBatchMarked = false;
            RecordDynamicGraphFileRefresh(record.Lang);
            if (!options.SymbolsOnly
                && (symbols.Count > 0 || references.Count > 0))
            {
                mutualRecursionRefreshNeeded = true;
            }

            return new UpdateFilePersistenceResult(
                0,
                mutualRecursionRefreshNeeded,
                $"  [OK  ] {item.Target.RelativePath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
        }
    }
}
