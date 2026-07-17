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
        var runtime = ResolveRuntime(dbPath, dbPathExplicit, cancellationToken);
        map.ProjectRoot = runtime.ProjectRoot;
        map.GitHead = runtime.RuntimeHead;
        map.GitIsDirty = runtime.IsDirty;
        if (runtime.ProjectRoot == null || map.IndexedHeadSnapshot == null)
            return;

        var snapshot = map.IndexedHeadSnapshot;
        map.IndexedHeadCommit = snapshot.LegacyFullScanHead;
        map.IndexedHeadSha = snapshot.LatestIndexHead;
        map.IndexedHeadBranch = snapshot.LatestIndexBranch;
        map.IndexedHeadTimestamp = snapshot.LatestIndexTimestamp;
        map.WorktreeHeadChanged = ResolveHeadChanged(
            runtime.RuntimeHead,
            runtime.RuntimeBranch,
            snapshot.LatestIndexHead,
            snapshot.LatestIndexBranch,
            snapshot.LatestIndexBranchStampPresent,
            snapshot.LegacyFullScanHead,
            snapshot.LegacyFullScanBranch,
            snapshot.LegacyFullScanBranchStampPresent);
        if (!string.IsNullOrWhiteSpace(map.IndexedHeadSha))
            map.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(runtime.ProjectRoot, map.IndexedHeadSha, cancellationToken);
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
        var runtime = ResolveRuntime(dbPath, dbPathExplicit, cancellationToken);
        if (runtime.ProjectRoot == null)
            return new(null, null, null, null, null, null, null, null);

        var indexedHead = DbPathResolver.TryReadIndexedHeadCommit(dbPath);
        var indexedHeadSha = DbPathResolver.TryReadIndexedHeadSha(dbPath);
        var indexedHeadBranch = DbPathResolver.TryReadIndexedHeadBranch(dbPath);
        var indexedHeadTimestamp = DbPathResolver.TryReadIndexedHeadTimestamp(dbPath);
        var hasIndexedHeadBranchStamp = DbPathResolver.TryHasIndexedHeadBranchStamp(dbPath);
        var indexedBranch = DbPathResolver.TryReadIndexedHeadCommitBranch(dbPath);
        var hasIndexedBranchStamp = DbPathResolver.TryHasIndexedHeadCommitBranchStamp(dbPath);
        var headChanged = ResolveHeadChanged(
            runtime.RuntimeHead,
            runtime.RuntimeBranch,
            indexedHeadSha,
            indexedHeadBranch,
            hasIndexedHeadBranchStamp,
            indexedHead,
            indexedBranch,
            hasIndexedBranchStamp);

        return new(
            runtime.ProjectRoot,
            runtime.RuntimeHead,
            runtime.IsDirty,
            indexedHead,
            indexedHeadSha,
            indexedHeadBranch,
            indexedHeadTimestamp,
            headChanged);
    }

    private static RuntimeWorkspaceMetadata ResolveRuntime(
        string dbPath,
        bool dbPathExplicit,
        CancellationToken cancellationToken)
    {
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (projectRoot == null)
            return new(null, null, null, null);

        return new(
            projectRoot,
            GitHelper.TryGetHeadCommit(projectRoot, cancellationToken),
            GitHelper.TryGetHeadBranch(projectRoot, cancellationToken),
            GitHelper.TryIsWorktreeDirty(projectRoot, cancellationToken));
    }

    private static bool? ResolveHeadChanged(
        string? runtimeHead,
        string? runtimeBranch,
        string? latestIndexedHead,
        string? latestIndexedBranch,
        bool latestIndexedBranchStampPresent,
        string? legacyIndexedHead,
        string? legacyIndexedBranch,
        bool legacyIndexedBranchStampPresent)
    {
        var comparisonHead = latestIndexedHead ?? legacyIndexedHead;
        var comparisonBranch = latestIndexedHead != null ? latestIndexedBranch : legacyIndexedBranch;
        var hasComparisonBranchStamp = latestIndexedHead != null
            ? latestIndexedBranchStampPresent
            : legacyIndexedBranchStampPresent;
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
        return commitChanged == true || branchChanged == true
            ? true
            : commitChanged;
    }

    private sealed record RuntimeWorkspaceMetadata(
        string? ProjectRoot,
        string? RuntimeHead,
        string? RuntimeBranch,
        bool? IsDirty);

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
