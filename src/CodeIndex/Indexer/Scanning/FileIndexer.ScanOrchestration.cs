using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    /// <summary>
    /// Enumerate all indexable files under the project root.
    /// プロジェクトルート以下のインデックス対象ファイルを列挙する。
    /// </summary>
    public IReadOnlyList<string> ScanFiles()
        => ScanFilesDetailed().Files;

    internal ScanFilesResult ScanFilesDetailed(
        IReadOnlySet<string>? checkpointedDirectories = null,
        bool continueOnError = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<string>();
        var fileLanguages = new Dictionary<string, string>(StringComparer.Ordinal);
        var languageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<ScanError>(_submoduleLoadWarnings.Count);
        var nonIndexablePaths = new HashSet<string>(StringComparer.Ordinal);
        var unknownExtensionFiles = new HashSet<string>(StringComparer.Ordinal);
        var probeFailedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var fullyScannedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var activeCheckpointedDirectories = checkpointedDirectories is { Count: > 0 }
            ? new HashSet<string>(checkpointedDirectories, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var attributePrunedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var nestedRepositories = new HashSet<string>(StringComparer.Ordinal);
        var danglingSymlinks = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIdentity>();
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal) { NormalizePathForComparison(_projectRoot) };
        var scanState = new DirectoryScanState(
            files,
            fileLanguages,
            languageCounts,
            errors,
            nonIndexablePaths,
            unknownExtensionFiles,
            probeFailedFilePaths,
            listedDirectories,
            fullyScannedDirectories,
            activeCheckpointedDirectories,
            attributePrunedDirectories,
            nestedRepositories,
            danglingSymlinks,
            visitedFileIdentities,
            visitedDirectories);
        errors.AddRange(_submoduleLoadWarnings);
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            ScanDirectory(_projectRoot, scanState, preloadResult.Rules, isProjectRoot: true, continueOnError, cancellationToken, depth: 0);
        }
        return new ScanFilesResult(
            scanState.Results,
            scanState.FileLanguages,
            scanState.Errors,
            MaterializePathSet(scanState.NonIndexablePaths),
            MaterializeSortedPathSet(scanState.UnknownExtensionFiles),
            MaterializePathSet(scanState.ProbeFailedFilePaths),
            MaterializePathSet(scanState.ListedDirectories),
            MaterializePathSet(scanState.FullyScannedDirectories),
            MaterializeCheckpointedDirectorySet(scanState.CheckpointedDirectories, scanState.FullyScannedDirectories),
            MaterializeAncestorIgnoreDirectories(),
            MaterializePathSet(scanState.AttributePrunedDirectories),
            MaterializeSortedPathSet(scanState.NestedRepositories),
            MaterializeSortedPathSet(scanState.DanglingSymlinks))
        {
            LanguageCounts = MaterializeLanguageCounts(scanState.LanguageCounts),
        };
    }

    private static IReadOnlyList<string> MaterializePathSet(HashSet<string> paths)
        => paths.Count == 0 ? Array.Empty<string>() : new List<string>(paths);

    private IReadOnlyList<string> MaterializeAncestorIgnoreDirectories()
        => _ancestorIgnoreDirectories.Count == 0
            ? Array.Empty<string>()
            : new List<string>(_ancestorIgnoreDirectories);

    private static IReadOnlyDictionary<string, int> MaterializeLanguageCounts(Dictionary<string, int> counts)
        => counts.Count == 0
            ? EmptyLanguageCounts
            : new Dictionary<string, int>(counts, StringComparer.Ordinal);

    private static IReadOnlyList<string> MaterializeSortedPathSet(HashSet<string> paths)
    {
        if (paths.Count == 0)
            return Array.Empty<string>();

        var sorted = new List<string>(paths);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static HashSet<string> MaterializeCheckpointedDirectorySet(
        HashSet<string> checkpointedDirectories,
        HashSet<string> fullyScannedDirectories)
    {
        var result = new HashSet<string>(
            checkpointedDirectories.Count + fullyScannedDirectories.Count,
            StringComparer.Ordinal);
        result.UnionWith(checkpointedDirectories);
        result.UnionWith(fullyScannedDirectories);
        return result;
    }

    private bool ScanDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool isProjectRoot = false,
        bool continueOnError = true,
        CancellationToken cancellationToken = default,
        int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativeDir = ToRelativePath(dir);

        if (depth > MaxDirectoryTraversalDepth)
        {
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Skipped directory because traversal depth exceeded {MaxDirectoryTraversalDepth}. Check for symlink loops or unexpectedly deep generated trees.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (scanState.CheckpointedDirectories.Contains(relativeDir))
            return true;

        var filterKind = GetDirectoryFilterKind(dir, relativeDir, activeIgnoreRules, isProjectRoot);
        if (filterKind != PathFilterKind.None)
        {
            scanState.ListedDirectories.Add(relativeDir);
            scanState.FullyScannedDirectories.Add(relativeDir);
            return true;
        }

        return EnumerateDirectory(dir, relativeDir, scanState, activeIgnoreRules, continueOnError, cancellationToken, depth);
    }

    private bool IsNestedGitRepository(string dir)
    {
        if (PathsEqual(dir, _projectRoot))
            return false;

        if (_nestedGitRepositoryCache.TryGetValue(dir, out var cached))
            return cached;

        var gitPath = LongPath.EnsureWindowsPrefix(Path.Combine(dir, ".git"));
        var isNestedRepository = Directory.Exists(gitPath) || File.Exists(gitPath);
        _nestedGitRepositoryCache[dir] = isNestedRepository;
        return isNestedRepository;
    }
}
