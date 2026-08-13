namespace CodeIndex.Database;

public partial class DbReader
{
    private static DependencySqlFragment BuildDependencyFinalSql(DependencyQueryRequest request)
    {
        var sql = new DependencySqlFragmentBuilder();
        AppendDependencyEdgeTotals(sql, request.SuppressDependencyNoise);
        AppendDependencyEvidence(sql);
        AppendDependencySymbolsAndSelect(sql, request.SuppressDependencyNoise);
        return sql.Build();
    }

    private static void AppendDependencyEdgeTotals(
        DependencySqlFragmentBuilder sql,
        bool suppressDependencyNoise)
    {
        var orderSql = suppressDependencyNoise
            ? "retained_reference_count DESC, reference_count DESC, source_path, target_path"
            : "reference_count DESC, source_path, target_path";
        sql.Append(@"
            ),
            edge_totals AS (
                SELECT source_path,
                       target_path,
                       SUM(ref_count) AS reference_count,
                       SUM(CASE WHEN origin = 'markdown_heading_name_match' THEN 0 ELSE ref_count END) AS retained_reference_count
                FROM edges
                GROUP BY source_path, target_path
            ),
            limited_edge_totals AS (
                SELECT source_path,
                       target_path,
                       reference_count,
                       retained_reference_count
                FROM edge_totals
                ORDER BY " + orderSql + @"
                LIMIT @limit
            ),");
    }

    private static void AppendDependencyEvidence(DependencySqlFragmentBuilder sql)
    {
        sql.Append(@"
            edge_evidence_rows AS (
                SELECT edges.source_path,
                       edges.target_path,
                       edges.source_lang,
                       edges.origin,
                       edges.raw_reference_kind,
                       edges.target_kind,
                       SUM(edges.ref_count) AS evidence_reference_count
                FROM edges
                JOIN limited_edge_totals
                  ON limited_edge_totals.source_path = edges.source_path
                 AND limited_edge_totals.target_path = edges.target_path
                GROUP BY edges.source_path,
                         edges.target_path,
                         edges.source_lang,
                         edges.origin,
                         edges.raw_reference_kind,
                         edges.target_kind
            ),
            ordered_edge_evidence AS (
                SELECT source_path,
                       target_path,
                       source_lang || char(31) ||
                       origin || char(31) ||
                       raw_reference_kind || char(31) ||
                       target_kind || char(31) ||
                       evidence_reference_count AS evidence_item
                FROM edge_evidence_rows
                ORDER BY source_path, target_path, source_lang, origin, raw_reference_kind, target_kind
            ),
            edge_evidence_payloads AS (
                SELECT source_path,
                       target_path,
                       GROUP_CONCAT(evidence_item, char(30)) AS evidence_payload
                FROM ordered_edge_evidence
                GROUP BY source_path, target_path
            ),");
    }

    private static void AppendDependencySymbolsAndSelect(
        DependencySqlFragmentBuilder sql,
        bool suppressDependencyNoise)
    {
        var retainedFilterSql = suppressDependencyNoise
            ? " WHERE edges.origin <> 'markdown_heading_name_match'"
            : string.Empty;
        var finalOrderSql = suppressDependencyNoise
            ? "limited_edge_totals.retained_reference_count DESC, limited_edge_totals.reference_count DESC, limited_edge_totals.source_path, limited_edge_totals.target_path"
            : "limited_edge_totals.reference_count DESC, limited_edge_totals.source_path, limited_edge_totals.target_path";
        sql.Append(@"
            distinct_edge_symbols AS (
                SELECT DISTINCT edges.source_path,
                                edges.target_path,
                                edges.symbol_name
                FROM edges
                JOIN limited_edge_totals
                  ON limited_edge_totals.source_path = edges.source_path
                 AND limited_edge_totals.target_path = edges.target_path" + retainedFilterSql + @"
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )
            SELECT limited_edge_totals.source_path,
                   limited_edge_totals.target_path,
                   limited_edge_totals.reference_count,
                   COALESCE(GROUP_CONCAT(CASE WHEN ranked_edge_symbols.symbol_rank <= @symbolSampleLimit THEN ranked_edge_symbols.symbol_name END, char(31)), '') AS symbols,
                   COALESCE(edge_evidence_payloads.evidence_payload, '') AS evidence_payload
            FROM limited_edge_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = limited_edge_totals.source_path
             AND ranked_edge_symbols.target_path = limited_edge_totals.target_path
            LEFT JOIN edge_evidence_payloads
              ON edge_evidence_payloads.source_path = limited_edge_totals.source_path
             AND edge_evidence_payloads.target_path = limited_edge_totals.target_path
            GROUP BY limited_edge_totals.source_path,
                     limited_edge_totals.target_path,
                     limited_edge_totals.reference_count,
                     edge_evidence_payloads.evidence_payload
            ORDER BY " + finalOrderSql);
    }
}
