using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal PathFilterResult EvaluatePathFilter(string absolutePath, bool isDirectory = false)
    {
        var errors = new List<ScanError>();
        return EvaluatePathFilterCore(absolutePath, isDirectory, errors);
    }

    internal bool ShouldSkipPath(string absolutePath, bool isDirectory = false) =>
        EvaluatePathFilterCore(absolutePath, isDirectory, errors: null).ShouldSkip;

    private PathFilterResult EvaluatePathFilterCore(
        string absolutePath,
        bool isDirectory,
        List<ScanError>? errors)
    {
        if (TryEvaluatePathFilterPrefix(absolutePath, errors, out var fullPath, out var relativePath) is { } prefixResult)
            return prefixResult;
        if (TryLoadRootPathFilterRules(errors, isDirectory, out var activeIgnoreRules) is { } rootResult)
            return rootResult;

        if (relativePath.Length == 0 || relativePath == ".")
            return CreatePathFilterResult(PathFilterKind.None, errors);

        var directoryResult = EvaluatePathFilterDirectorySegments(
            relativePath,
            isDirectory,
            errors,
            activeIgnoreRules,
            out var leafIgnoreRules,
            out var inSubmodulePassthrough);
        if (directoryResult != null)
            return directoryResult.Value;

        if (isDirectory)
            return CreatePathFilterResult(PathFilterKind.None, errors);

        return EvaluatePathFilterLeafFile(fullPath, errors, leafIgnoreRules, inSubmodulePassthrough);
    }

    private PathFilterResult? TryEvaluatePathFilterPrefix(
        string absolutePath,
        List<ScanError>? errors,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        if (!IsFilePathSyntaxIndexable(absolutePath))
        {
            errors?.Add(new ScanError(
                FormatPathForScanIssue(absolutePath),
                "Skipped file because its path contains NUL or control characters.",
                ScanIssueSeverity.Warning));
            return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultFile, errors);
        }

        fullPath = Path.GetFullPath(absolutePath);
        if (!IsPathEqualOrParent(_projectRoot, fullPath))
            return CreatePathFilterResult(PathFilterKind.OutsideProjectRoot, errors);

        relativePath = NormalizeIgnorePath(GetRelativePathFromProjectRoot(_projectRoot, fullPath));
        if (relativePath.StartsWith("../", StringComparison.Ordinal))
            return CreatePathFilterResult(PathFilterKind.None, errors);

        return null;
    }

    private PathFilterResult? TryLoadRootPathFilterRules(
        List<ScanError>? errors,
        bool isDirectory,
        out IgnoreRuleSet activeIgnoreRules)
    {
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        activeIgnoreRules = preloadResult.Rules;
        if (!preloadResult.IgnoreRulesAvailable)
            return CreatePathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        var projectRootFilterKind = GetDirectoryFilterKind(
            _projectRoot,
            string.Empty,
            activeIgnoreRules,
            isProjectRoot: true);
        return projectRootFilterKind != PathFilterKind.None
            ? CreatePathFilterResult(projectRootFilterKind, errors)
            : null;
    }

    private PathFilterResult? EvaluatePathFilterDirectorySegments(
        string relativePath,
        bool isDirectory,
        List<ScanError>? errors,
        IgnoreRuleSet activeIgnoreRules,
        out IgnoreRuleSet leafIgnoreRules,
        out bool inSubmodulePassthrough)
    {
        var currentDirectory = _projectRoot;
        var fullyScanned = true;
        var loadResult = LoadIgnoreRulesForDirectory(currentDirectory, activeIgnoreRules, errors, ref fullyScanned);
        leafIgnoreRules = loadResult.Rules;
        inSubmodulePassthrough = false;
        if (!loadResult.IgnoreRulesAvailable)
            return CreatePathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

        // Mirror EnumerateDirectory's passthrough behavior so update-mode filters (--files /
        // --commits) match a fresh full scan: when SkipDirs is overridden because we're
        // routing toward a declared submodule, files/subdirs that do not themselves lead
        // to a submodule must still be excluded.
        // EnumerateDirectory の passthrough と挙動を一致させ、--files / --commits などの
        // 更新モードのフィルタがフルスキャンと食い違わないようにする。submodule への通過のため
        // SkipDirs を上書きした場合でも、submodule に到達しないファイル・サブディレクトリは
        // 引き続き除外する。
        var directoryPathLength = isDirectory ? relativePath.Length : relativePath.LastIndexOf('/');
        if (directoryPathLength < 0)
            directoryPathLength = 0;

        var cumulativeRelPath = string.Empty;
        for (var segmentStart = 0; segmentStart < directoryPathLength;)
        {
            var slashIndex = relativePath.IndexOf('/', segmentStart, directoryPathLength - segmentStart);
            var segmentEnd = slashIndex >= 0 ? slashIndex : directoryPathLength;
            if (segmentEnd == segmentStart)
            {
                segmentStart++;
                continue;
            }

            var directoryName = relativePath.Substring(segmentStart, segmentEnd - segmentStart);
            var childDirectory = Path.Combine(currentDirectory, directoryName);
            cumulativeRelPath = cumulativeRelPath.Length == 0
                ? directoryName
                : string.Concat(cumulativeRelPath, "/", directoryName);
            var isSubmodule = _submodulePaths.Contains(cumulativeRelPath);
            var isSubmoduleAncestor = _submoduleAncestorPaths.Contains(cumulativeRelPath);

            if (IsNestedGitRepository(childDirectory) && !isSubmodule && !isSubmoduleAncestor)
                return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);

            if (SkipDirs.Contains(directoryName))
            {
                if (!isSubmodule && !isSubmoduleAncestor)
                    return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);
            }
            else if (inSubmodulePassthrough && !isSubmodule && !isSubmoduleAncestor)
            {
                return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);
            }

            if (isSubmodule)
                inSubmodulePassthrough = false;
            else if (isSubmoduleAncestor)
                inSubmodulePassthrough = true;

            if (leafIgnoreRules.IsIgnored(childDirectory, isDirectory: true))
                return CreatePathFilterResult(PathFilterKind.IgnoredByRules, errors);

            currentDirectory = childDirectory;
            fullyScanned = true;
            loadResult = LoadIgnoreRulesForDirectory(currentDirectory, leafIgnoreRules, errors, ref fullyScanned);
            leafIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return CreatePathFilterResult(PathFilterKind.IgnoreRulesUnavailable, errors);

            segmentStart = segmentEnd + 1;
        }

        return null;
    }

    private PathFilterResult EvaluatePathFilterLeafFile(
        string fullPath,
        List<ScanError>? errors,
        IgnoreRuleSet activeIgnoreRules,
        bool inSubmodulePassthrough)
    {
        // File directly inside a submodule-ancestor passthrough directory: walker would not
        // index it, so neither should this filter.
        // submodule 祖先（passthrough）に直接置かれているファイルは walker も索引しないため
        // ここでも除外する。
        if (inSubmodulePassthrough)
            return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultDirectory, errors);

        var fileName = Path.GetFileName(fullPath.AsSpan());
        if (IsDefaultExcludedFileName(fileName))
            return CreatePathFilterResult(PathFilterKind.ExcludedByDefaultFile, errors);

        return activeIgnoreRules.IsIgnored(fullPath, isDirectory: false)
            ? CreatePathFilterResult(PathFilterKind.IgnoredByRules, errors)
            : CreatePathFilterResult(PathFilterKind.None, errors);
    }

    private static PathFilterResult CreatePathFilterResult(PathFilterKind filterKind, List<ScanError>? errors) =>
        new(filterKind, errors is null ? Array.Empty<ScanError>() : errors);
}
