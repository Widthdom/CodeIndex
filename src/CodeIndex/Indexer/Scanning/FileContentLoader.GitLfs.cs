namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private const int GitLfsPointerMaxBytes = 1024;
    private static ReadOnlySpan<byte> GitLfsPointerPrefix
        => "version https://git-lfs.github.com/spec/v1"u8;
    private static ReadOnlySpan<byte> GitLfsExtensionPrefix => "ext-"u8;
    private static ReadOnlySpan<byte> GitLfsSha256OidPrefix => "oid sha256:"u8;
    private static ReadOnlySpan<byte> GitLfsSizePrefix => "size "u8;

    internal static bool IsGitLfsPointer(byte[] rawBytes)
    {
        if (rawBytes.Length == 0 || rawBytes.Length >= GitLfsPointerMaxBytes)
            return false;

        ReadOnlySpan<byte> remaining = rawBytes;
        if (!remaining.StartsWith(GitLfsPointerPrefix))
            return false;

        if (!TryReadGitLfsLine(ref remaining, out var line)
            || !line.SequenceEqual(GitLfsPointerPrefix))
        {
            return false;
        }

        if (!TryReadGitLfsLine(ref remaining, out line))
            return false;
        while (line.StartsWith(GitLfsExtensionPrefix))
        {
            if (!TryReadGitLfsLine(ref remaining, out line))
                return false;
        }

        if (!IsGitLfsSha256OidLine(line))
            return false;
        if (!TryReadGitLfsLine(ref remaining, out line)
            || !IsGitLfsSizeLine(line))
        {
            return false;
        }

        return remaining.IsEmpty;
    }

    private static bool TryReadGitLfsLine(
        ref ReadOnlySpan<byte> remaining,
        out ReadOnlySpan<byte> line)
    {
        if (remaining.IsEmpty)
        {
            line = default;
            return false;
        }

        var newlineIndex = remaining.IndexOfAny((byte)'\r', (byte)'\n');
        if (newlineIndex < 0)
        {
            line = remaining;
            remaining = ReadOnlySpan<byte>.Empty;
            return true;
        }

        line = remaining[..newlineIndex];
        var nextIndex = newlineIndex + 1;
        if (remaining[newlineIndex] == (byte)'\r'
            && nextIndex < remaining.Length
            && remaining[nextIndex] == (byte)'\n')
        {
            nextIndex++;
        }

        remaining = remaining[nextIndex..];
        return true;
    }

    private static bool IsGitLfsSha256OidLine(ReadOnlySpan<byte> line)
    {
        if (!line.StartsWith(GitLfsSha256OidPrefix))
            return false;

        var hash = line[GitLfsSha256OidPrefix.Length..];
        if (hash.Length != 64)
            return false;
        foreach (var value in hash)
        {
            if (!((value >= (byte)'0' && value <= (byte)'9')
                  || (value >= (byte)'a' && value <= (byte)'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsGitLfsSizeLine(ReadOnlySpan<byte> line)
    {
        if (!line.StartsWith(GitLfsSizePrefix))
            return false;

        var size = line[GitLfsSizePrefix.Length..];
        if (size.Length == 0)
            return false;
        foreach (var value in size)
        {
            if (value < (byte)'0' || value > (byte)'9')
                return false;
        }
        return true;
    }
}
