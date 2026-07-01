namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static bool IsWindowsDevicePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var path = filePath.AsSpan();
        if (StartsWithWindowsDeviceNamespace(path))
        {
            return true;
        }

        for (var start = 0; start < path.Length;)
        {
            while (start < path.Length && IsWindowsPathSeparator(path[start]))
                start++;
            if (start >= path.Length)
                break;

            var end = start;
            while (end < path.Length && !IsWindowsPathSeparator(path[end]))
                end++;

            var name = path[start..end];
            var extensionIndex = name.IndexOf('.');
            if (extensionIndex >= 0)
                name = name[..extensionIndex];

            if (IsWindowsReservedDeviceName(name))
                return true;

            start = end + 1;
        }

        return false;
    }

    private static bool StartsWithWindowsDeviceNamespace(ReadOnlySpan<char> path)
    {
        if (path.Length >= 4
            && IsWindowsPathSeparator(path[0])
            && IsWindowsPathSeparator(path[1])
            && path[2] == '.'
            && IsWindowsPathSeparator(path[3]))
        {
            return true;
        }

        if (path.Length < 22
            || !IsWindowsPathSeparator(path[0])
            || !IsWindowsPathSeparator(path[1])
            || path[2] != '?'
            || !IsWindowsPathSeparator(path[3]))
        {
            return false;
        }

        var remaining = path[4..];
        if (!remaining.StartsWith("GLOBALROOT".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["GLOBALROOT".Length..];
        if (remaining.IsEmpty || !IsWindowsPathSeparator(remaining[0]))
            return false;

        remaining = remaining[1..];
        if (!remaining.StartsWith("Device".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["Device".Length..];
        return !remaining.IsEmpty && IsWindowsPathSeparator(remaining[0]);
    }

    private static bool IsWindowsPathSeparator(char value)
        => value is '\\' or '/';

    private static bool IsWindowsReservedDeviceName(ReadOnlySpan<char> name)
    {
        if (name.Equals("CON".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT".AsSpan(), StringComparison.OrdinalIgnoreCase))
            && name[3] >= '1'
            && name[3] <= '9';
    }
}
