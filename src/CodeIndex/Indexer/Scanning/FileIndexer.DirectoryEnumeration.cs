using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private bool EnumerateDirectory(
        string dir,
        string relativeDir,
        DirectoryScanState scanState,
        IgnoreRuleSet inheritedIgnoreRules,
        bool continueOnError,
        CancellationToken cancellationToken = default,
        int depth = 0,
        DateTime? listingModifiedBeforeUtc = null,
        bool listingSnapshotAlreadyRecorded = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullyScanned = true;
        try
        {
            // Capture the parent listing before any membership configuration is probed.
            // This binds an initially absent ignore file that appears between its failed
            // open and entry enumeration without retaining two missing paths per directory.
            // membership設定probeより先に親listingを固定し、missing ignoreの生成raceを
            // 全directory×候補名の個別snapshotなしで検出する。
            var passthrough = IsSubmoduleAncestorPassthrough(relativeDir);
            var observedListingModifiedBeforeUtc = scanState.CaptureDirectoryListingSnapshots
                ? listingModifiedBeforeUtc ?? ReadDirectoryModifiedUtc(dir)
                : (DateTime?)null;
            if (observedListingModifiedBeforeUtc.HasValue && !listingSnapshotAlreadyRecorded)
                scanState.RecordDirectoryListingSnapshot(dir, observedListingModifiedBeforeUtc.Value);
            if (_bindConfigurationReadsToFileSystemIdentity)
            {
                Func<string, bool, bool>? observePatternDirectory = _suppressConfigurationInputObservation
                    ? null
                    : ObservePatternConfigurationDirectoryExists;
                ExtractorPluginRegistry.LoadAuthorizedPatternConfigsForDirectory(
                    _projectRoot,
                    dir,
                    _enumerateFileSystemEntries,
                    OpenObservedPatternConfigurationFileForRead,
                    observePatternDirectory,
                    ObservePatternConfigurationInput);
            }

            var loadResult = LoadIgnoreRulesForDirectory(dir, inheritedIgnoreRules, scanState.Errors, ref fullyScanned);
            var activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return false;

            // Submodule passthrough: we are inside a SkipDirs-named ancestor of a submodule
            // (e.g. vendor/ on the way to vendor/foo). Honor SkipDirs for this directory's
            // own files and unrelated subdirs while still descending toward the submodule.
            // submodule の祖先で SkipDirs 名のディレクトリ（例: vendor/foo の vendor/）の場合は、
            // 当該ディレクトリの直下ファイルおよび submodule と無関係なサブディレクトリには
            // SkipDirs を適用しつつ、submodule 方向にだけ降りる。
            if (_enumerateFilesForTesting is null)
            {
                // Materialize one scan-local snapshot after configuration, ignore, and submodule
                // decisions. The default case probe and the actual directory walk share this exact
                // ordered entry set; do not cache it across scans because filesystem races must stay
                // visible on the next scan.
                // config / ignore / submodule 判定後に scan-local snapshot を1回だけ作る。既定の
                // case probe と本走査で同じ順序の entry 集合を共有し、次回 scan へは cache しない。
                IReadOnlyList<string> entries;
                bool directoryIgnoreCase;
                if (_usesDefaultDirectoryIgnoreCaseProbe)
                {
                    entries = MaterializeDirectoryEntries(dir, cancellationToken);
                    directoryIgnoreCase = DirectoryUsesIgnoreCase(dir, entries);
                }
                else
                {
                    // Preserve the custom-probe contract, including one invocation for a directory
                    // whose subsequent entry enumeration fails.
                    // custom probe は後続の entry 列挙が失敗する directory でも従来どおり1回呼ぶ。
                    directoryIgnoreCase = DirectoryUsesIgnoreCase(dir);
                    entries = MaterializeDirectoryEntries(dir, cancellationToken);
                }
                RecordDirectoryCaseSensitivityWarning(relativeDir, directoryIgnoreCase, scanState);
                fullyScanned &= EnumerateDirectoryEntries(
                    entries,
                    relativeDir,
                    scanState,
                    activeIgnoreRules,
                    passthrough,
                    directoryIgnoreCase,
                    continueOnError,
                    cancellationToken,
                    depth);
            }
            else
            {
                var directoryIgnoreCase = DirectoryUsesIgnoreCase(dir);
                RecordDirectoryCaseSensitivityWarning(relativeDir, directoryIgnoreCase, scanState);
                if (!passthrough)
                    EnumerateIndexableFilesInDirectory(dir, scanState, activeIgnoreRules, directoryIgnoreCase, cancellationToken);

                // A successful file listing proves the direct children of this directory.
                // Child subtree failures must not revoke that authority for sibling-file purge.
                // ファイル列挙が成功した時点で、このディレクトリ直下の子要素については authoritative とみなせる。
                // 子サブツリー失敗が sibling file purge の authority を奪ってはいけない。
                scanState.ListedDirectories.Add(relativeDir);
                RecordDanglingFileSystemEntries(dir, scanState, cancellationToken);
                fullyScanned &= EnumerateSubdirectories(dir, scanState, activeIgnoreRules, passthrough, continueOnError, cancellationToken, depth);
            }

        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            scanState.Errors.Add(new ScanError(
                relativeDir,
                $"Could not scan directory due to {FileSystemTraversalFailure.DescribeReason(ex)}."));
            fullyScanned = false;
        }

        if (fullyScanned)
            scanState.FullyScannedDirectories.Add(relativeDir);

        return fullyScanned;
    }

    private static DateTime ReadDirectoryModifiedUtc(string directory)
    {
        DirectoryListingSnapshotProbeForTesting?.Invoke(directory);
        var info = new DirectoryInfo(LongPath.EnsureWindowsPrefix(directory));
        info.Refresh();
        if (!info.Exists)
            throw new DirectoryNotFoundException($"Directory disappeared while it was being scanned: {directory}");
        return info.LastWriteTimeUtc;
    }

    private IReadOnlyList<string> MaterializeDirectoryEntries(string dir, CancellationToken cancellationToken)
    {
        var entries = new List<string>();
        foreach (var enumeratedEntry in _enumerateFileSystemEntries(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(LongPath.RemoveWindowsPrefix(enumeratedEntry));
        }

        return entries;
    }

    private void RecordDirectoryCaseSensitivityWarning(
        string relativeDir,
        bool directoryIgnoreCase,
        DirectoryScanState scanState)
    {
        if (directoryIgnoreCase == _ignoreCase)
            return;

        scanState.Errors.Add(new ScanError(
            relativeDir,
            "Filesystem case-sensitivity differs from the project root; deduplicating file paths for this directory.",
            ScanIssueSeverity.Warning));
    }

    private bool EnumerateDirectoryEntries(
        IReadOnlyList<string> entries,
        string relativeDir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool passthrough,
        bool directoryIgnoreCase,
        bool continueOnError,
        CancellationToken cancellationToken,
        int depth)
    {
        var seenFilePaths = !passthrough && directoryIgnoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        List<ScannedSubdirectory>? subdirectories = null;
        var danglingCandidateLimit = _maxDanglingFileSystemEntryScanCandidates;
        var danglingCandidateCount = 0;
        var danglingScanTruncated = false;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountDanglingCandidate(relativeDir, scanState, danglingCandidateLimit, ref danglingCandidateCount, ref danglingScanTruncated);

            var probeStatus = FileSystemBoundary.TryGetAttributes(entry, out var attributes);
            if (probeStatus != FileSystemBoundaryProbeStatus.Found)
                continue;

            if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes) && !ReparsePointTargetExists(entry))
            {
                RecordDanglingFileSystemEntry(entry, scanState);
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                (subdirectories ??= new List<ScannedSubdirectory>())
                    .Add(new ScannedSubdirectory(entry, attributes));
                continue;
            }

            if (passthrough)
                continue;

            if (TryAcceptScannedFile(
                    entry,
                    scanState,
                    activeIgnoreRules,
                    seenFilePaths,
                    knownAttributes: attributes,
                    filePathCameFromDirectoryEnumeration: true))
                scanState.Results.Add(entry);
        }

        // A successful immediate-child listing proves this directory for sibling-file purge.
        // Child recursion happens after that authority has been captured.
        scanState.ListedDirectories.Add(relativeDir);
        if (subdirectories == null)
            return true;

        return ProcessSubdirectories(
            subdirectories,
            scanState,
            activeIgnoreRules,
            passthrough,
            continueOnError,
            cancellationToken,
            depth);
    }

    private static void CountDanglingCandidate(
        string relativeDir,
        DirectoryScanState scanState,
        int candidateLimit,
        ref int candidateCount,
        ref bool scanTruncated)
    {
        if (scanTruncated)
            return;

        candidateCount++;
        if (candidateCount <= candidateLimit)
            return;

        scanState.Errors.Add(new ScanError(
            relativeDir,
            $"Dangling filesystem entry scan truncated after {candidateLimit:N0} candidate(s); additional dangling symlink diagnostics in this directory may be omitted.",
            ScanIssueSeverity.Warning));
        scanTruncated = true;
    }

    private void EnumerateIndexableFilesInDirectory(
        string dir,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        bool directoryIgnoreCase,
        CancellationToken cancellationToken)
    {
        var enumerateFiles = _enumerateFilesForTesting ?? throw new InvalidOperationException("Test file enumeration is not configured.");
        var seenFilePaths = directoryIgnoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var enumeratedFile in enumerateFiles(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Strip any \\?\ prefix returned by EnumerateFiles when we passed a long-path
            // directory, so downstream relative-path math (which compares against the
            // un-prefixed _projectRoot) still produces the canonical project-relative key.
            // \\?\ 接頭辞付きの long-path ディレクトリを渡したとき EnumerateFiles も接頭辞付きで
            // 返すため、_projectRoot（接頭辞なし）と突き合わせる相対パス計算が崩れないよう剥がす。
            var file = LongPath.RemoveWindowsPrefix(enumeratedFile);
            if (!TryAcceptScannedFile(file, scanState, activeIgnoreRules, seenFilePaths))
                continue;

            scanState.Results.Add(file);
        }
    }
}
