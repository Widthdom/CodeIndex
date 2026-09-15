using System.Globalization;

namespace CodeIndex.Database;

internal sealed record IndexedCallSite(
    long? SourceId, long? TargetId, string Path, int Line, int Column, int Length,
    string Name, string? ResolutionState);

public partial class DbReader
{
    // Position and endpoint lookups return metadata only. Reconstructing overlapping
    // definition excerpts would bypass the LSP request's bounded source-file cache.
    internal List<SymbolResult> GetCallHierarchyDeclarations(string path, int line, int limit) =>
        GetSymbolsAtLine(path, line, limit, kind: null, lang: null);

    internal SymbolResult? GetCallHierarchySymbol(long symbolId)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = $"""
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")},
                   {GetSymbolColumnSql("end_line", "s.line")},
                   {GetSymbolColumnSql("body_start_line")},
                   {GetSymbolColumnSql("body_end_line")},
                   {GetSymbolColumnSql("signature")},
                   {GetSymbolColumnSql("container_kind")},
                   {GetSymbolColumnSql("container_name")},
                   {GetSymbolColumnSql("visibility")},
                   {GetSymbolColumnSql("return_type")}, s.id,
                   {GetSymbolColumnSql("container_qualified_name")},
                   {GetSymbolColumnSql("sub_kind")},
                   {GetSymbolColumnSql("start_column")}
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.id = @symbol
            LIMIT 1
            """;
        SqliteCommandPolicy.Add(command, "@symbol", symbolId);
        using var rows = command.ExecuteTrackedReader();
        return rows.TrackedRead() ? ReadSymbolResult(rows) : null;
    }

    // LSP needs individual sites and both endpoints, rather than the grouped CLI rows.
    // Use the same persisted reference identities and call kind; never bind by name.
    internal List<IndexedCallSite> GetCallHierarchySites(long symbolId, bool incoming, int limit)
    {
        using var command = _conn.CreateCommand();
        var identityFilter = incoming
            ? """
                (r.target_symbol_id = @symbol OR EXISTS (
                    SELECT 1 FROM symbol_reference_candidates c
                    WHERE c.reference_id = r.id AND c.symbol_id = @symbol))
                """
            : "r.source_symbol_id = @symbol";
        command.CommandText = $"""
            SELECT r.source_symbol_id, r.target_symbol_id, f.path, r.line,
                   r.column_number, r.span_length, r.symbol_name, r.resolution_state
            FROM symbol_references r
            JOIN files f ON f.id = r.file_id
            WHERE {identityFilter} AND r.reference_kind = 'call'
            ORDER BY r.id
            LIMIT @limit
            """;
        SqliteCommandPolicy.Add(command, "@symbol", symbolId);
        SqliteCommandPolicy.Add(command, "@limit", limit);
        var sites = new List<IndexedCallSite>();
        using var rows = command.ExecuteTrackedReader();
        while (rows.TrackedRead())
        {
            Cancellation.ThrowIfCancellationRequested();
            sites.Add(new IndexedCallSite(
                rows.IsDBNull(0) ? null : rows.GetInt64(0),
                rows.IsDBNull(1) ? null : rows.GetInt64(1),
                rows.GetString(2), rows.GetInt32(3), rows.GetInt32(4),
                rows.IsDBNull(5) ? 0 : rows.GetInt32(5), rows.GetString(6),
                rows.IsDBNull(7) ? null : rows.GetString(7)));
        }
        return sites;
    }

    internal bool CallHierarchyIdentityAvailable =>
        !_indexNewerThanReader && HasCurrentReferenceIdentityContractForRead()
        && _referenceColumns.Contains("source_symbol_id")
        && _referenceColumns.Contains("span_length")
        && HasTable("symbol_reference_candidates");

    internal string GetCallHierarchyGeneration() => string.Create(
        CultureInfo.InvariantCulture,
        $"{GetSymbolSelectorGenerationIdentity()}\n{ExecuteScalar("PRAGMA data_version")}\n{ExecuteScalar("SELECT total_changes()")}");
}
