using System.Collections.Concurrent;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Probe and cache the actual case-sensitivity of the filesystem hosting a given path,
/// so that path-equality / parent-prefix checks no longer depend solely on the
/// <see cref="OperatingSystem"/> family heuristic. Case-sensitive APFS volumes on
/// macOS, case-sensitive NTFS mounts (WSL / dev-drive), and case-sensitive ReFS are
/// the motivating cases — `OperatingSystem.IsWindows()`-keyed comparisons would
/// otherwise either collapse distinct files (`Foo.cs` vs `foo.cs`) or fail to detect
/// equivalent paths (Windows OrdinalIgnoreCase applied to a case-sensitive volume).
/// 指定パスのファイルシステムが大小区別するかを実際に試して判定・キャッシュする。OS 系列だけに
/// 依存した PathsEqual / IsPathEqualOrParent では、case-sensitive APFS（macOS）、WSL の
/// case-sensitive NTFS、ReFS のような実 FS と乖離した結果になるため、これを補正する。
/// </summary>
internal static class PathCasing
{
    private static readonly ConcurrentDictionary<string, bool> _ignoreCaseByAnchor =
        new(StringComparer.Ordinal);
    private static readonly AsyncLocal<Func<string, bool>?> _ignoreCaseProbeOverride = new();

    /// <summary>
    /// True when the filesystem at <paramref name="referencePath"/> treats names as
    /// case-insensitive. Probe failures are surfaced explicitly instead of falling back
    /// to OS heuristics. Cached per anchor directory so repeated comparisons on the
    /// same workspace probe at most once.
    /// 指定パスを抱える FS が case-insensitive なら true。アンカー（最寄り既存ディレクトリ）
    /// ごとに 1 回プローブし結果をキャッシュする。判定不能時は OS 系列にフォールバックせず
    /// 明示的な失敗として返す。
    /// </summary>
    public static bool IsIgnoreCase(string referencePath)
    {
        var anchor = ResolveAnchor(referencePath);
        if (_ignoreCaseByAnchor.TryGetValue(anchor, out var cachedIgnoreCase))
            return cachedIgnoreCase;

        if (IgnoreCaseProbeForTesting is not null)
            return ProbeIgnoreCase(anchor);

        return _ignoreCaseByAnchor.GetOrAdd(anchor, ProbeIgnoreCase);
    }

    public static StringComparison ComparisonFor(string referencePath)
        => IsIgnoreCase(referencePath)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return ReferenceEquals(left, right);

        // These conclusions are invariant under both Ordinal and OrdinalIgnoreCase,
        // so avoid a filesystem probe unless casing can actually change the result.
        // Ordinal / OrdinalIgnoreCase のどちらでも結論が同じ場合は FS probe を避ける。
        if (left.Length != right.Length)
            return false;
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;

        return string.Equals(left, right, ComparisonFor(left));
    }

    /// <summary>
    /// Compare absolute path membership component-by-component. A case-only component is
    /// equivalent only when both parent directory namespaces are case-insensitive and their
    /// directory identities agree. Existing intermediate components must also identify the
    /// same directory; a missing leaf may use its proven parent namespace, while a missing
    /// intermediate path fails closed.
    /// absolute path の membership を component 単位で比較する。case-only component は、
    /// 両側の親 directory namespace が case-insensitive で親 identity も一致する場合だけ
    /// 同値とする。既存の中間 component も同一 directory identity を必須とし、missing leaf
    /// は証明済みの親 namespace を利用できるが、missing 中間 path は fail closed とする。
    /// </summary>
    internal static bool PathsEqualByDirectoryNamespace(string left, string right)
        => PathsEqualByDirectoryNamespace(
            left,
            right,
            IsIgnoreCase,
            TryGetDirectoryIdentity);

    internal static bool PathsEqualByDirectoryNamespaceForTesting(
        string left,
        string right,
        Func<string, bool> directoryIgnoreCase,
        Func<string, FileIndexer.FileIdentity?> directoryIdentity)
        => PathsEqualByDirectoryNamespace(
            left,
            right,
            directoryIgnoreCase,
            directoryIdentity);

    /// <summary>
    /// Return true when <paramref name="parent"/> is the same path as
    /// <paramref name="child"/> or an ancestor of it. Case-only prefix components are
    /// accepted only when their parent directory namespaces are the same case-insensitive
    /// namespace and both spellings identify the same child directory. This prevents a
    /// case-insensitive project mount from making a distinct case-only sibling in its
    /// case-sensitive parent namespace appear internal.
    /// <paramref name="parent"/> が <paramref name="child"/> と同一、または祖先なら true を
    /// 返す。case-only な prefix component は、その親が同一の case-insensitive directory
    /// namespace で、両表記が同一 child directory を指す場合だけ受理する。これにより、
    /// case-insensitive な project mount の policy で、case-sensitive な親 namespace にある
    /// case-only sibling を内部 path と誤認しない。
    /// </summary>
    internal static bool IsPathEqualOrParentByDirectoryNamespace(string parent, string child)
        => IsPathEqualOrParentByDirectoryNamespace(
            parent,
            child,
            IsIgnoreCase,
            TryGetDirectoryIdentity);

    internal static bool IsPathEqualOrParentByDirectoryNamespaceForTesting(
        string parent,
        string child,
        Func<string, bool> directoryIgnoreCase,
        Func<string, FileIndexer.FileIdentity?> directoryIdentity)
        => IsPathEqualOrParentByDirectoryNamespace(
            parent,
            child,
            directoryIgnoreCase,
            directoryIdentity);

    private static bool IsPathEqualOrParentByDirectoryNamespace(
        string parent,
        string child,
        Func<string, bool> directoryIgnoreCase,
        Func<string, FileIndexer.FileIdentity?> directoryIdentity)
    {
        var fullParent = NormalizeBoundaryPath(parent);
        var fullChild = NormalizeBoundaryPath(child);
        if (string.Equals(fullParent, fullChild, StringComparison.Ordinal))
            return true;

        var parentRoot = Path.GetPathRoot(fullParent);
        var childRoot = Path.GetPathRoot(fullChild);
        if (string.IsNullOrEmpty(parentRoot)
            || string.IsNullOrEmpty(childRoot)
            || !string.Equals(
                parentRoot,
                childRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return false;
        }

        var parentComponents = SplitPathComponents(fullParent, parentRoot.Length);
        var childComponents = SplitPathComponents(fullChild, childRoot.Length);
        if (parentComponents.Length > childComponents.Length)
            return false;

        var parentNamespace = parentRoot;
        var childNamespace = childRoot;
        for (var index = 0; index < parentComponents.Length; index++)
        {
            var parentComponent = parentComponents[index];
            var childComponent = childComponents[index];
            var parentEntry = Path.Combine(parentNamespace, parentComponent);
            var childEntry = Path.Combine(childNamespace, childComponent);
            if (!string.Equals(parentComponent, childComponent, StringComparison.Ordinal))
            {
                if (!string.Equals(parentComponent, childComponent, StringComparison.OrdinalIgnoreCase))
                    return false;

                var parentNamespaceIdentity = directoryIdentity(parentNamespace);
                var childNamespaceIdentity = directoryIdentity(childNamespace);
                var parentEntryIdentity = directoryIdentity(parentEntry);
                var childEntryIdentity = directoryIdentity(childEntry);
                if (parentNamespaceIdentity == null
                    || childNamespaceIdentity == null
                    || parentNamespaceIdentity != childNamespaceIdentity
                    || parentEntryIdentity == null
                    || childEntryIdentity == null
                    || parentEntryIdentity != childEntryIdentity
                    || !directoryIgnoreCase(parentNamespace)
                    || !directoryIgnoreCase(childNamespace))
                {
                    return false;
                }
            }

            parentNamespace = parentEntry;
            childNamespace = childEntry;
        }

        return true;
    }

    private static bool PathsEqualByDirectoryNamespace(
        string left,
        string right,
        Func<string, bool> directoryIgnoreCase,
        Func<string, FileIndexer.FileIdentity?> directoryIdentity)
    {
        var fullLeft = NormalizeBoundaryPath(left);
        var fullRight = NormalizeBoundaryPath(right);
        if (string.Equals(fullLeft, fullRight, StringComparison.Ordinal))
            return true;

        var leftRoot = Path.GetPathRoot(fullLeft);
        var rightRoot = Path.GetPathRoot(fullRight);
        if (string.IsNullOrEmpty(leftRoot)
            || string.IsNullOrEmpty(rightRoot)
            || !string.Equals(
                leftRoot,
                rightRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return false;
        }

        var leftComponents = SplitPathComponents(fullLeft, leftRoot.Length);
        var rightComponents = SplitPathComponents(fullRight, rightRoot.Length);
        if (leftComponents.Length != rightComponents.Length)
            return false;

        var leftParent = leftRoot;
        var rightParent = rightRoot;
        for (var index = 0; index < leftComponents.Length; index++)
        {
            var leftComponent = leftComponents[index];
            var rightComponent = rightComponents[index];
            var leftEntry = Path.Combine(leftParent, leftComponent);
            var rightEntry = Path.Combine(rightParent, rightComponent);
            if (!string.Equals(leftComponent, rightComponent, StringComparison.Ordinal))
            {
                if (!string.Equals(leftComponent, rightComponent, StringComparison.OrdinalIgnoreCase))
                    return false;

                var leftParentIdentity = directoryIdentity(leftParent);
                var rightParentIdentity = directoryIdentity(rightParent);
                if (leftParentIdentity == null
                    || rightParentIdentity == null
                    || leftParentIdentity != rightParentIdentity
                    || !directoryIgnoreCase(leftParent)
                    || !directoryIgnoreCase(rightParent))
                {
                    return false;
                }

                if (index < leftComponents.Length - 1)
                {
                    var leftEntryIdentity = directoryIdentity(leftEntry);
                    var rightEntryIdentity = directoryIdentity(rightEntry);
                    if (leftEntryIdentity == null
                        || rightEntryIdentity == null
                        || leftEntryIdentity != rightEntryIdentity)
                    {
                        return false;
                    }
                }
            }

            leftParent = leftEntry;
            rightParent = rightEntry;
        }

        return true;
    }

    private static string[] SplitPathComponents(string path, int rootLength)
        => path[rootLength..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static FileIndexer.FileIdentity? TryGetDirectoryIdentity(string path)
    {
        if (!Directory.Exists(path))
            return null;
        return FileIndexer.TryGetFileIdentity(path, out var identity) ? identity : null;
    }

    public static string NormalizeBoundaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.Ordinal))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsFullPathEqualOrParent(string parent, string child)
        => IsPathEqualOrParent(NormalizeBoundaryPath(parent), NormalizeBoundaryPath(child));

    /// <summary>
    /// Return true when <paramref name="normalizedChild"/> is the same path as
    /// <paramref name="normalizedParent"/> or a descendant of it, using the
    /// case-sensitivity probed from <paramref name="normalizedParent"/>'s filesystem.
    /// Both arguments must already be normalized via <see cref="Path.GetFullPath(string)"/>
    /// (and trailing separators trimmed) — this helper does no normalization itself.
    /// </summary>
    public static bool IsPathEqualOrParent(string normalizedParent, string normalizedChild)
    {
        if (string.Equals(normalizedParent, normalizedChild, StringComparison.Ordinal))
            return true;

        var trimmedParent = normalizedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedParent.Length == 0)
            return Path.IsPathFullyQualified(normalizedChild);

        var directoryPrefix = trimmedParent + Path.DirectorySeparatorChar;
        var alternateDirectoryPrefix = trimmedParent + Path.AltDirectorySeparatorChar;
        if (normalizedChild.StartsWith(directoryPrefix, StringComparison.Ordinal)
            || normalizedChild.StartsWith(alternateDirectoryPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        var comparison = ComparisonFor(normalizedParent);
        if (string.Equals(normalizedParent, normalizedChild, comparison))
            return true;

        return normalizedChild.StartsWith(directoryPrefix, comparison)
            || normalizedChild.StartsWith(alternateDirectoryPrefix, comparison);
    }

    /// <summary>
    /// Record a workspace-level ignoreCase decision (e.g. resolved via
    /// <c>core.ignorecase</c> + workspace probe) so subsequent comparisons rooted at the
    /// same anchor reuse it instead of running a second probe. Best-effort: failures are
    /// silently ignored.
    /// 既に算出済みの workspace 単位 ignoreCase をキャッシュに先取り登録する。
    /// </summary>
    public static void SeedFromWorkspace(string workspaceRoot, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(workspaceRoot))
            return;
        SeedFromReferencePath(workspaceRoot, ignoreCase);
        SeedFromReferencePath(Path.Combine(workspaceRoot, CaseSensitivityProbeDirectory.DataDirectoryName), ignoreCase);
    }

    public static void SeedFromReferencePath(string referencePath, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(referencePath))
            return;
        var anchor = ResolveAnchor(referencePath);
        _ignoreCaseByAnchor[anchor] = ignoreCase;
    }

    internal static void ResetCacheForTests() => _ignoreCaseByAnchor.Clear();

    internal static Func<string, bool>? IgnoreCaseProbeForTesting
    {
        get => _ignoreCaseProbeOverride.Value;
        set => _ignoreCaseProbeOverride.Value = value;
    }

    private static string ResolveAnchor(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Path.GetFullPath(".");

        try
        {
            var probe = Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
            for (var safety = 0; safety < 64; safety++)
            {
                if (string.IsNullOrEmpty(probe))
                    break;
                if (Directory.Exists(probe))
                    return probe;
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
                    break;
                probe = parent;
            }
        }
        catch
        {
            // Fall through to a path-root key.
        }

        return Path.GetPathRoot(path) is string root && !string.IsNullOrEmpty(root)
            ? root
            : path;
    }

    private static bool ProbeIgnoreCase(string anchor)
    {
        try
        {
            if (IgnoreCaseProbeForTesting is { } probeOverride)
                return probeOverride(anchor);

            if (Directory.Exists(anchor))
            {
                try
                {
                    if (CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(anchor) is { } existingChildIgnoreCase)
                        return existingChildIgnoreCase;
                }
                catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
                {
                    // Path-boundary comparisons may need to classify an unreadable child.
                    // Its name belongs to the parent namespace, so use that namespace's
                    // policy instead of trying to create a probe inside the unreadable child.
                    // path boundary 比較では unreadable child 自体の分類が必要になる。
                    // child 名は親 namespace に属するため、child 内への probe 作成ではなく
                    // 親 namespace の policy を使う。
                    var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(anchor));
                    if (!string.IsNullOrEmpty(parent)
                        && !string.Equals(parent, anchor, StringComparison.Ordinal))
                    {
                        return ProbeIgnoreCase(parent);
                    }

                    throw;
                }
            }

            return CaseSensitivityProbeDirectory.ProbeIgnoreCase(anchor, "case-probe-");
        }
        catch (CaseSensitivityProbeException ex)
        {
            throw CreateCaseSensitivityProbeException(anchor, ex);
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            throw CreateCaseSensitivityProbeException(anchor, ex);
        }
    }

    private static CodeIndexException CreateCaseSensitivityProbeException(string anchor, Exception innerException)
        => new(
            code: CommandErrorCodes.FileSystemCaseProbeFailed,
            category: CodeIndexExceptionCategory.Filesystem,
            message: "Failed to determine filesystem case sensitivity.",
            path: TryNormalizePathForError(anchor),
            hint: "Ensure the workspace and its .cdidx probe directory are readable and writable, then rerun the command.",
            innerException: innerException);

    private static string TryNormalizePathForError(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            return path;
        }
    }

    private static bool IsCaseSensitivityProbeFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

}
