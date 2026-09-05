using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class IndexFreshnessChecker
{
    private const int MaxScanErrorPathChars = 180;
    internal const int MaxScanErrorSampleChars = 240;

    internal static IndexFreshnessCheckResult Check(
        DbReader reader,
        string? projectRoot,
        CancellationToken cancellationToken = default,
        bool? pathCaseSensitive = null,
        string? internalIndexDatabasePath = null,
        bool allowGitCommands = true,
        HashSet<string>? knownSkipWorktreePaths = null,
        bool knownSkipWorktreePathsComplete = true,
        string? knownWorkspaceHeadCommit = null,
        string? knownRepositoryRoot = null,
        Action<string>? beforeFileLoadForTesting = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return new IndexFreshnessCheckResult
            {
                Checked = false,
                MatchesWorkspace = false,
                Reason = "project_root_unavailable",
            };
        }

        var indexedHeadCommit = reader.GetMetaString(DbContext.IndexedHeadCommitMetaKey);
        var workspaceVerifiedHeadSha = reader.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey);
        var workspaceHeadCommit = allowGitCommands
            ? GitHelper.TryGetHeadCommit(projectRoot, cancellationToken)
            : knownWorkspaceHeadCommit;
        var comparisonHead = string.IsNullOrWhiteSpace(workspaceVerifiedHeadSha)
            ? indexedHeadCommit
            : workspaceVerifiedHeadSha;
        // Only treat HEAD as diverged when both sides are available. Prefer the explicit
        // whole-workspace verification stamp; legacy databases fall back conservatively to
        // the full-scan baseline, while non-git workspaces retain no HEAD signal.
        // 比較材料が揃ったときのみ HEAD 不一致と判定する。workspace 全体を検証した明示 stamp
        // を優先し、旧 DB は full-scan stamp へ保守的に fallback する。非 git workspace は
        // HEAD signal を持たない。
        var headChanged = !string.IsNullOrWhiteSpace(comparisonHead)
            && !string.IsNullOrWhiteSpace(workspaceHeadCommit)
            && !string.Equals(comparisonHead, workspaceHeadCommit, StringComparison.Ordinal);
        var headEvidenceUnavailable = !allowGitCommands
            && !string.IsNullOrWhiteSpace(knownRepositoryRoot)
            && !string.IsNullOrWhiteSpace(comparisonHead)
            && string.IsNullOrWhiteSpace(workspaceHeadCommit);
        var result = new IndexFreshnessCheckResult
        {
            IndexedHeadCommit = string.IsNullOrWhiteSpace(comparisonHead) ? null : comparisonHead,
            WorkspaceHeadCommit = string.IsNullOrWhiteSpace(workspaceHeadCommit) ? null : workspaceHeadCommit,
            HeadChanged = headChanged,
        };

        var ignoreCase = pathCaseSensitive.HasValue
            ? !pathCaseSensitive.Value
            : allowGitCommands
                ? GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken)
                : PathCasing.IsIgnoreCase(projectRoot);
        if (pathCaseSensitive.HasValue)
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase);
        var ignoreRuleRoot = allowGitCommands
            ? GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot)
            : knownRepositoryRoot ?? Path.GetFullPath(projectRoot);
        var symlinkPolicy = ReadIndexedSymlinkPolicy(reader);
        var indexer = new FileIndexer(
            projectRoot,
            ignoreCase,
            ignoreRuleRoot,
            maxFileSizeBytes: IndexedFileSizePolicy.Resolve(reader, freshness: true),
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: symlinkPolicy,
            internalIndexDatabasePath: internalIndexDatabasePath);
        var scanWithTargets = indexer.ScanFilesDetailedWithIndexingTargets(
            cancellationToken: cancellationToken);
        var scan = scanWithTargets.ScanResult;
        var indexingTargets = scanWithTargets.IndexingTargets;
        var probeFailedPaths = scan.ProbeFailedFilePaths
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var error in scan.Errors)
        {
            if (!error.IsFatal)
                continue;

            result.ScanErrorCount++;
            AddSample(result.ScanErrors, FormatScanSample(error.Path, error.Message));
        }

        using var indexedEnumerator = reader.EnumerateIndexedFileSnapshots().GetEnumerator();
        var hasIndexed = MoveNextIndexed();
        var skipWorktreePathsLoaded = false;
        HashSet<string>? skipWorktreePaths = knownSkipWorktreePaths;
        var skipWorktreeEvidenceUnavailable = false;

        var targetOrder = new int[indexingTargets.Count];
        for (var index = 0; index < targetOrder.Length; index++)
            targetOrder[index] = index;
        Array.Sort(targetOrder, (left, right) =>
        {
            var comparison = string.Compare(
                indexingTargets[left].IndexPath,
                indexingTargets[right].IndexPath,
                StringComparison.Ordinal);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        foreach (var targetIndex in targetOrder)
        {
            var target = indexingTargets[targetIndex];
            cancellationToken.ThrowIfCancellationRequested();
            // Advance by discovered path before loading: a failed read is not a deletion.
            while (hasIndexed && string.Compare(indexedEnumerator.Current.Path, target.IndexPath, StringComparison.Ordinal) < 0)
            {
                AddMissingIndexedPath(indexedEnumerator.Current.Path);
                hasIndexed = MoveNextIndexed();
            }
            try
            {
                beforeFileLoadForTesting?.Invoke(target.IndexPath);
                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                    target.FilePath,
                    target.RelativePath,
                    target.ReusableLanguage,
                    detectGeneratedCode: false,
                    cancellationToken: cancellationToken);
                var record = loaded.Record;
                result.WorkspaceFileCount++;
                if (!hasIndexed || string.Compare(indexedEnumerator.Current.Path, record.Path, StringComparison.Ordinal) > 0)
                {
                    result.UnindexedFileCount++;
                    AddSample(result.UnindexedFiles, record.Path);
                    continue;
                }

                var indexedFile = indexedEnumerator.Current;
                if (string.IsNullOrWhiteSpace(indexedFile.Checksum))
                {
                    result.UnverifiableFileCount++;
                    AddSample(result.UnverifiableFiles, record.Path);
                    hasIndexed = MoveNextIndexed();
                    continue;
                }

                if (!string.Equals(indexedFile.Checksum, record.Checksum ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    || (indexedFile.Lines.HasValue && indexedFile.Lines.Value != record.Lines))
                {
                    result.ChangedFileCount++;
                    AddSample(result.ChangedFiles, record.Path);
                    hasIndexed = MoveNextIndexed();
                    continue;
                }

                result.MatchedFileCount++;
                hasIndexed = MoveNextIndexed();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result.ScanErrorCount++;
                AddSample(result.ScanErrors, FormatScanFailureSample(target.DisplayRelativePath, ex));
                if (hasIndexed && string.Equals(indexedEnumerator.Current.Path, target.IndexPath, StringComparison.Ordinal))
                {
                    result.UnverifiableFileCount++;
                    AddSample(result.UnverifiableFiles, target.IndexPath);
                    hasIndexed = MoveNextIndexed();
                }
            }
        }

        while (hasIndexed)
        {
            AddMissingIndexedPath(indexedEnumerator.Current.Path);
            hasIndexed = MoveNextIndexed();
        }

        result.Checked = result.ScanErrorCount == 0
            && !headEvidenceUnavailable
            && !skipWorktreeEvidenceUnavailable;
        result.MatchesWorkspace = result.Checked
            && !result.HeadChanged
            && result.ChangedFileCount == 0
            && result.MissingFileCount == 0
            && result.UnindexedFileCount == 0
            && result.UnverifiableFileCount == 0;
        result.Reason = headEvidenceUnavailable
            ? "head_unavailable"
            : skipWorktreeEvidenceUnavailable
                ? "skip_worktree_metadata_unavailable"
                : BuildReason(result);
        return result;

        bool MoveNextIndexed()
        {
            var moved = indexedEnumerator.MoveNext();
            if (moved)
                result.IndexedFileCount++;
            return moved;
        }

        void AddMissingIndexedPath(string path)
        {
            if (probeFailedPaths.Contains(path))
            {
                result.UnverifiableFileCount++;
                AddSample(result.UnverifiableFiles, path);
                return;
            }
            // Skip-worktree paths are intentionally absent from disk (sparse-checkout cone/non-cone,
            // partial clone, or manual update-index --skip-worktree). Reclassify them so the freshness
            // gate stops flagging them as "missing" and rebuilds.
            // skip-worktree のパスは意図的に worktree から外されている(sparse-checkout cone/non-cone、
            // partial clone、手動の update-index --skip-worktree)。これらを "missing" から切り分け、
            // 不要な rebuild トリガーを止める。
            if (!skipWorktreePathsLoaded)
            {
                skipWorktreePaths = allowGitCommands
                    ? GitHelper.TryGetSkipWorktreePaths(projectRoot, cancellationToken)
                    : knownSkipWorktreePaths;
                skipWorktreePathsLoaded = true;
            }

            if (skipWorktreePaths != null && IsSkipWorktreePath(skipWorktreePaths, path))
            {
                result.OutsideSparseConeFileCount++;
                AddSample(result.OutsideSparseConeFiles, path);
            }
            else if (!allowGitCommands && !knownSkipWorktreePathsComplete)
            {
                // A split/corrupt/unsupported index cannot prove that an indexed-but-absent path
                // is a real deletion. Keep the readiness result unavailable instead of emitting a
                // false missing-file diagnosis.
                // split/corrupt/未対応 index では、disk 上にない indexed path が実際の削除かを
                // 証明できない。誤った missing-file 判定を返さず readiness を unavailable にする。
                skipWorktreeEvidenceUnavailable = true;
            }
            else
            {
                result.MissingFileCount++;
                AddSample(result.MissingFiles, path);
            }
        }
    }

    internal static bool IsSkipWorktreePath(HashSet<string> skipWorktreePaths, string path)
    {
        if (skipWorktreePaths.Contains(path) || skipWorktreePaths.Contains("/"))
            return true;

        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
        {
            if (skipWorktreePaths.Contains(path[..(index + 1)]))
                return true;
        }

        return false;
    }

    internal static FileIndexer.SymlinkPolicy ReadIndexedSymlinkPolicy(DbReader reader)
    {
        var raw = reader.GetMetaString(DbContext.IndexedFollowSymlinksPolicyMetaKey);
        if (string.IsNullOrWhiteSpace(raw))
            return FileIndexer.SymlinkPolicy.None;

        return raw.Trim().ToLowerInvariant() switch
        {
            "internal" => FileIndexer.SymlinkPolicy.Internal,
            "all" => FileIndexer.SymlinkPolicy.All,
            _ => FileIndexer.SymlinkPolicy.None,
        };
    }

    private static string BuildReason(IndexFreshnessCheckResult result)
    {
        if (result.ScanErrorCount > 0)
            return "scan_errors";
        if (result.UnverifiableFileCount > 0)
            return "unverifiable_db_rows";
        if (result.ChangedFileCount > 0)
            return "changed_files";
        if (result.MissingFileCount > 0)
            return "missing_indexed_files";
        if (result.UnindexedFileCount > 0)
            return "unindexed_workspace_files";
        // HEAD divergence with otherwise-matching files is still stale: a partial rebuild after
        // checkout may leave the DB byte-equal for surviving files while missing branch-specific
        // additions / deletions that the per-file scan cannot prove. Emit this as the lowest
        // priority so an actual file mismatch above takes precedence and the message stays
        // specific. Issue #1508.
        // ファイル単位の不一致がない場合でも HEAD が変わっていれば stale 扱い。優先度は最後で、
        // 実ファイル差分の reason が立っているときはそちらを優先表示する。Issue #1508。
        if (result.HeadChanged)
            return "head_changed";
        return "matched";
    }

    private static void AddSample(List<string> samples, string value)
    {
        if (samples.Count < WorkspaceCheckPathSamples.PathLimit)
            samples.Add(value);
    }

    internal static string FormatScanFailureSample(string relativePath, Exception ex) =>
        FormatScanSample(relativePath, ClassifyScanFailure(ex));

    private static string FormatScanSample(string relativePath, string message)
    {
        var path = DiagnosticRedactor.BoundDiagnosticText(
            FileIndexer.NormalizePathSeparators(relativePath),
            MaxScanErrorPathChars);
        return DiagnosticRedactor.BoundDiagnosticText($"{path}: {message}", MaxScanErrorSampleChars);
    }

    private static string ClassifyScanFailure(Exception ex) =>
        ex switch
        {
            UnauthorizedAccessException => "access-denied",
            IOException => "io-error",
            InvalidOperationException => "probe-failed",
            _ => "probe-failed",
        };
}
