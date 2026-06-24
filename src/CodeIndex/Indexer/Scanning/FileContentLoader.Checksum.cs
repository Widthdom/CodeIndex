using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    internal static string ComputeChecksum(byte[] bytes)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pendingCarriageReturn = false;
        AppendNormalizedChecksumBytes(hasher, bytes, ref pendingCarriageReturn);
        FlushPendingChecksumCarriageReturn(hasher, ref pendingCarriageReturn);
        return FinishChecksum(hasher);
    }

    internal static string ComputeChecksumFromNormalizedContent(string content)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        const int MaxCharsPerChunk = 1024;
        for (var offset = 0; offset < content.Length;)
        {
            var charCount = Math.Min(MaxCharsPerChunk, content.Length - offset);
            if (offset + charCount < content.Length
                && charCount > 0
                && char.IsHighSurrogate(content[offset + charCount - 1])
                && char.IsLowSurrogate(content[offset + charCount]))
            {
                charCount--;
            }

            if (charCount == 0)
                charCount = 1;

            var written = Encoding.UTF8.GetBytes(content.AsSpan(offset, charCount), buffer);
            if (written > 0)
                hasher.AppendData(buffer[..written]);
            offset += charCount;
        }

        return FinishChecksum(hasher);
    }

    internal static bool TryComputeChecksum(
        string filePath,
        long maxBytes,
        out string checksum,
        CancellationToken cancellationToken = default)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum byte count must be non-negative.");

        checksum = string.Empty;
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.SequentialScan);
        return TryComputeChecksum(stream, maxBytes, out checksum, cancellationToken);
    }

    internal static bool TryComputeChecksum(
        Stream stream,
        long maxBytes,
        out string checksum,
        CancellationToken cancellationToken = default)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum byte count must be non-negative.");

        checksum = string.Empty;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var pendingCarriageReturn = false;
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
