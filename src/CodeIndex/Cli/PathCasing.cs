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
        var anchor = !string.IsNullOrEmpty(left) ? left : right;
        return string.Equals(left, right, ComparisonFor(anchor));
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
        var comparison = ComparisonFor(normalizedParent);
        if (string.Equals(normalizedParent, normalizedChild, comparison))
            return true;

        var trimmedParent = normalizedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedParent.Length == 0)
            return Path.IsPathFullyQualified(normalizedChild);
        return normalizedChild.StartsWith(trimmedParent + Path.DirectorySeparatorChar, comparison)
            || normalizedChild.StartsWith(trimmedParent + Path.AltDirectorySeparatorChar, comparison);
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
        var anchor = ResolveAnchor(workspaceRoot);
        _ignoreCaseByAnchor[anchor] = ignoreCase;
    }

    internal static void ResetCacheForTests() => _ignoreCaseByAnchor.Clear();

    internal static Func<string, bool>? IgnoreCaseProbeForTesting { get; set; }

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

            if (Directory.Exists(anchor) && TryCreateCaseVariant(anchor, out var variant))
                return Directory.Exists(variant);

            using var probe = CaseSensitivityProbeDirectory.CreateProbePathScope(anchor, "case-probe-");
            var probePath = probe.Path;
            FileWriteProbe.WriteEmptyFile(probePath);
            try
            {
                if (TryCreateCaseVariant(probePath, out var probeVariant))
                    return File.Exists(LongPath.EnsureWindowsPrefix(probeVariant));
            }
            finally
            {
                FileWriteProbe.DeleteFileIfExists(probePath);
            }

            throw new CaseSensitivityProbeException(
                "Failed to create a case-variant path for filesystem case-sensitivity probing.",
                anchor,
                probePath: probePath);
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

    private static bool TryCreateCaseVariant(string path, out string variant)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var ch = chars[i];
            if (!char.IsLetter(ch))
                continue;
            chars[i] = char.IsUpper(ch)
                ? char.ToLowerInvariant(ch)
                : char.ToUpperInvariant(ch);
            variant = new string(chars);
            return true;
        }

        variant = path;
        return false;
    }
}
