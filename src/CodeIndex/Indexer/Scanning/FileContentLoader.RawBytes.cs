namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
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
            using (var stream = new FileStream(
                ioPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.SequentialScan))
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

    private (byte[] Bytes, long SizeBytes) ReadStreamBytesAfterGrowth(
        FileStream stream,
        byte[] prefix,
        int prefixLength,
        byte firstExtraByte,
        string normalizedRelativePath,
        CancellationToken cancellationToken)
    {
        var total = (long)prefixLength + 1;
        if (total > maxFileSizeBytes)
            throw new FileIndexer.FileTooLargeSkippedException(
                normalizedRelativePath,
                total,
                maxFileSizeBytes,
                BuildFileTooLargeMessage(total, grewDuringRead: true));

        var initialCapacity = (int)Math.Min(
            maxFileSizeBytes,
            Math.Max(total, prefixLength + 81920L));
        using var accumulator = new MemoryStream(initialCapacity);
        if (prefixLength > 0)
            accumulator.Write(prefix, 0, prefixLength);
        accumulator.WriteByte(firstExtraByte);

        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > maxFileSizeBytes)
                throw new FileIndexer.FileTooLargeSkippedException(
                    normalizedRelativePath,
                    total,
                    maxFileSizeBytes,
                    BuildFileTooLargeMessage(total, grewDuringRead: true));
            accumulator.Write(buffer, 0, read);
        }

        return (accumulator.ToArray(), total);
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
