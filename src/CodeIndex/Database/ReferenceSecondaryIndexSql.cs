namespace CodeIndex.Database;

internal readonly record struct ReferenceSecondaryIndexDefinition(
    string Name,
    string CreateSql,
    bool RequiresFoldedColumns = false);

/// <summary>
/// Canonical DDL for secondary indexes used by reference persistence and graph queries.
/// The raw-persistence set stays available while bulk extraction is writing rows; the
/// candidate reverse lookup is dropped only when candidate materialization begins; the
/// graph-finalization set is restored immediately before mutual-recursion evaluation, and
/// the remaining query set is restored after graph finalization completes.
/// </summary>
internal static class ReferenceSecondaryIndexSql
{
    private static readonly string[] RetiredDefinitions =
    [
        // These single-column indexes are strict left prefixes of retained composite
        // indexes with the same collation. Keeping both multiplies rebuild time and disk
        // usage without adding a distinct seek path.
        "idx_symbol_refs_name",
        "idx_symbol_refs_container",
        "idx_symbol_refs_name_nocase",
        "idx_symbol_refs_container_nocase",
        "idx_symbol_refs_symbol_name_folded",
        "idx_symbol_refs_container_name_folded",
        // The replacement partial index contains only unresolved call-graph edges.
        "idx_symbol_refs_mutual_folded",
    ];

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

    private static readonly ReferenceSecondaryIndexDefinition[] GraphFinalizationRequiredDefinitions =
    [
        // Mutual-recursion refresh is the first post-persistence phase that needs reverse
        // unresolved-edge probes. Keep the partial predicate aligned with its explicit plan.
        new(
            "idx_symbol_refs_unresolved_mutual_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_unresolved_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded) WHERE source_symbol_id IS NULL AND target_symbol_id IS NULL AND is_self_reference = 0 AND container_name_folded IS NOT NULL AND container_name_folded <> '' AND symbol_name_folded IS NOT NULL AND symbol_name_folded <> '' AND reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')",
            RequiresFoldedColumns: true),
        // Legacy rows can lack folded values until their backfill contract is authoritative.
        // Retain the NOCASE reverse-call path during mutual-recursion finalization.
        new(
            "idx_symbol_refs_container_nocase_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)"),
        // Mutual-recursion refresh probes the reverse of each resolved edge. Restrict the
        // covering index to rows that can participate so unresolved rows add no steady-state
        // storage cost.
        new(
            "idx_symbol_refs_resolved_source_target_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind) WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL"),
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] RemainingQueryBeforeDeferredGraphDefinitions =
    [
        new(
            "idx_symbol_refs_container_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)"),
        new(
            "idx_symbol_refs_name_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind ON symbol_references(symbol_name, reference_kind)"),
        new(
            "idx_symbol_refs_name_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file ON symbol_references(symbol_name, file_id)"),
        // NOCASE indexes keep exact reference/caller/callee queries bounded on legacy or
        // partially migrated databases whose Unicode-folded columns are not authoritative.
        new(
            "idx_symbol_refs_name_nocase_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)"),
        new(
            "idx_symbol_refs_name_nocase_file",
            "CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)"),
        // Folded indexes are the authoritative Unicode-aware exact-match paths once the
        // folded-name readiness contract is stamped.
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
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] CandidatePopulationDeferredDefinitions =
    [
        // Candidate materialization and resolution use the primary key's reference_id
        // prefix. Defer the reverse symbol lookup so bulk graph refresh can populate the
        // candidate table without maintaining a second B-tree row by row.
        new(
            "idx_symbol_ref_candidates_symbol",
            "CREATE INDEX IF NOT EXISTS idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)"),
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] RemainingQueryDefinitions =
    [
        .. RemainingQueryBeforeDeferredGraphDefinitions,
        .. CandidatePopulationDeferredDefinitions,
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] DeferredGraphPreparationDefinitions =
    [
        .. GraphFinalizationRequiredDefinitions,
        .. RemainingQueryBeforeDeferredGraphDefinitions,
    ];

    private static readonly ReferenceSecondaryIndexDefinition[] DeferredDuringBulkLoadDefinitions =
    [
        .. GraphFinalizationRequiredDefinitions,
        .. RemainingQueryDefinitions,
    ];

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> RawPersistenceRequired { get; }
        = Array.AsReadOnly(RawPersistenceRequiredDefinitions);

    internal static IReadOnlyList<string> Retired { get; }
        = Array.AsReadOnly(RetiredDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> DeferredDuringBulkLoad { get; }
        = Array.AsReadOnly(DeferredDuringBulkLoadDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> GraphFinalizationRequired { get; }
        = Array.AsReadOnly(GraphFinalizationRequiredDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> DeferredGraphPreparation { get; }
        = Array.AsReadOnly(DeferredGraphPreparationDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> CandidatePopulationDeferred { get; }
        = Array.AsReadOnly(CandidatePopulationDeferredDefinitions);

    internal static IReadOnlyList<ReferenceSecondaryIndexDefinition> RemainingQuery { get; }
        = Array.AsReadOnly(RemainingQueryDefinitions);

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
