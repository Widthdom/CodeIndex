using System.Runtime.InteropServices;

namespace CodeIndex.Indexer;

// Apply the Windows extended-length path prefix (\\?\ or \\?\UNC\) to absolute
// paths that approach MAX_PATH so the FileIndexer walker does not silently skip
// files in deep monorepo trees (e.g. node_modules/.pnpm/<pkg>@<ver>/node_modules/...).
// MAX_PATH(=260) 近傍の絶対パスに Windows 拡張長パス接頭辞 (\\?\ または \\?\UNC\) を
// 付与し、深い monorepo ツリーで FileIndexer の walker がファイルを silent skip するのを防ぐ。
internal static class LongPath
{
    // Windows MAX_PATH is 260, but Win32 APIs that may append a child segment
    // (like FindFirstFile when given a directory) need headroom for an 8.3 name +
    // null terminator, so we apply the extended-length prefix once a path reaches
    // 248 characters. This matches the threshold called out in the upstream issue
    // and the .NET runtime's own internal threshold on legacy frameworks.
    // Windows MAX_PATH は 260 だが、ディレクトリ末尾に子要素 (8.3 + null) が付く
    // FindFirstFile などのために余裕が要るため、248 文字を境に拡張長接頭辞を適用する。
    internal const int LongPathThreshold = 248;
    internal const string ExtendedLengthPrefix = @"\\?\";
    internal const string ExtendedUncPrefix = @"\\?\UNC\";
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string EnsureWindowsPrefix(string path)
        => EnsureWindowsPrefixCore(path, IsWindows);

    public static string RemoveWindowsPrefix(string path)
        => RemoveWindowsPrefixCore(path, IsWindows);

    // Internal overloads take an explicit isWindows flag so unit tests can exercise
    // the Windows prefix logic on POSIX CI without depending on RuntimeInformation.
    // 単体テストが POSIX CI 上で Windows 用 prefix ロジックを検証できるよう、
    // isWindows を明示的に取る内部オーバーロードを用意する。
    internal static string EnsureWindowsPrefixCore(string path, bool isWindows)
    {
        if (!isWindows || string.IsNullOrEmpty(path) || path.Length < LongPathThreshold)
            return path;
        if (HasDevicePathPrefix(path))
            return path;
        if (IsWindowsUncPath(path))
            return ExtendedUncPrefix + path[2..];
        if (IsWindowsDriveAbsolutePath(path))
            return ExtendedLengthPrefix + path;
        return path;
    }

    internal static string RemoveWindowsPrefixCore(string path, bool isWindows)
    {
        if (!isWindows || string.IsNullOrEmpty(path))
            return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
            return @"\\" + path[ExtendedUncPrefix.Length..];
        if (path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal))
            return path[ExtendedLengthPrefix.Length..];
        return path;
    }

    private static bool HasDevicePathPrefix(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal);

    private static bool IsWindowsUncPath(string path)
        => path.Length >= 3
            && (path[0] == '\\' || path[0] == '/')
            && (path[1] == '\\' || path[1] == '/')
            && path[2] != '?'
            && path[2] != '.';

    private static bool IsWindowsDriveAbsolutePath(string path)
        => path.Length >= 3
            && IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');

    private static bool IsAsciiLetter(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
