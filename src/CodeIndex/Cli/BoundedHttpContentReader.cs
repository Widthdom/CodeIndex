using System.Buffers;

namespace CodeIndex.Cli;

internal static class BoundedHttpContentReader
{
    private const int BufferSize = 81920;

    internal static async Task<byte[]> ReadAsByteArrayAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ValidateMaxBytes(maxBytes);
        ThrowIfContentLengthExceedsLimit(content, maxBytes);

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = CreateMemoryStream(content);
        await CopyToAsync(source, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    internal static async Task WriteToPrivateFileAsync(
        HttpContent content,
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ValidateMaxBytes(maxBytes);
        ThrowIfContentLengthExceedsLimit(content, maxBytes);

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = CreatePrivateFileStream(path);
        await CopyToAsync(source, destination, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static MemoryStream CreateMemoryStream(HttpContent content)
    {
        if (content.Headers.ContentLength is long contentLength
            && contentLength > 0
            && contentLength <= int.MaxValue)
        {
            return new MemoryStream((int)contentLength);
        }

        return new MemoryStream();
    }

    private static FileStream CreatePrivateFileStream(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.SequentialScan,
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return new FileStream(path, options);
    }

    private static async Task CopyToAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long copied = 0;
            while (true)
            {
                var remaining = maxBytes - copied;
                var readLimit = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                var read = await source.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return;

                if (read > maxBytes - copied)
                    throw CreateExceededLimitException(maxBytes);

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ThrowIfContentLengthExceedsLimit(HttpContent content, long maxBytes)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw CreateExceededLimitException(maxBytes);
    }

    private static InvalidDataException CreateExceededLimitException(long maxBytes)
        => new($"HTTP response body exceeded the {maxBytes} byte limit.");

    private static void ValidateMaxBytes(long maxBytes)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "The byte limit must be non-negative.");
    }
}
