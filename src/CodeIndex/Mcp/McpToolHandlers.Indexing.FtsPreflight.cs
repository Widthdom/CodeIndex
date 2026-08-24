using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class McpIndexReadableByteTracker(int capacity)
    {
        internal Dictionary<string, long> Sizes { get; } =
            new(capacity, StringComparer.Ordinal);
        internal long Total { get; private set; }
        internal bool EstimateComplete { get; private set; } = true;

        internal void Remember(string path, long size)
        {
            var priorSize = Sizes.TryGetValue(path, out var prior)
                ? prior
                : (long?)null;
            if (EstimateComplete)
            {
                EstimateComplete = FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                    Total,
                    priorSize,
                    size,
                    out var updatedTotal);
                Total = updatedTotal;
            }
            Sizes[path] = size;
        }
    }

    private sealed class McpIndexFtsPreflightState(
        IndexedFileStatReuseResult?[]? statMatches,
        bool[]? completed,
        bool useBulkLoad,
        bool everyTargetMatched)
    {
        internal IndexedFileStatReuseResult?[]? StatMatches { get; } = statMatches;
        internal bool[]? Completed { get; } = completed;
        internal bool UseBulkLoad { get; } = useBulkLoad;
        internal bool EveryTargetMatched { get; } = everyTargetMatched;

        internal void InvalidateTarget(int targetIndex)
        {
            StatMatches![targetIndex] = null;
            Completed![targetIndex] = true;
        }
    }

    private static McpIndexFtsPreflightState BuildMcpIndexFtsPreflight(
        FileIndexer.IndexingFileTargetCollection targets,
        FilePurgePlan purgePlan,
        bool rebuild,
        bool startedWithNoIndexedFiles,
        bool scanHadErrors,
        ReusableIndexedFileStatsSnapshot? reusableFiles,
        McpPathBoundary.IndexRootAuthorization authorizedRoot,
        McpIndexStatMatchResolver getStatMatch,
        McpIndexReadableByteTracker readableBytes,
        CancellationToken cancellationToken)
    {
        if (rebuild || startedWithNoIndexedFiles)
            return new(null, null, useBulkLoad: true, everyTargetMatched: false);

        var statMatches = new IndexedFileStatReuseResult?[targets.Length];
        var completed = new bool[targets.Length];
        McpIndexFtsStatPreflightBufferAllocatedForTesting?.Invoke(targets.Length);
        var everyTargetMatched = purgePlan.Count == 0;
        var dirtyBytes = purgePlan.DeletedBytes;
        var persistedSizeExcessBytes = 0L;
        var estimateComplete = !scanHadErrors
            && purgePlan.ByteEstimateComplete
            && readableBytes.EstimateComplete;
        for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[targetIndex];
            completed[targetIndex] = TryPreflightMcpIndexTarget(
                in target,
                reusableFiles!,
                authorizedRoot,
                getStatMatch,
                readableBytes,
                cancellationToken,
                ref dirtyBytes,
                ref persistedSizeExcessBytes,
                ref estimateComplete,
                out statMatches[targetIndex]);
            if (!completed[targetIndex] || statMatches[targetIndex] == null)
                everyTargetMatched = false;
        }

        var useBulkLoad = ShouldUseMcpIndexFtsBulkLoad(
            purgePlan,
            readableBytes,
            dirtyBytes,
            persistedSizeExcessBytes,
            estimateComplete);
        return new(statMatches, completed, useBulkLoad, everyTargetMatched);
    }

    private static bool TryPreflightMcpIndexTarget(
        in FileIndexer.IndexingFileTarget target,
        ReusableIndexedFileStatsSnapshot reusableFiles,
        McpPathBoundary.IndexRootAuthorization authorizedRoot,
        McpIndexStatMatchResolver getStatMatch,
        McpIndexReadableByteTracker readableBytes,
        CancellationToken cancellationToken,
        ref long dirtyBytes,
        ref long persistedSizeExcessBytes,
        ref bool estimateComplete,
        out IndexedFileStatReuseResult? statMatch)
    {
        try
        {
            authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
            statMatch = getStatMatch(in target);
            if (statMatch != null)
            {
                readableBytes.Remember(target.FilePath, statMatch.Value.Size);
                return true;
            }

            var info = new FileInfo(target.FilePath);
            if (!info.Exists || info.Length < 0)
            {
                estimateComplete = false;
                return true;
            }

            readableBytes.Remember(target.FilePath, info.Length);
            var persistedSize = reusableFiles.GetPersistedSize(target.IndexPath);
            if (!FtsBulkLoadTriggerGuard.TryAccumulateDirtyFileBytes(
                    dirtyBytes,
                    persistedSizeExcessBytes,
                    info.Length,
                    persistedSize,
                    out dirtyBytes,
                    out persistedSizeExcessBytes))
            {
                estimateComplete = false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpIndexAuthorizationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Leave the real file loop to retry and report its existing per-file error.
            statMatch = null;
            estimateComplete = false;
            return false;
        }
    }

    private static bool ShouldUseMcpIndexFtsBulkLoad(
        FilePurgePlan purgePlan,
        McpIndexReadableByteTracker readableBytes,
        long dirtyBytes,
        long persistedSizeExcessBytes,
        bool estimateComplete)
    {
        estimateComplete &= readableBytes.EstimateComplete;
        var totalBytes = readableBytes.Total;
        if (!readableBytes.EstimateComplete
            || totalBytes > long.MaxValue - purgePlan.DeletedBytes)
        {
            estimateComplete = false;
        }
        else
        {
            totalBytes += purgePlan.DeletedBytes;
        }

        if (totalBytes > long.MaxValue - persistedSizeExcessBytes)
            estimateComplete = false;
        else
            totalBytes += persistedSizeExcessBytes;

        return estimateComplete
            && FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(dirtyBytes, totalBytes);
    }
}
