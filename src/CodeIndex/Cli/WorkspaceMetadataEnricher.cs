using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Enrich repo/status responses with workspace-level freshness metadata.
/// ワークスペース単位の鮮度メタデータで repo/status レスポンスを補強する。
/// </summary>
public static class WorkspaceMetadataEnricher
{
    public static void Enrich(StatusResult status, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty, indexedHead, headChanged) =>
        {
            status.ProjectRoot = root;
            status.GitHead = head;
            status.GitIsDirty = dirty;
            status.IndexedHeadCommit = indexedHead;
            status.WorktreeHeadChanged = headChanged;
        });

    public static void Enrich(RepoMapResult map, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty, indexedHead, headChanged) =>
        {
            map.ProjectRoot = root;
            map.GitHead = head;
            map.GitIsDirty = dirty;
            map.IndexedHeadCommit = indexedHead;
            map.WorktreeHeadChanged = headChanged;
        });

    public static void Enrich(SymbolAnalysisResult analysis, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty, indexedHead, headChanged) =>
        {
            analysis.ProjectRoot = root;
            analysis.GitHead = head;
            analysis.GitIsDirty = dirty;
            analysis.IndexedHeadCommit = indexedHead;
            analysis.WorktreeHeadChanged = headChanged;
        });

    /// <summary>
    /// Resolve workspace metadata once and apply it via callback.
    /// ワークスペースメタデータを一度解決し、コールバックで適用する。
    /// </summary>
    private static void Apply(string dbPath, bool dbPathExplicit, Action<string?, string?, bool?, string?, bool?> setter)
    {
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (projectRoot == null)
        {
            setter(null, null, null, null, null);
            return;
        }

        var runtimeHead = GitHelper.TryGetHeadCommit(projectRoot);
        var dirty = GitHelper.TryIsWorktreeDirty(projectRoot);
        var indexedHead = DbPathResolver.TryReadIndexedHeadCommit(dbPath);
        // Detect a per-worktree branch / HEAD switch by comparing the runtime HEAD against
        // the HEAD captured at index time. Only meaningful when both sides have a value —
        // legacy DBs or projects indexed outside git report null and must not trigger a
        // false-positive switch warning. Issue #1512.
        // worktree 内の branch / HEAD 切替検出。index 時点と現在で HEAD を突き合わせる。
        // 両側が値を持つ場合のみ有意義（legacy DB や非 git は null になり誤検出を避ける）。
        bool? headChanged = (indexedHead != null && runtimeHead != null)
            ? !string.Equals(indexedHead, runtimeHead, StringComparison.OrdinalIgnoreCase)
            : (bool?)null;

        setter(projectRoot, runtimeHead, dirty, indexedHead, headChanged);
    }
}
