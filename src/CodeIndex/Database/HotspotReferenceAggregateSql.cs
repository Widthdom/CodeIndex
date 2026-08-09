using CodeIndex.Indexer;

namespace CodeIndex.Database;

internal readonly record struct HotspotReferenceAggregateIndexDefinition(
    string Name,
    string CreateSql);

/// <summary>
/// Owns the maintained per-file reference aggregate used by hotspot readers.
/// hotspot reader が使う file 単位の maintained reference aggregate を管理する。
/// </summary>
internal static class HotspotReferenceAggregateSql
{
    internal const string TableName = "hotspot_reference_counts";
    internal const string DeferredDirtyFilesTableName = "temp_hotspot_reference_dirty_files";

    internal const string ReferenceKindsSql = DbReader.CallGraphReferenceKindsSql;
    private static readonly string CSharpCommonQualifiedMemberCallNamesSql = string.Join(
        ", ",
        CSharpReferenceExtractor.CommonQualifiedMemberCallNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'"));

    internal const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS hotspot_reference_counts (
            file_id                 INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            lang                    TEXT NOT NULL,
            raw_symbol_name         TEXT NOT NULL,
            symbol_name             TEXT NOT NULL,
            symbol_segment_count    INTEGER NOT NULL,
            allow_leaf_fallback     INTEGER NOT NULL,
            reference_count         INTEGER NOT NULL,
            reference_score         REAL NOT NULL,
            PRIMARY KEY (
                file_id,
                lang,
                raw_symbol_name,
                symbol_name,
                symbol_segment_count,
                allow_leaf_fallback
            )
        )
        """;

    private static readonly HotspotReferenceAggregateIndexDefinition[] IndexDefinitions =
    [
        new(
            "idx_hotspot_reference_counts_global",
            "CREATE INDEX IF NOT EXISTS idx_hotspot_reference_counts_global ON hotspot_reference_counts(lang, symbol_name, symbol_segment_count)"),
        new(
            "idx_hotspot_reference_counts_file",
            "CREATE INDEX IF NOT EXISTS idx_hotspot_reference_counts_file ON hotspot_reference_counts(lang, file_id, symbol_name, symbol_segment_count)"),
        new(
            "idx_hotspot_reference_counts_leaf",
            "CREATE INDEX IF NOT EXISTS idx_hotspot_reference_counts_leaf ON hotspot_reference_counts(lang, file_id, raw_symbol_name, allow_leaf_fallback) WHERE allow_leaf_fallback = 1"),
        new(
            "idx_hotspot_reference_counts_rank",
            "CREATE INDEX IF NOT EXISTS idx_hotspot_reference_counts_rank ON hotspot_reference_counts(reference_score DESC, reference_count DESC, lang, symbol_name, symbol_segment_count)"),
    ];

    internal static IReadOnlyList<HotspotReferenceAggregateIndexDefinition> Indexes { get; }
        = Array.AsReadOnly(IndexDefinitions);

    internal static string BuildRefreshSql(bool singleFile, bool includeTestCheckpoint = false)
    {
        var deletePredicate = singleFile ? " WHERE file_id = @file_id" : string.Empty;
        var referencePredicate = singleFile ? " AND sr.file_id = @file_id" : string.Empty;
        return BuildRefreshSqlCore(deletePredicate, referencePredicate, includeTestCheckpoint);
    }

    internal static string BuildDeferredRefreshSql(bool includeTestCheckpoint = false)
    {
        const string dirtyFilePredicate = "SELECT file_id FROM temp_hotspot_reference_dirty_files";
        return BuildRefreshSqlCore(
            $" WHERE file_id IN ({dirtyFilePredicate})",
            $" AND sr.file_id IN ({dirtyFilePredicate})",
            includeTestCheckpoint);
    }

    private static string BuildRefreshSqlCore(
        string deletePredicate,
        string referencePredicate,
        bool includeTestCheckpoint)
    {
        var logicalReferenceKindSql = DbReader.GetLogicalReferenceKindSql("sr.reference_kind");
        var referenceWeightSql = DbReader.GetHotspotReferenceWeightSql("sr.reference_kind");
        var testCheckpointPredicate = includeTestCheckpoint
            ? " AND hotspot_refresh_test_checkpoint() = 0"
            : string.Empty;
        return $"""
            DELETE FROM hotspot_reference_counts{deletePredicate};

            INSERT INTO hotspot_reference_counts (
                file_id,
                lang,
                raw_symbol_name,
                symbol_name,
                symbol_segment_count,
                allow_leaf_fallback,
                reference_count,
                reference_score
            )
            WITH non_sql_raw_sites AS (
                SELECT sr.file_id,
                       f.lang,
                       sr.symbol_name AS raw_symbol_name,
                       CASE
                           WHEN f.lang = 'markdown' AND instr(sr.symbol_name, '#') > 0
                               THEN substr(sr.symbol_name, 1, instr(sr.symbol_name, '#') - 1)
                           ELSE sr.symbol_name
                       END AS symbol_name,
                       1 AS symbol_segment_count,
                       0 AS allow_leaf_fallback,
                       sr.line,
                       sr.column_number,
                       {logicalReferenceKindSql} AS logical_reference_kind,
                       {referenceWeightSql} AS reference_weight
                FROM symbol_references sr
                JOIN files f ON f.id = sr.file_id
                WHERE sr.reference_kind IN {ReferenceKindsSql}
                  AND sr.symbol_name IS NOT NULL
                  AND sr.symbol_name <> ''
                  AND NOT (
                      f.lang = 'csharp'
                      AND sr.reference_kind = 'call'
                      AND sr.symbol_name IN ({CSharpCommonQualifiedMemberCallNamesSql})
                      AND sr.target_qualifier IS NOT NULL
                      AND COALESCE(sr.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group')
                  )
                  AND f.lang != 'sql'{referencePredicate}{testCheckpointPredicate}
            ),
            non_sql_logical_sites AS (
                SELECT file_id,
                       lang,
                       MIN(raw_symbol_name) AS raw_symbol_name,
                       symbol_name,
                       symbol_segment_count,
                       allow_leaf_fallback,
                       line,
                       column_number,
                       logical_reference_kind,
                       MAX(reference_weight) AS reference_weight
                FROM non_sql_raw_sites
                GROUP BY file_id,
                         lang,
                         symbol_name,
                         symbol_segment_count,
                         allow_leaf_fallback,
                         line,
                         column_number,
                         logical_reference_kind
            ),
            sql_raw_sites AS (
                SELECT sr.file_id,
                       f.lang,
                       sr.symbol_name AS raw_symbol_name,
                       sql_resolve_reference_name_at(
                           sr.symbol_name,
                           COALESCE(sr.context, rl.context),
                           sr.container_name,
                           sr.column_number) AS symbol_name,
                       sql_resolve_reference_segment_count_at(
                           sr.symbol_name,
                           COALESCE(sr.context, rl.context),
                           sr.container_name,
                           sr.column_number) AS symbol_segment_count,
                       sql_allow_leaf_fallback_at(
                           sr.symbol_name,
                           COALESCE(sr.context, rl.context),
                           sr.container_name,
                           sr.column_number) AS allow_leaf_fallback,
                       sr.line,
                       sr.column_number,
                       {logicalReferenceKindSql} AS logical_reference_kind,
                       {referenceWeightSql} AS reference_weight
                FROM symbol_references sr
                JOIN files f ON f.id = sr.file_id
                LEFT JOIN reference_lines rl ON rl.id = sr.reference_line_id
                WHERE sr.reference_kind IN {ReferenceKindsSql}
                  AND sr.symbol_name IS NOT NULL
                  AND sr.symbol_name <> ''
                  AND f.lang = 'sql'{referencePredicate}{testCheckpointPredicate}
            ),
            sql_logical_sites AS (
                SELECT file_id,
                       lang,
                       MIN(raw_symbol_name) AS raw_symbol_name,
                       symbol_name,
                       symbol_segment_count,
                       MAX(allow_leaf_fallback) AS allow_leaf_fallback,
                       line,
                       column_number,
                       logical_reference_kind,
                       MAX(reference_weight) AS reference_weight
                FROM sql_raw_sites
                GROUP BY file_id,
                         lang,
                         symbol_name,
                         symbol_segment_count,
                         line,
                         column_number,
                         logical_reference_kind
            ),
            logical_sites AS (
                SELECT * FROM non_sql_logical_sites
                UNION ALL
                SELECT * FROM sql_logical_sites
            )
            SELECT file_id,
                   lang,
                   raw_symbol_name,
                   symbol_name,
                   symbol_segment_count,
                   allow_leaf_fallback,
                   COUNT(*) AS reference_count,
                   SUM(reference_weight) AS reference_score
            FROM logical_sites
            GROUP BY file_id,
                     lang,
                     raw_symbol_name,
                     symbol_name,
                     symbol_segment_count,
                     allow_leaf_fallback;
            """;
    }
}
