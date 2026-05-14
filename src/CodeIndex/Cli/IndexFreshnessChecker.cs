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

        var indexed = reader.GetIndexedFileSnapshots()
            .ToDictionary(file => file.Path, StringComparer.Ordinal);
        var workspace = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new IndexFreshnessCheckResult
        {
            IndexedFileCount = indexed.Count,
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

        foreach (var absolutePath in scan.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var (record, _, _, _) = indexer.BuildRecordWithRawBytes(absolutePath);
                workspace[record.Path] = record.Checksum ?? string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
                result.ScanErrorCount++;
                AddSample(result.ScanErrors, $"{relativePath}: {ex.Message}");
            }
        }

        result.WorkspaceFileCount = workspace.Count;

        foreach (var (path, checksum) in workspace.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!indexed.TryGetValue(path, out var indexedFile))
            {
                result.UnindexedFileCount++;
                AddSample(result.UnindexedFiles, path);
                continue;
            }

            if (string.IsNullOrWhiteSpace(indexedFile.Checksum))
            {
                result.UnverifiableFileCount++;
                AddSample(result.UnverifiableFiles, path);
                continue;
            }

            if (!string.Equals(indexedFile.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                result.ChangedFileCount++;
                AddSample(result.ChangedFiles, path);
                continue;
            }

            result.MatchedFileCount++;
        }

        var missingCandidates = indexed.Keys
            .Except(workspace.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // Skip-worktree paths are intentionally absent from disk (sparse-checkout cone/non-cone,
        // partial clone, or manual update-index --skip-worktree). Reclassify them so the freshness
        // gate stops flagging them as "missing" and rebuilds.
        // skip-worktree のパスは意図的に worktree から外されている(sparse-checkout cone/non-cone、
        // partial clone、手動の update-index --skip-worktree)。これらを "missing" から切り分け、
        // 不要な rebuild トリガーを止める。
        HashSet<string>? skipWorktreePaths = null;
        if (missingCandidates.Count > 0)
            skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectRoot);

        foreach (var path in missingCandidates)
        {
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

        result.Checked = result.ScanErrorCount == 0;
        result.MatchesWorkspace = result.Checked
            && result.ChangedFileCount == 0
            && result.MissingFileCount == 0
            && result.UnindexedFileCount == 0
            && result.UnverifiableFileCount == 0;
        result.Reason = BuildReason(result);
        return result;
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
        return "matched";
    }

    private static void AddSample(List<string> samples, string value)
    {
        if (samples.Count < SampleLimit)
            samples.Add(value);
    }
}
