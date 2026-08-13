using System.Collections.Concurrent;
using System.Diagnostics;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private readonly record struct FullScanExtractionConsumerResources(
        PostExtractionHookRunner PostExtractionHooks,
        SymbolExtractionWorkerClient SymbolExtractionWorker,
        BlockingCollection<FullScanFileWorkItem> ExtractionResults,
        Task[] Workers,
        TimeSpan ExtractionStallTimeout,
        ActiveExtractionPhase?[] ActiveExtractionPhases,
        CancellationTokenSource ExtractionCancellation,
        int ProcessedBeforeExtraction);

    private sealed partial class FullScanExtractionSession
    {
        internal void ConsumeFullScanExtractionResults(
            in FullScanExtractionConsumerResources resources)
        {
            var lastExtractionProgressAt = Stopwatch.GetTimestamp();
            while (!resources.ExtractionResults.IsCompleted)
            {
                var processed = resources.ProcessedBeforeExtraction + Processed;
                ThrowIfFullScanCancelled(processed, FilesCount);
                if (!resources.ExtractionResults.TryTake(out var item, millisecondsTimeout: 100))
                {
                    ThrowIfFullScanExtractionStalled(
                        processed,
                        FilesCount,
                        resources.ExtractionStallTimeout,
                        lastExtractionProgressAt,
                        CurrentJsonIndexFile,
                        resources.ActiveExtractionPhases,
                        resources.ExtractionCancellation);
                    continue;
                }

                lastExtractionProgressAt = Stopwatch.GetTimestamp();
                CurrentJsonIndexFile = item.RelativePath;
                ProcessFullScanExtractionItem(in resources, item);
                CompleteFullScanExtractionItem(resources.ProcessedBeforeExtraction);
            }

            Task.WaitAll(resources.Workers, CancellationToken);
        }

        private void ProcessFullScanExtractionItem(
            in FullScanExtractionConsumerResources resources,
            FullScanFileWorkItem item)
        {
            var options = Options;
            var writer = Writer;
            var indexFilePhase = item.FailurePhase ?? "preparing";
            var itemFileExtracted = item.Record == null ? 0L : 1L;
            var itemChunksExtracted = item.Chunks?.Count ?? 0L;
            var itemSymbolsExtracted = item.Symbols?.Count ?? 0L;
            var itemReferencesExtracted = item.References?.Count ?? 0L;
            FullScanProgress.EnsureIndexingActivityVisible();
            if (item.Exception is IndexExtractionStalledException stalledException)
                RethrowPreservingStackTrace(stalledException);

            try
            {
                if (ShouldDeferFullScanCSharpItem(item))
                {
                    Skipped++;
                    return;
                }

                if (item.Exception != null)
                    RethrowPreservingStackTrace(item.Exception);

                if (item.Record == null)
                {
                    RecordSkippedFullScanExtractionItem(item);
                    return;
                }

                var record = item.Record;
                ReadableFileBytes.Remember(item.FileIndex, record.Size);
                if (item.Warning != null && !options.Json && !options.Quiet)
                {
                    IndexProgress.Pause();
                    ConsoleUi.PrintWarning(item.Warning);
                    IndexProgress.Resume();
                }

                var generatedSuppressionIssue = item.GeneratedSuppressionChecked
                    ? item.GeneratedSuppressionIssue
                    : Indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path);
                var existingId = GetReusableFullScanFileId(record, generatedSuppressionIssue);
                if (existingId != null)
                {
                    RecordReusedFullScanExtractionItem(record);
                    return;
                }

                if (record.Lang == "csharp")
                    CSharpMetadataTargetsNeedRefresh = true;
                if (record.Lang == "typescript")
                    RequireTypeScriptAugmentationRefresh();

                var persistence = PersistFullScanFile(new FullScanFilePersistenceContext
                {
                    Writer = writer,
                    Indexer = Indexer,
                    Options = options,
                    ProjectRoot = ProjectRoot,
                    Item = item,
                    Record = record,
                    GeneratedSuppressionIssue = generatedSuppressionIssue,
                    StartedWithNoIndexedFiles = StartedWithNoIndexedFiles,
                    DeferCSharpMutationsForIncompleteScan =
                        DeferCSharpMutationsForIncompleteScan,
                    CSharpWorkspace = CSharpWorkspace,
                    CSharpPrepassSymbolArtifacts =
                        CSharpPrepassSymbolArtifacts,
                    PostExtractionHooks = resources.PostExtractionHooks,
                    SymbolExtractionWorker = resources.SymbolExtractionWorker,
                    CancellationToken = CancellationToken,
                    ExtractionSession = this,
                    WriteProjectRootOnce = WriteProjectRootOnce,
                    SetPhase = (path, phase) =>
                    {
                        CurrentJsonIndexFile = path;
                        indexFilePhase = phase;
                    },
                });
                itemChunksExtracted = persistence.ExtractedChunks;
                itemSymbolsExtracted = persistence.ExtractedSymbols;
                itemReferencesExtracted = persistence.ExtractedReferences;
                SymbolsDroppedByKindFilter += persistence.SymbolsDroppedByKindFilter;
                MutualRecursionRefreshNeeded |= persistence.MutualRecursionRefreshNeeded;
                CSharpMetadataTargetsNeedRefresh |= persistence.CSharpMetadataTargetsNeedRefresh;
                FtsMutated = true;
                if (persistence.StampSymbolExtractorLanguage
                    && !string.IsNullOrWhiteSpace(record.Lang))
                {
                    IndexedSymbolExtractorLanguages.Add(record.Lang);
                }
                CountFreshInsertedRows(
                    persistence.PersistedChunks,
                    persistence.PersistedSymbols,
                    persistence.PersistedReferences);
                IndexProgress.WriteVerbose(persistence.VerboseMessage);
            }
            catch (IndexExtractionStalledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogIndexFileFailure("index_file_failed", item.FilePath, indexFilePhase, ex);
                ErrorsAdded++;
                var errorMessage = FormatIndexFileException(ex);
                ErrorList.Add(new CliJsonMessage(item.FilePath, errorMessage));
                if (FileErrorList.Count < PartialIndexFileErrorLimit)
                    FileErrorList.Add(BuildIndexFileError(item.RelativePath, indexFilePhase, ex));
                if (!options.Json)
                {
                    IndexProgress.Pause();
                    ConsoleUi.ClearProgressLine();
                    ConsoleUi.TryWriteErrorLine(
                        FormatPerFileErrorLine("ERR ", item.FilePath, ex, errorMessage));
                    IndexProgress.Resume();
                }
            }
            finally
            {
                ExtractedFiles += itemFileExtracted;
                ExtractedChunks += itemChunksExtracted;
                ExtractedSymbols += itemSymbolsExtracted;
                ExtractedReferences += itemReferencesExtracted;
            }
        }

        private bool ShouldDeferFullScanCSharpItem(
            FullScanFileWorkItem item)
        {
            if (item.FileIndex < 0
                || FileTargets[item.FileIndex].Language != "csharp")
            {
                return false;
            }

            var deferCurrentItem = DeferCSharpMutationsForIncompleteScan;
            if (!deferCurrentItem
                && item.Exception is CSharpWorkspaceSnapshotDriftException driftException)
            {
                DeferCSharpMutationsForLoadedSnapshotDrift(driftException.Path);
                return true;
            }

            var workspaceFileSnapshots = CSharpWorkspaceFileSnapshots;
            if (!deferCurrentItem
                && item.Record != null
                && workspaceFileSnapshots != null
                && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                    item.FilePath,
                    FileTargets[item.FileIndex].IndexPath,
                    FileTargets[item.FileIndex].DisplayRelativePath,
                    item.Record.Size,
                    item.Record.Modified,
                    workspaceFileSnapshots,
                    out var changedPath,
                    CancellationToken))
            {
                DeferCSharpMutationsForLoadedSnapshotDrift(
                    changedPath ?? FileTargets[item.FileIndex].DisplayRelativePath);
                return true;
            }

            return deferCurrentItem;
        }

        private void RecordSkippedFullScanExtractionItem(
            FullScanFileWorkItem item)
        {
            var path = item.RelativePath;
            Warnings++;
            WarningList.Add(new CliJsonMessage(path, item.Warning ?? "File skipped"));
            if (!Options.Json
                && !Options.Quiet
                && item.Warning != null)
            {
                IndexProgress.Pause();
                ConsoleUi.PrintWarning(item.Warning);
                IndexProgress.Resume();
            }

            if (!Writer.HasFileAtPath(path))
            {
                Skipped++;
                return;
            }

            using var deleteTxn = Writer.BeginTransaction(
                CancellationToken,
                "full scan delete skipped file");
            if (!Writer.DeleteFileByPath(path))
                return;

            CSharpMetadataTargetsNeedRefresh = true;
            RequireTypeScriptAugmentationRefresh();
            WriteProjectRootOnce();
            deleteTxn.Commit();
            FtsMutated = true;
        }

        private long? GetReusableFullScanFileId(
            FileRecord record,
            FileIssue? generatedSuppressionIssue)
        {
            var options = Options;
            if (ForceExtractorRefresh
                || options.Rebuild
                || StartedWithNoIndexedFiles
                || options.SymbolsOnly)
            {
                return null;
            }

            var targetRequiresRefresh =
                TargetRequiresJavaScriptTypeScriptRefresh(record.Lang, record.Path);
            return Writer.GetReusableUnchangedFileId(
                record.Path,
                record.Modified,
                record.Checksum,
                size: record.Size,
                lines: record.Lines,
                language: record.Lang,
                generated: record.Generated,
                maxSymbolsPerFile: options.MaxSymbolsPerFile,
                maxReferencesPerFile: options.MaxReferencesPerFile,
                generatedExtractionSuppressed: generatedSuppressionIssue != null,
                allowReuse: SymbolKindFilterMatchesPrior
                    && !targetRequiresRefresh
                    && !PriorSymbolsOnlyGraphOmitted
                    && (record.Lang != "csharp" || CSharpIndexedProjectRootCompatible)
                    && (record.Lang != "csharp" || CSharpSymbolNameContractMatchesCurrent)
                    && (record.Lang != "csharp" || !CSharpWorkspace.HasStaticInterfaceContracts)
                    && (record.Lang != "sql" || SqlGraphContractMatchesCurrent)
                    && (record.Lang is not ("verilog" or "systemverilog" or "vhdl")
                        || HdlGraphContractMatchesCurrent)
                    && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang));
        }

        private void RecordReusedFullScanExtractionItem(
            FileRecord record)
        {
            var stalePurged = DeferCSharpMutationsForIncompleteScan
                ? 0
                : Writer.PurgeStaleFilesSharingChecksum(
                    ProjectRoot,
                    record.Path,
                    record.Checksum);
            if (stalePurged > 0)
            {
                FtsMutated = true;
                CSharpMetadataTargetsNeedRefresh = true;
                RequireTypeScriptAugmentationRefresh();
                if (!Options.SymbolsOnly)
                    MutualRecursionRefreshNeeded = true;
            }

            Skipped++;
            if (!string.IsNullOrWhiteSpace(record.Lang))
            {
                SkippedSymbolExtractorLanguages ??=
                    new HashSet<string>(StringComparer.Ordinal);
                SkippedSymbolExtractorLanguages.Add(record.Lang);
            }
            if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang)
                && record.Lang != null)
            {
                ReusedHotspotFamilyLanguages ??=
                    new HashSet<string>(StringComparer.Ordinal);
                ReusedHotspotFamilyLanguages.Add(record.Lang);
            }
            if (Options.Verbose
                && !Options.Json
                && !Options.Quiet)
            {
                IndexProgress.Pause();
                ConsoleUi.ClearProgressLine();
                CommandOutputWriter.WriteLine($"  [SKIP] {record.Path}");
                IndexProgress.Resume();
            }
        }

        private void CompleteFullScanExtractionItem(int processedBeforeExtraction)
        {
            Processed++;
            var processed = processedBeforeExtraction + Processed;
            ProcessedCount = processed;
            CurrentJsonIndexFile = null;
            ThrowIfFullScanCancelled(processed, FilesCount);
            FullScanProgress.ReportJsonIndexProgressIfNeeded();
            if (Options.Json || Options.Quiet)
                return;

            IndexProgress.Pause();
            ConsoleUi.PrintProgress(processed, FilesCount);
            IndexProgress.Resume();
        }
    }
}
