using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Enrich repo/status responses with workspace-level freshness metadata.
/// ワークスペース単位の鮮度メタデータで repo/status レスポンスを補強する。
/// </summary>
public static class WorkspaceMetadataEnricher
{
    internal static AsyncLocal<Action?> StatusRuntimeMetadataResolvedForTesting { get; } = new();
    internal static AsyncLocal<Action?> AnalysisRuntimeMetadataResolvedForTesting { get; } = new();

    public static void Enrich(
        StatusResult status,
        string dbPath,
        bool dbPathExplicit = false,
        CancellationToken cancellationToken = default,
        bool evaluateOrdinaryFreshness = true)
    {
        if (status.HeadMetadataSnapshotCaptured)
        {
            var runtime = ResolveRuntime(
                dbPath,
                dbPathExplicit,
                cancellationToken,
                status.ProjectRoot,
                projectRootSnapshotCaptured: true);
            StatusRuntimeMetadataResolvedForTesting.Value?.Invoke();
            status.ProjectRoot = runtime.ProjectRoot;
            status.GitHead = runtime.RuntimeHead;
            status.GitIsDirty = runtime.IsDirty;
            status.WorktreeHeadChanged = ResolveHeadChanged(
                runtime.RuntimeHead,
                runtime.RuntimeBranch,
                status.WorkspaceVerifiedHeadSha,
                status.IndexedHeadSha,
                status.IndexedHeadBranch,
                status.IndexedHeadBranchStampPresentSnapshot,
                status.IndexedHeadCommit,
                status.IndexedHeadCommitBranchSnapshot,
                status.IndexedHeadCommitBranchStampPresentSnapshot);
            status.GitIndexMayHideWorktreeChanges = ResolveGitIndexVisibility(
                status,
                runtime.ProjectRoot,
                evaluateOrdinaryFreshness,
                cancellationToken);
            if (runtime.ProjectRoot != null && !string.IsNullOrWhiteSpace(status.IndexedHeadSha))
            {
                status.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(
                    runtime.ProjectRoot,
                    status.IndexedHeadSha,
                    cancellationToken);
            }
            return;
        }

        var metadata = Resolve(dbPath, dbPathExplicit, cancellationToken);
        status.ProjectRoot = metadata.ProjectRoot;
        status.GitHead = metadata.RuntimeHead;
        status.GitIsDirty = metadata.IsDirty;
        status.IndexedHeadCommit = metadata.LegacyIndexedHead;
        status.WorkspaceVerifiedHeadSha = metadata.WorkspaceVerifiedHead;
        status.WorktreeHeadChanged = metadata.HeadChanged;
        status.GitIndexMayHideWorktreeChanges = ResolveGitIndexVisibility(
            status,
            metadata.ProjectRoot,
            evaluateOrdinaryFreshness,
            cancellationToken);
        // Keep commit-drift diagnostics tied to the latest-write SHA. Whole-workspace
        // freshness uses the separate verification stamp above, so these two provenance
        // signals remain explicit instead of silently substituting for each other.
        if (metadata.ProjectRoot != null && !string.IsNullOrWhiteSpace(status.IndexedHeadSha))
            status.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(metadata.ProjectRoot, status.IndexedHeadSha, cancellationToken);
    }

    private static bool? ResolveGitIndexVisibility(
        StatusResult status,
        string? projectRoot,
        bool evaluateOrdinaryFreshness,
        CancellationToken cancellationToken)
    {
        if (!ShouldProbeGitIndexVisibility(status, projectRoot, evaluateOrdinaryFreshness))
            return null;

        // Keep ordinary status cheap outside the checksum-reused no-op case. Only the
        // fallback proof needs to rule out index flags that can mask later changes.
        // checksum 再利用 no-op の fallback 証拠を使う場合だけ index flag を確認し、
        // それ以外の通常 status には追加の Git 列挙を行わない。
        return GitHelper.TryHasWorktreeVisibilityLimitingIndexFlags(projectRoot!, cancellationToken);
    }

    internal static bool ShouldProbeGitIndexVisibility(
        StatusResult status,
        string? projectRoot,
        bool evaluateOrdinaryFreshness)
    {
        if (!evaluateOrdinaryFreshness
            || projectRoot == null
            || status.GitIsDirty != false
            || status.WorktreeHeadChanged != false
            || !status.IndexedAt.HasValue
            || !status.LatestModified.HasValue
            || !status.LastWorkspaceFreshenedAt.HasValue
            || status.IndexedAt.Value >= status.LatestModified.Value
            || status.LastWorkspaceFreshenedAt.Value < status.LatestModified.Value)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(status.GitHead)
            && !string.IsNullOrWhiteSpace(status.WorkspaceVerifiedHeadSha)
            && !string.IsNullOrWhiteSpace(status.IndexedHeadSha)
            && string.Equals(status.GitHead, status.WorkspaceVerifiedHeadSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(status.WorkspaceVerifiedHeadSha, status.IndexedHeadSha, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enrich(
        RepoMapResult map,
        string dbPath,
        bool dbPathExplicit = false,
        CancellationToken cancellationToken = default)
    {
        var runtime = ResolveRuntime(
            dbPath,
            dbPathExplicit,
            cancellationToken,
            map.IndexedHeadSnapshot?.ProjectRoot,
            projectRootSnapshotCaptured: map.IndexedHeadSnapshot != null);
        map.ProjectRoot = runtime.ProjectRoot;
        map.GitHead = runtime.RuntimeHead;
        map.GitIsDirty = runtime.IsDirty;
        if (runtime.ProjectRoot == null || map.IndexedHeadSnapshot == null)
            return;

        var snapshot = map.IndexedHeadSnapshot;
        map.IndexedHeadCommit = snapshot.LegacyFullScanHead;
        map.WorkspaceVerifiedHeadSha = snapshot.WorkspaceVerifiedHead;
        map.IndexedHeadSha = snapshot.LatestIndexHead;
        map.IndexedHeadBranch = snapshot.LatestIndexBranch;
        map.IndexedHeadTimestamp = snapshot.LatestIndexTimestamp;
        map.WorktreeHeadChanged = ResolveHeadChanged(
            runtime.RuntimeHead,
            runtime.RuntimeBranch,
            snapshot.WorkspaceVerifiedHead,
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
        if (analysis.HeadMetadataSnapshotCaptured)
        {
            var runtime = ResolveRuntime(
                dbPath,
                dbPathExplicit,
                cancellationToken,
                analysis.ProjectRoot,
                projectRootSnapshotCaptured: true);
            AnalysisRuntimeMetadataResolvedForTesting.Value?.Invoke();
            analysis.ProjectRoot = runtime.ProjectRoot;
            analysis.GitHead = runtime.RuntimeHead;
            analysis.GitIsDirty = runtime.IsDirty;
            analysis.WorktreeHeadChanged = ResolveHeadChanged(
                runtime.RuntimeHead,
                runtime.RuntimeBranch,
                analysis.WorkspaceVerifiedHeadSha,
                analysis.IndexedHeadSha,
                analysis.IndexedHeadBranchSnapshot,
                analysis.IndexedHeadBranchStampPresentSnapshot,
                analysis.IndexedHeadCommit,
                analysis.IndexedHeadCommitBranchSnapshot,
                analysis.IndexedHeadCommitBranchStampPresentSnapshot);
            return;
        }

        var metadata = Resolve(dbPath, dbPathExplicit, cancellationToken);
        analysis.ProjectRoot = metadata.ProjectRoot;
        analysis.GitHead = metadata.RuntimeHead;
        analysis.GitIsDirty = metadata.IsDirty;
        analysis.IndexedHeadCommit = metadata.LegacyIndexedHead;
        analysis.WorkspaceVerifiedHeadSha = metadata.WorkspaceVerifiedHead;
        analysis.IndexedHeadSha = metadata.IndexedHeadSha;
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
            return new(null, null, null, null, null, null, null, null, null);

        var indexedHead = DbPathResolver.TryReadIndexedHeadCommit(dbPath);
        var workspaceVerifiedHead = DbPathResolver.TryReadWorkspaceVerifiedHeadSha(dbPath);
        var indexedHeadSha = DbPathResolver.TryReadIndexedHeadSha(dbPath);
        var indexedHeadBranch = DbPathResolver.TryReadIndexedHeadBranch(dbPath);
        var indexedHeadTimestamp = DbPathResolver.TryReadIndexedHeadTimestamp(dbPath);
        var hasIndexedHeadBranchStamp = DbPathResolver.TryHasIndexedHeadBranchStamp(dbPath);
        var indexedBranch = DbPathResolver.TryReadIndexedHeadCommitBranch(dbPath);
        var hasIndexedBranchStamp = DbPathResolver.TryHasIndexedHeadCommitBranchStamp(dbPath);
        var headChanged = ResolveHeadChanged(
            runtime.RuntimeHead,
            runtime.RuntimeBranch,
            workspaceVerifiedHead,
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
            workspaceVerifiedHead,
            indexedHeadSha,
            indexedHeadBranch,
            indexedHeadTimestamp,
            headChanged);
    }

    private static RuntimeWorkspaceMetadata ResolveRuntime(
        string dbPath,
        bool dbPathExplicit,
        CancellationToken cancellationToken,
        string? capturedProjectRoot = null,
        bool projectRootSnapshotCaptured = false)
    {
        var projectRoot = projectRootSnapshotCaptured
            ? DbPathResolver.ResolveProjectLocalRootForQuery(
                    dbPath,
                    dbPathExplicit,
                    capturedProjectRoot)
                ?? capturedProjectRoot
            : capturedProjectRoot ?? DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (projectRoot == null)
            return new(null, null, null, null);

        return new(
            projectRoot,
            GitHelper.TryGetHeadCommit(projectRoot, cancellationToken),
            GitHelper.TryGetHeadBranch(projectRoot, cancellationToken),
            GitHelper.TryIsWorktreeDirty(projectRoot, cancellationToken));
    }

    internal static bool? ResolveHeadChanged(
        string? runtimeHead,
        string? runtimeBranch,
        string? workspaceVerifiedHead,
        string? latestIndexedHead,
        string? latestIndexedBranch,
        bool latestIndexedBranchStampPresent,
        string? legacyIndexedHead,
        string? legacyIndexedBranch,
        bool legacyIndexedBranchStampPresent)
    {
        var comparisonHead = workspaceVerifiedHead ?? legacyIndexedHead;
        var workspaceVerificationMatchesLatest = workspaceVerifiedHead != null
            && string.Equals(workspaceVerifiedHead, latestIndexedHead, StringComparison.OrdinalIgnoreCase);
        var comparisonBranch = workspaceVerificationMatchesLatest
            ? latestIndexedBranch
            : workspaceVerifiedHead == null
                ? legacyIndexedBranch
                : null;
        var hasComparisonBranchStamp = workspaceVerificationMatchesLatest
            ? latestIndexedBranchStampPresent
            : workspaceVerifiedHead == null && legacyIndexedBranchStampPresent;
        // Detect a per-worktree branch / HEAD switch by comparing the runtime HEAD against
        // the whole-workspace verification stamp. Fall back to the older full-scan-only stamp
        // only for legacy DBs. Also compare the matching branch stamp when one is available
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
        string? WorkspaceVerifiedHead,
        string? IndexedHeadSha,
        string? IndexedHeadBranch,
        DateTimeOffset? IndexedHeadTimestamp,
        bool? HeadChanged);
}
