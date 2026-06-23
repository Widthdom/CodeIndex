using CodeIndex.Indexer;

namespace CodeIndex;

internal static class BoundedFile
{
    internal const int DefaultReadBufferSize = 81920;
    internal const int SmallReadBufferSize = 8192;

    internal static FileStream OpenReadForLengthCheckedText(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static FileStream OpenReadForPrefixProbe(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static FileStream OpenReadForTail(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static FileStream OpenReadForHash(string path)
        => OpenRead(path, FileShare.Read, DefaultReadBufferSize);

    internal static FileStream OpenReadTrustedArchiveSource(string path)
        => OpenRead(path, FileShare.Read, DefaultReadBufferSize);

    internal static FileStream OpenRead(
        string path,
        FileShare share,
        int bufferSize = DefaultReadBufferSize,
        FileOptions options = FileOptions.SequentialScan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Read buffer size must be positive.");

        return new FileStream(
            LongPath.EnsureWindowsPrefix(path),
            FileMode.Open,
            FileAccess.Read,
            share,
            bufferSize: bufferSize,
            options: options);
    }
}
