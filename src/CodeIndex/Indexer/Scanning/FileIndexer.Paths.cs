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
