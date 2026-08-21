using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static bool HasUnknownLanguageMapping(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.IsNullOrEmpty(extension)
            || (!LangMap.ContainsKey(extension)
                && !ExtractorPluginRegistry.TryGetLanguageForExtension(extension, out _));
    }

    private bool IsInternalIndexArtifactPath(string relativePath)
    {
        if (_internalIndexDatabaseRelativePath is null)
            return false;

        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (relativePath.Equals(_internalIndexDatabaseRelativePath, comparison))
            return true;
        if (!relativePath.StartsWith(_internalIndexDatabaseRelativePath, comparison))
            return false;

        var suffix = relativePath[_internalIndexDatabaseRelativePath.Length..];
        return suffix.Equals("-wal", comparison)
            || suffix.Equals("-shm", comparison)
            || suffix.Equals("-journal", comparison)
            || suffix.Equals(".lock", comparison)
            || suffix.Equals(".lock.info", comparison)
            || suffix.Equals(".checkpoints", comparison)
            || suffix.StartsWith(".checkpoints/", comparison)
            || suffix.StartsWith(".restore-tmp-", comparison)
            || suffix.StartsWith(".restore-backup-", comparison);
    }

    private static string? ResolveInternalIndexDatabaseRelativePath(
        string projectRoot,
        string? internalIndexDatabasePath)
    {
        if (string.Equals(internalIndexDatabasePath, ":memory:", StringComparison.Ordinal))
            return null;

        var configuredPath = internalIndexDatabasePath
            ?? Path.Combine(projectRoot, CaseSensitivityProbeDirectory.DataDirectoryName, "codeindex.db");
        var absolutePath = Path.IsPathFullyQualified(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(projectRoot, configuredPath));
        var relativePath = NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
        return relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            ? null
            : relativePath;
    }

    private PathFilterKind GetDirectoryFilterKind(
        string dir,
        string relativeDir,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false)
    {
        if (!isProjectRoot)
        {
            if (IsInternalIndexArtifactPath(relativeDir))
                return PathFilterKind.ExcludedByDefaultDirectory;

            var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir.AsSpan()));
            if (IsDefaultExcludedDirectoryName(dirName) && !IsSubmoduleOrAncestor(relativeDir))
                return PathFilterKind.ExcludedByDefaultDirectory;
        }

        return activeIgnoreRules.IsIgnored(dir, isDirectory: true)
            ? PathFilterKind.IgnoredByRules
            : PathFilterKind.None;
    }

    // True when relpath under _projectRoot matches a .gitmodules-declared submodule
    // working-tree path or one of its ancestor directories. Allows the walker to
    // descend through SkipDirs-named ancestors (e.g. vendor/) to reach declared
    // submodules without dropping the broader SkipDirs policy elsewhere.
    // _projectRoot 配下の相対パスが .gitmodules で宣言された submodule のワークツリーまたは
    // その祖先ディレクトリに一致するときに true。vendor/ のような SkipDirs 名の祖先を
    // 通過して submodule に到達できるよう、限定的に SkipDirs を上書きする。
    private bool IsSubmoduleOrAncestor(string relativePath)
    {
        if (_submodulePaths.Count == 0)
            return false;
        if (relativePath.Length == 0)
            return false;
        return _submodulePaths.Contains(relativePath) || _submoduleAncestorPaths.Contains(relativePath);
    }

    private bool IsSubmoduleAncestorPassthrough(string relativePath)
    {
        if (_submoduleAncestorPaths.Count == 0)
            return false;
        if (relativePath.Length == 0)
            return false;
        if (_submodulePaths.Contains(relativePath))
            return false;
        if (!_submoduleAncestorPaths.Contains(relativePath))
            return false;
        // Passthrough propagates from any SkipDirs-named ancestor along the path. If no
        // segment of relativePath matches SkipDirs, this directory would have been walked
        // normally without our override, so the override is not in effect here.
        // SkipDirs 名の祖先からは下方向に passthrough を伝播する。relativePath のどの segment も
        // SkipDirs に該当しない場合、我々の上書き無しでも walker は通っていたはずなので
        // ここでの上書きは効いていない。
        var remaining = relativePath.AsSpan();
        while (!remaining.IsEmpty)
        {
            var separatorIndex = remaining.IndexOf('/');
            var segment = separatorIndex >= 0 ? remaining[..separatorIndex] : remaining;
            if (!segment.IsEmpty && IsDefaultExcludedDirectoryName(segment))
                return true;
            if (separatorIndex < 0)
                break;
            remaining = remaining[(separatorIndex + 1)..];
        }

        return false;
    }
}
