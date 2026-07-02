using System.Security.Cryptography;

namespace CodeIndex.Cli;

internal static class Sha256StreamHasher
{
    internal const int DefaultBufferSize = 81920;

    internal static string ComputeHex(
        Stream stream,
        CancellationToken cancellationToken = default,
        Action<long>? progressBytesRead = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DefaultBufferSize];
        long totalBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            hasher.AppendData(buffer.AsSpan(0, read));
            totalBytes += read;
            progressBytesRead?.Invoke(totalBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return HexEncoding.ToLowerHexString(hasher.GetHashAndReset());
    }
}
