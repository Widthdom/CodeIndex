using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private readonly record struct ScannedSubdirectory(string Path, FileAttributes? KnownAttributes);

    private bool EnumerateSubdirectories(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        return ProcessSubdirectoryPaths(
            CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir)),
            scanState,
            activeIgnoreRules,
            passthrough,
            continueOnError,
            cancellationToken,
            depth);
    }

    private bool ProcessSubdirectories(
        IEnumerable<ScannedSubdirectory> subdirectories,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var fullyScanned = true;
        foreach (var subdirectory in subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProcessSubdirectory(
                    subdirectory.Path,
                    subdirectory.KnownAttributes,
                    scanState,
                    activeIgnoreRules,
                    passthrough,
                    continueOnError,
                    cancellationToken,
                    depth,
                    ref fullyScanned))
            {
                break;
            }
        }

        return fullyScanned;
    }

    private bool ProcessSubdirectoryPaths(
        IEnumerable<string> subdirectoryPaths,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var fullyScanned = true;
        foreach (var subdirectoryPath in subdirectoryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subDir = LongPath.RemoveWindowsPrefix(subdirectoryPath);
            if (!ProcessSubdirectory(
                    subDir,
                    knownAttributes: null,
                    scanState,
                    activeIgnoreRules,
                    passthrough,
                    continueOnError,
                    cancellationToken,
                    depth,
                    ref fullyScanned))
            {
                break;
            }
        }

        return fullyScanned;
    }

    private bool ProcessSubdirectory(
        string subDir,
        FileAttributes? knownAttributes,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth,
        ref bool fullyScanned)
    {
        if (TryRecordPassthroughSubdirectory(subDir, scanState, passthrough))
            return true;

        var subRelative = ToRelativePath(subDir);
        if (scanState.CheckpointedDirectories.Contains(subRelative))
            return true;

        // Skip directory symlinks/reparse points to prevent infinite recursion on ancestor loops
        // and duplicate indexing when a symlink points inside the same tree. On Windows, also
        // skip Hidden/System directories so drive-root scans do not descend into OS-owned caches.
        // Record the skipped directory itself as listed (for the immediate-parent purge path) AND
        // as a prune prefix so the purge walker can authoritatively drop deep descendants that
        // earlier runs left behind.
        // ディレクトリ symlink / reparse point は親方向ループでの無限再帰や、
        // ツリー内を指す symlink での二重 index を防ぐためスキップする。Windows では
        // drive root 走査で OS 管理 cache に降りないよう Hidden/System ディレクトリもスキップする。
        // skip したディレクトリ自身を listed 扱い（immediate parent purge 用）かつ prune prefix として
        // 記録することで、以前の実行でできた深い子孫エントリも purge walker が確実に削除できる。
        if (ShouldSkipDirectoryLink(subDir, scanState.Errors, scanState, knownAttributes))
        {
            RecordPrunedDirectory(subDir, scanState);
            return true;
        }

        if (GetDirectoryFilterKind(subDir, subRelative, activeIgnoreRules) != PathFilterKind.None)
        {
            scanState.ListedDirectories.Add(subRelative);
            scanState.FullyScannedDirectories.Add(subRelative);
            return true;
        }

        // Bind the child listing before probing its .git marker. A marker created after
        // a negative probe can otherwise become the baseline seen by child traversal.
        // childの.git判定より先にlistingを固定し、negative probe直後のmarker生成を
        // child走査側の新baselineとして取り込んでしまうraceを防ぐ。
        var childListingModifiedBeforeUtc = scanState.CaptureDirectoryListingSnapshots
            ? ReadDirectoryModifiedUtc(subDir)
            : (DateTime?)null;
        if (childListingModifiedBeforeUtc.HasValue)
            scanState.RecordDirectoryListingSnapshot(subDir, childListingModifiedBeforeUtc.Value);
        NestedRepositoryListingCapturedBeforeProbeForTesting?.Invoke(Path.Combine(subDir, ".git"));

        if (TryRecordNestedRepository(subDir, scanState))
            return true;

        var resolvedSubDir = NormalizePathForComparison(GetDirectoryTraversalIdentity(subDir, knownAttributes));
        if (!scanState.VisitedDirectories.Add(resolvedSubDir))
        {
            scanState.Errors.Add(new ScanError(subRelative, "Skipped symlinked directory because its resolved target was already scanned.", ScanIssueSeverity.Warning));
            RecordPrunedDirectory(subDir, scanState);
            return true;
        }

        var childFullyScanned = ScanDirectory(
            subDir,
            scanState,
            activeIgnoreRules,
            continueOnError: continueOnError,
            cancellationToken: cancellationToken,
            depth: depth + 1,
            listingModifiedBeforeUtc: childListingModifiedBeforeUtc,
            listingSnapshotAlreadyRecorded: childListingModifiedBeforeUtc.HasValue);
        fullyScanned &= childFullyScanned;
        return continueOnError || childFullyScanned;
    }

    private bool TryRecordNestedRepository(string subDir, DirectoryScanState scanState)
    {
        if (IsNestedGitRepository(subDir))
        {
            var subRelative = ToRelativePath(subDir);
            if (!IsSubmoduleOrAncestor(subRelative))
            {
                var markerPath = Path.Combine(subDir, ".git");
                NestedRepositoryDetectedBeforeSnapshotForTesting?.Invoke(markerPath);
                RecordObservedNestedRepositoryMarker(markerPath);
                scanState.ListedDirectories.Add(subRelative);
                scanState.FullyScannedDirectories.Add(subRelative);
                scanState.RecordNestedRepository(subRelative);
                return true;
            }
        }

        return false;
    }

    private bool TryRecordPassthroughSubdirectory(
        string subDir,
        DirectoryScanState scanState,
        bool passthrough)
    {
        // In passthrough mode, only descend into subdirectories that are themselves
        // submodules or submodule ancestors. Treat siblings the same way SkipDirs
        // would have treated them at this point.
        // passthrough 中は、submodule 自体または submodule の祖先に該当する
        // サブディレクトリのみ降りる。その他は本来 SkipDirs で止まっていた扱いに戻す。
        if (passthrough)
        {
            var subRelative = ToRelativePath(subDir);
            if (!IsSubmoduleOrAncestor(subRelative))
            {
                scanState.ListedDirectories.Add(subRelative);
                scanState.FullyScannedDirectories.Add(subRelative);
                return true;
            }
        }

        return false;
    }

    private void RecordPrunedDirectory(string dir, DirectoryScanState scanState)
    {
        var relativeDir = ToRelativePath(dir);
        scanState.ListedDirectories.Add(relativeDir);
        scanState.FullyScannedDirectories.Add(relativeDir);
        scanState.RecordAttributePrunedDirectory(relativeDir);
    }
}
