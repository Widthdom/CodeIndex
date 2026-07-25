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
    private sealed class FullScanExtractionConsumerContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileTarget[] FileTargets { get; init; }
        internal required int FilesCount { get; init; }
        internal required int ProcessedBeforeExtraction { get; init; }
        internal required bool ForceExtractorRefresh { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool PriorSymbolsOnlyGraphOmitted { get; init; }
        internal required bool SymbolKindFilterMatchesPrior { get; init; }
        internal required bool CSharpIndexedProjectRootCompatible { get; init; }
        internal required bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal required bool SqlGraphContractMatchesCurrent { get; init; }
        internal required bool HdlGraphContractMatchesCurrent { get; init; }
        internal required ReadableFileByteTracker ReadableFileBytes { get; init; }
        internal required PostExtractionHookRunner PostExtractionHooks { get; init; }
        internal required SymbolExtractionWorkerClient SymbolExtractionWorker { get; init; }
        internal required IndexProgressReporter IndexProgress { get; init; }
        internal required BlockingCollection<FullScanFileWorkItem> ExtractionResults { get; init; }
        internal required Task[] Workers { get; init; }
        internal required TimeSpan ExtractionStallTimeout { get; init; }
        internal required ActiveExtractionPhase?[] ActiveExtractionPhases { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action CancelExtraction { get; init; }
        internal required Action EnsureIndexingActivityVisible { get; init; }
        internal required Action ReportJsonIndexProgressIfNeeded { get; init; }
        internal required Action<int, int?> ThrowIfFullScanCancelled { get; init; }
        internal required Action<int> PublishProcessedCount { get; init; }
        internal required Action<string?> SetCurrentJsonIndexFile { get; init; }
        internal required Func<string?> GetCurrentJsonIndexFile { get; init; }
        internal required Func<bool> GetDeferCSharpMutationsForIncompleteScan { get; init; }
        internal required Func<bool> GetFtsMutated { get; init; }
        internal required Func<CSharpStaticInterfaceWorkspaceSymbols> GetCSharpWorkspace { get; init; }
        internal required Func<Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?> GetCSharpWorkspaceFileSnapshots { get; init; }
        internal required Action<string> DeferCSharpMutationsForLoadedSnapshotDrift { get; init; }
        internal required Func<string?, string, bool> TargetRequiresJavaScriptTypeScriptRefresh { get; init; }
        internal required Func<string?, bool> AllowReuseWithCurrentHotspotFamilyTrust { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<long, IReadOnlyList<FileIssue>> InsertIssuesForIndexedFile { get; init; }
        internal required Action<int, int, int> CountFreshInsertedRows { get; init; }
        internal required FullScanExtractionConsumerState State { get; init; }
    }

    private sealed class FullScanExtractionConsumerState
    {
        internal int Processed { get; set; }
        internal int Skipped { get; set; }
        internal int Warnings { get; set; }
        internal int ErrorsAdded { get; set; }
        internal bool FtsMutated { get; set; }
        internal bool MutualRecursionRefreshNeeded { get; set; }
        internal bool CSharpMetadataTargetsNeedRefresh { get; set; }
        internal int SymbolsDroppedByKindFilter { get; set; }
        internal long ExtractedFiles { get; set; }
        internal long ExtractedChunks { get; set; }
        internal long ExtractedSymbols { get; set; }
        internal long ExtractedReferences { get; set; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get; set; }
        internal HashSet<string>? SkippedSymbolExtractorLanguages { get; set; }
        internal required HashSet<string> IndexedSymbolExtractorLanguages { get; init; }
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
    }

    private static FullScanExtractionConsumerState ConsumeFullScanExtractionResults(
        FullScanExtractionConsumerContext context)
    {
        var lastExtractionProgressAt = Stopwatch.GetTimestamp();
        while (!context.ExtractionResults.IsCompleted)
        {
            var processed = context.ProcessedBeforeExtraction + context.State.Processed;
            context.ThrowIfFullScanCancelled(processed, context.FilesCount);
            if (!context.ExtractionResults.TryTake(out var item, millisecondsTimeout: 100))
            {
                ThrowIfFullScanExtractionStalled(
                    processed,
                    context.FilesCount,
                    context.ExtractionStallTimeout,
                    lastExtractionProgressAt,
                    context.GetCurrentJsonIndexFile(),
                    context.ActiveExtractionPhases,
                    context.CancelExtraction);
                continue;
            }

            lastExtractionProgressAt = Stopwatch.GetTimestamp();
            context.SetCurrentJsonIndexFile(item.RelativePath);
            ProcessFullScanExtractionItem(context, item);
            CompleteFullScanExtractionItem(context);
        }

        Task.WaitAll(context.Workers, context.CancellationToken);
        return context.State;
    }

    private static void ProcessFullScanExtractionItem(
        FullScanExtractionConsumerContext context,
        FullScanFileWorkItem item)
    {
        var state = context.State;
        var options = context.Options;
        var writer = context.Writer;
        var indexFilePhase = item.FailurePhase ?? "preparing";
        var itemFileExtracted = item.Record == null ? 0L : 1L;
        var itemChunksExtracted = item.Chunks?.Count ?? 0L;
        var itemSymbolsExtracted = item.Symbols?.Count ?? 0L;
        var itemReferencesExtracted = item.References?.Count ?? 0L;
        context.EnsureIndexingActivityVisible();
        if (item.Exception is IndexExtractionStalledException stalledException)
            RethrowPreservingStackTrace(stalledException);

        try
        {
            if (ShouldDeferFullScanCSharpItem(context, item))
            {
                state.Skipped++;
                return;
            }

            if (item.Exception != null)
                RethrowPreservingStackTrace(item.Exception);

            if (item.Record == null)
            {
                RecordSkippedFullScanExtractionItem(context, item);
                return;
            }

            var record = item.Record;
            context.ReadableFileBytes.Remember(item.FileIndex, record.Size);
            if (item.Warning != null && !options.Json && !options.Quiet)
            {
                context.IndexProgress.Pause();
                ConsoleUi.PrintWarning(item.Warning);
                context.IndexProgress.Resume();
            }

            var generatedSuppressionIssue = item.GeneratedSuppressionChecked
                ? item.GeneratedSuppressionIssue
                : context.Indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path);
            var existingId = GetReusableFullScanFileId(context, record, generatedSuppressionIssue);
            if (existingId != null)
            {
                RecordReusedFullScanExtractionItem(context, record);
                return;
            }

            if (record.Lang == "csharp")
                state.CSharpMetadataTargetsNeedRefresh = true;
            if (record.Lang == "typescript")
                context.RequireTypeScriptAugmentationRefresh();

            var persistence = PersistFullScanFile(new FullScanFilePersistenceContext
            {
                Writer = writer,
                Indexer = context.Indexer,
                Options = options,
                ProjectRoot = context.ProjectRoot,
                Item = item,
                Record = record,
                GeneratedSuppressionIssue = generatedSuppressionIssue,
                StartedWithNoIndexedFiles = context.StartedWithNoIndexedFiles,
                DeferCSharpMutationsForIncompleteScan =
                    context.GetDeferCSharpMutationsForIncompleteScan(),
                CSharpWorkspace = context.GetCSharpWorkspace(),
                PostExtractionHooks = context.PostExtractionHooks,
                SymbolExtractionWorker = context.SymbolExtractionWorker,
                CancellationToken = context.CancellationToken,
                InsertIssuesForIndexedFile = context.InsertIssuesForIndexedFile,
                WriteProjectRootOnce = context.WriteProjectRootOnce,
                SetPhase = (path, phase) =>
                {
                    context.SetCurrentJsonIndexFile(path);
                    indexFilePhase = phase;
                },
            });
            itemChunksExtracted = persistence.ExtractedChunks;
            itemSymbolsExtracted = persistence.ExtractedSymbols;
            itemReferencesExtracted = persistence.ExtractedReferences;
            state.SymbolsDroppedByKindFilter += persistence.SymbolsDroppedByKindFilter;
            state.MutualRecursionRefreshNeeded |= persistence.MutualRecursionRefreshNeeded;
            state.CSharpMetadataTargetsNeedRefresh |= persistence.CSharpMetadataTargetsNeedRefresh;
            state.FtsMutated = true;
            if (persistence.StampSymbolExtractorLanguage
                && !string.IsNullOrWhiteSpace(record.Lang))
            {
                state.IndexedSymbolExtractorLanguages.Add(record.Lang);
            }
            context.CountFreshInsertedRows(
                persistence.PersistedChunks,
                persistence.PersistedSymbols,
                persistence.PersistedReferences);
            context.IndexProgress.WriteVerbose(persistence.VerboseMessage);
        }
        catch (IndexExtractionStalledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIndexFileFailure("index_file_failed", item.FilePath, indexFilePhase, ex);
            state.ErrorsAdded++;
            var errorMessage = FormatIndexFileException(ex);
            state.ErrorList.Add(new CliJsonMessage(item.FilePath, errorMessage));
            if (state.FileErrorList.Count < PartialIndexFileErrorLimit)
                state.FileErrorList.Add(BuildIndexFileError(item.RelativePath, indexFilePhase, ex));
            if (!options.Json)
            {
                context.IndexProgress.Pause();
                ConsoleUi.ClearProgressLine();
                ConsoleUi.TryWriteErrorLine(
                    FormatPerFileErrorLine("ERR ", item.FilePath, ex, errorMessage));
                context.IndexProgress.Resume();
            }
        }
        finally
        {
            state.ExtractedFiles += itemFileExtracted;
            state.ExtractedChunks += itemChunksExtracted;
            state.ExtractedSymbols += itemSymbolsExtracted;
            state.ExtractedReferences += itemReferencesExtracted;
        }
    }

    private static bool ShouldDeferFullScanCSharpItem(
        FullScanExtractionConsumerContext context,
        FullScanFileWorkItem item)
    {
        if (item.FileIndex < 0
            || context.FileTargets[item.FileIndex].Language != "csharp")
        {
            return false;
        }

        var deferCurrentItem = context.GetDeferCSharpMutationsForIncompleteScan();
        if (!deferCurrentItem
            && item.Exception is CSharpWorkspaceSnapshotDriftException driftException)
        {
            context.DeferCSharpMutationsForLoadedSnapshotDrift(driftException.Path);
            context.State.FtsMutated = context.GetFtsMutated();
            return true;
        }

        var workspaceFileSnapshots = context.GetCSharpWorkspaceFileSnapshots();
        if (!deferCurrentItem
            && item.Record != null
            && workspaceFileSnapshots != null
            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                item.FilePath,
                context.FileTargets[item.FileIndex].IndexPath,
                context.FileTargets[item.FileIndex].DisplayRelativePath,
                item.Record.Size,
                item.Record.Modified,
                workspaceFileSnapshots,
                out var changedPath,
                context.CancellationToken))
        {
            context.DeferCSharpMutationsForLoadedSnapshotDrift(
                changedPath ?? context.FileTargets[item.FileIndex].DisplayRelativePath);
            context.State.FtsMutated = context.GetFtsMutated();
            return true;
        }

        return deferCurrentItem;
    }

    private static void RecordSkippedFullScanExtractionItem(
        FullScanExtractionConsumerContext context,
        FullScanFileWorkItem item)
    {
        var state = context.State;
        var path = item.RelativePath;
        state.Warnings++;
        state.WarningList.Add(new CliJsonMessage(path, item.Warning ?? "File skipped"));
        if (!context.Options.Json
            && !context.Options.Quiet
            && item.Warning != null)
        {
            context.IndexProgress.Pause();
            ConsoleUi.PrintWarning(item.Warning);
            context.IndexProgress.Resume();
        }

        if (!context.Writer.HasFileAtPath(path))
        {
            state.Skipped++;
            return;
        }

        using var deleteTxn = context.Writer.BeginTransaction(
            context.CancellationToken,
            "full scan delete skipped file");
        if (!context.Writer.DeleteFileByPath(path))
            return;

        state.CSharpMetadataTargetsNeedRefresh = true;
        context.RequireTypeScriptAugmentationRefresh();
        context.WriteProjectRootOnce();
        deleteTxn.Commit();
        state.FtsMutated = true;
    }

    private static long? GetReusableFullScanFileId(
        FullScanExtractionConsumerContext context,
        FileRecord record,
        FileIssue? generatedSuppressionIssue)
    {
        var options = context.Options;
        if (context.ForceExtractorRefresh
            || options.Rebuild
            || context.StartedWithNoIndexedFiles
            || options.SymbolsOnly)
        {
            return null;
        }

        var targetRequiresRefresh =
            context.TargetRequiresJavaScriptTypeScriptRefresh(record.Lang, record.Path);
        return context.Writer.GetReusableUnchangedFileId(
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
            allowReuse: context.SymbolKindFilterMatchesPrior
                && !targetRequiresRefresh
                && !context.PriorSymbolsOnlyGraphOmitted
                && (record.Lang != "csharp" || context.CSharpIndexedProjectRootCompatible)
                && (record.Lang != "csharp" || context.CSharpSymbolNameContractMatchesCurrent)
                && (record.Lang != "csharp" || !context.GetCSharpWorkspace().HasStaticInterfaceContracts)
                && (record.Lang != "sql" || context.SqlGraphContractMatchesCurrent)
                && (record.Lang is not ("verilog" or "systemverilog" or "vhdl")
                    || context.HdlGraphContractMatchesCurrent)
                && context.AllowReuseWithCurrentHotspotFamilyTrust(record.Lang));
    }

    private static void RecordReusedFullScanExtractionItem(
        FullScanExtractionConsumerContext context,
        FileRecord record)
    {
        var state = context.State;
        var stalePurged = context.GetDeferCSharpMutationsForIncompleteScan()
            ? 0
            : context.Writer.PurgeStaleFilesSharingChecksum(
                context.ProjectRoot,
                record.Path,
                record.Checksum);
        if (stalePurged > 0)
        {
            state.FtsMutated = true;
            state.CSharpMetadataTargetsNeedRefresh = true;
            context.RequireTypeScriptAugmentationRefresh();
            if (!context.Options.SymbolsOnly)
                state.MutualRecursionRefreshNeeded = true;
        }

        state.Skipped++;
        if (!string.IsNullOrWhiteSpace(record.Lang))
        {
            state.SkippedSymbolExtractorLanguages ??=
                new HashSet<string>(StringComparer.Ordinal);
            state.SkippedSymbolExtractorLanguages.Add(record.Lang);
        }
        if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang)
            && record.Lang != null)
        {
            state.ReusedHotspotFamilyLanguages ??=
                new HashSet<string>(StringComparer.Ordinal);
            state.ReusedHotspotFamilyLanguages.Add(record.Lang);
        }
        if (context.Options.Verbose
            && !context.Options.Json
            && !context.Options.Quiet)
        {
            context.IndexProgress.Pause();
            ConsoleUi.ClearProgressLine();
            CommandOutputWriter.WriteLine($"  [SKIP] {record.Path}");
            context.IndexProgress.Resume();
        }
    }

    private static void CompleteFullScanExtractionItem(
        FullScanExtractionConsumerContext context)
    {
        context.State.Processed++;
        var processed = context.ProcessedBeforeExtraction + context.State.Processed;
        context.PublishProcessedCount(processed);
        context.SetCurrentJsonIndexFile(null);
        context.ThrowIfFullScanCancelled(processed, context.FilesCount);
        context.ReportJsonIndexProgressIfNeeded();
        if (context.Options.Json || context.Options.Quiet)
            return;

        context.IndexProgress.Pause();
        ConsoleUi.PrintProgress(processed, context.FilesCount);
        context.IndexProgress.Resume();
    }
}
