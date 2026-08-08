namespace CodeIndex.Database;

internal readonly record struct ReferenceSecondaryIndexDefinition(
    string Name,
    string CreateSql,
    bool RequiresFoldedColumns = false);

/// <summary>
/// Canonical DDL for secondary indexes on <c>symbol_references</c>.
/// The raw-persistence set stays available while bulk extraction is writing rows; the
/// deferred set is safe to rebuild once, before reference-graph finalization begins.
/// </summary>
internal static class ReferenceSecondaryIndexSql
{
    private static readonly ReferenceSecondaryIndexDefinition[] RawPersistenceRequiredDefinitions =
    [
        new(
            "idx_symbol_refs_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_file ON symbol_references(file_id)"),
        // Deleting a reference-line row applies ON DELETE SET NULL to matching references.
        // Keep this probe indexed during file replacement and stale-file cleanup.
        new(
            "idx_symbol_refs_reference_line",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_reference_line ON symbol_references(reference_line_id)"),
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] DeferredDuringBulkLoadDefinitions =
    [
        new(
            "idx_symbol_refs_name",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name ON symbol_references(symbol_name)"),
        new(
            "idx_symbol_refs_container",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container ON symbol_references(container_name)"),
        new(
            "idx_symbol_refs_container_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)"),
        new(
            "idx_symbol_refs_name_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind ON symbol_references(symbol_name, reference_kind)"),
        new(
            "idx_symbol_refs_name_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file ON symbol_references(symbol_name, file_id)"),
        new(
            "idx_symbol_refs_mutual_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded, reference_kind, is_self_reference)",
            RequiresFoldedColumns: true),
        // NOCASE indexes keep exact reference/caller/callee queries bounded on legacy or
        // partially migrated databases whose Unicode-folded columns are not authoritative.
        new(
            "idx_symbol_refs_name_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase ON symbol_references(symbol_name COLLATE NOCASE)"),
        new(
            "idx_symbol_refs_container_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE)"),
        new(
            "idx_symbol_refs_name_nocase_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)"),
        new(
            "idx_symbol_refs_name_nocase_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)"),
        new(
            "idx_symbol_refs_container_nocase_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)"),
        // Folded indexes are the authoritative Unicode-aware exact-match paths once the
        // folded-name readiness contract is stamped.
        new(
            "idx_symbol_refs_symbol_name_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded ON symbol_references(symbol_name_folded)",
            RequiresFoldedColumns: true),
        new(
            "idx_symbol_refs_container_name_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded ON symbol_references(container_name_folded)",
            RequiresFoldedColumns: true),
        new(
            "idx_symbol_refs_symbol_name_folded_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)",
            RequiresFoldedColumns: true),
        new(
            "idx_symbol_refs_symbol_name_folded_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)",
            RequiresFoldedColumns: true),
        new(
            "idx_symbol_refs_container_name_folded_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)",
            RequiresFoldedColumns: true),
        new(
            "idx_symbol_refs_source_symbol",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)"),
        new(
            "idx_symbol_refs_target_symbol",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)"),
        // Mutual-recursion refresh probes the reverse of each resolved edge. Restrict the
        // covering index to rows that can participate so unresolved rows add no steady-state
        // storage cost.
        new(
            "idx_symbol_refs_resolved_source_target_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind) WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL"),
    ];

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> RawPersistenceRequired { get; }
        = Array.AsReadOnly(RawPersistenceRequiredDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> DeferredDuringBulkLoad { get; }
        = Array.AsReadOnly(DeferredDuringBulkLoadDefinitions);

    internal static IEnumerable<ReferenceSecondaryIndexDefinition> All
    {
        get
        {
            foreach (var definition in RawPersistenceRequiredDefinitions)
                yield return definition;
            foreach (var definition in DeferredDuringBulkLoadDefinitions)
                yield return definition;
        }
    }
}
