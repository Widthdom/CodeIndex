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
    private readonly record struct FullScanExtractionCore(
        DbWriter Writer,
        FileIndexer Indexer,
        IndexCommandOptions Options,
        string ProjectRoot,
        FullScanFileTarget[] FileTargets,
        ReadableFileByteTracker ReadableFileBytes,
        IndexProgressReporter IndexProgress,
        FullScanProgressSession FullScanProgress);

    private readonly record struct FullScanExtractionWork(
        List<int>? FileIndexes,
        int ItemCount,
        int Parallelism,
        int FilesCount,
        bool ForceExtractorRefresh,
        DbWriter.AuthoritativeFreshFoldRowsClaim? AuthoritativeFreshFoldRowsClaim,
        CancellationToken CancellationToken,
        string ActualMode);

    private readonly record struct FullScanExtractionContracts(
        bool PriorSymbolsOnlyGraphOmitted,
        bool SymbolKindFilterMatchesPrior,
        bool CSharpIndexedProjectRootCompatible,
        bool CSharpSymbolNameContractMatchesCurrent,
        bool SqlGraphContractMatchesCurrent,
        bool HdlGraphContractMatchesCurrent,
        bool StartedWithNoIndexedFiles);

    private readonly record struct FullScanExtractionReuse(
        bool JavaScriptTypeScriptRefreshRequired,
        IReadOnlyDictionary<string, bool> HotspotFamilyTrustMatchesCurrent);

    private struct FullScanExtractionRefreshState
    {
        internal bool FtsMutated { get; set; }
        internal bool MutualRecursionRefreshNeeded { get; set; }
        internal bool CSharpMetadataTargetsNeedRefresh { get; set; }
        internal int SymbolsDroppedByKindFilter { get; set; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get; set; }
        internal HashSet<string>? SkippedSymbolExtractorLanguages { get; set; }
        internal HashSet<string> IndexedSymbolExtractorLanguages { get; set; }
    }

    private struct FullScanExtractionCounts
    {
        internal int Processed { get; set; }
        internal int Skipped { get; set; }
        internal int Warnings { get; set; }
        internal int Errors { get; set; }
        internal long ExtractedFiles { get; set; }
        internal long ExtractedChunks { get; set; }
        internal long ExtractedSymbols { get; set; }
        internal long ExtractedReferences { get; set; }
    }

    private struct FullScanExtractionPersistenceCounts
    {
        internal long PersistedFiles { get; set; }
        internal long PersistedChunks { get; set; }
        internal long PersistedSymbols { get; set; }
        internal long PersistedReferences { get; set; }
        internal long FreshFiles { get; set; }
        internal long FreshChunks { get; set; }
        internal long FreshSymbols { get; set; }
        internal long FreshReferences { get; set; }
    }

    private readonly record struct FullScanExtractionRequest(
        FullScanExtractionCore Core,
        FullScanExtractionWork Work,
        FullScanExtractionContracts Contracts,
        FullScanExtractionReuse Reuse);

    private sealed class FullScanExtractionState
    {
        internal required FullScanPreWriteState PreWrite { get; init; }
        internal FullScanExtractionRefreshState Refresh;
        internal FullScanExtractionCounts Counts;
        internal FullScanExtractionPersistenceCounts PersistenceCounts;
    }

    private readonly record struct FullScanExtractionExternalOperations(
        Action RequireTypeScriptAugmentationRefresh,
        Action WriteProjectRootOnce);

    private sealed partial class FullScanExtractionSession
    {
        internal required FullScanExtractionRequest Request { get; init; }
        internal required FullScanExtractionState State { get; init; }
        internal required FullScanExtractionExternalOperations External { get; init; }
        internal FullScanExtractionWorkerResources? Lifetime { get; set; }

        private FullScanExtractionCore Core => Request.Core;
        private FullScanExtractionWork Work => Request.Work;
        private FullScanExtractionContracts Contracts => Request.Contracts;
        private FullScanExtractionReuse Reuse => Request.Reuse;
        internal FullScanPreWriteState PreWriteState => State.PreWrite;
        internal DbWriter Writer => Core.Writer;
        internal FileIndexer Indexer => Core.Indexer;
        internal IndexCommandOptions Options => Core.Options;
        internal string ProjectRoot => Core.ProjectRoot;
        internal FullScanFileTarget[] FileTargets => Core.FileTargets;
        internal ReadableFileByteTracker ReadableFileBytes => Core.ReadableFileBytes;
        internal IndexProgressReporter IndexProgress => Core.IndexProgress;
        internal FullScanProgressSession FullScanProgress => Core.FullScanProgress;
        internal List<int>? ExtractionFileIndexes => Work.FileIndexes;
        internal int ExtractionWorkItemCount => Work.ItemCount;
        internal int ExtractionParallelism => Work.Parallelism;
        internal int FilesCount => Work.FilesCount;
        internal bool ForceExtractorRefresh => Work.ForceExtractorRefresh;
        internal bool StartedWithNoIndexedFiles => Contracts.StartedWithNoIndexedFiles;
        internal DbWriter.AuthoritativeFreshFoldRowsClaim? AuthoritativeFreshFoldRowsClaim
            => Work.AuthoritativeFreshFoldRowsClaim;
        internal CancellationToken CancellationToken => Work.CancellationToken;
        internal bool PriorSymbolsOnlyGraphOmitted => Contracts.PriorSymbolsOnlyGraphOmitted;
        internal bool SymbolKindFilterMatchesPrior => Contracts.SymbolKindFilterMatchesPrior;
        internal bool CSharpIndexedProjectRootCompatible
            => Contracts.CSharpIndexedProjectRootCompatible;
        internal bool CSharpSymbolNameContractMatchesCurrent
            => Contracts.CSharpSymbolNameContractMatchesCurrent;
        internal bool SqlGraphContractMatchesCurrent => Contracts.SqlGraphContractMatchesCurrent;
        internal bool HdlGraphContractMatchesCurrent => Contracts.HdlGraphContractMatchesCurrent;
        internal int ProcessedCount
        {
            get => PreWriteState.Selection.Processed;
            set => PreWriteState.Selection.Processed = value;
        }
        internal bool IndexProgressVisible
        {
            get => FullScanProgress.IndexProgressVisible;
            set => FullScanProgress.IndexProgressVisible = value;
        }
        internal ActiveExtractionPhase?[] ActiveExtractionPhases
        {
            get => FullScanProgress.ActiveExtractionPhases;
            set => FullScanProgress.ActiveExtractionPhases = value;
        }
        internal string? CurrentJsonIndexFile
        {
            get => FullScanProgress.CurrentJsonIndexFile;
            set => FullScanProgress.CurrentJsonIndexFile = value;
        }
        internal bool DeferCSharpMutationsForIncompleteScan
            => PreWriteState.Scan.DeferCSharpMutationsForIncompleteScan;
        internal CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace
            => PreWriteState.CSharp.Workspace;
        internal CSharpPrepassSymbolArtifactCache? CSharpPrepassSymbolArtifacts
            => PreWriteState.CSharp.PrepassSymbolArtifacts;
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceFileSnapshots
            => PreWriteState.CSharp.WorkspaceFileSnapshots;
        internal Action RequireTypeScriptAugmentationRefresh
            => External.RequireTypeScriptAugmentationRefresh;
        internal Action WriteProjectRootOnce => External.WriteProjectRootOnce;
        internal int Processed { get => State.Counts.Processed; set => State.Counts.Processed = value; }
        internal int Skipped { get => State.Counts.Skipped; set => State.Counts.Skipped = value; }
        internal int Warnings { get => State.Counts.Warnings; set => State.Counts.Warnings = value; }
        internal int ErrorsAdded { get => State.Counts.Errors; set => State.Counts.Errors = value; }
        internal long ExtractedFiles { get => State.Counts.ExtractedFiles; set => State.Counts.ExtractedFiles = value; }
        internal long ExtractedChunks { get => State.Counts.ExtractedChunks; set => State.Counts.ExtractedChunks = value; }
        internal long ExtractedSymbols { get => State.Counts.ExtractedSymbols; set => State.Counts.ExtractedSymbols = value; }
        internal long ExtractedReferences { get => State.Counts.ExtractedReferences; set => State.Counts.ExtractedReferences = value; }
        internal bool FtsMutated { get => State.Refresh.FtsMutated; set => State.Refresh.FtsMutated = value; }
        internal bool MutualRecursionRefreshNeeded { get => State.Refresh.MutualRecursionRefreshNeeded; set => State.Refresh.MutualRecursionRefreshNeeded = value; }
        internal bool CSharpMetadataTargetsNeedRefresh { get => State.Refresh.CSharpMetadataTargetsNeedRefresh; set => State.Refresh.CSharpMetadataTargetsNeedRefresh = value; }
        internal int SymbolsDroppedByKindFilter { get => State.Refresh.SymbolsDroppedByKindFilter; set => State.Refresh.SymbolsDroppedByKindFilter = value; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get => State.Refresh.ReusedHotspotFamilyLanguages; set => State.Refresh.ReusedHotspotFamilyLanguages = value; }
        internal HashSet<string>? SkippedSymbolExtractorLanguages { get => State.Refresh.SkippedSymbolExtractorLanguages; set => State.Refresh.SkippedSymbolExtractorLanguages = value; }
        internal HashSet<string> IndexedSymbolExtractorLanguages => State.Refresh.IndexedSymbolExtractorLanguages;
        internal List<CliJsonMessage> ErrorList => PreWriteState.Diagnostics.ErrorList;
        internal List<StatusIndexFileError> FileErrorList => PreWriteState.Diagnostics.FileErrorList;
        internal List<CliJsonMessage> WarningList => PreWriteState.Diagnostics.WarningList;

        internal bool TargetRequiresJavaScriptTypeScriptRefresh(
            string? language,
            string indexPath)
            => Reuse.JavaScriptTypeScriptRefreshRequired
               && (IsJavaScriptTypeScriptLanguage(language)
                   || IsJavaScriptTypeScriptConfigPath(indexPath));

        internal bool AllowReuseWithCurrentHotspotFamilyTrust(string? language)
            => IndexCommandRunner.AllowReuseWithCurrentHotspotFamilyTrust(
                language,
                Reuse.HotspotFamilyTrustMatchesCurrent);

        internal void InsertIssuesForIndexedFile(
            long fileId,
            IReadOnlyList<FileIssue> issues)
        {
            if (StartedWithNoIndexedFiles)
                Writer.InsertIssuesForNewFile(fileId, issues);
            else
                Writer.InsertIssues(fileId, issues);
        }

        internal void CountFreshInsertedRows(
            int chunkCount,
            int symbolCount,
            int referenceCount)
        {
            State.PersistenceCounts.PersistedFiles++;
            State.PersistenceCounts.PersistedChunks += chunkCount;
            State.PersistenceCounts.PersistedSymbols += symbolCount;
            State.PersistenceCounts.PersistedReferences += referenceCount;
            if (!StartedWithNoIndexedFiles)
                return;

            State.PersistenceCounts.FreshFiles++;
            State.PersistenceCounts.FreshChunks += chunkCount;
            State.PersistenceCounts.FreshSymbols += symbolCount;
            State.PersistenceCounts.FreshReferences += referenceCount;
        }

        internal void ThrowIfFullScanCancelled(int filesProcessed, int? filesTotal)
        {
            if (!CancellationToken.IsCancellationRequested)
                return;

            throw new IndexInterruptedException(
                filesProcessed,
                filesTotal,
                Work.ActualMode);
        }

        internal void DeferCSharpMutationsForLoadedSnapshotDrift(string path)
        {
            Lifetime?.TakeCSharpArtifacts()?.Clear();
            var scan = PreWriteState.Scan;
            var csharp = PreWriteState.CSharp;
            csharp.PrepassSymbolArtifacts = null;
            path = FormatCSharpWorkspaceSnapshotPath(ProjectRoot, path);
            scan.DeferCSharpMutationsForIncompleteScan = true;
            csharp.PreservePriorPositiveSourceNoOp = false;
            csharp.Evidence.ForStamp = false;
            csharp.Evidence.Complete = false;
            csharp.WorkspaceFileSnapshots = null;
            csharp.Workspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: [path]);
            Writer.SetCSharpStaticInterfaceSourceEvidence(null);

            var diagnostics = PreWriteState.Diagnostics;
            const string phase = "csharp_workspace_validation";
            if (!diagnostics.ReportedCSharpWorkspaceFailures.Add(
                    $"{phase}\n{path}"))
            {
                return;
            }

            var exception = new IOException(
                "A C# source changed after workspace preflight; rerun indexing to refresh the complete C# graph.");
            diagnostics.Errors++;
            diagnostics.ErrorList.Add(
                new CliJsonMessage(path, FormatIndexFileException(exception)));
            if (diagnostics.FileErrorList.Count < PartialIndexFileErrorLimit)
            {
                diagnostics.FileErrorList.Add(
                    BuildIndexFileError(path, phase, exception));
            }
        }
    }

    private readonly record struct FullScanExtractionScheduling(
        bool Parallelize,
        string? Reason);

    private readonly record struct FullScanExtractionTailCandidate(
        int WorkOrdinal,
        long? Length);

    private sealed class FullScanExtractionWorkerResources(
        CSharpPrepassSymbolArtifactCache? csharpPrepassSymbolArtifacts,
        PostExtractionHookRunner postExtractionHooks)
    {
        private int disposed;
        private Task workerCompletion = Task.CompletedTask;
        private CSharpPrepassSymbolArtifactCache? csharpArtifacts =
            csharpPrepassSymbolArtifacts;

        internal BlockingCollection<FullScanFileWorkItem>? Results { get; private set; }
        internal CancellationTokenSource? Cancellation { get; private set; }
        internal LazyDisposable<SymbolExtractionWorkerClient>? SymbolExtractionWorker
        { get; private set; }

        internal void AttachResults(BlockingCollection<FullScanFileWorkItem> results)
            => Results = results;

        internal void AttachCancellation(CancellationTokenSource cancellation)
            => Cancellation = cancellation;

        internal void AttachSymbolExtractionWorker(
            LazyDisposable<SymbolExtractionWorkerClient> symbolExtractionWorker)
            => SymbolExtractionWorker = symbolExtractionWorker;

        internal CSharpPrepassSymbolArtifactCache? TakeCSharpArtifacts()
            => Interlocked.Exchange(ref csharpArtifacts, null);

        internal void AttachWorkers(Task[] workers)
        {
            workerCompletion = Task.WhenAll(workers).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    try
                    {
                        Results?.CompleteAdding();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void DisposeNowOrWhenWorkersStop()
        {
            var completion = workerCompletion;
            if (completion.IsCompleted)
            {
                _ = completion.Exception;
                DisposeResources();
                return;
            }

            _ = completion.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((FullScanExtractionWorkerResources)state!).DisposeResources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            try { TakeCSharpArtifacts()?.Clear(); } catch { }
            try { postExtractionHooks.Dispose(); } catch { }
            try { SymbolExtractionWorker?.Dispose(); } catch { }
            try { Results?.Dispose(); } catch { }
            try { Cancellation?.Dispose(); } catch { }
            try { FullScanExtractionWorkersStoppedForTesting?.Invoke(); } catch { }
        }
    }

    private const int FullScanExtractionTailWorkerWaves = 4;
    internal const int MaxFullScanExtractionTailProbeCount = 64;

    private static PostExtractionHookRunner?
        RunFullScanExtractionPipeline(
            FullScanExtractionSession context)
    {
        if (context.ExtractionWorkItemCount == 0)
        {
            context.CSharpPrepassSymbolArtifacts?.Clear();
            context.PreWriteState.CSharp.PrepassSymbolArtifacts = null;
            FullScanExtractionSchedulingForTesting?.Invoke(false, null);
            return null;
        }

        var csharpArtifacts = context.CSharpPrepassSymbolArtifacts;
        var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault(
            context.Options.MaxFileSizeBytes,
            maxSymbolCount: context.Options.MaxSymbolsPerFile + 1,
            maxReferenceCount: context.Options.MaxReferencesPerFile + 1);
        var workerResources = new FullScanExtractionWorkerResources(
            csharpArtifacts,
            postExtractionHooks);
        context.Lifetime = workerResources;
        try
        {
            if (postExtractionHooks.HasHooks)
                context.AuthoritativeFreshFoldRowsClaim?.Invalidate();
            var scheduling = ResolveFullScanExtractionScheduling(
                context,
                postExtractionHooks);
            FullScanExtractionSchedulingForTesting?.Invoke(
                scheduling.Parallelize,
                scheduling.Reason);
            context.FullScanProgress.EnsureIndexingActivityVisible();
            context.FullScanProgress.StartJsonHeartbeatIfNeeded();
            ExecuteFullScanExtractionPipeline(
                context,
                workerResources,
                postExtractionHooks,
                scheduling.Parallelize);
            return postExtractionHooks;
        }
        finally
        {
            workerResources.DisposeNowOrWhenWorkersStop();
            context.Lifetime = null;
            context.CurrentJsonIndexFile = null;
            context.FullScanProgress.StopJsonHeartbeat();
        }
    }

    private static FullScanExtractionScheduling
        ResolveFullScanExtractionScheduling(
            FullScanExtractionSession context,
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

    internal static int[] BuildFullScanExtractionTailSchedule(
        int workItemCount,
        int workerCount,
        long maxFileSizeBytes,
        Func<int, long?> getFileLength,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workItemCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFileSizeBytes);
        ArgumentNullException.ThrowIfNull(getFileLength);
        cancellationToken.ThrowIfCancellationRequested();
        if (workerCount <= 1 || workItemCount <= workerCount)
            return [];

        // Dynamic claiming balances the main body of the scan, but a large file at the
        // input tail otherwise starts in the final worker wave. Probe only a fixed-size
        // suffix so the remedy never turns into an all-repository metadata pass.
        // 本体はdynamic claimで均等化し、末尾だけを固定上限でsize順にして全件statを避ける。
        var workerBound = workerCount >= MaxFullScanExtractionTailProbeCount / FullScanExtractionTailWorkerWaves
            ? MaxFullScanExtractionTailProbeCount
            : workerCount * FullScanExtractionTailWorkerWaves;
        var tailCount = Math.Min(
            workItemCount,
            Math.Min(workerBound, MaxFullScanExtractionTailProbeCount));
        var tailStart = workItemCount - tailCount;
        var candidates = new FullScanExtractionTailCandidate[tailCount];
        for (var tailIndex = 0; tailIndex < tailCount; tailIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workOrdinal = tailStart + tailIndex;
            long? length;
            try
            {
                length = getFileLength(workOrdinal);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException
                    or System.Security.SecurityException)
            {
                length = null;
            }

            var eligibleLength = length is >= 0
                && length <= maxFileSizeBytes
                    ? length
                    : null;
            candidates[tailIndex] = new FullScanExtractionTailCandidate(
                workOrdinal,
                eligibleLength);
        }

        Array.Sort(
            candidates,
            static (left, right) =>
            {
                if (left.Length.HasValue != right.Length.HasValue)
                    return left.Length.HasValue ? -1 : 1;

                if (left.Length.HasValue)
                {
                    var lengthComparison = right.Length.GetValueOrDefault().CompareTo(
                        left.Length.GetValueOrDefault());
                    if (lengthComparison != 0)
                        return lengthComparison;
                }

                return left.WorkOrdinal.CompareTo(right.WorkOrdinal);
            });

        var schedule = new int[candidates.Length];
        for (var index = 0; index < candidates.Length; index++)
            schedule[index] = candidates[index].WorkOrdinal;
        return schedule;
    }

    internal static int ResolveFullScanExtractionFileIndex(
        IReadOnlyList<int>? extractionFileIndexes,
        int workOrdinal)
        => extractionFileIndexes == null
            ? workOrdinal
            : extractionFileIndexes[workOrdinal];

    private static long? ReadFullScanExtractionFileLength(string filePath)
    {
        var info = new FileInfo(filePath);
        return info.Exists ? info.Length : null;
    }

    private static void ExecuteFullScanExtractionPipeline(
            FullScanExtractionSession context,
            FullScanExtractionWorkerResources workerResources,
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
        context.ActiveExtractionPhases = activeExtractionPhases;
        var extractionQueueCapacity = parallelizeExtraction
            ? Math.Max(1, extractionWorkerCount * 2)
            : 1;
        FullScanExtractionQueueCapacityForTesting?.Invoke(
            extractionQueueCapacity);

        var extractionResults =
            new BlockingCollection<FullScanFileWorkItem>(
                extractionQueueCapacity);
        workerResources.AttachResults(extractionResults);
        var extractionStallCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken);
        workerResources.AttachCancellation(extractionStallCts);
        var mainSymbolExtractionWorker =
            new LazyDisposable<SymbolExtractionWorkerClient>(
                () => new SymbolExtractionWorkerClient(
                    context.Options.MaxFileSizeBytes));
        workerResources.AttachSymbolExtractionWorker(mainSymbolExtractionWorker);
        try
        {
            var extractionTailSchedule = parallelizeExtraction
                ? BuildFullScanExtractionTailSchedule(
                    context.ExtractionWorkItemCount,
                    extractionWorkerCount,
                    context.Indexer.MaxFileSizeBytes,
                    workOrdinal => ReadFullScanExtractionFileLength(
                        context.FileTargets[
                            ResolveFullScanExtractionFileIndex(
                                context.ExtractionFileIndexes,
                                workOrdinal)].FilePath),
                    context.CancellationToken)
                : [];
            var workers = StartFullScanExtractionWorkers(
                new FullScanExtractionWorkerContext
                {
                    Indexer = context.Indexer,
                    Options = context.Options,
                    ProjectRoot = context.ProjectRoot,
                    FileTargets = context.FileTargets,
                    ExtractionFileIndexes = context.ExtractionFileIndexes,
                    ExtractionWorkItemCount = context.ExtractionWorkItemCount,
                    ExtractionWorkerCount = extractionWorkerCount,
                    ParallelizeExtraction = parallelizeExtraction,
                    ExtractionTailSchedule = extractionTailSchedule,
                    CSharpWorkspace = context.CSharpWorkspace,
                    CSharpPrepassSymbolArtifacts = context.CSharpPrepassSymbolArtifacts,
                    CSharpWorkspaceFileSnapshots = context.CSharpWorkspaceFileSnapshots,
                    PostExtractionHooks = postExtractionHooks,
                    ActiveExtractionPhases = activeExtractionPhases,
                    ExtractionResults = extractionResults,
                    ExtractionCancellationToken = extractionStallCts.Token,
                    CancellationToken = context.CancellationToken,
                },
                workerResources.AttachWorkers);
            var processedBeforeExtraction = context.ProcessedCount;
            var consumerResources = new FullScanExtractionConsumerResources(
                postExtractionHooks,
                mainSymbolExtractionWorker.Value,
                extractionResults,
                workers,
                IndexExtractionStallTimeoutForTesting?.Invoke()
                    ?? IndexExtractionStallTimeout,
                activeExtractionPhases,
                extractionStallCts,
                processedBeforeExtraction);
            context.ConsumeFullScanExtractionResults(in consumerResources);
            context.ProcessedCount = processedBeforeExtraction + context.State.Counts.Processed;
        }
        catch
        {
            try { extractionStallCts.Cancel(); } catch { }
            throw;
        }
    }

    private static void PrepareFullScanExtractionProgress(
        FullScanExtractionSession context)
    {
        if (context.Options.Json || context.Options.Quiet)
            return;

        context.IndexProgress.Pause();
        context.IndexProgressVisible = true;
        ConsoleUi.PrintProgress(0, context.FilesCount);
    }

}
