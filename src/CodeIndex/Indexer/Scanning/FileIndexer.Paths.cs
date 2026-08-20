using System.Runtime.InteropServices;
using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal readonly record struct NativeProjectPathMatch(
        string RelativePath,
        string CanonicalLexicalPath);

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

    /// <summary>
    /// Resolve a lexical project-relative path while accepting native-equivalent spellings
    /// of the project-root directory. This keeps components below the matched root lexical,
    /// so later symlink-policy checks can still inspect the actual selected path instead of
    /// a fully resolved target path.
    /// project root directory の native-equivalent な表記を受理しつつ lexical な相対 path を
    /// 解決する。root 配下の component は lexical なまま保持し、後続の symlink policy 検証が
    /// fully-resolved target ではなく実際に選択された path を検査できるようにする。
    /// </summary>
    internal static bool TryGetNativeEquivalentProjectRelativePath(
        string projectRoot,
        string path,
        out NativeProjectPathMatch match)
    {
        var fullProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directRelativePath = NormalizePathSeparators(
            GetRelativePathFromDirectory(fullProjectRoot, fullPath));
        var ordinalRelativePath = TryGetRelativePathFromDirectoryPrefix(
            fullProjectRoot,
            fullPath,
            StringComparison.Ordinal);
        if (ordinalRelativePath != null)
        {
            match = CreateNativeProjectPathMatch(fullProjectRoot, ordinalRelativePath);
            return true;
        }

        // Path.GetRelativePath cannot recognize every native spelling alias. In particular,
        // normalization-equivalent macOS names and Windows 8.3 names can make an in-root
        // absolute selection look like ../<alias>/file. Walk lexical ancestors until one is
        // proven to be another spelling of the configured root, retaining the tail exactly.
        // Path.GetRelativePath は macOS の正規化同値名や Windows 8.3 名をすべて認識できず、
        // root 内の absolute selection を ../<alias>/file と見なすことがある。lexical ancestor
        // が設定 root の別表記だと証明できるまで遡り、root 配下の tail はそのまま保持する。
        var tail = new Stack<string>();
        var current = fullPath;
        while (true)
        {
            if (NativeLexicalDirectorySpellingsMatch(fullProjectRoot, current))
            {
                var relativePath = tail.Count == 0
                    ? "."
                    : NormalizePathSeparators(string.Join(Path.DirectorySeparatorChar, tail));
                match = CreateNativeProjectPathMatch(fullProjectRoot, relativePath);
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            var name = Path.GetFileName(current);
            if (name.Length > 0)
                tail.Push(name);
            current = Path.TrimEndingDirectorySeparator(parent);
        }

        match = new NativeProjectPathMatch(directRelativePath, fullPath);
        return false;
    }

    private static NativeProjectPathMatch CreateNativeProjectPathMatch(
        string fullProjectRoot,
        string relativePath)
    {
        var normalizedRelativePath = NormalizeNativeEquivalentRelativePathSpelling(
            fullProjectRoot,
            NormalizePathSeparators(relativePath));
        var canonicalLexicalPath = normalizedRelativePath == "."
            ? fullProjectRoot
            : Path.GetFullPath(Path.Combine(
                fullProjectRoot,
                NormalizeRelativePathForCurrentPlatform(normalizedRelativePath)));
        return new NativeProjectPathMatch(normalizedRelativePath, canonicalLexicalPath);
    }

    private static string NormalizeNativeEquivalentRelativePathSpelling(
        string fullProjectRoot,
        string relativePath)
    {
        if (!OperatingSystem.IsWindows()
            || relativePath == "."
            || relativePath.IndexOf('~') < 0)
            return relativePath;

        var components = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
            return relativePath;

        var normalizedComponents = new string[components.Length];
        var lexicalProbe = fullProjectRoot;
        for (var i = 0; i < components.Length; i++)
        {
            lexicalProbe = Path.Combine(lexicalProbe, components[i]);
            if (!TryGetWindowsLongPathName(lexicalProbe, out var longPath))
            {
                Array.Copy(components, i, normalizedComponents, i, components.Length - i);
                break;
            }

            // GetLongPathName expands 8.3 names without resolving a reparse-point target.
            // Keep only this lexical component so later symlink-policy checks still inspect
            // the selected path, and preserve the first missing component and its suffix.
            // GetLongPathName は reparse-point の target を解決せず 8.3 名だけを展開する。
            // この lexical component のみを採用して後続の symlink-policy 検査を維持し、
            // 最初の missing component 以降は選択された表記のまま保持する。
            normalizedComponents[i] = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(longPath));
        }

        return NormalizePathSeparators(string.Join(Path.DirectorySeparatorChar, normalizedComponents));
    }

    internal static string? TryGetRelativePathFromProjectRootPrefix(string projectRoot, string path)
        => TryGetRelativePathFromDirectoryPrefix(projectRoot, path);

    internal static string GetRelativePathFromDirectory(string directory, string path) =>
        TryGetRelativePathFromDirectoryPrefix(directory, path) ?? Path.GetRelativePath(directory, path);

    internal static string? TryGetRelativePathFromDirectoryPrefix(string directory, string path)
        => TryGetRelativePathFromDirectoryPrefix(
            directory,
            path,
            IsWindowsPlatform ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? TryGetRelativePathFromDirectoryPrefix(
        string directory,
        string path,
        StringComparison comparison)
    {
        var root = Path.TrimEndingDirectorySeparator(directory);
        if (root.Length == 0)
            return null;

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

    private static bool NativeLexicalDirectorySpellingsMatch(string projectRoot, string candidate)
    {
        var normalizedProjectRoot = NormalizeNativeLexicalDirectorySpelling(projectRoot);
        var normalizedCandidate = NormalizeNativeLexicalDirectorySpelling(candidate);
        if (!CodeIndex.Cli.PathCasing.PathsEqualByDirectoryNamespace(
                normalizedProjectRoot,
                normalizedCandidate))
            return false;

        // String equivalence alone would collapse two distinct NFC/NFD directories on a
        // normalization-sensitive mount. Require the live directory identity as proof.
        // normalization-sensitive mount 上の別 directory を文字列正規化だけで同一視しないよう、
        // live directory identity の一致も必須とする。
        return TryGetFileIdentity(projectRoot, out var projectRootIdentity)
            && TryGetFileIdentity(candidate, out var candidateIdentity)
            && projectRootIdentity == candidateIdentity;
    }

    private static string NormalizeNativeLexicalDirectorySpelling(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsMacOS())
            return fullPath.Normalize(NormalizationForm.FormC);
        if (OperatingSystem.IsWindows()
            && TryGetWindowsLongPathName(fullPath, out var longPath))
        {
            return Path.TrimEndingDirectorySeparator(longPath);
        }

        return fullPath;
    }

    private static bool TryGetWindowsLongPathName(string path, out string longPath)
    {
        longPath = string.Empty;
        try
        {
            var capacity = 512;
            while (capacity <= short.MaxValue)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetLongPathName(
                    LongPath.EnsureWindowsPrefix(path),
                    buffer,
                    (uint)buffer.Capacity);
                if (length == 0)
                    return false;
                if (length < buffer.Capacity)
                {
                    longPath = Path.GetFullPath(LongPath.RemoveWindowsPrefix(buffer.ToString()));
                    return true;
                }

                capacity = checked((int)length + 1);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   or EntryPointNotFoundException
                                   or ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or OverflowException)
        {
        }

        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(
        string shortPath,
        StringBuilder longPath,
        uint bufferLength);

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
