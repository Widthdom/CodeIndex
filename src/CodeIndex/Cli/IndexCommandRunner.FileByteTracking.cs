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
}
