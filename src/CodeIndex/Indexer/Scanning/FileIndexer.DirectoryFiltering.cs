using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static bool HasUnknownExtension(string filePath)
    {
        var extension = Path.GetExtension(Path.GetFileName(filePath));
        return !string.IsNullOrEmpty(extension)
            && !LangMap.ContainsKey(extension)
            && !ExtractorPluginRegistry.LanguageExtensions.ContainsKey(extension);
    }

    private static bool IsInternalIndexArtifactPath(string relativePath)
        => relativePath.Equals(".cdidx", StringComparison.Ordinal)
            || relativePath.StartsWith(".cdidx/", StringComparison.Ordinal);

    private PathFilterKind GetDirectoryFilterKind(
        string dir,
        string relativeDir,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false)
    {
        if (!isProjectRoot)
        {
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
