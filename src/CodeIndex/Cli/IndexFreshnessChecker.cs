using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class IndexFreshnessChecker
{
    private const int SampleLimit = 20;
    private const int MaxScanErrorPathChars = 180;
    internal const int MaxScanErrorSampleChars = 240;

    internal static IndexFreshnessCheckResult Check(
        DbReader reader,
        string? projectRoot,
        CancellationToken cancellationToken = default,
        bool? pathCaseSensitive = null,
        string? internalIndexDatabasePath = null)
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
        var workspaceHeadCommit = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
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
        var result = new IndexFreshnessCheckResult
        {
            IndexedHeadCommit = string.IsNullOrWhiteSpace(comparisonHead) ? null : comparisonHead,
            WorkspaceHeadCommit = string.IsNullOrWhiteSpace(workspaceHeadCommit) ? null : workspaceHeadCommit,
            HeadChanged = headChanged,
        };

        var ignoreCase = pathCaseSensitive.HasValue
            ? !pathCaseSensitive.Value
            : GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken);
        if (pathCaseSensitive.HasValue)
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase);
        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot);
        var symlinkPolicy = ReadIndexedSymlinkPolicy(reader);
        var indexer = new FileIndexer(
            projectRoot,
            ignoreCase,
            ignoreRuleRoot,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: symlinkPolicy,
            internalIndexDatabasePath: internalIndexDatabasePath);
        var scan = indexer.ScanFilesDetailed(cancellationToken: cancellationToken);
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
        HashSet<string>? skipWorktreePaths = null;

        var workspaceFileTargets = scan.Files
            .Select(path => WorkspaceFileTarget.Create(projectRoot, path))
            .OrderBy(target => target.IndexPath, StringComparer.Ordinal);
        foreach (var target in workspaceFileTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var knownLanguage = FileIndexer.GetReusableDetectedLanguage(target.AbsolutePath, scan.FileLanguages);
                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                    target.AbsolutePath,
                    target.RelativePath,
                    knownLanguage,
                    detectGeneratedCode: false,
                    cancellationToken: cancellationToken);
                var record = loaded.Record;
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
                result.ScanErrorCount++;
                AddSample(result.ScanErrors, FormatScanFailureSample(target.DisplayRelativePath, ex));
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
                skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectRoot, cancellationToken);
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

    private static FileIndexer.SymlinkPolicy ReadIndexedSymlinkPolicy(DbReader reader)
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

    private readonly record struct WorkspaceFileTarget(
        string AbsolutePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath)
    {
        public static WorkspaceFileTarget Create(string projectRoot, string absolutePath)
        {
            var relativePath = FileIndexer.GetRelativePathFromProjectRoot(projectRoot, absolutePath);
            return new WorkspaceFileTarget(
                absolutePath,
                relativePath,
                FileIndexer.NormalizePathSeparators(relativePath),
                FileIndexer.NormalizeIndexPath(relativePath));
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
