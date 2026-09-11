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

    private static string DependencyCycleDeclaredTypeNodeSql(string symbol)
        => $"CASE WHEN {symbol}.is_partial_declaration = 1 AND NULLIF({symbol}.family_key, '') IS NOT NULL THEN 'csharp-type:' || codeindex_partial_family_id({symbol}.family_key) ELSE 'csharp-type:symbol:' || {symbol}.id END";

    private static string DependencyCycleTypeNodeSql(string symbol, string file)
    {
        const string typeKinds = "('class', 'struct', 'interface', 'record', 'enum')";
        const string ownerName = "COALESCE(type_owner.container_qualified_name || '.', '') || type_owner.name";
        // Resolve the nearest containing type, including references in local functions.
        // A non-partial nested type can carry its parent's hotspot key; only the
        // owning declaration itself decides whether cross-file grouping is valid.
        return $"""
            CASE WHEN {file}.lang <> 'csharp' OR {symbol}.kind IN ('namespace', 'import')
            THEN 'file:' || {file}.path
            WHEN {symbol}.kind IN {typeKinds}
            THEN {DependencyCycleDeclaredTypeNodeSql(symbol)}
            WHEN {symbol}.container_kind IN {typeKinds} AND NULLIF({symbol}.family_key, '') IS NOT NULL
              AND EXISTS (SELECT 1 FROM symbols type_owner
                  WHERE type_owner.file_id = {symbol}.file_id
                    AND type_owner.kind IN {typeKinds} AND type_owner.is_partial_declaration = 1
                    AND type_owner.family_key = {symbol}.family_key
                    AND ({ownerName}) = {symbol}.container_qualified_name)
            THEN 'csharp-type:' || codeindex_partial_family_id({symbol}.family_key)
            ELSE COALESCE((
                SELECT CASE WHEN COUNT(*) = 1 THEN MIN(node_id) END
                FROM (
                    SELECT {DependencyCycleDeclaredTypeNodeSql("type_owner")} AS node_id,
                           DENSE_RANK() OVER (ORDER BY LENGTH({ownerName}) DESC,
                               type_owner.start_line DESC, COALESCE(type_owner.start_column, 0) DESC) AS owner_rank
                    FROM symbols type_owner
                    WHERE type_owner.file_id = {symbol}.file_id
                      AND type_owner.kind IN {typeKinds}
                      AND type_owner.start_line <= {symbol}.start_line
                      AND type_owner.end_line >= {symbol}.end_line
                      AND (type_owner.start_line < {symbol}.start_line
                           OR COALESCE(type_owner.start_column, 0) <= COALESCE({symbol}.start_column, 2147483647))
                      AND ({symbol}.container_qualified_name = ({ownerName})
                           OR SUBSTR({symbol}.container_qualified_name, 1, LENGTH({ownerName}) + 1) = ({ownerName}) || '.')
                ) WHERE owner_rank = 1
            ), 'file:' || {file}.path) END
            """;
    }

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

    internal (List<string> Nodes, long Count) ListDependencyCycleMappingNodes(int offset, int limit)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = $"""
            WITH nodes AS (
                SELECT 'file:' || path AS node FROM files
                UNION
                SELECT {DependencyCycleDeclaredTypeNodeSql("s")} AS node
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind IN ('class', 'struct', 'interface', 'record', 'enum')
            )
            SELECT node, COUNT(*) OVER () FROM nodes ORDER BY node COLLATE BINARY LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        using var cancellation = Cancellation.Register(command.Cancel);
        using var rows = command.ExecuteTrackedReader();
        var nodes = new List<string>();
        long count = 0;
        while (rows.TrackedRead())
        {
            Cancellation.ThrowIfCancellationRequested();
            nodes.Add(rows.GetString(0));
            count = rows.GetInt64(1);
        }
        return (nodes, count);
    }

    internal JsonObject DescribeDependencyCycleNode(string node, int offset = 0, int pathLimit = 20)
    {
        if (node.StartsWith("file:", StringComparison.Ordinal))
        {
            using var fileCommand = _conn.CreateCommand();
            fileCommand.CommandText = "SELECT EXISTS (SELECT 1 FROM files WHERE path = @path COLLATE BINARY)";
            fileCommand.Parameters.AddWithValue("@path", node[5..]);
            using var fileCancellation = Cancellation.Register(fileCommand.Cancel);
            var exists = Convert.ToInt64(fileCommand.ExecuteScalar()) != 0;
            return new JsonObject
            {
                ["id"] = node,
                ["kind"] = "file_scope",
                ["files"] = exists && offset == 0 ? new JsonArray(node[5..]) : new JsonArray(),
                ["file_count"] = exists ? 1 : 0,
                ["files_truncated"] = false,
                ["files_omitted_count"] = 0,
                ["file_limit"] = pathLimit,
            };
        }
        using var command = _conn.CreateCommand();
        command.CommandText = $"""
            WITH members AS (
                SELECT DISTINCT f.path, COALESCE(CASE WHEN s.is_partial_declaration = 1 THEN s.family_key END,
                    s.container_qualified_name || '.' || s.name, s.name) AS family_key
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind IN ('class', 'struct', 'interface', 'record', 'enum')
                  AND {DependencyCycleDeclaredTypeNodeSql("s")} = @node
            )
            SELECT path, family_key, COUNT(*) OVER () FROM members ORDER BY path COLLATE BINARY LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@node", node);
        command.Parameters.AddWithValue("@limit", pathLimit);
        command.Parameters.AddWithValue("@offset", offset);
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
            ["files_truncated"] = count > paths.Count + offset,
            ["files_omitted_count"] = Math.Max(0, count - paths.Count - offset),
            ["mapping_scope"] = "indexed_declarations",
        };
    }
}
