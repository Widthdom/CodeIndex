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
    internal enum UpdateParallelExtractionEventKind
    {
        WorkerStarted,
        ExtractionQueued,
        ExtractionStarted,
        ExtractionCompleted,
        PersistenceStarted,
        PersistenceCompleted,
    }

    internal readonly record struct UpdateParallelExtractionTestEvent(
        UpdateParallelExtractionEventKind Kind,
        int TargetIndex,
        string RelativePath,
        int WorkerIndex,
        int RetainedSymbolCount = 0,
        bool HasSourceContractEvidence = false,
        string? KnownLanguage = null);

    private sealed record UpdateParallelExtractionRequest(
        int TargetIndex,
        UpdateFileTarget Target,
        string? KnownLanguage,
        bool GeneratedExtractionSuppressed,
        TaskCompletionSource<UpdateParallelExtractionResult> Completion)
    {
        internal CancellationToken WindowCancellationToken { get; set; }

        internal UpdateParallelExtractionProgress Progress { get; } = new();
    }

    private sealed class UpdateParallelExtractionProgress
    {
        private readonly object loadedRecordLock = new();
        private string? phase;
        private FileRecord? record;
        private string? warning;
        private int hasCSharpStaticInterfaceSourceContract;
        private int csharpStaticInterfaceSourceContractCandidateState;
        private long version;

        internal string? Phase => Volatile.Read(ref phase);

        internal long Version => Volatile.Read(ref version);

        internal void SetPhase(string value)
        {
            Volatile.Write(ref phase, value);
            Interlocked.Increment(ref version);
        }

        internal void PublishLoadedRecord(FileRecord value, string? loadedWarning)
        {
            lock (loadedRecordLock)
            {
                warning = loadedWarning;
                record = value;
            }
            Interlocked.Increment(ref version);
        }

        internal (FileRecord? Record, string? Warning) GetLoadedRecord()
        {
            lock (loadedRecordLock)
                return (record, warning);
        }

        internal bool HasCSharpStaticInterfaceSourceContract
            => Volatile.Read(ref hasCSharpStaticInterfaceSourceContract) != 0;

        internal void PublishCSharpStaticInterfaceSourceContract(bool hasContract)
        {
            if (hasContract)
            {
                Interlocked.Exchange(ref hasCSharpStaticInterfaceSourceContract, 1);
                Interlocked.Increment(ref version);
            }
        }

        internal bool CSharpStaticInterfaceSourceContractCandidateEvaluated
            => Volatile.Read(
                ref csharpStaticInterfaceSourceContractCandidateState) != 0;

        internal bool MayContainCSharpStaticInterfaceSourceContract
            => Volatile.Read(
                ref csharpStaticInterfaceSourceContractCandidateState) == 2;

        internal void PublishMayContainCSharpStaticInterfaceSourceContract(
            bool mayContainContract)
        {
            Interlocked.Exchange(
                ref csharpStaticInterfaceSourceContractCandidateState,
                mayContainContract ? 2 : 1);
            Interlocked.Increment(ref version);
        }
    }

    private sealed record UpdateParallelExtractionResult(
        int TargetIndex,
        UpdateFileTarget Target,
        string? KnownLanguage,
        FileRecord? Record,
        string? Warning,
        IReadOnlyList<ChunkRecord>? Chunks,
        IReadOnlyList<SymbolRecord>? Symbols,
        IReadOnlyList<ReferenceRecord>? References,
        IReadOnlyList<FileIssue>? Issues,
        FileIssue? GeneratedSuppressionIssue,
        bool HasCSharpStaticInterfaceSourceContract,
        bool SymbolCapExceeded,
        string? FailurePhase,
        Exception? Exception)
    {
        internal static UpdateParallelExtractionResult Failure(
            UpdateParallelExtractionRequest request,
            string phase,
            Exception exception,
            FileRecord? record = null,
            string? warning = null,
            bool hasCSharpStaticInterfaceSourceContract = false)
            => new(
                request.TargetIndex,
                request.Target,
                request.KnownLanguage,
                record,
                warning,
                null,
                null,
                null,
                null,
                null,
                hasCSharpStaticInterfaceSourceContract,
                false,
                phase,
                exception);
    }

    private sealed record UpdateParallelExtractionWindowResult(
        IReadOnlyList<UpdateParallelExtractionResult> Results,
        UpdateParallelExtractionResult? ActualFatalResult,
        bool FatalWasNormalized,
        bool UnconsumedSourceContractCandidateBeforeFatal);

    private sealed class UpdateParallelExtractionPipeline : IDisposable
    {
        private static readonly Task NeverCompletedTask =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        private readonly BlockingCollection<UpdateParallelExtractionRequest> requests;
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task[] workers;
        private readonly Action<UpdateParallelExtractionTestEvent>? extractionEventForTesting;
        private readonly Func<string, string, Exception?>? extractionFailureForTesting;
        private readonly TimeSpan extractionStallTimeout;
        private readonly Action? workersStoppedForTesting;
        private int abandonWorkers;
        private int resourcesDisposed;
        private bool disposed;

        internal UpdateParallelExtractionPipeline(
            FileIndexer indexer,
            IndexCommandOptions options,
            string projectRoot,
            CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace,
            Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots,
            int workerCount,
            Action<UpdateParallelExtractionTestEvent>? extractionEventForTesting,
            Func<string, string, Exception?>? extractionFailureForTesting,
            Func<TimeSpan>? extractionStallTimeoutForTesting,
            Action? workersStoppedForTesting)
        {
            WorkerCount = workerCount;
            WindowCapacity = checked(workerCount * 2);
            this.extractionEventForTesting = extractionEventForTesting;
            this.extractionFailureForTesting = extractionFailureForTesting;
            this.workersStoppedForTesting = workersStoppedForTesting;
            extractionStallTimeout =
                extractionStallTimeoutForTesting?.Invoke()
                ?? IndexExtractionStallTimeout;
            requests = new BlockingCollection<UpdateParallelExtractionRequest>(WindowCapacity);
            workers = Enumerable.Range(0, workerCount)
                .Select(workerIndex => Task.Factory.StartNew(
                    () => RunWorker(
                        workerIndex,
                        indexer,
                        options,
                        projectRoot,
                        csharpWorkspace,
                        snapshots),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
        }

        internal int WorkerCount { get; }

        internal int WindowCapacity { get; }

        internal UpdateParallelExtractionWindowResult ExtractWindow(
            IReadOnlyList<UpdateParallelExtractionRequest> window,
            CancellationToken cancellationToken,
            Action<string, string> reportActivePhase)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (window.Count > WindowCapacity)
                throw new ArgumentOutOfRangeException(nameof(window));

            var windowCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdown.Token);
            var releaseWindowCancellation = true;
            var enqueuedCount = 0;
            try
            {
                foreach (var request in window)
                {
                    request.WindowCancellationToken = windowCancellation.Token;
                    extractionEventForTesting?.Invoke(
                        new UpdateParallelExtractionTestEvent(
                            UpdateParallelExtractionEventKind.ExtractionQueued,
                            request.TargetIndex,
                            request.Target.DisplayRelativePath,
                            WorkerIndex: -1,
                            KnownLanguage: request.KnownLanguage));
                    requests.Add(request, windowCancellation.Token);
                    enqueuedCount++;
                }

                var results = new UpdateParallelExtractionResult[window.Count];
                var completionTasks = new Task<UpdateParallelExtractionResult>[window.Count];
                var waitTasks = new Task[window.Count];
                var handled = new bool[window.Count];
                var observedProgressVersions = new long[window.Count];
                for (var index = 0; index < window.Count; index++)
                {
                    var completion = window[index].Completion.Task;
                    completionTasks[index] = completion;
                    waitTasks[index] = completion;
                    observedProgressVersions[index] = window[index].Progress.Version;
                }

                var remainingCount = window.Count;
                var lastProgressTimestamp = Stopwatch.GetTimestamp();
                while (remainingCount > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var activeRequest = FindFirstActiveRequest(window);
                    if (activeRequest != null)
                    {
                        reportActivePhase(
                            activeRequest.Target.DisplayRelativePath,
                            activeRequest.Progress.Phase ?? "reading");
                    }
                    var completedTaskIndex = Task.WaitAny(
                        waitTasks,
                        millisecondsTimeout: 100,
                        cancellationToken);
                    if (completedTaskIndex < 0)
                    {
                        var completionRacedWithTimeout = false;
                        for (var index = 0; index < completionTasks.Length; index++)
                        {
                            if (!handled[index] && completionTasks[index].IsCompleted)
                            {
                                completionRacedWithTimeout = true;
                                break;
                            }
                        }
                        if (completionRacedWithTimeout)
                            continue;
                        if (ObserveWindowProgress(
                                window,
                                handled,
                                observedProgressVersions))
                        {
                            lastProgressTimestamp = Stopwatch.GetTimestamp();
                            continue;
                        }
                        if (extractionStallTimeout <= TimeSpan.Zero
                            || Stopwatch.GetElapsedTime(lastProgressTimestamp)
                                < extractionStallTimeout)
                        {
                            continue;
                        }

                        var stalledRequest = FindFirstActiveRequest(window);
                        if (stalledRequest == null)
                            continue;
                        var stalledPhase = stalledRequest.Progress.Phase ?? "reading";
                        var loaded = stalledRequest.Progress.GetLoadedRecord();
                        var stalledResult = UpdateParallelExtractionResult.Failure(
                            stalledRequest,
                            stalledPhase,
                            new IndexExtractionStalledException(
                                0,
                                null,
                                extractionStallTimeout,
                                FormatIndexPhasePath(
                                    stalledRequest.Target.DisplayRelativePath,
                                    stalledPhase)),
                            loaded.Record,
                            loaded.Warning,
                            stalledRequest.Progress.HasCSharpStaticInterfaceSourceContract);
                        AbortWindow(windowCancellation);
                        releaseWindowCancellation = false;
                        ReleaseWindowCancellationWhenCompleted(
                            windowCancellation,
                            window);
                        return BuildFatalWindowResults(window, results, stalledResult);
                    }

                    lastProgressTimestamp = Stopwatch.GetTimestamp();
                    UpdateParallelExtractionResult? fatalResult = null;
                    for (var index = 0; index < completionTasks.Length; index++)
                    {
                        if (handled[index] || !completionTasks[index].IsCompleted)
                            continue;

                        var result = completionTasks[index].GetAwaiter().GetResult();
                        results[index] = result;
                        handled[index] = true;
                        waitTasks[index] = NeverCompletedTask;
                        remainingCount--;
                        if (result.Exception is IndexExtractionStalledException
                            && (fatalResult == null
                                || result.TargetIndex < fatalResult.TargetIndex))
                        {
                            fatalResult = result;
                        }
                    }
                    for (var index = 0; index < window.Count; index++)
                    {
                        if (!handled[index])
                        {
                            observedProgressVersions[index] =
                                window[index].Progress.Version;
                        }
                    }

                    if (fatalResult != null)
                    {
                        AbortWindow(windowCancellation);
                        releaseWindowCancellation = false;
                        ReleaseWindowCancellationWhenCompleted(
                            windowCancellation,
                            window);
                        return BuildFatalWindowResults(window, results, fatalResult);
                    }
                }

                releaseWindowCancellation = false;
                windowCancellation.Dispose();
                return new UpdateParallelExtractionWindowResult(
                    results,
                    ActualFatalResult: null,
                    FatalWasNormalized: false,
                    UnconsumedSourceContractCandidateBeforeFatal: false);
            }
            catch
            {
                for (var index = enqueuedCount; index < window.Count; index++)
                {
                    var notEnqueued = window[index];
                    notEnqueued.Completion.TrySetResult(
                        UpdateParallelExtractionResult.Failure(
                            notEnqueued,
                            notEnqueued.Progress.Phase ?? "reading",
                            new OperationCanceledException(
                                "Parallel update extraction was not enqueued.")));
                }
                AbortWindow(windowCancellation);
                throw;
            }
            finally
            {
                if (releaseWindowCancellation)
                {
                    ReleaseWindowCancellationWhenCompleted(
                        windowCancellation,
                        window);
                }
            }
        }

        private static UpdateParallelExtractionRequest? FindFirstActiveRequest(
            IReadOnlyList<UpdateParallelExtractionRequest> window)
            => window.FirstOrDefault(static request =>
                    !request.Completion.Task.IsCompleted
                    && request.Progress.Phase != null)
                ?? window.FirstOrDefault(static request => !request.Completion.Task.IsCompleted);

        private static bool ObserveWindowProgress(
            IReadOnlyList<UpdateParallelExtractionRequest> window,
            IReadOnlyList<bool> handled,
            long[] observedVersions)
        {
            var changed = false;
            for (var index = 0; index < window.Count; index++)
            {
                if (handled[index])
                    continue;

                var current = window[index].Progress.Version;
                if (current == observedVersions[index])
                    continue;

                observedVersions[index] = current;
                changed = true;
            }
            return changed;
        }

        private static UpdateParallelExtractionWindowResult BuildFatalWindowResults(
            IReadOnlyList<UpdateParallelExtractionRequest> window,
            IReadOnlyList<UpdateParallelExtractionResult> results,
            UpdateParallelExtractionResult fatalResult)
        {
            var consumable = new List<UpdateParallelExtractionResult>(window.Count);
            var fatalIncluded = false;
            for (var index = 0; index < window.Count; index++)
            {
                var result = results[index];
                if (result == null)
                    break;

                consumable.Add(result);
                if (result.Exception is IndexExtractionStalledException)
                {
                    fatalIncluded = true;
                    break;
                }
            }

            var firstUnconsumedIndex = consumable.Count;
            if (!fatalIncluded)
            {
                var nextRequest = window[consumable.Count];
                var loaded = nextRequest.Progress.GetLoadedRecord();
                consumable.Add(
                    UpdateParallelExtractionResult.Failure(
                        nextRequest,
                        nextRequest.Progress.Phase
                            ?? fatalResult.FailurePhase
                            ?? "reading",
                        fatalResult.Exception!,
                        loaded.Record,
                        loaded.Warning,
                        nextRequest.Progress.HasCSharpStaticInterfaceSourceContract));
            }
            return new UpdateParallelExtractionWindowResult(
                consumable,
                fatalResult,
                FatalWasNormalized: !fatalIncluded,
                UnconsumedSourceContractCandidateBeforeFatal:
                    !fatalIncluded
                    && HasUnconsumedSourceContractCandidateBeforeFatal(
                        window,
                        results,
                        firstUnconsumedIndex,
                        fatalResult.TargetIndex));
        }

        private static bool HasUnconsumedSourceContractCandidateBeforeFatal(
            IReadOnlyList<UpdateParallelExtractionRequest> window,
            IReadOnlyList<UpdateParallelExtractionResult> results,
            int firstUnconsumedIndex,
            int fatalTargetIndex)
        {
            for (var index = firstUnconsumedIndex; index < window.Count; index++)
            {
                if (window[index].TargetIndex > fatalTargetIndex)
                    break;
                var progress = window[index].Progress;
                if (progress.HasCSharpStaticInterfaceSourceContract
                    || (window[index].TargetIndex < fatalTargetIndex
                        && (!progress
                                .CSharpStaticInterfaceSourceContractCandidateEvaluated
                            || progress
                                .MayContainCSharpStaticInterfaceSourceContract))
                    || results[index]?.HasCSharpStaticInterfaceSourceContract == true)
                {
                    return true;
                }
            }
            return false;
        }

        private void AbortWindow(CancellationTokenSource windowCancellation)
        {
            Interlocked.Exchange(ref abandonWorkers, 1);
            try
            {
                windowCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            while (requests.TryTake(out var pending))
            {
                pending.Completion.TrySetResult(
                    UpdateParallelExtractionResult.Failure(
                        pending,
                        pending.Progress.Phase ?? "reading",
                        new OperationCanceledException(
                            "Parallel update extraction was cancelled.",
                            windowCancellation.Token)));
            }
        }

        private static void ReleaseWindowCancellationWhenCompleted(
            CancellationTokenSource windowCancellation,
            IReadOnlyList<UpdateParallelExtractionRequest> window)
        {
            var pendingObservers = window
                .Where(static request => !request.Completion.Task.IsCompleted)
                .Select(static request => ObserveCompletionWithoutRetainingResult(
                    request.Completion.Task))
                .ToArray();
            if (pendingObservers.Length == 0)
            {
                windowCancellation.Dispose();
                return;
            }

            var completion = Task.WhenAll(pendingObservers);
            _ = completion.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                windowCancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task ObserveCompletionWithoutRetainingResult(
            Task<UpdateParallelExtractionResult> completion)
        {
            _ = await completion.ConfigureAwait(false);
        }

        private void RunWorker(
            int workerIndex,
            FileIndexer indexer,
            IndexCommandOptions options,
            string projectRoot,
            CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace,
            Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots)
        {
            using var symbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(
                () => new SymbolExtractionWorkerClient(options.MaxFileSizeBytes));
            Exception? workerStartupException = null;
            try
            {
                extractionEventForTesting?.Invoke(
                    new UpdateParallelExtractionTestEvent(
                        UpdateParallelExtractionEventKind.WorkerStarted,
                        TargetIndex: -1,
                        RelativePath: string.Empty,
                        workerIndex));
            }
            catch (Exception ex)
            {
                workerStartupException = ex;
            }
            try
            {
                foreach (var request in requests.GetConsumingEnumerable(shutdown.Token))
                {
                    request.Progress.SetPhase("reading");
                    UpdateParallelExtractionResult result;
                    if (workerStartupException != null)
                    {
                        result = UpdateParallelExtractionResult.Failure(
                            request,
                            "reading",
                            workerStartupException);
                    }
                    else
                    {
                        try
                        {
                            extractionEventForTesting?.Invoke(
                                new UpdateParallelExtractionTestEvent(
                                    UpdateParallelExtractionEventKind.ExtractionStarted,
                                    request.TargetIndex,
                                    request.Target.DisplayRelativePath,
                                    workerIndex));
                            result = ExtractUpdateTarget(
                                request,
                                indexer,
                                options,
                                projectRoot,
                                csharpWorkspace,
                                snapshots,
                                symbolExtractionWorker,
                                extractionFailureForTesting,
                                request.WindowCancellationToken,
                                extractionStallTimeout);
                        }
                        catch (Exception ex)
                        {
                            var loaded = request.Progress.GetLoadedRecord();
                            result = UpdateParallelExtractionResult.Failure(
                                request,
                                request.Progress.Phase ?? "reading",
                                ex,
                                loaded.Record,
                                loaded.Warning,
                                request.Progress.HasCSharpStaticInterfaceSourceContract);
                        }
                    }
                    try
                    {
                        extractionEventForTesting?.Invoke(
                            new UpdateParallelExtractionTestEvent(
                                UpdateParallelExtractionEventKind.ExtractionCompleted,
                                request.TargetIndex,
                                request.Target.DisplayRelativePath,
                                workerIndex,
                                result.Symbols?.Count ?? 0,
                                result.HasCSharpStaticInterfaceSourceContract));
                    }
                    catch (Exception ex)
                    {
                        if (result.Exception == null)
                        {
                            result = UpdateParallelExtractionResult.Failure(
                                request,
                                result.FailurePhase ?? "validating",
                                ex,
                                result.Record,
                                result.Warning,
                                result.HasCSharpStaticInterfaceSourceContract);
                        }
                    }
                    request.Completion.TrySetResult(result);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            requests.CompleteAdding();
            while (requests.TryTake(out var pending))
            {
                pending.Completion.TrySetResult(
                    UpdateParallelExtractionResult.Failure(
                        pending,
                        pending.Progress.Phase ?? "reading",
                        new OperationCanceledException(
                            "Parallel update extraction pipeline was shut down.")));
            }

            try
            {
                if (Volatile.Read(ref abandonWorkers) == 0)
                    Task.WaitAll(workers, TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // A worker failure is represented by its request result. Disposal must not
                // replace the command result or a more useful terminal extraction error.
            }
            finally
            {
                DisposeResourcesWhenWorkersStop();
            }
        }

        private void DisposeResourcesWhenWorkersStop()
        {
            var completion = Task.WhenAll(workers);
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
                    ((UpdateParallelExtractionPipeline)state!).DisposeResources();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0)
                return;

            try
            {
                workersStoppedForTesting?.Invoke();
            }
            finally
            {
                requests.Dispose();
                shutdown.Dispose();
            }
        }
    }

    private static IReadOnlyList<UpdateParallelExtractionRequest> TryBuildUpdateParallelWindow(
        FileIndexer indexer,
        UpdateFileTarget[] targets,
        int startIndex,
        int capacity,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots,
        IReadOnlyDictionary<string, string>? scannedUpdateLanguages,
        CancellationToken cancellationToken)
    {
        if (startIndex + 1 >= targets.Length
            || !snapshots.ContainsKey(targets[startIndex + 1].IndexPath))
        {
            return [];
        }

        var window = new List<UpdateParallelExtractionRequest>(capacity);
        var endIndex = Math.Min(targets.Length, startIndex + capacity);
        for (var targetIndex = startIndex; targetIndex < endIndex; targetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[targetIndex];
            var probeFailure =
                UpdateParallelWindowProbeFailureForTesting?.Invoke(
                    target.DisplayRelativePath);
            if (probeFailure != null)
                throw probeFailure;
            if (!snapshots.TryGetValue(target.IndexPath, out var snapshot))
                break;
            if (!CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                    target.FilePath,
                    target.IndexPath,
                    target.DisplayRelativePath,
                    snapshot.Size,
                    snapshot.ModifiedUtc,
                    snapshots,
                    out _,
                    cancellationToken))
            {
                break;
            }

            var pathFilter = indexer.EvaluatePathFilter(target.FilePath);
            if (pathFilter.Errors.Count > 0 || pathFilter.ShouldSkip)
                break;

            var indexability = indexer.GetFileIndexabilityForIndexing(target.FilePath);
            var detection = indexer.TryDetectLanguageForIndexing(
                target.FilePath,
                knownIndexability: indexability);
            if (indexability != FileIndexer.FileProbeStatus.Supported
                || detection.Status != FileIndexer.FileProbeStatus.Supported
                || detection.Language != "csharp")
            {
                break;
            }
            if (FileIndexer.TryGetFileIdentity(target.FilePath, out _, out var linkCount)
                && linkCount > 1)
            {
                break;
            }

            var detectedLanguage = GetStatReusableLanguage(target.FilePath, detection);
            var knownLanguage = scannedUpdateLanguages == null
                ? detectedLanguage
                : FileIndexer.GetReusableDetectedLanguage(
                    target.FilePath,
                    scannedUpdateLanguages);
            window.Add(new UpdateParallelExtractionRequest(
                targetIndex,
                target,
                knownLanguage,
                indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath),
                new TaskCompletionSource<UpdateParallelExtractionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously)));
        }

        return window.Count >= 2 ? window : [];
    }

    private static UpdateParallelExtractionResult ExtractUpdateTarget(
        UpdateParallelExtractionRequest request,
        FileIndexer indexer,
        IndexCommandOptions options,
        string projectRoot,
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots,
        LazyDisposable<SymbolExtractionWorkerClient> symbolExtractionWorker,
        Func<string, string, Exception?>? extractionFailureForTesting,
        CancellationToken cancellationToken,
        TimeSpan extractionStallTimeout)
    {
        var target = request.Target;
        var phase = "reading";
        FileRecord? validatedRecord = null;
        string? validatedWarning = null;
        var hasCSharpStaticInterfaceSourceContract = false;
        void SetPhase(string value)
        {
            phase = value;
            request.Progress.SetPhase(value);
        }
        void ThrowInjectedFailure()
        {
            var exception = extractionFailureForTesting?.Invoke(
                target.DisplayRelativePath,
                phase);
            if (exception != null)
                throw exception;
        }

        try
        {
            ThrowInjectedFailure();
            var loaded = indexer.BuildLoadedRecordWithRawBytes(
                target.FilePath,
                target.RelativePath,
                request.KnownLanguage,
                cancellationToken);
            var record = loaded.Record;
            if (record.Lang != "csharp"
                || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                    target.FilePath,
                    target.IndexPath,
                    target.DisplayRelativePath,
                    record.Size,
                    record.Modified,
                    snapshots,
                    out _,
                    cancellationToken))
            {
                return UpdateParallelExtractionResult.Failure(
                    request,
                    "csharp_workspace_validation",
                    new CSharpWorkspaceChangedException(
                        "The C# file changed while the authoritative update pass was reading it."));
            }
            validatedRecord = record;
            validatedWarning = loaded.Warning;
            request.Progress.PublishLoadedRecord(record, loaded.Warning);

            var content = loaded.Content;
            request.Progress.PublishMayContainCSharpStaticInterfaceSourceContract(
                !request.GeneratedExtractionSuppressed
                && !csharpWorkspace.HasSourceStaticInterfaceContracts
                && CSharpStaticInterfacePrepass
                    .MayContainCSharpStaticInterfaceContract(content));
            SetPhase("chunking");
            ThrowInjectedFailure();
            var chunks = ChunkSplitter.SplitNormalized(
                0,
                content,
                loaded.Facts);
            var generatedSuppressionIssue = request.GeneratedExtractionSuppressed
                ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                : null;
            if (generatedSuppressionIssue != null)
            {
                SetPhase("validating");
                ThrowInjectedFailure();
                var generatedIssues = AppendIssueIfMissing(
                    FileIndexer.ValidateContent(
                        record.Path,
                        loaded.RawBytes,
                        content,
                        record.Lang,
                        loaded.Inspection,
                        loaded.Facts),
                    generatedSuppressionIssue);
                return new UpdateParallelExtractionResult(
                    request.TargetIndex,
                    target,
                    request.KnownLanguage,
                    record,
                    loaded.Warning,
                    chunks,
                    [],
                    [],
                    generatedIssues,
                    generatedSuppressionIssue,
                    false,
                    false,
                    null,
                    null);
            }

            SetPhase("symbols");
            ThrowInjectedFailure();
            var symbolExtraction = ExtractSymbolsWithStallTimeout(
                0,
                record.Lang,
                content,
                target.FilePath,
                projectRoot,
                record.Path,
                FormatIndexPhasePath(target.DisplayRelativePath, "symbols"),
                true,
                loaded.HasOversizeLine,
                loaded.ConflictMarkerLine,
                symbolExtractionWorker.Value,
                options.SymlinkPolicy,
                cancellationToken,
                extractionStallTimeout);
            var symbols = symbolExtraction.Symbols;
            var symbolRegexTimeoutIssue = symbolExtraction.RegexTimeoutIssue;
            hasCSharpStaticInterfaceSourceContract =
                CSharpStaticInterfacePrepass.HasCSharpStaticInterfaceContractSymbol(
                    symbols);
            request.Progress.PublishCSharpStaticInterfaceSourceContract(
                hasCSharpStaticInterfaceSourceContract);
            if (symbols.Count > options.MaxSymbolsPerFile)
            {
                var issue = BuildSymbolCountExceededIssue(
                    record.Path,
                    symbols.Count,
                    options.MaxSymbolsPerFile);
                IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                    ? [issue]
                    : AppendIssue([symbolRegexTimeoutIssue], issue);
                return new UpdateParallelExtractionResult(
                    request.TargetIndex,
                    target,
                    request.KnownLanguage,
                    record,
                    loaded.Warning,
                    [],
                    [],
                    [],
                    capIssues,
                    null,
                    hasCSharpStaticInterfaceSourceContract,
                    true,
                    null,
                    null);
            }

            SymbolExtractor.ApplyFamilyScope(
                symbols,
                indexer.GetFamilyScopeKey(target.FilePath, record.Lang),
                record.Lang);
            ReferenceExtractionResult referenceExtraction;
            IReadOnlyList<ReferenceRecord> references;
            FileIssue? referenceRegexTimeoutIssue = null;
            SetPhase("references");
            ThrowInjectedFailure();
            using (var regexTimeouts = BoundedRegex.CaptureTimeouts(
                       record.Lang,
                       "reference_extraction"))
            {
                referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                    0,
                    record.Lang,
                    content,
                    loaded.HasOversizeLine,
                    symbols,
                    record.Path,
                    csharpWorkspace.Symbols,
                    cancellationToken,
                    maxReferenceCount: options.MaxReferencesPerFile + 1,
                    conflictMarkerLine: loaded.ConflictMarkerLine,
                    workspaceRoot: projectRoot,
                    csharpStaticInterfaceMemberLookups:
                        csharpWorkspace.StaticInterfaceMemberLookups,
                    csharpQualifiedPatternLookups:
                        csharpWorkspace.QualifiedPatternLookups);
                references = referenceExtraction.References;
                referenceRegexTimeoutIssue = BuildRegexTimeoutIssue(
                    record.Path,
                    regexTimeouts);
            }

            SetPhase("validating");
            ThrowInjectedFailure();
            IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(
                record.Path,
                loaded.RawBytes,
                content,
                record.Lang,
                loaded.Inspection,
                loaded.Facts);
            if (symbolRegexTimeoutIssue != null)
                issues = AppendIssue(issues, symbolRegexTimeoutIssue);
            if (referenceRegexTimeoutIssue != null)
                issues = AppendIssue(issues, referenceRegexTimeoutIssue);
            issues = AppendReferenceExtractionDiagnosticIssues(
                issues,
                record.Path,
                referenceExtraction.Diagnostics);
            if (references.Count > options.MaxReferencesPerFile)
            {
                issues = AppendIssue(
                    issues,
                    BuildReferenceCountExceededIssue(
                        record.Path,
                        references.Count,
                        options.MaxReferencesPerFile));
                references = [];
            }

            return new UpdateParallelExtractionResult(
                request.TargetIndex,
                target,
                request.KnownLanguage,
                record,
                loaded.Warning,
                chunks,
                symbols,
                references,
                issues,
                null,
                hasCSharpStaticInterfaceSourceContract,
                false,
                null,
                null);
        }
        catch (FileIndexer.BinaryFileSkippedException ex)
        {
            return UpdateParallelExtractionResult.Failure(
                request,
                "reading",
                ex);
        }
        catch (FileIndexer.FileTooLargeSkippedException ex)
        {
            return UpdateParallelExtractionResult.Failure(
                request,
                "reading",
                ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return UpdateParallelExtractionResult.Failure(
                request,
                "csharp_workspace_validation",
                new CSharpWorkspaceChangedException(
                    "The C# file disappeared during its authoritative update pass."),
                validatedRecord,
                validatedWarning,
                hasCSharpStaticInterfaceSourceContract);
        }
        catch (Exception ex)
        {
            return UpdateParallelExtractionResult.Failure(
                request,
                phase,
                ex,
                validatedRecord,
                validatedWarning,
                hasCSharpStaticInterfaceSourceContract);
        }
    }

}
