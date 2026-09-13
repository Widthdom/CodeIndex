using System.Globalization;

namespace CodeIndex.Database;

internal sealed record IndexedCallSite(
    long? SourceId, long? TargetId, string Path, int Line, int Column, int Length,
    string Name, string? ResolutionState);

public partial class DbReader
{
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
