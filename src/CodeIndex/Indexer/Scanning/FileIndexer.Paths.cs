using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static string NormalizeIgnorePath(string path)
    {
        if (CanSkipIgnorePathNormalization(path))
            return path;

        return NormalizePathSeparators(path).TrimEnd('/');
    }

    private static bool CanSkipIgnorePathNormalization(string path)
        => (path.Length == 0 || path[^1] != '/')
            && (Path.DirectorySeparatorChar != '\\' || !path.Contains('\\'));

    /// <summary>
    /// Normalize OS path separators to '/' for DB storage and lookup.
    /// On Windows this converts '\' to '/'. On POSIX it returns the path
    /// unchanged so filenames that legitimately contain '\' (e.g. "back\slash.py")
    /// survive round-trip through the index.
    /// DB は '/' 固定で保存するため OS に応じて区切り文字だけを正規化する。
    /// Windows は '\' を '/' に変換し、POSIX ではファイル名内の '\' を壊さないよう何もしない。
    /// </summary>
    public static string NormalizePathSeparators(string path)
        => Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;

    internal static string NormalizeRelativePathForCurrentPlatform(string path)
        => Path.DirectorySeparatorChar == '/' ? path : path.Replace('/', Path.DirectorySeparatorChar);

    internal static string GetRelativePathFromProjectRoot(string projectRoot, string path) =>
        GetRelativePathFromDirectory(projectRoot, path);

    internal static string? TryGetRelativePathFromProjectRootPrefix(string projectRoot, string path)
        => TryGetRelativePathFromDirectoryPrefix(projectRoot, path);

    internal static string GetRelativePathFromDirectory(string directory, string path) =>
        TryGetRelativePathFromDirectoryPrefix(directory, path) ?? Path.GetRelativePath(directory, path);

    internal static string? TryGetRelativePathFromDirectoryPrefix(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(directory);
        if (root.Length == 0)
            return null;

        var comparison = IsWindowsPlatform
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (path.Length == root.Length)
            return path.AsSpan().Equals(root.AsSpan(), comparison) ? "." : null;
        if (path.Length <= root.Length
            || !path.AsSpan(0, root.Length).Equals(root.AsSpan(), comparison)
            || !IsPathDirectorySeparator(path[root.Length]))
        {
            return null;
        }

        return path[(root.Length + 1)..];
    }

    private static bool IsPathDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    /// <summary>
    /// Normalize index paths to the DB invariant: platform separators plus Unicode NFC.
    /// DB 保存・lookup 用 path は区切り文字正規化に加えて Unicode NFC に正規化する。
    /// </summary>
    public static string NormalizeIndexPath(string path)
    {
        if (CanSkipIndexPathNormalization(path))
            return path;

        return NormalizePathSeparators(path).Normalize(NormalizationForm.FormC);
    }

    private static bool CanSkipIndexPathNormalization(string path)
    {
        foreach (var ch in path)
        {
            if (ch > 0x7f)
                return false;
            if (Path.DirectorySeparatorChar == '\\' && ch == '\\')
                return false;
        }

        return true;
    }

    internal static bool IsFilePathSyntaxIndexable(string path)
    {
        foreach (var c in path)
        {
            if (c < ' ')
                return false;
        }

        return true;
    }
}
