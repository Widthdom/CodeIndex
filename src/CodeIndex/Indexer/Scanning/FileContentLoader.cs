using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Indexer;

internal sealed class FileContentLoader(long maxFileSizeBytes)
{
    private const int GitLfsPointerMaxBytes = 1024;

    internal LoadedFileContent Load(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var (bytes, sizeBytes, modifiedUtc) = ReadRawBytesWithSizeLimit(
            absolutePath,
            normalizedRelativePath,
            cancellationToken);
        var (content, warning) = DecodeIndexableContent(bytes, relativePath);
        content = NormalizeLineEndings(content);
        content = StripLineLeadingInvisibles(content);
        if (IsGitLfsPointer(bytes))
            content = string.Empty;

        return new LoadedFileContent(
            content,
            bytes,
            sizeBytes,
            modifiedUtc,
            FileIndexer.CountPhysicalLines(content),
            ComputeChecksum(Encoding.UTF8.GetBytes(content)),
            warning);
    }

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
                bufferSize: 4096,
                useAsync: false))
            {
                var initialLength = stream.Length;
                if (initialLength > maxFileSizeBytes)
                    throw new FileIndexer.FileTooLargeSkippedException(
                        normalizedRelativePath,
                        initialLength,
                        maxFileSizeBytes,
                        BuildFileTooLargeMessage(initialLength, grewDuringRead: false));

                var initialCapacity = (int)Math.Min(initialLength, maxFileSizeBytes);
                using var accumulator = new MemoryStream(initialCapacity);
                var buffer = new byte[81920];
                long total = 0;
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
                bytes = accumulator.ToArray();
                sizeBytes = total;
            }
            modifiedUtc = File.GetLastWriteTimeUtc(ioPath);
            if (modifiedUtc == modifiedBeforeRead || attempt > 0)
                break;
        }

        return (bytes, sizeBytes, modifiedUtc);
    }

    private (string Content, string? Warning) DecodeIndexableContent(byte[] bytes, string relativePath)
    {
        var isUtf16Encoded = TryDetectUtf16Encoding(bytes, allowHeuristic: true, out var utf16BigEndian, out var hasUtf16Bom);

        if (!isUtf16Encoded && ContainsIndexBlockingNullByte(bytes))
            throw new FileIndexer.BinaryFileSkippedException($"{relativePath}: binary file skipped because it contains NULL bytes");

        if (isUtf16Encoded)
        {
            var content = new UnicodeEncoding(utf16BigEndian, byteOrderMark: hasUtf16Bom, throwOnInvalidBytes: false)
                .GetString(bytes);
            return (content, null);
        }

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), null);
        }
        catch (DecoderFallbackException)
        {
            var content = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
            return (content, $"{relativePath}: contains invalid UTF-8 bytes (replaced with U+FFFD)");
        }
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

    internal static string NormalizeLineEndings(string content)
    {
        var firstCarriageReturn = content.IndexOf('\r');
        if (firstCarriageReturn < 0)
            return content;

        var builder = new StringBuilder(content.Length);
        builder.Append(content, 0, firstCarriageReturn);

        for (var index = firstCarriageReturn; index < content.Length; index++)
        {
            if (content[index] != '\r')
            {
                builder.Append(content[index]);
                continue;
            }

            builder.Append('\n');
            if (index + 1 < content.Length && content[index + 1] == '\n')
                index++;
        }

        return builder.ToString();
    }

    internal static string StripLineLeadingInvisibles(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        if (!content.Contains('\uFEFF') && !content.Contains('\u200B'))
            return content;

        var firstStripIndex = -1;
        var atLineStart = true;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
            {
                firstStripIndex = i;
                break;
            }
            atLineStart = c == '\n';
        }
        if (firstStripIndex < 0)
            return content;

        var sb = new StringBuilder(content.Length - 1);
        if (firstStripIndex > 0)
            sb.Append(content, 0, firstStripIndex);
        atLineStart = true;
        for (var i = firstStripIndex + 1; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
                continue;
            sb.Append(c);
            atLineStart = c == '\n';
        }
        return sb.ToString();
    }

    private static bool IsLineLeadingInvisible(char c) => c is '\uFEFF' or '\u200B';

    internal static bool IsGitLfsPointer(byte[] rawBytes)
    {
        if (rawBytes.Length == 0 || rawBytes.Length >= GitLfsPointerMaxBytes)
            return false;

        var pointerText = Encoding.UTF8.GetString(rawBytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = pointerText.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        if (lines.Length < 3)
            return false;
        if (!string.Equals(lines[0], "version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            return false;

        var lineIndex = 1;
        while (lineIndex < lines.Length && lines[lineIndex].StartsWith("ext-", StringComparison.Ordinal))
            lineIndex++;

        if (lineIndex + 1 >= lines.Length)
            return false;
        if (!IsGitLfsSha256OidLine(lines[lineIndex]))
            return false;
        lineIndex++;
        if (!IsGitLfsSizeLine(lines[lineIndex]))
            return false;

        return lineIndex == lines.Length - 1;
    }

    private static bool IsGitLfsSha256OidLine(string line)
    {
        const string prefix = "oid sha256:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var hash = line.AsSpan(prefix.Length);
        if (hash.Length != 64)
            return false;
        foreach (var c in hash)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    private static bool IsGitLfsSizeLine(string line)
    {
        const string prefix = "size ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var size = line.AsSpan(prefix.Length);
        if (size.Length == 0)
            return false;
        foreach (var c in size)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }

    internal static bool ContainsIndexBlockingNullByte(byte[] rawBytes)
    {
        return !TryDetectUtf16Encoding(rawBytes, allowHeuristic: true, out _, out _) && rawBytes.Any(b => b == 0);
    }

    internal static bool TryDetectUtf16Encoding(
        byte[] rawBytes,
        bool allowHeuristic,
        out bool bigEndian,
        out bool hasBom)
    {
        bigEndian = false;
        hasBom = false;

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
        {
            bigEndian = true;
            hasBom = true;
            return true;
        }

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE
            && !(rawBytes.Length >= 4 && rawBytes[2] == 0x00 && rawBytes[3] == 0x00))
        {
            hasBom = true;
            return true;
        }

        if (!allowHeuristic || rawBytes.Length < 4)
            return false;

        var sampleLength = Math.Min(rawBytes.Length, 4096);
        sampleLength -= sampleLength % 2;
        var pairs = sampleLength / 2;
        if (pairs == 0)
            return false;

        var evenNulls = 0;
        var oddNulls = 0;
        var oddTextBytes = 0;
        var evenTextBytes = 0;
        for (var i = 0; i < sampleLength; i += 2)
        {
            if (rawBytes[i] == 0)
                evenNulls++;
            if (rawBytes[i + 1] == 0)
                oddNulls++;
            if (IsLikelyTextByte(rawBytes[i + 1]))
                oddTextBytes++;
            if (IsLikelyTextByte(rawBytes[i]))
                evenTextBytes++;
        }

        const double NullParityThreshold = 0.30;
        const double OppositeNullThreshold = 0.01;
        const double TextByteThreshold = 0.80;
        var beScore = (double)evenNulls / pairs;
        var leScore = (double)oddNulls / pairs;
        var beOppositeScore = (double)oddNulls / pairs;
        var leOppositeScore = (double)evenNulls / pairs;

        if (beScore >= NullParityThreshold
            && beOppositeScore <= OppositeNullThreshold
            && (double)oddTextBytes / pairs >= TextByteThreshold)
        {
            bigEndian = true;
            return true;
        }

        if (leScore >= NullParityThreshold
            && leOppositeScore <= OppositeNullThreshold
            && (double)evenTextBytes / pairs >= TextByteThreshold)
        {
            bigEndian = false;
            return true;
        }

        return false;
    }

    private static bool IsLikelyTextByte(byte value)
        => value is 0x09 or 0x0A or 0x0D || value >= 0x20;

    internal static string ComputeChecksum(byte[] bytes)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pendingCarriageReturn = false;
        AppendNormalizedChecksumBytes(hasher, bytes, ref pendingCarriageReturn);
        FlushPendingChecksumCarriageReturn(hasher, ref pendingCarriageReturn);
        return FinishChecksum(hasher);
    }

    internal static bool TryComputeChecksum(string filePath, long maxBytes, out string checksum)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum byte count must be non-negative.");

        checksum = string.Empty;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.SequentialScan);

        var buffer = new byte[81920];
        var pendingCarriageReturn = false;
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                return false;

            AppendNormalizedChecksumBytes(hasher, buffer.AsSpan(0, read), ref pendingCarriageReturn);
        }

        FlushPendingChecksumCarriageReturn(hasher, ref pendingCarriageReturn);
        checksum = FinishChecksum(hasher);
        return true;
    }

    private static void AppendNormalizedChecksumBytes(
        IncrementalHash hasher,
        ReadOnlySpan<byte> bytes,
        ref bool pendingCarriageReturn)
    {
        Span<byte> normalized = stackalloc byte[4096];
        var n = 0;

        if (pendingCarriageReturn)
        {
            if (bytes.Length > 0 && bytes[0] == 0x0A)
                bytes = bytes[1..];
            normalized[n++] = 0x0A;
            pendingCarriageReturn = false;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0x0D)
            {
                if (i + 1 == bytes.Length)
                {
                    pendingCarriageReturn = true;
                    continue;
                }

                normalized[n++] = 0x0A;
                if (i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                    i++;
            }
            else
            {
                normalized[n++] = b;
            }

            if (n == normalized.Length)
            {
                hasher.AppendData(normalized);
                n = 0;
            }
        }

        if (n > 0)
            hasher.AppendData(normalized[..n]);
    }

    private static void FlushPendingChecksumCarriageReturn(IncrementalHash hasher, ref bool pendingCarriageReturn)
    {
        if (!pendingCarriageReturn)
            return;

        Span<byte> lineFeed = stackalloc byte[1];
        lineFeed[0] = 0x0A;
        hasher.AppendData(lineFeed);
        pendingCarriageReturn = false;
    }

    private static string FinishChecksum(IncrementalHash hasher)
    {
        Span<byte> hash = stackalloc byte[32];
        if (!hasher.TryGetHashAndReset(hash, out var written) || written != hash.Length)
            throw new InvalidOperationException("SHA256 produced an unexpected hash length");
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal readonly record struct LoadedFileContent(
    string Content,
    byte[] RawBytes,
    long SizeBytes,
    DateTime ModifiedUtc,
    int LineCount,
    string Checksum,
    string? Warning);
