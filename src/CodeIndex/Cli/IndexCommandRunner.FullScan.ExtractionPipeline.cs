using System.Collections.Concurrent;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanExtractionPipelineContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileTarget[] FileTargets { get; init; }
        internal List<int>? ExtractionFileIndexes { get; init; }
        internal required int ExtractionWorkItemCount { get; init; }
        internal required int ExtractionParallelism { get; init; }
        internal required int FilesCount { get; init; }
        internal required bool ForceExtractorRefresh { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool PriorSymbolsOnlyGraphOmitted { get; init; }
        internal required bool SymbolKindFilterMatchesPrior { get; init; }
        internal required bool CSharpIndexedProjectRootCompatible
        {
            get;
            init;
        }

        internal required bool CSharpSymbolNameContractMatchesCurrent
        {
            get;
            init;
        }

        internal required bool SqlGraphContractMatchesCurrent { get; init; }
        internal required bool HdlGraphContractMatchesCurrent { get; init; }
        internal required ReadableFileByteTracker ReadableFileBytes
        {
            get;
            init;
        }

        internal required IndexProgressReporter IndexProgress { get; init; }
        internal required FullScanProgressSession FullScanProgress { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Func<int> GetProcessedCount { get; init; }
        internal required Action<int> PublishProcessedCount { get; init; }
        internal required Action<int, int?> ThrowIfFullScanCancelled
        {
            get;
            init;
        }

        internal required Action<bool> SetIndexProgressVisible { get; init; }
        internal required Action<ActiveExtractionPhase?[]>
            SetActiveExtractionPhases
        { get; init; }
        internal required Action<string?> SetCurrentJsonIndexFile { get; init; }
        internal required Func<string?> GetCurrentJsonIndexFile { get; init; }
        internal required Func<bool>
            GetDeferCSharpMutationsForIncompleteScan
        { get; init; }
        internal required Func<bool> GetFtsMutated { get; init; }
        internal required Func<CSharpStaticInterfaceWorkspaceSymbols>
            GetCSharpWorkspace
        { get; init; }
        internal required Func<Dictionary<string,
            CSharpStaticInterfacePrepass.FileStatSnapshot>?>
            GetCSharpWorkspaceFileSnapshots
        { get; init; }
        internal required Action<string>
            DeferCSharpMutationsForLoadedSnapshotDrift
        { get; init; }
        internal required Func<string?, string, bool>
            TargetRequiresJavaScriptTypeScriptRefresh
        { get; init; }
        internal required Func<string?, bool>
            AllowReuseWithCurrentHotspotFamilyTrust
        { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh
        {
            get;
            init;
        }

        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<long, IReadOnlyList<FileIssue>>
            InsertIssuesForIndexedFile
        { get; init; }
        internal required Action<int, int, int> CountFreshInsertedRows
        {
            get;
            init;
        }

        internal required FullScanExtractionConsumerState ConsumerState
        {
            get;
            init;
        }
    }

    private sealed record FullScanExtractionPipelineResult(
        PostExtractionHookRunner? PostExtractionHooks,
        FullScanExtractionConsumerState? ConsumerState);

    private readonly record struct FullScanExtractionScheduling(
        bool Parallelize,
        string? Reason);

    private static FullScanExtractionPipelineResult
        RunFullScanExtractionPipeline(
            FullScanExtractionPipelineContext context)
    {
        if (context.ExtractionWorkItemCount == 0)
        {
            FullScanExtractionSchedulingForTesting?.Invoke(false, null);
            return new FullScanExtractionPipelineResult(null, null);
        }

        var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault(
            context.Options.MaxFileSizeBytes,
            maxSymbolCount: context.Options.MaxSymbolsPerFile + 1,
            maxReferenceCount: context.Options.MaxReferencesPerFile + 1);
        var scheduling = ResolveFullScanExtractionScheduling(
            context,
            postExtractionHooks);
        FullScanExtractionSchedulingForTesting?.Invoke(
            scheduling.Parallelize,
            scheduling.Reason);
        context.FullScanProgress.EnsureIndexingActivityVisible();
        context.FullScanProgress.StartJsonHeartbeatIfNeeded();
        try
        {
            var consumerState = ExecuteFullScanExtractionPipeline(
                context,
                postExtractionHooks,
                scheduling.Parallelize);
            return new FullScanExtractionPipelineResult(
                postExtractionHooks,
                consumerState);
        }
        finally
        {
            context.SetCurrentJsonIndexFile(null);
            context.FullScanProgress.StopJsonHeartbeat();
            postExtractionHooks.Dispose();
        }
    }

    private static FullScanExtractionScheduling
        ResolveFullScanExtractionScheduling(
            FullScanExtractionPipelineContext context,
            PostExtractionHookRunner postExtractionHooks)
    {
        var parallelize = !context.Options.SymbolKindFilter.IsActive
            && postExtractionHooks.Hooks.Count == 0;
        var reason = !parallelize
            ? null
            : context.Options.Rebuild
                ? "rebuild"
                : context.StartedWithNoIndexedFiles
                    ? "empty_index"
                    : "incremental_changes";
        return new FullScanExtractionScheduling(parallelize, reason);
    }

    private static FullScanExtractionConsumerState
        ExecuteFullScanExtractionPipeline(
            FullScanExtractionPipelineContext context,
            PostExtractionHookRunner postExtractionHooks,
            bool parallelizeExtraction)
    {
        PrepareFullScanExtractionProgress(context);
        FullScanExtractionWorkStartedForTesting?.Invoke();
        var extractionWorkerCount = Math.Min(
            context.ExtractionParallelism,
            context.ExtractionWorkItemCount);
        var activeExtractionPhases =
            new ActiveExtractionPhase?[extractionWorkerCount];
        context.SetActiveExtractionPhases(activeExtractionPhases);
        var extractionQueueCapacity = parallelizeExtraction
            ? Math.Max(1, extractionWorkerCount * 2)
            : 1;
        FullScanExtractionQueueCapacityForTesting?.Invoke(
            extractionQueueCapacity);

        using var extractionResults =
            new BlockingCollection<FullScanFileWorkItem>(
                extractionQueueCapacity);
        using var extractionStallCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken);
        using var mainSymbolExtractionWorker =
            new LazyDisposable<SymbolExtractionWorkerClient>(
                () => new SymbolExtractionWorkerClient(
                    context.Options.MaxFileSizeBytes));
        var workers = StartFullScanExtractionWorkers(
            new FullScanExtractionWorkerContext
            {
                Indexer = context.Indexer,
                Options = context.Options,
                ProjectRoot = context.ProjectRoot,
                FileTargets = context.FileTargets,
                ExtractionFileIndexes =
                    context.ExtractionFileIndexes,
                ExtractionWorkItemCount =
                    context.ExtractionWorkItemCount,
                ExtractionWorkerCount = extractionWorkerCount,
                ParallelizeExtraction = parallelizeExtraction,
                CSharpWorkspace = context.GetCSharpWorkspace(),
                CSharpWorkspaceFileSnapshots =
                    context.GetCSharpWorkspaceFileSnapshots(),
                PostExtractionHooks = postExtractionHooks,
                ActiveExtractionPhases = activeExtractionPhases,
                ExtractionResults = extractionResults,
                ExtractionCancellationToken =
                    extractionStallCts.Token,
                CancellationToken = context.CancellationToken,
            });
        CompleteFullScanExtractionQueueWhenWorkersFinish(
            workers,
            extractionResults);

        var processedBeforeExtraction = context.GetProcessedCount();
        var consumerContext = CreateFullScanExtractionConsumerContext(
            context,
            postExtractionHooks,
            mainSymbolExtractionWorker.Value,
            extractionResults,
            extractionStallCts,
            workers,
            activeExtractionPhases,
            processedBeforeExtraction);
        var consumerState =
            ConsumeFullScanExtractionResults(consumerContext);
        context.PublishProcessedCount(
            processedBeforeExtraction + consumerState.Processed);
        return consumerState;
    }

    private static void PrepareFullScanExtractionProgress(
        FullScanExtractionPipelineContext context)
    {
        if (context.Options.Json || context.Options.Quiet)
            return;

        context.IndexProgress.Pause();
        context.SetIndexProgressVisible(true);
        ConsoleUi.PrintProgress(0, context.FilesCount);
    }

    private static void CompleteFullScanExtractionQueueWhenWorkersFinish(
        Task[] workers,
        BlockingCollection<FullScanFileWorkItem> extractionResults)
    {
        _ = Task.WhenAll(workers).ContinueWith(
            _ => extractionResults.CompleteAdding(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static FullScanExtractionConsumerContext
        CreateFullScanExtractionConsumerContext(
            FullScanExtractionPipelineContext context,
            PostExtractionHookRunner postExtractionHooks,
            SymbolExtractionWorkerClient symbolExtractionWorker,
            BlockingCollection<FullScanFileWorkItem> extractionResults,
            CancellationTokenSource extractionStallCts,
            Task[] workers,
            ActiveExtractionPhase?[] activeExtractionPhases,
            int processedBeforeExtraction)
        => new()
        {
            Writer = context.Writer,
            Indexer = context.Indexer,
            Options = context.Options,
            ProjectRoot = context.ProjectRoot,
            FileTargets = context.FileTargets,
            FilesCount = context.FilesCount,
            ProcessedBeforeExtraction = processedBeforeExtraction,
            ForceExtractorRefresh = context.ForceExtractorRefresh,
            StartedWithNoIndexedFiles =
                context.StartedWithNoIndexedFiles,
            PriorSymbolsOnlyGraphOmitted =
                context.PriorSymbolsOnlyGraphOmitted,
            SymbolKindFilterMatchesPrior =
                context.SymbolKindFilterMatchesPrior,
            CSharpIndexedProjectRootCompatible =
                context.CSharpIndexedProjectRootCompatible,
            CSharpSymbolNameContractMatchesCurrent =
                context.CSharpSymbolNameContractMatchesCurrent,
            SqlGraphContractMatchesCurrent =
                context.SqlGraphContractMatchesCurrent,
            HdlGraphContractMatchesCurrent =
                context.HdlGraphContractMatchesCurrent,
            ReadableFileBytes = context.ReadableFileBytes,
            PostExtractionHooks = postExtractionHooks,
            SymbolExtractionWorker = symbolExtractionWorker,
            IndexProgress = context.IndexProgress,
            ExtractionResults = extractionResults,
            Workers = workers,
            ExtractionStallTimeout =
                IndexExtractionStallTimeoutForTesting?.Invoke()
                ?? IndexExtractionStallTimeout,
            ActiveExtractionPhases = activeExtractionPhases,
            CancellationToken = context.CancellationToken,
            CancelExtraction = extractionStallCts.Cancel,
            EnsureIndexingActivityVisible =
                context.FullScanProgress.EnsureIndexingActivityVisible,
            ReportJsonIndexProgressIfNeeded =
                context.FullScanProgress.ReportJsonIndexProgressIfNeeded,
            ThrowIfFullScanCancelled =
                context.ThrowIfFullScanCancelled,
            PublishProcessedCount = context.PublishProcessedCount,
            SetCurrentJsonIndexFile = context.SetCurrentJsonIndexFile,
            GetCurrentJsonIndexFile = context.GetCurrentJsonIndexFile,
            GetDeferCSharpMutationsForIncompleteScan =
                context.GetDeferCSharpMutationsForIncompleteScan,
            GetFtsMutated = context.GetFtsMutated,
            GetCSharpWorkspace = context.GetCSharpWorkspace,
            GetCSharpWorkspaceFileSnapshots =
                context.GetCSharpWorkspaceFileSnapshots,
            DeferCSharpMutationsForLoadedSnapshotDrift =
                context.DeferCSharpMutationsForLoadedSnapshotDrift,
            TargetRequiresJavaScriptTypeScriptRefresh =
                context.TargetRequiresJavaScriptTypeScriptRefresh,
            AllowReuseWithCurrentHotspotFamilyTrust =
                context.AllowReuseWithCurrentHotspotFamilyTrust,
            RequireTypeScriptAugmentationRefresh =
                context.RequireTypeScriptAugmentationRefresh,
            WriteProjectRootOnce = context.WriteProjectRootOnce,
            InsertIssuesForIndexedFile =
                context.InsertIssuesForIndexedFile,
            CountFreshInsertedRows = context.CountFreshInsertedRows,
            State = context.ConsumerState,
        };
}
