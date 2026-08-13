using System.Text;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static class ImpactDefinitionQuerySql
    {
        private const string CanonicalRepresentativeOrder =
            "canonical_primary_rank, canonical_generated_rank, canonical_semantic_score DESC, canonical_declaration_identity COLLATE BINARY, path COLLATE BINARY, start_line, stable_start_column, symbol_id";
        private const string ResultOrder =
            "match_order, path_bucket, visibility_rank, name, path COLLATE BINARY, line, symbol_id";

        public static string Build(string matchingSql, bool pathCaseSensitive)
        {
            var pathDistinctSql = pathCaseSensitive
                ? "COUNT(DISTINCT path)"
                : "COUNT(DISTINCT path COLLATE NOCASE)";
            var precisePathDistinctSql = pathCaseSensitive
                ? "COUNT(DISTINCT CASE WHEN is_precise = 1 THEN path END)"
                : "COUNT(DISTINCT CASE WHEN is_precise = 1 THEN path END COLLATE NOCASE)";
            var sql = new StringBuilder();
            sql.Append($@"
            WITH matching_definitions AS (
                {matchingSql}
            ),
            ");
            AppendRankingCtes(sql);
            sql.Append(@"
            ");
            AppendSelectionCtes(sql, pathDistinctSql, precisePathDistinctSql);
            sql.Append(@"
            ");
            AppendFinalSelect(sql);
            return sql.ToString();
        }

        private static void AppendRankingCtes(StringBuilder sql)
        {
            sql.Append($@"ranked_definitions AS (
                SELECT matching_definitions.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY {CanonicalRepresentativeOrder}
                       ) AS logical_row_number,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY path COLLATE BINARY, start_line, stable_start_column, symbol_id
                       ) AS family_member_row_number,
                       COUNT(*) OVER (PARTITION BY logical_partial_key) AS logical_definition_sites
                FROM matching_definitions
            ),
            family_ranked_definitions AS (
                SELECT ranked_definitions.*,
                       MAX(CASE WHEN logical_row_number = 1 THEN family_member_row_number END) OVER (
                           PARTITION BY logical_partial_key
                       ) AS representative_member_row_number
                FROM ranked_definitions
            ),
            family_metadata_definitions AS (
                SELECT family_ranked_definitions.*,
                       MIN(canonical_primary_rank) OVER (PARTITION BY logical_partial_key) AS logical_primary_rank_min,
                       MAX(canonical_primary_rank) OVER (PARTITION BY logical_partial_key) AS logical_primary_rank_max,
                       MIN(canonical_generated_rank) OVER (PARTITION BY logical_partial_key) AS logical_generated_rank_min,
                       MAX(canonical_generated_rank) OVER (PARTITION BY logical_partial_key) AS logical_generated_rank_max,
                       MIN(canonical_semantic_score) OVER (PARTITION BY logical_partial_key) AS logical_semantic_score_min,
                       MAX(canonical_semantic_score) OVER (PARTITION BY logical_partial_key) AS logical_semantic_score_max,
                       MIN(canonical_declaration_identity) OVER (PARTITION BY logical_partial_key) AS logical_declaration_identity_min,
                       MAX(canonical_declaration_identity) OVER (PARTITION BY logical_partial_key) AS logical_declaration_identity_max,
                       json_group_array(json_object(
                           'symbol_id', symbol_id,
                           'path', path,
                           'line', line,
                           'start_line', start_line,
                           'start_column', start_column,
                           'end_line', end_line,
                           'name', name,
                           'signature', signature,
                           'identifier_start_column', identifier_start_column,
                           'generated', canonical_generated_rank
                       )) FILTER (WHERE
                           family_member_row_number <= CASE
                               WHEN representative_member_row_number <= {LogicalPartialSymbolGrouper.FamilyMemberLimit}
                               THEN {LogicalPartialSymbolGrouper.FamilyMemberLimit}
                               ELSE {LogicalPartialSymbolGrouper.FamilyMemberLimit - 1}
                           END
                           OR logical_row_number = 1
                       ) OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY path COLLATE BINARY, start_line, stable_start_column, symbol_id
                           ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                       ) AS logical_family_members_json
                FROM family_ranked_definitions
            ),");
        }

        private static void AppendSelectionCtes(
            StringBuilder sql,
            string pathDistinctSql,
            string precisePathDistinctSql)
        {
            sql.Append($@"logical_definitions AS (
                SELECT *
                FROM family_metadata_definitions
                WHERE logical_row_number = 1
            ),
            requested_definitions AS (
                SELECT logical_partial_key, 1 AS requested_row
                FROM logical_definitions
                ORDER BY {ResultOrder}
                LIMIT @definitionLimit OFFSET @definitionOffset
            ),
            single_precise_definition AS (
                SELECT logical_partial_key, 0 AS requested_row
                FROM logical_definitions
                WHERE is_precise = 1
                ORDER BY {ResultOrder}
                LIMIT 1
            ),
            selected_definition_keys AS (
                SELECT logical_partial_key, requested_row
                FROM requested_definitions
                UNION ALL
                SELECT precise.logical_partial_key, precise.requested_row
                FROM single_precise_definition precise
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM requested_definitions requested
                    WHERE requested.logical_partial_key = precise.logical_partial_key
                )
            ),
            definition_stats AS (
                SELECT COUNT(*) AS physical_count,
                       {pathDistinctSql} AS physical_file_count,
                       COUNT(DISTINCT logical_partial_key) AS logical_count,
                       SUM(is_precise) AS precise_count,
                       COUNT(DISTINCT CASE WHEN is_precise = 1 THEN logical_partial_key END) AS precise_logical_count,
                       {precisePathDistinctSql} AS precise_file_count,
                       SUM(is_non_callable) AS non_callable_count
                FROM matching_definitions
            )");
        }

        private static void AppendFinalSelect(StringBuilder sql)
        {
            sql.Append($@"SELECT logical.path, logical.lang, logical.kind, logical.name, logical.line,
                   logical.start_line, logical.start_column, logical.end_line,
                   logical.body_start_line, logical.body_end_line, logical.signature,
                   logical.container_kind, logical.container_name,
                   logical.visibility, logical.return_type,
                   logical.container_qualified_name, logical.logical_partial_key,
                   logical.symbol_id, logical.logical_definition_sites, selected.requested_row,
                   stats.physical_count, stats.physical_file_count, stats.logical_count,
                   stats.precise_count, stats.precise_file_count, stats.non_callable_count,
                   CASE
                       WHEN logical.logical_primary_rank_min <> logical.logical_primary_rank_max THEN '{LogicalPartialSymbolGrouper.ImplementationBodyReason}'
                       WHEN logical.logical_generated_rank_min <> logical.logical_generated_rank_max THEN '{LogicalPartialSymbolGrouper.NonGeneratedSourceReason}'
                       WHEN logical.logical_semantic_score_min <> logical.logical_semantic_score_max THEN '{LogicalPartialSymbolGrouper.SemanticDeclarationReason}'
                       WHEN logical.logical_declaration_identity_min <> logical.logical_declaration_identity_max THEN '{LogicalPartialSymbolGrouper.CanonicalDeclarationIdentityReason}'
                       ELSE '{LogicalPartialSymbolGrouper.StableLocationReason}'
                   END AS representative_reason,
                   logical.logical_family_members_json,
                   CASE WHEN logical.logical_definition_sites > {LogicalPartialSymbolGrouper.FamilyMemberLimit} THEN 1 ELSE 0 END AS family_members_truncated,
                   logical.identifier_start_column,
                   stats.precise_logical_count
            FROM selected_definition_keys selected
            JOIN logical_definitions logical
              ON logical.logical_partial_key = selected.logical_partial_key
            CROSS JOIN definition_stats stats
            ORDER BY {ResultOrder}");
        }
    }
}
