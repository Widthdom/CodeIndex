using CodeIndex.Cli;
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
        int? initialFileCapacity = null,
        CancellationToken cancellationToken = default)
        => ScanFilesDetailedCore(
            checkpointedDirectories,
            continueOnError,
            initialFileCapacity,
            captureDirectoryListingSnapshots: false,
            cancellationToken: cancellationToken).ScanResult;

    internal ScanFilesWithDirectoryListingSnapshotsResult ScanFilesDetailedWithDirectoryListingSnapshots(
        IReadOnlySet<string>? checkpointedDirectories = null,
        bool continueOnError = true,
        int? initialFileCapacity = null,
        CancellationToken cancellationToken = default)
        => ScanFilesDetailedCore(
            checkpointedDirectories,
            continueOnError,
            initialFileCapacity,
            captureDirectoryListingSnapshots: true,
            cancellationToken: cancellationToken);

    private ScanFilesWithDirectoryListingSnapshotsResult ScanFilesDetailedCore(
        IReadOnlySet<string>? checkpointedDirectories,
        bool continueOnError,
        int? initialFileCapacity,
        bool captureDirectoryListingSnapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousSuppression = _suppressConfigurationInputObservation;
        _suppressConfigurationInputObservation = !captureDirectoryListingSnapshots;
        Volatile.Write(ref _projectMarkerScopeSnapshot, null);
        ResetPatternConfigurationDirectoryExistenceCache();
        _nestedGitRepositoryCache.Clear();
        try
        {
            return ScanFilesDetailedCoreWithConfigurationObservation(
                checkpointedDirectories,
                continueOnError,
                initialFileCapacity,
                captureDirectoryListingSnapshots,
                cancellationToken);
        }
        finally
        {
            _suppressConfigurationInputObservation = previousSuppression;
        }
    }

    private ScanFilesWithDirectoryListingSnapshotsResult ScanFilesDetailedCoreWithConfigurationObservation(
        IReadOnlySet<string>? checkpointedDirectories,
        bool continueOnError,
        int? initialFileCapacity,
        bool captureDirectoryListingSnapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedFileCapacity = ResolveInitialScanFileCapacity(initialFileCapacity);
        var resolvedDirectoryCapacity = ResolveInitialScanDirectoryCapacity(resolvedFileCapacity);
        var files = new List<string>(resolvedFileCapacity);
        var fileLanguages = new Dictionary<string, string>(resolvedFileCapacity, StringComparer.Ordinal);
        var languageCounts = new Dictionary<string, int>(InitialScanLanguageCapacity, StringComparer.Ordinal);
        var errors = new List<ScanError>(_submoduleLoadWarnings.Count);
        var listedDirectories = new HashSet<string>(resolvedDirectoryCapacity, StringComparer.Ordinal);
        var directoryListingSnapshots = captureDirectoryListingSnapshots
            ? new List<DirectoryListingSnapshot>(resolvedDirectoryCapacity)
            : null;
        var fullyScannedDirectories = new HashSet<string>(resolvedDirectoryCapacity, StringComparer.Ordinal);
        IReadOnlySet<string> activeCheckpointedDirectories = checkpointedDirectories is { Count: > 0 }
            ? new HashSet<string>(checkpointedDirectories, StringComparer.Ordinal)
            : EmptyCheckpointedDirectorySet;
        var visitedDirectories = new HashSet<string>(resolvedDirectoryCapacity, StringComparer.Ordinal)
        {
            NormalizePathForComparison(_projectRoot),
        };
        var projectMarkerTraversalStates = CreateDefaultProjectMarkerFingerprintTraversalStates();
        var projectMarkerScopeCollection = new ProjectMarkerScopeCollectionState(
            _ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var scanState = new DirectoryScanState(
            files,
            fileLanguages,
            languageCounts,
            errors,
            listedDirectories,
            directoryListingSnapshots,
            fullyScannedDirectories,
            activeCheckpointedDirectories,
            visitedDirectories,
            projectMarkerTraversalStates,
            projectMarkerScopeCollection,
            captureDirectoryListingSnapshots);
        errors.AddRange(_submoduleLoadWarnings);
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            ScanDirectory(_projectRoot, scanState, preloadResult.Rules, isProjectRoot: true, continueOnError, cancellationToken, depth: 0);
        }
        else
        {
            projectMarkerScopeCollection.IsComplete = false;
            MarkSharedProjectMarkerTraversalIncomplete(
                projectMarkerTraversalStates,
                "ancestor ignore-rule loading failed");
        }
        var inputSnapshot = captureDirectoryListingSnapshots
            ? MaterializeScanInputSnapshot(
                scanState.DirectoryListingSnapshots,
                scanState.DirectoryListingSnapshotsComplete,
                scanState.DirectoryListingSnapshotsIncompletePath,
                scanState.DirectoryListingSnapshotsIncompleteReason)
            : EmptyScanInputSnapshot;
        if (captureDirectoryListingSnapshots && !inputSnapshot.IsComplete)
        {
            scanState.Errors.Add(new ScanError(
                ToRelativePath(inputSnapshot.IncompletePath ?? _projectRoot),
                inputSnapshot.IncompleteReason is { Length: > 0 } incompleteReason
                    ? $"{incompleteReason} Reduce or split the indexed workspace before retrying."
                    : "Could not capture every directory listing or configuration input needed for one stable source snapshot; rerun indexing."));
        }
        if (projectMarkerScopeCollection.IsComplete)
        {
            Volatile.Write(
                ref _projectMarkerScopeSnapshot,
                new ProjectMarkerScopeSnapshot(projectMarkerScopeCollection.Directories));
        }

        var scanResult = new ScanFilesResult(
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
            ProjectMarkerFingerprints = MaterializeProjectMarkerFingerprintResults(projectMarkerTraversalStates),
        };
        return new ScanFilesWithDirectoryListingSnapshotsResult(scanResult, inputSnapshot);
    }

    private static IReadOnlyList<string> MaterializePathSet(HashSet<string>? paths)
        => paths is not { Count: > 0 } ? Array.Empty<string>() : paths.ToArray();

    private IReadOnlyList<string> MaterializeAncestorIgnoreDirectories()
        => _ancestorIgnoreDirectories.Count == 0
            ? Array.Empty<string>()
            : _ancestorIgnoreDirectories.ToArray();

    private static IReadOnlyDictionary<string, int> MaterializeLanguageCounts(Dictionary<string, int> counts)
        => counts.Count == 0
            ? EmptyLanguageCounts
            : new Dictionary<string, int>(counts, StringComparer.Ordinal);

    private static IReadOnlyList<string> MaterializeSortedPathSet(HashSet<string>? paths)
    {
        if (paths is not { Count: > 0 })
            return Array.Empty<string>();

        var sorted = paths.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    private static IReadOnlySet<string> MaterializeCheckpointedDirectorySet(
        IReadOnlySet<string> checkpointedDirectories,
        HashSet<string> fullyScannedDirectories)
    {
        if (checkpointedDirectories.Count == 0)
        {
            return fullyScannedDirectories.Count == 0
                ? EmptyCheckpointedDirectorySet
                : fullyScannedDirectories;
        }

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
        int depth = 0,
        DateTime? listingModifiedBeforeUtc = null,
        bool listingSnapshotAlreadyRecorded = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativeDir = ToRelativePath(dir);
        try
        {
            _pathAccessValidator?.Invoke(dir);
        }
        catch (IOException ex)
        {
            scanState.ProjectMarkerScopeCollection.IsComplete = false;
            MarkSharedProjectMarkerTraversalFailure(scanState.ProjectMarkerTraversalStates, dir, ex);
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Could not scan directory due to {FileSystemTraversalFailure.DescribeReason(ex)}."));
            return false;
        }

        if (depth > MaxDirectoryTraversalDepth)
        {
            scanState.ProjectMarkerScopeCollection.IsComplete = false;
            TruncateSharedProjectMarkerTraversal(
                scanState.ProjectMarkerTraversalStates,
                dir,
                $"directory traversal depth exceeded {MaxDirectoryTraversalDepth}");
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Skipped directory because traversal depth exceeded {MaxDirectoryTraversalDepth}. Check for symlink loops or unexpectedly deep generated trees.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (scanState.CheckpointedDirectories.Contains(relativeDir))
        {
            scanState.ProjectMarkerScopeCollection.IsComplete = false;
            TruncateSharedProjectMarkerTraversal(
                scanState.ProjectMarkerTraversalStates,
                dir,
                "a checkpointed directory was omitted from this scan");
            return true;
        }

        var filterKind = GetDirectoryFilterKind(dir, relativeDir, activeIgnoreRules, isProjectRoot);
        if (filterKind != PathFilterKind.None)
        {
            scanState.ListedDirectories.Add(relativeDir);
            scanState.FullyScannedDirectories.Add(relativeDir);
            return true;
        }

        RecordSharedProjectMarkerDirectoryVisit(scanState.ProjectMarkerTraversalStates, dir);

        return EnumerateDirectory(
            dir,
            relativeDir,
            scanState,
            activeIgnoreRules,
            continueOnError,
            cancellationToken,
            depth,
            listingModifiedBeforeUtc,
            listingSnapshotAlreadyRecorded);
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
