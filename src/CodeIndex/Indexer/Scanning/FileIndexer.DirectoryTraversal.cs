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
        var subdirectories = CreateSubdirectoryCandidates(
            CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir)));
        return ProcessSubdirectories(
            subdirectories,
            scanState,
            activeIgnoreRules,
            passthrough,
            continueOnError,
            cancellationToken,
            depth);
    }

    private static IEnumerable<ScannedSubdirectory> CreateSubdirectoryCandidates(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            yield return new ScannedSubdirectory(
                LongPath.RemoveWindowsPrefix(path),
                KnownAttributes: null);
        }
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
            var subDir = subdirectory.Path;
            var knownAttributes = subdirectory.KnownAttributes;
            if (TryRecordNonRecursiveSubdirectory(subDir, scanState, passthrough))
                continue;

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
            if (ShouldSkipDirectoryLink(subDir, scanState.Errors, scanState.DanglingSymlinks, knownAttributes))
            {
                RecordPrunedDirectory(subDir, scanState);
                continue;
            }

            var resolvedSubDir = NormalizePathForComparison(GetDirectoryTraversalIdentity(subDir, knownAttributes));
            if (!scanState.VisitedDirectories.Add(resolvedSubDir))
            {
                var subRelative = ToRelativePath(subDir);
                scanState.Errors.Add(new ScanError(subRelative, "Skipped symlinked directory because its resolved target was already scanned.", ScanIssueSeverity.Warning));
                RecordPrunedDirectory(subDir, scanState);
                continue;
            }

            var childFullyScanned = ScanDirectory(subDir, scanState, activeIgnoreRules, continueOnError: continueOnError, cancellationToken: cancellationToken, depth: depth + 1);
            fullyScanned &= childFullyScanned;
            if (!continueOnError && !childFullyScanned)
                break;
        }

        return fullyScanned;
    }

    private bool TryRecordNonRecursiveSubdirectory(string subDir, DirectoryScanState scanState, bool passthrough)
    {
        string? subRelative = null;
        if (IsNestedGitRepository(subDir))
        {
            subRelative = ToRelativePath(subDir);
            if (!IsSubmoduleOrAncestor(subRelative))
            {
                scanState.ListedDirectories.Add(subRelative);
                scanState.FullyScannedDirectories.Add(subRelative);
                scanState.NestedRepositories.Add(subRelative);
                return true;
            }
        }

        // In passthrough mode, only descend into subdirectories that are themselves
        // submodules or submodule ancestors. Treat siblings the same way SkipDirs
        // would have treated them at this point.
        // passthrough 中は、submodule 自体または submodule の祖先に該当する
        // サブディレクトリのみ降りる。その他は本来 SkipDirs で止まっていた扱いに戻す。
        if (passthrough)
        {
            subRelative ??= ToRelativePath(subDir);
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
        scanState.AttributePrunedDirectories.Add(relativeDir);
    }
}
