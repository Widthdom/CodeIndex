using System.Text.Json.Nodes;

namespace CodeIndex.Database;

public partial class DbReader
{
    internal int DependencyCycleRawCandidateCount { get; private set; }
    internal bool DependencyCycleGroupingReady => _hotspotFamilyReadyLanguages.Contains("csharp")
        && HasCurrentReferenceIdentityContractForRead()
        && _referenceColumns.Contains("source_symbol_id")
        && _referenceColumns.Contains("target_symbol_id")
        && _symbolColumns.Contains("family_key")
        && _symbolColumns.Contains("is_partial_declaration");

    private static string DependencyCycleTypeNodeSql(string symbol, string file)
        => $"""
            CASE WHEN {file}.lang = 'csharp' AND {symbol}.kind IN ('class', 'struct', 'interface', 'record', 'enum')
              AND COALESCE({symbol}.is_partial_declaration, 0) <> 1
            THEN 'csharp-type:symbol:' || {symbol}.id
            WHEN {file}.lang = 'csharp' AND {symbol}.kind NOT IN ('namespace', 'import')
              AND NULLIF({symbol}.family_key, '') IS NOT NULL
              AND EXISTS (SELECT 1 FROM symbols type_owner
                  WHERE type_owner.file_id = {symbol}.file_id
                    AND type_owner.family_key = {symbol}.family_key
                    AND type_owner.kind IN ('class', 'struct', 'interface', 'record')
                    AND type_owner.is_partial_declaration = 1)
            THEN 'csharp-type:' || codeindex_partial_family_id({symbol}.family_key)
            WHEN {file}.lang = 'csharp' AND {symbol}.kind IN ('class', 'struct', 'interface', 'record', 'enum')
            THEN 'csharp-type:symbol:' || {symbol}.id
            WHEN {file}.lang = 'csharp' AND {symbol}.container_kind IN ('class', 'struct', 'interface', 'record', 'enum')
            THEN COALESCE((SELECT CASE WHEN COUNT(*) = 1 THEN 'csharp-type:symbol:' || MIN(type_owner.id) END
                FROM symbols type_owner
                WHERE type_owner.file_id = {symbol}.file_id
                  AND type_owner.kind IN ('class', 'struct', 'interface', 'record', 'enum')
                  AND type_owner.name = {symbol}.container_name
                  AND COALESCE(type_owner.container_qualified_name || '.', '') || type_owner.name = {symbol}.container_qualified_name
                  AND type_owner.start_line <= {symbol}.start_line AND type_owner.end_line >= {symbol}.end_line),
                'file:' || {file}.path)
            ELSE 'file:' || {file}.path END
            """;

    private string DependencyCycleSourceNodeSql()
        => $"COALESCE((SELECT {DependencyCycleTypeNodeSql("owner", "src")} FROM symbols owner WHERE owner.id = r.source_symbol_id AND owner.file_id = src.id), 'file:' || src.path)";

    private string DependencyCycleTargetNodeSql()
        => $"""
            CASE WHEN (r.resolution_state = 'resolved' AND r.target_symbol_id = s.id)
                OR (r.resolution_state = 'resolved_group' AND EXISTS (
                    SELECT 1 FROM symbol_reference_candidates identity_candidate
                    WHERE identity_candidate.reference_id = r.id AND identity_candidate.symbol_id = s.id))
            THEN {DependencyCycleTypeNodeSql("s", "dst")}
            ELSE 'file:' || dst.path END
            """;

    internal JsonObject DescribeDependencyCycleNode(string node)
    {
        const int pathLimit = 20;
        if (node.StartsWith("file:", StringComparison.Ordinal))
            return new JsonObject
            {
                ["id"] = node,
                ["kind"] = "file_scope",
                ["files"] = new JsonArray(node[5..]),
                ["file_count"] = 1,
                ["files_truncated"] = false,
                ["files_omitted_count"] = 0,
                ["file_limit"] = pathLimit,
            };
        using var command = _conn.CreateCommand();
        command.CommandText = $"""
            WITH members AS (
                SELECT DISTINCT f.path, COALESCE(CASE WHEN s.is_partial_declaration = 1 THEN s.family_key END,
                    s.container_qualified_name || '.' || s.name, s.name) AS family_key
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind IN ('class', 'struct', 'interface', 'record', 'enum')
                  AND {DependencyCycleTypeNodeSql("s", "f")} = @node
            )
            SELECT path, family_key, COUNT(*) OVER () FROM members ORDER BY path LIMIT @limit
            """;
        command.Parameters.AddWithValue("@node", node);
        command.Parameters.AddWithValue("@limit", pathLimit);
        using var cancellation = Cancellation.Register(command.Cancel);
        using var rows = command.ExecuteTrackedReader();
        var paths = new JsonArray();
        var count = 0;
        string? name = null;
        while (rows.TrackedRead())
        {
            Cancellation.ThrowIfCancellationRequested();
            paths.Add(rows.GetString(0));
            name ??= rows.GetString(1);
            count = rows.GetInt32(2);
        }
        return new JsonObject
        {
            ["id"] = node,
            ["kind"] = "csharp_type",
            ["family_identity"] = name,
            ["files"] = paths,
            ["file_count"] = count,
            ["file_limit"] = pathLimit,
            ["files_truncated"] = count > paths.Count,
            ["files_omitted_count"] = count - paths.Count,
            ["mapping_scope"] = "indexed_declarations",
        };
    }
}
