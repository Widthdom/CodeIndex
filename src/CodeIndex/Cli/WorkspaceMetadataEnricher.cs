using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Enrich repo/status responses with workspace-level freshness metadata.
/// ワークスペース単位の鮮度メタデータで repo/status レスポンスを補強する。
/// </summary>
public static class WorkspaceMetadataEnricher
{
    public static void Enrich(
        StatusResult status,
        string dbPath,
        bool dbPathExplicit = false,
        CancellationToken cancellationToken = default)
    {
        var metadata = Resolve(dbPath, dbPathExplicit, cancellationToken);
        status.ProjectRoot = metadata.ProjectRoot;
        status.GitHead = metadata.RuntimeHead;
        status.GitIsDirty = metadata.IsDirty;
        status.IndexedHeadCommit = metadata.LegacyIndexedHead;
        status.WorktreeHeadChanged = metadata.HeadChanged;
        // #1509: compare the current HEAD against the SHA stamped at index time. Only
        // makes sense when both sides are known; otherwise leave the field null so the
        // CLI/MCP consumer can render "indexed at <sha>" without a misleading 0/N hint.
        // Note this reads `status.IndexedHeadSha` which was populated by the DbReader
        // (#1509 keys, stamped on every successful index — distinct from the legacy
        // full-scan-only `indexed_head_commit`).
        if (metadata.ProjectRoot != null && !string.IsNullOrWhiteSpace(status.IndexedHeadSha))
            status.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(metadata.ProjectRoot, status.IndexedHeadSha, cancellationToken);
    }

    public static void Enrich(
        RepoMapResult map,
        string dbPath,
        bool dbPathExplicit = false,
        CancellationToken cancellationToken = default)
    {
        var metadata = Resolve(dbPath, dbPathExplicit, cancellationToken);
        map.ProjectRoot = metadata.ProjectRoot;
        map.GitHead = metadata.RuntimeHead;
        map.GitIsDirty = metadata.IsDirty;
        map.IndexedHeadCommit = metadata.LegacyIndexedHead;
        map.IndexedHeadSha = metadata.IndexedHeadSha;
        map.IndexedHeadBranch = metadata.IndexedHeadBranch;
        map.IndexedHeadTimestamp = metadata.IndexedHeadTimestamp;
        map.WorktreeHeadChanged = metadata.HeadChanged;
        if (metadata.ProjectRoot != null && !string.IsNullOrWhiteSpace(map.IndexedHeadSha))
            map.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(metadata.ProjectRoot, map.IndexedHeadSha, cancellationToken);
    }

    public static void Enrich(
        SymbolAnalysisResult analysis,
        string dbPath,
        bool dbPathExplicit = false,
        CancellationToken cancellationToken = default)
    {
        var metadata = Resolve(dbPath, dbPathExplicit, cancellationToken);
        analysis.ProjectRoot = metadata.ProjectRoot;
        analysis.GitHead = metadata.RuntimeHead;
        analysis.GitIsDirty = metadata.IsDirty;
        analysis.IndexedHeadCommit = metadata.LegacyIndexedHead;
        analysis.WorktreeHeadChanged = metadata.HeadChanged;
    }

    /// <summary>
    /// Resolve runtime and persisted workspace metadata once.
    /// runtime と永続化済みのワークスペースメタデータを一度解決する。
    /// </summary>
    private static WorkspaceMetadata Resolve(
        string dbPath,
        bool dbPathExplicit,
        CancellationToken cancellationToken)
    {
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (projectRoot == null)
            return new(null, null, null, null, null, null, null, null);

        var indexedHead = DbPathResolver.TryReadIndexedHeadCommit(dbPath);
        var indexedHeadSha = DbPathResolver.TryReadIndexedHeadSha(dbPath);
        var indexedHeadBranch = DbPathResolver.TryReadIndexedHeadBranch(dbPath);
        var indexedHeadTimestamp = DbPathResolver.TryReadIndexedHeadTimestamp(dbPath);
        var runtimeHead = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
        var runtimeBranch = GitHelper.TryGetHeadBranch(projectRoot, cancellationToken);
        var dirty = GitHelper.TryIsWorktreeDirty(projectRoot, cancellationToken);
        var hasIndexedHeadBranchStamp = DbPathResolver.TryHasIndexedHeadBranchStamp(dbPath);
        var indexedBranch = DbPathResolver.TryReadIndexedHeadCommitBranch(dbPath);
        var hasIndexedBranchStamp = DbPathResolver.TryHasIndexedHeadCommitBranchStamp(dbPath);
        var comparisonHead = indexedHeadSha ?? indexedHead;
        var comparisonBranch = indexedHeadSha != null ? indexedHeadBranch : indexedBranch;
        var hasComparisonBranchStamp = indexedHeadSha != null ? hasIndexedHeadBranchStamp : hasIndexedBranchStamp;
        // Detect a per-worktree branch / HEAD switch by comparing the runtime HEAD against
        // the latest successful index HEAD. Fall back to the older full-scan-only stamp only
        // for legacy DBs. Also compare the matching branch stamp when a HEAD stamp is present
        // so branch <-> detached transitions at the same commit are still visible.
        // Only meaningful when enough metadata exists; legacy DBs or projects indexed outside
        // git report null and must not trigger a false-positive switch warning. Issues #1512
        // and #2094. Issue #3367.
        // worktree 内の branch / HEAD 切替検出。最新 index HEAD と現在で HEAD を突き合わせる。
        // 旧 DB では full-scan-only stamp へ fallback する。
        // 同一 commit の branch/detached 遷移も branch stamp で検出する。
        var commitChanged = comparisonHead != null && runtimeHead != null
            ? !string.Equals(comparisonHead, runtimeHead, StringComparison.OrdinalIgnoreCase)
            : (bool?)null;
        var branchChanged = comparisonHead != null
            && runtimeHead != null
            && hasComparisonBranchStamp
            && !string.Equals(comparisonBranch, runtimeBranch, StringComparison.Ordinal)
            ? true
            : (bool?)null;
        bool? headChanged = commitChanged == true || branchChanged == true
            ? true
            : commitChanged;

        return new(
            projectRoot,
            runtimeHead,
            dirty,
            indexedHead,
            indexedHeadSha,
            indexedHeadBranch,
            indexedHeadTimestamp,
            headChanged);
    }

    private sealed record WorkspaceMetadata(
        string? ProjectRoot,
        string? RuntimeHead,
        bool? IsDirty,
        string? LegacyIndexedHead,
        string? IndexedHeadSha,
        string? IndexedHeadBranch,
        DateTimeOffset? IndexedHeadTimestamp,
        bool? HeadChanged);
}
