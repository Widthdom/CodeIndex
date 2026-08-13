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

    private sealed partial class FullScanPreWriteSession
    {
        internal void DecideFtsBulkLoad()
        {
            var request = Request;
            var options = request.Core.Options;
            var baseline = request.Baseline;
            var runtime = request.Runtime;
            var scan = State.Scan;
            var csharp = State.CSharp;
            var selection = State.Selection;
            if (options.Rebuild || baseline.StartedWithNoIndexedFiles)
            {
                selection.UseFtsBulkLoad = true;
                return;
            }

            if (selection.ExtractionWorkItemCount == 0
                && scan.StaleFilePurgePlan.Count == 0)
            {
                selection.UseFtsBulkLoad = false;
                return;
            }

            var dirtyBytes = scan.StaleFilePurgePlan.DeletedBytes;
            var persistedSizeExcessBytes = 0L;
            var byteEstimateComplete = !baseline.ScanHadErrors
                && scan.StaleFilePurgePlan.ByteEstimateComplete
                && selection.ReadableFileBytes.EstimateComplete;

            void AddDirtyFileBytes(int fileIndex)
            {
                ThrowIfFullScanCancelled();
                try
                {
                    var target = runtime.FileTargets[fileIndex];
                    var info = new FileInfo(target.FilePath);
                    if (!info.Exists || info.Length < 0)
                    {
                        byteEstimateComplete = false;
                        return;
                    }

                    selection.ReadableFileBytes.Remember(
                        fileIndex,
                        info.Length);
                    var persistedSize = csharp.ReusableIndexedFileStats!
                        .GetPersistedSize(target.IndexPath);
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

            if (selection.ExtractionFileIndexes != null)
            {
                foreach (var fileIndex in selection.ExtractionFileIndexes)
                    AddDirtyFileBytes(fileIndex);
            }
            else
            {
                for (var fileIndex = 0;
                     fileIndex < runtime.FileTargets.Length;
                     fileIndex++)
                {
                    AddDirtyFileBytes(fileIndex);
                }
            }

            byteEstimateComplete &=
                selection.ReadableFileBytes.EstimateComplete;
            var totalBytes = selection.ReadableFileBytes.KnownBytes;
            if (!selection.ReadableFileBytes.EstimateComplete
                || totalBytes
                > long.MaxValue - scan.StaleFilePurgePlan.DeletedBytes)
            {
                byteEstimateComplete = false;
            }
            else
            {
                totalBytes += scan.StaleFilePurgePlan.DeletedBytes;
            }

            if (totalBytes > long.MaxValue - persistedSizeExcessBytes)
                byteEstimateComplete = false;
            else
                totalBytes += persistedSizeExcessBytes;

            selection.UseFtsBulkLoad = byteEstimateComplete
                && FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(
                    dirtyBytes,
                    totalBytes);
        }
    }
}
