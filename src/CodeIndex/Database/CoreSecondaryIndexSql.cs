namespace CodeIndex.Database;

internal readonly record struct CoreSecondaryIndexDefinition(
    string Name,
    string CreateSql);

/// <summary>
/// Canonical DDL for the language-neutral secondary indexes on files, chunks,
/// file issues, and symbols. Authoritative empty-database CLI loads may defer
/// the subset that raw persistence does not need; UNIQUE autoindexes and the
/// per-file symbol lookup used while inserting references remain active.
/// </summary>
internal static class CoreSecondaryIndexSql
{
    private const string ReferenceSourceLookupIndexName = "idx_symbols_file";

    private static readonly CoreSecondaryIndexDefinition[] IndexDefinitions =
    [
        new(
            "idx_files_lang",
            "CREATE INDEX IF NOT EXISTS idx_files_lang ON files(lang)"),
        new(
            "idx_files_modified",
            "CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified)"),
        new(
            "idx_files_generated",
            "CREATE INDEX IF NOT EXISTS idx_files_generated ON files(generated)"),
        new(
            "idx_files_checksum",
            "CREATE INDEX IF NOT EXISTS idx_files_checksum ON files(checksum)"),
        // The UNIQUE path constraint supplies the BINARY exact index. This
        // separate index is only for bounded ASCII case-alias candidates.
        new(
            "idx_files_path_nocase",
            "CREATE INDEX IF NOT EXISTS idx_files_path_nocase ON files(path COLLATE NOCASE)"),
        new(
            "idx_file_issues_file_kind",
            "CREATE INDEX IF NOT EXISTS idx_file_issues_file_kind ON file_issues(file_id, kind)"),
        new(
            "idx_chunks_file",
            "CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id)"),
        new(
            "idx_chunks_file_end_start_nonnull",
            "CREATE INDEX IF NOT EXISTS idx_chunks_file_end_start_nonnull ON chunks(file_id, end_line, start_line, chunk_index) WHERE content IS NOT NULL"),
        new(
            "idx_chunks_file_start_chunk_nonnull",
            "CREATE INDEX IF NOT EXISTS idx_chunks_file_start_chunk_nonnull ON chunks(file_id, start_line, chunk_index, end_line) WHERE content IS NOT NULL"),
        new(
            "idx_symbols_name",
            "CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name)"),
        // Legacy and partially migrated databases still use this NOCASE path.
        new(
            "idx_symbols_name_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE)"),
        new(
            "idx_symbols_file",
            "CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id)"),
        new(
            "idx_symbols_start",
            "CREATE INDEX IF NOT EXISTS idx_symbols_start ON symbols(start_line)"),
        new(
            "idx_symbols_file_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbols_file_kind ON symbols(file_id, kind)"),
        new(
            "idx_files_lang_modified",
            "CREATE INDEX IF NOT EXISTS idx_files_lang_modified ON files(lang, modified)"),
        new(
            "idx_symbols_kind",
            "CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind)"),
        new(
            "idx_symbols_visibility",
            "CREATE INDEX IF NOT EXISTS idx_symbols_visibility ON symbols(visibility)"),
        new(
            "idx_symbols_name_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbols_name_folded ON symbols(name_folded)"),
        new(
            "idx_symbols_display_name_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbols_display_name_folded ON symbols(display_name_folded) WHERE display_name_folded IS NOT NULL"),
        new(
            "idx_symbols_file_name_folded",
            "CREATE INDEX IF NOT EXISTS idx_symbols_file_name_folded ON symbols(file_id, name_folded)"),
        new(
            "idx_symbols_file_name_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbols_file_name_nocase ON symbols(file_id, name COLLATE NOCASE)"),
        new(
            "idx_symbols_name_folded_container_name_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_name_nocase ON symbols(name_folded, container_name COLLATE NOCASE)"),
        new(
            "idx_symbols_name_folded_container_qualified_name_nocase",
            "CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_qualified_name_nocase ON symbols(name_folded, container_qualified_name COLLATE NOCASE)"),
    ];

    internal static IReadOnlyList<CoreSecondaryIndexDefinition> All { get; }
        = Array.AsReadOnly(IndexDefinitions);

    /// <summary>
    /// Indexes whose maintenance can be postponed until raw file persistence is
    /// complete. idx_symbols_file deliberately remains available because every
    /// fresh reference resolves its containing source symbol by file while it is
    /// inserted; without it, that correlated lookup scans the complete symbol table.
    /// </summary>
    internal static IReadOnlyList<CoreSecondaryIndexDefinition> DeferredDuringAuthoritativeFreshLoad { get; }
        = Array.AsReadOnly(
            IndexDefinitions
                .Where(static definition => definition.Name != ReferenceSourceLookupIndexName)
                .ToArray());

    internal static CoreSecondaryIndexDefinition GetRequired(string name)
    {
        foreach (var definition in IndexDefinitions)
        {
            if (string.Equals(definition.Name, name, StringComparison.Ordinal))
                return definition;
        }

        throw new InvalidOperationException($"Unknown core secondary index: {name}");
    }
}
