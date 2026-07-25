using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class ReadableFileByteTracker(
        int fileCount,
        Func<int, string> getFilePath,
        string projectRoot,
        List<string>? indexRunDiagnostics)
    {
        private readonly long[] knownSizes = new long[fileCount];
        private readonly bool[] sizeKnown = new bool[fileCount];
        private int knownCount;
        private long knownBytes;
        private bool estimateComplete = true;

        internal long KnownBytes => knownBytes;
        internal bool EstimateComplete => estimateComplete;

        internal void Remember(int fileIndex, long size)
        {
            long? priorSize = null;
            if (sizeKnown[fileIndex])
            {
                priorSize = knownSizes[fileIndex];
            }
            else
            {
                sizeKnown[fileIndex] = true;
                knownCount++;
            }

            if (estimateComplete
                && !FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                    knownBytes,
                    priorSize,
                    size,
                    out knownBytes))
            {
                estimateComplete = false;
            }

            knownSizes[fileIndex] = size;
        }

        internal FileByteReadSummary MeasureRemaining()
        {
            var total = knownBytes;
            long skipped = estimateComplete ? 0 : 1;
            var totalComplete = estimateComplete;
            if (knownCount == fileCount)
                return new FileByteReadSummary(total, skipped);

            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                if (sizeKnown[fileIndex])
                    continue;

                var path = getFilePath(fileIndex);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists
                        && totalComplete
                        && !FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                            total,
                            previousBytes: null,
                            info.Length,
                            out total))
                    {
                        totalComplete = false;
                        skipped++;
                    }
                }
                catch (Exception ex) when (
                    ex is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or ArgumentException)
                {
                    skipped++;
                    RecordIndexRunDiagnostic(
                        indexRunDiagnostics,
                        "file_size_bytes_skipped",
                        FormatDiagnosticPath(projectRoot, path),
                        ex);
                }
            }

            return new FileByteReadSummary(total, skipped);
        }
    }

    private static bool ShouldUseFullScanFtsBulkLoad(
        bool rebuild,
        bool startedWithNoIndexedFiles,
        int extractionWorkItemCount,
        FilePurgePlan staleFilePurgePlan,
        bool scanHadErrors,
        ReadableFileByteTracker readableFileBytes,
        ReusableIndexedFileStatsSnapshot reusableIndexedFileStats,
        IReadOnlyList<FullScanFileTarget> fileTargets,
        IReadOnlyList<int>? extractionFileIndexes,
        Action throwIfCancelled)
    {
        if (rebuild || startedWithNoIndexedFiles)
            return true;
        if (extractionWorkItemCount == 0 && staleFilePurgePlan.Count == 0)
            return false;

        var dirtyBytes = staleFilePurgePlan.DeletedBytes;
        var persistedSizeExcessBytes = 0L;
        var byteEstimateComplete = !scanHadErrors
            && staleFilePurgePlan.ByteEstimateComplete
            && readableFileBytes.EstimateComplete;

        void AddDirtyFileBytes(int fileIndex)
        {
            throwIfCancelled();
            try
            {
                var target = fileTargets[fileIndex];
                var info = new FileInfo(target.FilePath);
                if (!info.Exists || info.Length < 0)
                {
                    byteEstimateComplete = false;
                    return;
                }

                readableFileBytes.Remember(fileIndex, info.Length);
                var persistedSize = reusableIndexedFileStats.GetPersistedSize(target.IndexPath);
                if (!FtsBulkLoadTriggerGuard.TryAccumulateDirtyFileBytes(
                        dirtyBytes,
                        persistedSizeExcessBytes,
                        info.Length,
                        persistedSize,
                        out dirtyBytes,
                        out persistedSizeExcessBytes))
                {
                    byteEstimateComplete = false;
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                byteEstimateComplete = false;
            }
        }

        if (extractionFileIndexes != null)
        {
            foreach (var fileIndex in extractionFileIndexes)
                AddDirtyFileBytes(fileIndex);
        }
        else
        {
            for (var fileIndex = 0; fileIndex < fileTargets.Count; fileIndex++)
                AddDirtyFileBytes(fileIndex);
        }

        byteEstimateComplete &= readableFileBytes.EstimateComplete;
        var totalBytes = readableFileBytes.KnownBytes;
        if (!readableFileBytes.EstimateComplete
            || totalBytes > long.MaxValue - staleFilePurgePlan.DeletedBytes)
        {
            byteEstimateComplete = false;
        }
        else
        {
            totalBytes += staleFilePurgePlan.DeletedBytes;
        }

        if (totalBytes > long.MaxValue - persistedSizeExcessBytes)
            byteEstimateComplete = false;
        else
            totalBytes += persistedSizeExcessBytes;

        return byteEstimateComplete
            && FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(dirtyBytes, totalBytes);
    }
}
