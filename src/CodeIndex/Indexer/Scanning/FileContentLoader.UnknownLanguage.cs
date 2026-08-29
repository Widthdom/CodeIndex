using System.Buffers;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private const int UnknownLanguageUtf16SampleByteLimit = 4096;

    internal readonly record struct UnknownLanguageProbeResult(
        FileIndexer.LanguageDetectionResult LanguageDetection,
        bool IsCoverageCandidate);

    internal UnknownLanguageProbeResult ProbeUnknownLanguage(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return ProbeUnknownLanguageCore(
                absolutePath,
                normalizedRelativePath,
                relativePath,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ProbeResult(FileIndexer.FileProbeStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeResult(FileIndexer.FileProbeStatus.Missing);
        }
        catch (IOException)
        {
            return ProbeResult(FileIndexer.FileProbeStatus.ProbeFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeResult(FileIndexer.FileProbeStatus.ProbeFailed);
        }
    }

    private UnknownLanguageProbeResult ProbeUnknownLanguageCore(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        Span<byte> headerBytes = stackalloc byte[FileIndexer.ShebangProbeByteLimit];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readPath = _resolveFileReadPath(absolutePath);
            using var stream = OpenValidatedReadStream(
                absolutePath,
                readPath,
                out var initialSnapshot);
            var initialLength = initialSnapshot.Length;
            var readGrowthToEnd = attempt > 0;
            var headerByteLimit = readGrowthToEnd
                ? headerBytes.Length
                : (int)Math.Min(initialLength, headerBytes.Length);

            var headerByteCount = FileIndexer.ReadScriptHeaderPrefix(
                stream,
                headerBytes[..headerByteLimit],
                cancellationToken);
            var language = FileIndexer.DetectLanguageFromScriptHeaderBytes(
                headerBytes[..headerByteCount],
                allowZshCompdef: true);
            if (language.Status == FileIndexer.FileProbeStatus.Supported)
            {
                var finalSnapshot = CaptureFileHandleSnapshot(stream);
                var headerLengthChanged = finalSnapshot.Length != initialLength;
                var headerPathIdentityChanged = ReadPathIdentityChanged(
                    absolutePath,
                    finalSnapshot);
                if (attempt == 0
                    && (finalSnapshot.ModifiedUtc != initialSnapshot.ModifiedUtc
                        || headerLengthChanged
                        || finalSnapshot.Identity != initialSnapshot.Identity
                        || headerPathIdentityChanged))
                {
                    continue;
                }

                return new UnknownLanguageProbeResult(language, IsCoverageCandidate: false);
            }

            ThrowIfInitialLengthExceedsMaxFileSize(
                normalizedRelativePath,
                initialLength);

            var coverageBuffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
            try
            {
                headerBytes[..headerByteCount].CopyTo(coverageBuffer);
                var prefixLength = headerByteCount;
                var total = (long)headerByteCount;
                ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);
                var nullByteOffset = FindNullByteOffset(
                    headerBytes[..headerByteCount],
                    absoluteOffset: 0);

                var reachedEof = false;
                while (prefixLength < UnknownLanguageUtf16SampleByteLimit
                       && (readGrowthToEnd || total < initialLength))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestedReadLength = UnknownLanguageUtf16SampleByteLimit - prefixLength;
                    if (!readGrowthToEnd)
                    {
                        requestedReadLength = (int)Math.Min(
                            requestedReadLength,
                            initialLength - total);
                    }
                    var read = stream.Read(
                        coverageBuffer,
                        prefixLength,
                        GetReadLengthWithinLimit(
                            total,
                            maxFileSizeBytes,
                            requestedReadLength));
                    if (read == 0)
                    {
                        reachedEof = true;
                        break;
                    }

                    CaptureNullByteOffset(
                        coverageBuffer.AsSpan(prefixLength, read),
                        total,
                        ref nullByteOffset);
                    total += read;
                    ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);
                    prefixLength += read;
                }

                var readBuffer = coverageBuffer.AsSpan(UnknownLanguageUtf16SampleByteLimit);
                while (!reachedEof && (readGrowthToEnd || total < initialLength))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestedReadLength = readBuffer.Length;
                    if (!readGrowthToEnd)
                    {
                        requestedReadLength = (int)Math.Min(
                            requestedReadLength,
                            initialLength - total);
                    }
                    var read = stream.Read(
                        readBuffer[..GetReadLengthWithinLimit(
                            total,
                            maxFileSizeBytes,
                            requestedReadLength)]);
                    if (read == 0)
                        break;

                    CaptureNullByteOffset(
                        readBuffer[..read],
                        total,
                        ref nullByteOffset);
                    total += read;
                    ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var finalSnapshot = CaptureFileHandleSnapshot(stream);
                ThrowIfReadExceedsMaxFileSize(
                    normalizedRelativePath,
                    finalSnapshot.Length);
                var pathIdentityChanged = ReadPathIdentityChanged(absolutePath, finalSnapshot);
                if (attempt == 0
                    && !InitialLengthReadIsStable(
                        initialSnapshot,
                        finalSnapshot,
                        total,
                        pathIdentityChanged))
                {
                    continue;
                }

                var prefix = coverageBuffer.AsSpan(0, prefixLength);
                if (total < GitLfsPointerMaxBytes
                    && IsGitLfsPointer(prefix[..(int)total]))
                {
                    return new UnknownLanguageProbeResult(
                        language,
                        IsCoverageCandidate: false);
                }

                if (TryDetectUtf16Encoding(
                        prefix,
                        allowHeuristic: true,
                        out _,
                        out _))
                {
                    return new UnknownLanguageProbeResult(
                        language,
                        IsCoverageCandidate: true);
                }

                if (nullByteOffset >= 0)
                {
                    throw new FileIndexer.BinaryFileSkippedException(
                        relativePath,
                        nullByteOffset,
                        $"{relativePath}: binary file skipped because it contains NULL byte at byte offset {nullByteOffset}");
                }

                return new UnknownLanguageProbeResult(
                    language,
                    IsCoverageCandidate: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(coverageBuffer);
            }
        }
    }

    private static UnknownLanguageProbeResult ProbeResult(
        FileIndexer.FileProbeStatus status)
        => new(
            new FileIndexer.LanguageDetectionResult(status, Language: null),
            IsCoverageCandidate: false);

    private static long FindNullByteOffset(
        ReadOnlySpan<byte> bytes,
        long absoluteOffset)
    {
        var relativeOffset = bytes.IndexOf((byte)0);
        return relativeOffset < 0 ? -1 : absoluteOffset + relativeOffset;
    }

    private static void CaptureNullByteOffset(
        ReadOnlySpan<byte> bytes,
        long absoluteOffset,
        ref long nullByteOffset)
    {
        if (nullByteOffset >= 0)
            return;

        nullByteOffset = FindNullByteOffset(bytes, absoluteOffset);
    }
}
