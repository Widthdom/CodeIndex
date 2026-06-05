using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class IndexFreshnessChecker
{
    private const int SampleLimit = 20;

    internal static IndexFreshnessCheckResult Check(DbReader reader, string? projectRoot)
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
        var indexedHeadSha = reader.GetMetaString(DbContext.IndexedHeadShaMetaKey);
        var commitScopedFreshHeadSha = reader.GetMetaString(DbContext.CommitScopedFreshHeadShaMetaKey);
        var workspaceHeadCommit = GitHelper.TryGetHeadCommit(projectRoot);
        var currentHeadCoveredByCommitRefresh = !string.IsNullOrWhiteSpace(indexedHeadSha)
            && !string.IsNullOrWhiteSpace(commitScopedFreshHeadSha)
            && !string.IsNullOrWhiteSpace(workspaceHeadCommit)
            && string.Equals(indexedHeadSha, workspaceHeadCommit, StringComparison.Ordinal)
            && string.Equals(commitScopedFreshHeadSha, workspaceHeadCommit, StringComparison.Ordinal);
        // Only treat HEAD as diverged when we have both sides to compare. A legacy DB (no
        // captured HEAD) or a non-git workspace (no current HEAD) intentionally degrades to
        // "no signal" rather than spuriously flagging every status check as stale. A newer
        // commit-scoped coverage stamp also satisfies the check after targeted commit refreshes.
        // 比較材料が揃ったときのみ HEAD 不一致と判定する。片側でも欠ければ意図せず stale 化しない。
        // commit-scoped refresh が現在 HEAD を cover した stamp を持つ場合だけ stale 化しない。
        var headChanged = !string.IsNullOrWhiteSpace(indexedHeadCommit)
            && !string.IsNullOrWhiteSpace(workspaceHeadCommit)
            && !string.Equals(indexedHeadCommit, workspaceHeadCommit, StringComparison.Ordinal)
            && !currentHeadCoveredByCommitRefresh;
        var result = new IndexFreshnessCheckResult
        {
            IndexedHeadCommit = string.IsNullOrWhiteSpace(indexedHeadCommit) ? null : indexedHeadCommit,
            WorkspaceHeadCommit = string.IsNullOrWhiteSpace(workspaceHeadCommit) ? null : workspaceHeadCommit,
            HeadChanged = headChanged,
        };

        var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot);
        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(projectRoot) ?? Path.GetFullPath(projectRoot);
        var indexer = new FileIndexer(projectRoot, ignoreCase, ignoreRuleRoot);
        var scan = indexer.ScanFilesDetailed();
        foreach (var error in scan.Errors)
        {
            if (!error.IsFatal)
                continue;

            result.ScanErrorCount++;
            AddSample(result.ScanErrors, $"{error.Path}: {error.Message}");
        }

        using var indexedEnumerator = reader.EnumerateIndexedFileSnapshots().GetEnumerator();
        var hasIndexed = MoveNextIndexed();
        var skipWorktreePathsLoaded = false;
        HashSet<string>? skipWorktreePaths = null;

        foreach (var absolutePath in scan.Files.OrderBy(path => FileIndexer.NormalizeIndexPath(Path.GetRelativePath(projectRoot, path)), StringComparer.Ordinal))
        {
            try
            {
                var (record, _, _, _) = indexer.BuildRecordWithRawBytes(absolutePath);
                result.WorkspaceFileCount++;
                while (hasIndexed && string.Compare(indexedEnumerator.Current.Path, record.Path, StringComparison.Ordinal) < 0)
                {
                    AddMissingIndexedPath(indexedEnumerator.Current.Path);
                    hasIndexed = MoveNextIndexed();
                }

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
                var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
                result.ScanErrorCount++;
                AddSample(result.ScanErrors, $"{relativePath}: {ex.Message}");
            }
        }

        while (hasIndexed)
        {
            AddMissingIndexedPath(indexedEnumerator.Current.Path);
            hasIndexed = MoveNextIndexed();
        }

        result.Checked = result.ScanErrorCount == 0;
        result.MatchesWorkspace = result.Checked
            && !result.HeadChanged
            && result.ChangedFileCount == 0
            && result.MissingFileCount == 0
            && result.UnindexedFileCount == 0
            && result.UnverifiableFileCount == 0;
        result.Reason = BuildReason(result);
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
            // Skip-worktree paths are intentionally absent from disk (sparse-checkout cone/non-cone,
            // partial clone, or manual update-index --skip-worktree). Reclassify them so the freshness
            // gate stops flagging them as "missing" and rebuilds.
            // skip-worktree のパスは意図的に worktree から外されている(sparse-checkout cone/non-cone、
            // partial clone、手動の update-index --skip-worktree)。これらを "missing" から切り分け、
            // 不要な rebuild トリガーを止める。
            if (!skipWorktreePathsLoaded)
            {
                skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectRoot);
                skipWorktreePathsLoaded = true;
            }

            if (skipWorktreePaths != null && skipWorktreePaths.Contains(path))
            {
                result.OutsideSparseConeFileCount++;
                AddSample(result.OutsideSparseConeFiles, path);
            }
            else
            {
                result.MissingFileCount++;
                AddSample(result.MissingFiles, path);
            }
        }
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
        if (samples.Count < SampleLimit)
            samples.Add(value);
    }
}
