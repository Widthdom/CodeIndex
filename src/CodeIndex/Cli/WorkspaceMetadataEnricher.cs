using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Enrich repo/status responses with workspace-level freshness metadata.
/// ワークスペース単位の鮮度メタデータで repo/status レスポンスを補強する。
/// </summary>
public static class WorkspaceMetadataEnricher
{
    public static void Enrich(StatusResult status, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty) =>
        {
            status.ProjectRoot = root;
            status.GitHead = head;
            status.GitIsDirty = dirty;
            // #1509: compare the current HEAD against the SHA stamped at index time. Only
            // makes sense when both sides are known; otherwise leave the field null so the
            // CLI/MCP consumer can render "indexed at <sha>" without a misleading 0/N hint.
            // #1509: index 時 HEAD と現 HEAD を比較し、両方判明している時のみ N を載せる。
            if (root != null && !string.IsNullOrWhiteSpace(status.IndexedHeadSha))
                status.CommitsAheadOfIndexedHead = GitHelper.TryCountCommitsAhead(root, status.IndexedHeadSha);
        });

    public static void Enrich(RepoMapResult map, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty) => { map.ProjectRoot = root; map.GitHead = head; map.GitIsDirty = dirty; });

    public static void Enrich(SymbolAnalysisResult analysis, string dbPath, bool dbPathExplicit = false) =>
        Apply(dbPath, dbPathExplicit, (root, head, dirty) => { analysis.ProjectRoot = root; analysis.GitHead = head; analysis.GitIsDirty = dirty; });

    /// <summary>
    /// Resolve workspace metadata once and apply it via callback.
    /// ワークスペースメタデータを一度解決し、コールバックで適用する。
    /// </summary>
    private static void Apply(string dbPath, bool dbPathExplicit, Action<string?, string?, bool?> setter)
    {
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (projectRoot == null)
        {
            setter(null, null, null);
            return;
        }

        setter(projectRoot, GitHelper.TryGetHeadCommit(projectRoot), GitHelper.TryIsWorktreeDirty(projectRoot));
    }
}
