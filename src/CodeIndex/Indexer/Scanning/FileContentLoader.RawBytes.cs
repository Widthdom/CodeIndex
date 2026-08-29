using System.Buffers;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private const int StreamBufferSize = 81920;

    internal delegate bool RawByteChunkPredicate(ReadOnlySpan<byte> bytes);

    private readonly record struct RawFileSnapshot(
        byte[] Bytes,
        long SizeBytes,
        DateTime ModifiedUtc);

    private RawFileSnapshot ReadRawBytesWithSizeLimit(
        string absolutePath,
        string normalizedRelativePath,
        CancellationToken cancellationToken)
    {
        // Read raw bytes through one FileStream. Attempt zero stops at the initial
        // handle length and validates the final handle metadata instead of probing
        // EOF; a changed snapshot retries once with the conventional bounded EOF
        // loop. Both paths reject a final handle length over the configured cap, and
        // the retry's running total prevents concurrent growth from forcing an
        // unbounded allocation.
        // 1本のFileStreamでraw byteを読みます。attempt 0はinitial handle lengthで停止し、
        // EOF probeの代わりにfinal handle metadataを検証します。snapshot変化時だけ従来の
        // bounded EOF loopで1回retryし、どちらの経路もfinal handle lengthの上限超過を拒否します。
        byte[] bytes;
        long sizeBytes;
        DateTime modifiedUtc;
        for (var attempt = 0; ; attempt++)
        {
            var readPath = _resolveFileReadPath(absolutePath);
            FileIndexer.FileHandleSnapshot initialSnapshot;
            bool pathIdentityChanged;
            using (var stream = OpenValidatedReadStream(
                       absolutePath,
                       readPath,
                       out initialSnapshot))
            {
                var initialLength = initialSnapshot.Length;
                ThrowIfInitialLengthExceedsMaxFileSize(
                    normalizedRelativePath,
                    initialLength);

                (bytes, sizeBytes) = ReadStreamBytesWithKnownInitialLength(
                    stream,
                    initialLength,
                    normalizedRelativePath,
                    readGrowthToEnd: attempt > 0,
                    cancellationToken);
                var finalSnapshot = CaptureFileHandleSnapshot(stream);
                modifiedUtc = finalSnapshot.ModifiedUtc;
                ThrowIfReadExceedsMaxFileSize(
                    normalizedRelativePath,
                    finalSnapshot.Length);
                pathIdentityChanged = ReadPathIdentityChanged(absolutePath, finalSnapshot);

                if (attempt > 0
                    || InitialLengthReadIsStable(
                        initialSnapshot,
                        finalSnapshot,
                        sizeBytes,
                        pathIdentityChanged))
                {
                    break;
                }
            }
        }

        return new RawFileSnapshot(bytes, sizeBytes, modifiedUtc);
    }

    internal bool RawByteChunksMayMatch(
        string absolutePath,
        string normalizedRelativePath,
        RawByteChunkPredicate chunkPredicate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var readPath = _resolveFileReadPath(absolutePath);
            FileIndexer.FileHandleSnapshot initialSnapshot;
            FileIndexer.FileHandleSnapshot finalSnapshot;
            bool pathIdentityChanged;
            RawByteScanResult scan;
            using (var stream = OpenValidatedReadStream(
                       absolutePath,
                       readPath,
                       out initialSnapshot))
            {
                var initialLength = initialSnapshot.Length;
                ThrowIfInitialLengthExceedsMaxFileSize(
                    normalizedRelativePath,
                    initialLength);

                scan = RawByteChunksMayMatch(
                    stream,
                    initialLength,
                    normalizedRelativePath,
                    chunkPredicate,
                    readGrowthToEnd: attempt > 0,
                    cancellationToken);
                finalSnapshot = CaptureFileHandleSnapshot(stream);
                pathIdentityChanged = ReadPathIdentityChanged(absolutePath, finalSnapshot);
            }

            if (scan.Matched)
                return true;

            ThrowIfReadExceedsMaxFileSize(
                normalizedRelativePath,
                finalSnapshot.Length);
            if (attempt > 0
                || InitialLengthReadIsStable(
                    initialSnapshot,
                    finalSnapshot,
                    scan.BytesRead,
                    pathIdentityChanged))
            {
                return false;
            }
        }
    }

    private static bool InitialLengthReadIsStable(
        FileIndexer.FileHandleSnapshot initialSnapshot,
        FileIndexer.FileHandleSnapshot finalSnapshot,
        long bytesRead,
        bool pathIdentityChanged)
        => bytesRead == initialSnapshot.Length
            && finalSnapshot.Length == initialSnapshot.Length
            && finalSnapshot.Length == bytesRead
            && finalSnapshot.ModifiedUtc == initialSnapshot.ModifiedUtc
            && finalSnapshot.Identity == initialSnapshot.Identity
            && !pathIdentityChanged;

    private FileStream OpenValidatedReadStream(
        string absolutePath,
        string expectedReadPath,
        out FileIndexer.FileHandleSnapshot initialSnapshot)
    {
        initialSnapshot = default;
        _validateResolvedFileReadPath?.Invoke(expectedReadPath);
        FileIndexer.FileIdentity expectedIdentity = default;
        if (_bindReadToFileSystemIdentity
            && !FileIndexer.TryGetFileIdentity(expectedReadPath, out expectedIdentity))
        {
            throw new IOException(
                "Failed to capture the filesystem identity of a symlink target before opening it.");
        }

        var stream = _openReadForIndexContent(absolutePath);
        try
        {
            initialSnapshot = CaptureFileHandleSnapshot(stream);
            if (!_bindReadToFileSystemIdentity)
                return stream;

            if (initialSnapshot.Identity is not { } openedIdentity
                || openedIdentity != expectedIdentity)
            {
                throw new IOException(
                    "File symlink target identity changed while it was opened; rerun indexing.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool ReadPathIdentityChanged(
        string absolutePath,
        FileIndexer.FileHandleSnapshot snapshot)
    {
        if (snapshot.Identity is not { } openedIdentity)
            return false;

        return !FileIndexer.TryGetFileIdentity(absolutePath, out var currentIdentity)
            || currentIdentity != openedIdentity;
    }

    private (byte[] Bytes, long SizeBytes) ReadStreamBytesWithKnownInitialLength(
        FileStream stream,
        long initialLength,
        string normalizedRelativePath,
        bool readGrowthToEnd,
        CancellationToken cancellationToken)
    {
        var expectedLength = (int)initialLength;
        var bytes = expectedLength == 0 ? Array.Empty<byte>() : new byte[expectedLength];
        var total = 0;

        while (total < expectedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, total, expectedLength - total);
            if (read == 0)
                return (ResizeReadBuffer(bytes, total), total);
            total += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!readGrowthToEnd)
            return (bytes, total);

        var extra = stream.ReadByte();
        if (extra < 0)
            return (bytes, total);

        return ReadStreamBytesAfterGrowth(
            stream,
            bytes,
            total,
            (byte)extra,
            normalizedRelativePath,
            cancellationToken);
    }

    private readonly record struct RawByteScanResult(bool Matched, long BytesRead);

    private RawByteScanResult RawByteChunksMayMatch(
        FileStream stream,
        long initialLength,
        string normalizedRelativePath,
        RawByteChunkPredicate chunkPredicate,
        bool readGrowthToEnd,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            long total = 0;

            while (total < initialLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, initialLength - total));
                if (read == 0)
                    return new RawByteScanResult(Matched: false, total);

                total += read;
                if (chunkPredicate(buffer.AsSpan(0, read)))
                    return new RawByteScanResult(Matched: true, total);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!readGrowthToEnd)
                return new RawByteScanResult(Matched: false, total);

            return RawByteGrowthChunksMayMatch(
                stream,
                total,
                normalizedRelativePath,
                buffer,
                chunkPredicate,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private RawByteScanResult RawByteGrowthChunksMayMatch(
        FileStream stream,
        long total,
        string normalizedRelativePath,
        byte[] buffer,
        RawByteChunkPredicate chunkPredicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(
                buffer,
                0,
                GetReadLengthWithinLimit(total, maxFileSizeBytes, buffer.Length));
            if (read == 0)
                return new RawByteScanResult(Matched: false, total);

            total += read;
            ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);

            if (chunkPredicate(buffer.AsSpan(0, read)))
                return new RawByteScanResult(Matched: true, total);
        }
    }

    private (byte[] Bytes, long SizeBytes) ReadStreamBytesAfterGrowth(
        FileStream stream,
        byte[] prefix,
        int prefixLength,
        byte firstExtraByte,
        string normalizedRelativePath,
        CancellationToken cancellationToken)
    {
        var total = (long)prefixLength + 1;
        ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);
        using var accumulator = CreateGrowthAccumulator(prefix, prefixLength, firstExtraByte, total);

        total = AppendRemainingStreamBytesWithinLimit(
            stream,
            accumulator,
            total,
            normalizedRelativePath,
            cancellationToken);

        return (accumulator.ToArray(), total);
    }

    private MemoryStream CreateGrowthAccumulator(
        byte[] prefix,
        int prefixLength,
        byte firstExtraByte,
        long total)
    {
        var initialCapacity = (int)Math.Min(
            maxFileSizeBytes,
            Math.Max(total, prefixLength + (long)StreamBufferSize));
        var accumulator = new MemoryStream(initialCapacity);
        if (prefixLength > 0)
            accumulator.Write(prefix, 0, prefixLength);
        accumulator.WriteByte(firstExtraByte);
        return accumulator;
    }

    private long AppendRemainingStreamBytesWithinLimit(
        FileStream stream,
        MemoryStream accumulator,
        long total,
        string normalizedRelativePath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(
                       buffer,
                       0,
                       GetReadLengthWithinLimit(total, maxFileSizeBytes, buffer.Length))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);
                accumulator.Write(buffer, 0, read);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ThrowIfReadExceedsMaxFileSize(string normalizedRelativePath, long total)
    {
        if (total <= maxFileSizeBytes)
            return;

        throw new FileIndexer.FileTooLargeSkippedException(
            normalizedRelativePath,
            total,
            maxFileSizeBytes,
            BuildFileTooLargeMessage(total, grewDuringRead: true));
    }

    private void ThrowIfInitialLengthExceedsMaxFileSize(
        string normalizedRelativePath,
        long initialLength)
    {
        if (initialLength <= maxFileSizeBytes)
            return;

        throw new FileIndexer.FileTooLargeSkippedException(
            normalizedRelativePath,
            initialLength,
            maxFileSizeBytes,
            BuildFileTooLargeMessage(initialLength, grewDuringRead: false));
    }

    private static int GetReadLengthWithinLimit(long total, long maxBytes, int bufferLength)
    {
        var remaining = maxBytes - total;
        if (remaining >= bufferLength)
            return bufferLength;
        if (remaining < 0)
            return 1;

        return (int)Math.Min(bufferLength, remaining + 1);
    }

    private static byte[] ResizeReadBuffer(byte[] bytes, int length)
    {
        if (length == bytes.Length)
            return bytes;
        if (length == 0)
            return [];

        var resized = new byte[length];
        Buffer.BlockCopy(bytes, 0, resized, 0, length);
        return resized;
    }

    private string BuildFileTooLargeMessage(long actualBytes, bool grewDuringRead)
    {
        var actual = FormatBytesForError(actualBytes);
        var limit = FormatBytesForError(maxFileSizeBytes);
        var observed = grewDuringRead
            ? $"File too large (> {limit} limit; grew during read)"
            : $"File too large ({actual} > {limit} limit)";
        return $"{observed}. Override with --max-file-bytes <bytes> or {FileIndexer.MaxFileSizeEnvironmentVariable}=<bytes> when this source file is intentionally indexable.";
    }

    private static string FormatBytesForError(long bytes)
    {
        if (bytes % (1024L * 1024L) == 0)
            return $"{bytes / 1024L / 1024L} MiB";
        if (bytes % 1024L == 0)
            return $"{bytes / 1024L} KiB";
        return $"{bytes} bytes";
    }
}
