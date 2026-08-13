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
    private sealed partial class UpdateFileLoopSession
    {
        internal UpdateFileLoopOutcome Run()
        {
            updateProgress.Start();

            var updateTargets = new UpdateFileTarget[targetPaths.Count];
            var updateTargetIndex = 0;
            foreach (var targetPath in targetPaths)
                updateTargets[updateTargetIndex++] = UpdateFileTarget.Create(projectRoot, targetPath);
            readableFileBytes = new ReadableFileByteTracker(
                updateTargets.Length,
                targetIndex => updateTargets[targetIndex].FilePath,
                projectRoot,
                indexRunDiagnostics);

            WriteIndexJsonLiveness(options, $"updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
            var updateHeartbeat = StartIndexJsonPhaseHeartbeat(
                options,
                "updating index",
                () => currentUpdatePath == null
                    ? $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed"
                    : $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed, current {currentUpdatePath}");
            var updateExtractionWorkStarted = 0;
            void NotifyUpdateExtractionWorkStarted()
            {
                if (Interlocked.Exchange(ref updateExtractionWorkStarted, 1) == 0)
                    UpdateExtractionWorkStartedForTesting?.Invoke();
            }

            using var symbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(() =>
            {
                NotifyUpdateExtractionWorkStarted();
                return new SymbolExtractionWorkerClient(options.MaxFileSizeBytes);
            });
            var parallelExtractionFallbackReason = options.Parallelism <= 1
                ? "parallelism_one"
                : !csharpWorkspace.HasStaticInterfaceContracts
                    ? "non_authoritative_csharp_workspace"
                    : csharpWorkspaceSnapshots == null
                        ? "missing_csharp_workspace_snapshots"
                        : csharpWorkspaceSnapshots.Count < 2
                            ? "insufficient_authoritative_csharp_targets"
                            : options.SymbolKindFilter.IsActive
                                ? "active_symbol_kind_filter"
                                : UpdateFileContentLoadForTesting != null
                                    ? "content_load_test_hook"
                                    : postExtractionHooks.Value.HasHooks
                                        ? "post_extraction_hooks"
                                        : null;
            var parallelizeAuthoritativeCSharpUpdates =
                parallelExtractionFallbackReason == null;
            var parallelExtractionWorkerCount = parallelizeAuthoritativeCSharpUpdates
                ? Math.Min(options.Parallelism, csharpWorkspaceSnapshots!.Count)
                : 0;
            var parallelExtractionWindowCapacity = parallelizeAuthoritativeCSharpUpdates
                ? checked(parallelExtractionWorkerCount * 2)
                : 0;
            UpdateParallelExtractionSchedulingForTesting?.Invoke(
                parallelizeAuthoritativeCSharpUpdates,
                parallelExtractionFallbackReason,
                parallelExtractionWorkerCount,
                parallelExtractionWindowCapacity);
            using var parallelExtractionPipeline =
                new LazyDisposable<UpdateParallelExtractionPipeline>(() =>
                {
                    NotifyUpdateExtractionWorkStarted();
                    return new UpdateParallelExtractionPipeline(
                        indexer,
                        options,
                        projectRoot,
                        csharpWorkspace,
                        csharpWorkspaceSnapshots!,
                        parallelExtractionWorkerCount,
                        parallelExtractionEventForTesting,
                        parallelExtractionFailureForTesting,
                        extractionStallTimeoutForTesting,
                        parallelExtractionWorkersStoppedForTesting);
                });

            try
            {
                for (var targetIndex = 0; targetIndex < updateTargets.Length; targetIndex++)
                {
                    ThrowIfUpdateCancelled();
                    if (parallelizeAuthoritativeCSharpUpdates
                        && csharpWorkspaceSnapshots!.ContainsKey(
                            updateTargets[targetIndex].IndexPath))
                    {
                        IReadOnlyList<UpdateParallelExtractionRequest> parallelWindow;
                        try
                        {
                            parallelWindow = TryBuildUpdateParallelWindow(
                                indexer,
                                updateTargets,
                                targetIndex,
                                parallelExtractionWindowCapacity,
                                csharpWorkspaceSnapshots,
                                scannedUpdateLanguages,
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                            when (cancellationToken.IsCancellationRequested)
                        {
                            ThrowIfUpdateCancelled();
                            throw;
                        }
                        catch (Exception)
                        {
                            // Probing belongs to the per-file serial error boundary. If a
                            // speculative window probe fails, let the ordinary consumer
                            // retry the natural first target and contain any repeat there.
                            parallelizeAuthoritativeCSharpUpdates = false;
                            parallelWindow = [];
                        }
                        if (parallelWindow.Count > 0)
                        {
                            UpdateParallelExtractionWindowResult windowResult;
                            try
                            {
                                windowResult = parallelExtractionPipeline.Value.ExtractWindow(
                                    parallelWindow,
                                    cancellationToken,
                                    (path, phase) =>
                                    {
                                        currentUpdatePath = FormatIndexPhasePath(path, phase);
                                        currentUpdatePhase = phase;
                                    });
                            }
                            catch (OperationCanceledException)
                            {
                                var sawValidatedLoad = false;
                                foreach (var request in parallelWindow)
                                {
                                    var loaded = request.Progress.GetLoadedRecord();
                                    if (loaded.Record == null)
                                        continue;

                                    sawValidatedLoad = true;
                                    break;
                                }
                                if (sawValidatedLoad)
                                {
                                    DemoteReadinessOnce();
                                    csharpMetadataTargetsNeedRefresh = true;
                                }
                                ThrowIfUpdateCancelled();
                                throw;
                            }
                            var results = windowResult.Results;
                            parallelSourceWorkspaceDriftDetected = false;
                            var recoverSerialSuffix = false;
                            var consumedWindowCount = 0;
                            var consumableResultCount = windowResult.FatalWasNormalized
                                ? results.Count - 1
                                : results.Count;
                            for (var resultIndex = 0;
                                 resultIndex < consumableResultCount;
                                 resultIndex++)
                            {
                                var item = results[resultIndex];
                                ConsumeParallelUpdateResult(item);
                                consumedWindowCount++;
                                if (item.Exception is IndexExtractionStalledException
                                    || parallelSourceWorkspaceDriftDetected)
                                {
                                    recoverSerialSuffix = true;
                                    break;
                                }
                            }
                            if (windowResult.FatalWasNormalized
                                && !recoverSerialSuffix)
                            {
                                var sourceContractPrecedesFatal =
                                    windowResult
                                        .UnconsumedSourceContractCandidateBeforeFatal
                                    && !csharpWorkspace
                                        .HasSourceStaticInterfaceContracts
                                    && !postExtractionHooks.Value
                                        .SawCSharpStaticInterfaceSourceContract;
                                if (sourceContractPrecedesFatal)
                                {
                                    // The natural first unconsumed target must be
                                    // retried before a later extraction fatal can
                                    // become terminal. Keep the fatal's readiness
                                    // and batch effects out of this recovery path.
                                    recoverSerialSuffix = true;
                                }
                                else
                                {
                                    var actualFatal =
                                        windowResult.ActualFatalResult!;
                                    if (actualFatal.Record != null
                                        || !string.Equals(
                                            actualFatal.FailurePhase,
                                            "reading",
                                            StringComparison.Ordinal))
                                    {
                                        DemoteReadinessOnce();
                                        csharpMetadataTargetsNeedRefresh = true;
                                    }
                                    if (!string.Equals(
                                            actualFatal.FailurePhase,
                                            "reading",
                                            StringComparison.Ordinal))
                                    {
                                        writer.MarkBatchInProgress();
                                    }

                                    var normalizedFatal = results[^1];
                                    ConsumeParallelUpdateResult(normalizedFatal);
                                    consumedWindowCount++;
                                    if (parallelSourceWorkspaceDriftDetected)
                                        recoverSerialSuffix = true;
                                }
                            }
                            if (recoverSerialSuffix)
                            {
                                parallelizeAuthoritativeCSharpUpdates = false;
                                targetIndex += consumedWindowCount - 1;
                                continue;
                            }
                            if (results.Count != parallelWindow.Count)
                            {
                                throw new InvalidOperationException(
                                    "A shortened parallel update window returned without a terminal extraction error.");
                            }
                            targetIndex += parallelWindow.Count - 1;
                            continue;
                        }
                    }

                    ConsumeSerialUpdateTarget(
                        updateTargets[targetIndex],
                        targetIndex,
                        symbolExtractionWorker);
                }
            }
            finally
            {
                StopIndexJsonPhaseHeartbeat(updateHeartbeat);
            }
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("extraction", stopwatch));
            return new UpdateFileLoopOutcome(
                counters,
                new UpdateFileLoopRefreshResult(
                    ftsMutated,
                    mutualRecursionRefreshNeeded,
                    csharpMetadataTargetsNeedRefresh),
                readableFileBytes);
        }
    }
}
