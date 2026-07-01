using System.Buffers;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private const int StreamBufferSize = 81920;

    internal delegate bool RawByteChunkPredicate(ReadOnlySpan<byte> bytes);

    private (byte[] Bytes, long SizeBytes, DateTime ModifiedUtc) ReadRawBytesWithSizeLimit(
        string absolutePath,
        string normalizedRelativePath,
        CancellationToken cancellationToken)
    {
        // Read raw bytes through a single FileStream and cap the accumulated payload at
        // the configured max-file limit so a file that grew between the size probe and the read can no
        // longer bypass the cap. Splitting `FileInfo.Length` from `File.ReadAllBytes`
        // left a TOCTOU window where an attacker (or any build/log emitter rapidly
        // appending to a generated file) could grow a 1 MB file to multi-GB between
        // stat and read and force the indexer into an OOM-sized allocation; reading
        // through one open handle removes the second stat call, and the read loop's
        // running total guarantees we never accumulate more than the configured max-file bytes
        // regardless of how aggressively a concurrent writer extends the file.
        // ファイルを 1 本の FileStream で開き、設定された max-file byte 上限として累積バッファを
        // 制限することで、サイズ確認と読み込みの間にファイルが肥大化しても上限を
        // 回避できないようにする。
        byte[] bytes;
        long sizeBytes;
        DateTime modifiedUtc;
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        for (var attempt = 0; ; attempt++)
        {
            var modifiedBeforeRead = File.GetLastWriteTimeUtc(ioPath);
            using (var stream = BoundedFile.OpenReadForIndexContent(absolutePath))
            {
                var initialLength = stream.Length;
                if (initialLength > maxFileSizeBytes)
                    throw new FileIndexer.FileTooLargeSkippedException(
                        normalizedRelativePath,
                        initialLength,
                        maxFileSizeBytes,
                        BuildFileTooLargeMessage(initialLength, grewDuringRead: false));

                (bytes, sizeBytes) = ReadStreamBytesWithKnownInitialLength(
                    stream,
                    initialLength,
                    normalizedRelativePath,
                    cancellationToken);
            }
            modifiedUtc = File.GetLastWriteTimeUtc(ioPath);
            if (modifiedUtc == modifiedBeforeRead || attempt > 0)
                break;
        }

        return (bytes, sizeBytes, modifiedUtc);
    }

    internal bool RawByteChunksMayMatch(
        string absolutePath,
        string normalizedRelativePath,
        RawByteChunkPredicate chunkPredicate,
        CancellationToken cancellationToken)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        for (var attempt = 0; ; attempt++)
        {
            var modifiedBeforeRead = File.GetLastWriteTimeUtc(ioPath);
            bool matched;
            using (var stream = BoundedFile.OpenReadForIndexContent(absolutePath))
            {
                var initialLength = stream.Length;
                if (initialLength > maxFileSizeBytes)
                    throw new FileIndexer.FileTooLargeSkippedException(
                        normalizedRelativePath,
                        initialLength,
                        maxFileSizeBytes,
                        BuildFileTooLargeMessage(initialLength, grewDuringRead: false));

                matched = RawByteChunksMayMatch(
                    stream,
                    initialLength,
                    normalizedRelativePath,
                    chunkPredicate,
                    cancellationToken);
            }

            var modifiedUtc = File.GetLastWriteTimeUtc(ioPath);
            if (matched || modifiedUtc == modifiedBeforeRead || attempt > 0)
                return matched;
        }
    }

    private (byte[] Bytes, long SizeBytes) ReadStreamBytesWithKnownInitialLength(
        FileStream stream,
        long initialLength,
        string normalizedRelativePath,
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

    private bool RawByteChunksMayMatch(
        FileStream stream,
        long initialLength,
        string normalizedRelativePath,
        RawByteChunkPredicate chunkPredicate,
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
                    return false;

                total += read;
                if (chunkPredicate(buffer.AsSpan(0, read)))
                    return true;
            }

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

    private bool RawByteGrowthChunksMayMatch(
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
                return false;

            total += read;
            ThrowIfReadExceedsMaxFileSize(normalizedRelativePath, total);

            if (chunkPredicate(buffer.AsSpan(0, read)))
                return true;
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
