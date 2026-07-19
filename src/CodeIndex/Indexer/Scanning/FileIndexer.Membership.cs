namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal enum IndexInputInvalidationKind
    {
        None,
        MembershipRules,
        ExtractorConfiguration,
    }

    /// <summary>
    /// Classifies non-source inputs whose changes require a workspace reconciliation. These
    /// paths are excluded from ordinary source membership, but watch/update callers must retain
    /// them long enough to trigger a full scan after the debounce window.
    /// 通常の source membership からは除外するが、debounce 後の full scan を起動するために
    /// watch/update caller が保持すべき非 source 入力を分類する。
    /// </summary>
    internal static IndexInputInvalidationKind ClassifyIndexInputInvalidation(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(path))
            return IndexInputInvalidationKind.None;

        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(fullProjectRoot, path));

        if (IsIgnoreFilePath(fullPath))
        {
            var ignoreDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(ignoreDirectory)
                && (IsPathEqualOrParent(ignoreDirectory, fullProjectRoot)
                    || IsPathEqualOrParent(fullProjectRoot, ignoreDirectory)))
            {
                return IndexInputInvalidationKind.MembershipRules;
            }
        }

        var relativePath = NormalizePathSeparators(GetRelativePathFromProjectRoot(fullProjectRoot, fullPath));
        if (IsOutsideProjectRelativePath(relativePath))
            return IndexInputInvalidationKind.None;

        return IsSameOrUnderRelativeDirectory(relativePath, ".cdidx/patterns")
            || IsSameOrUnderRelativeDirectory(relativePath, ".cdidx/plugins")
                ? IndexInputInvalidationKind.ExtractorConfiguration
                : IndexInputInvalidationKind.None;
    }

    private static bool IsOutsideProjectRelativePath(string relativePath)
        => relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal);

    private static bool IsSameOrUnderRelativeDirectory(string relativePath, string directory)
        => relativePath.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
}
