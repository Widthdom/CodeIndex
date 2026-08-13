namespace CodeIndex.Database;

public partial class DbReader
{
    private static string GetGenericSymbolRankNamePenaltySql(string nameSql)
        => $"CASE WHEN lower({nameSql}) IN {GenericSymbolRankNamesSql} THEN {GenericSymbolRankNamePenaltySqlLiteral} ELSE 1.0 END";

    private static string BuildLogicalPartialSymbolQuery(string matchingSymbolsSql, SymbolSortMode sortMode)
    {
        var orderBy = BuildLogicalPartialSortOrderBy(sortMode);
        return $@"
            WITH matching_symbols AS (
                {matchingSymbolsSql}
            ),
            ranked_symbols AS (
                SELECT matching_symbols.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY canonical_primary_rank,
                                    canonical_generated_rank,
                                    canonical_semantic_score DESC,
                                    canonical_declaration_identity COLLATE BINARY,
                                    logical_partial_key COLLATE BINARY,
                                    path COLLATE BINARY,
                                    start_line,
                                    stable_start_column,
                                    symbol_id
                       ) AS logical_row_number,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY path COLLATE BINARY, start_line, stable_start_column, symbol_id
                       ) AS family_member_row_number,
                       COUNT(*) OVER (PARTITION BY logical_partial_key) AS logical_definition_sites
                FROM matching_symbols
            ),
            family_ranked_symbols AS (
                SELECT ranked_symbols.*,
                       MAX(CASE WHEN logical_row_number = 1 THEN family_member_row_number END) OVER (
                           PARTITION BY logical_partial_key
                       ) AS representative_member_row_number
                FROM ranked_symbols
            ),
            logical_symbols AS (
                SELECT family_ranked_symbols.*,
                       MAX(reference_count) OVER (PARTITION BY logical_partial_key) AS logical_reference_count,
                       MAX(hotspot_score) OVER (PARTITION BY logical_partial_key) AS logical_hotspot_score,
                       MAX(ranking_reference_score) OVER (PARTITION BY logical_partial_key) AS logical_ranking_reference_score,
                       MAX(ranking_hotspot_score) OVER (PARTITION BY logical_partial_key) AS logical_ranking_hotspot_score,
                       MAX(generic_name_penalty) OVER (PARTITION BY logical_partial_key) AS logical_generic_name_penalty,
                       MAX(structural_rank_penalty) OVER (PARTITION BY logical_partial_key) AS logical_structural_rank_penalty,
                       MAX(size_lines) OVER (PARTITION BY logical_partial_key) AS logical_size_lines,
                       MAX(complexity_score) OVER (PARTITION BY logical_partial_key) AS logical_complexity_score,
                       MIN(exact_name_order) OVER (PARTITION BY logical_partial_key) AS logical_exact_name_order,
                       MIN(path_bucket) OVER (PARTITION BY logical_partial_key) AS logical_path_bucket,
                       MIN(visibility_rank) OVER (PARTITION BY logical_partial_key) AS logical_visibility_rank,
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
                FROM family_ranked_symbols
            )
            SELECT path, lang, kind, sub_kind, name, line,
                   start_line, start_column, end_line,
                   body_start_line, body_end_line, signature,
                   container_kind, container_name, visibility, return_type,
                   logical_reference_count, logical_hotspot_score,
                   logical_ranking_reference_score, logical_ranking_hotspot_score,
                   logical_generic_name_penalty, logical_structural_rank_penalty,
                   logical_definition_sites, logical_size_lines, logical_complexity_score,
                   container_qualified_name, logical_partial_key, symbol_id,
                   CASE
                       WHEN logical_primary_rank_min <> logical_primary_rank_max THEN '{LogicalPartialSymbolGrouper.ImplementationBodyReason}'
                       WHEN logical_generated_rank_min <> logical_generated_rank_max THEN '{LogicalPartialSymbolGrouper.NonGeneratedSourceReason}'
                       WHEN logical_semantic_score_min <> logical_semantic_score_max THEN '{LogicalPartialSymbolGrouper.SemanticDeclarationReason}'
                       WHEN logical_declaration_identity_min <> logical_declaration_identity_max THEN '{LogicalPartialSymbolGrouper.CanonicalDeclarationIdentityReason}'
                       ELSE '{LogicalPartialSymbolGrouper.StableLocationReason}'
                   END AS representative_reason,
                   logical_family_members_json,
                   CASE WHEN logical_definition_sites > {LogicalPartialSymbolGrouper.FamilyMemberLimit} THEN 1 ELSE 0 END AS family_members_truncated,
                   identifier_start_column
            FROM logical_symbols
            WHERE logical_row_number = 1
            {orderBy}";
    }

    private static string BuildLogicalPartialSortOrderBy(SymbolSortMode sortMode)
    {
        const string stableTieBreakers = "logical_path_bucket, logical_visibility_rank, name, path COLLATE BINARY, line, stable_start_column, symbol_id";
        return sortMode switch
        {
            SymbolSortMode.Hotspot => $"ORDER BY logical_ranking_hotspot_score DESC, logical_ranking_reference_score DESC, logical_hotspot_score DESC, logical_reference_count DESC, logical_size_lines DESC, {stableTieBreakers}",
            SymbolSortMode.References => $"ORDER BY logical_ranking_reference_score DESC, logical_ranking_hotspot_score DESC, logical_reference_count DESC, logical_hotspot_score DESC, logical_size_lines DESC, {stableTieBreakers}",
            SymbolSortMode.Size => $"ORDER BY logical_size_lines DESC, logical_ranking_reference_score DESC, logical_ranking_hotspot_score DESC, logical_reference_count DESC, {stableTieBreakers}",
            SymbolSortMode.Complexity => $"ORDER BY logical_complexity_score DESC, logical_ranking_hotspot_score DESC, logical_ranking_reference_score DESC, logical_reference_count DESC, logical_size_lines DESC, {stableTieBreakers}",
            SymbolSortMode.Path => "ORDER BY path COLLATE BINARY, line, stable_start_column, name, symbol_id",
            _ => $"ORDER BY logical_exact_name_order, {stableTieBreakers}",
        };
    }

    private string BuildSymbolSortOrderBy(
        SymbolSortMode sortMode,
        string exactNameOrderSql,
        string referenceCountSql,
        string rawHotspotScoreSql,
        string rankingReferenceScoreSql,
        string rankingHotspotScoreSql,
        string sizeLinesSql,
        string complexityScoreSql,
        string startColumnSql)
    {
        var stableTieBreakers = $"{PathBucketOrder}, {VisibilityOrder}, s.name, f.path, s.line, {startColumnSql} ASC, s.id ASC";
        return sortMode switch
        {
            SymbolSortMode.Hotspot => $" ORDER BY {rankingHotspotScoreSql} DESC, {rankingReferenceScoreSql} DESC, {rawHotspotScoreSql} DESC, {referenceCountSql} DESC, {sizeLinesSql} DESC, {stableTieBreakers}",
            SymbolSortMode.References => $" ORDER BY {rankingReferenceScoreSql} DESC, {rankingHotspotScoreSql} DESC, {referenceCountSql} DESC, {rawHotspotScoreSql} DESC, {sizeLinesSql} DESC, {stableTieBreakers}",
            SymbolSortMode.Size => $" ORDER BY {sizeLinesSql} DESC, {rankingReferenceScoreSql} DESC, {rankingHotspotScoreSql} DESC, {referenceCountSql} DESC, {stableTieBreakers}",
            SymbolSortMode.Complexity => $" ORDER BY {complexityScoreSql} DESC, {rankingHotspotScoreSql} DESC, {rankingReferenceScoreSql} DESC, {referenceCountSql} DESC, {sizeLinesSql} DESC, {stableTieBreakers}",
            SymbolSortMode.Path => $" ORDER BY f.path, s.line, {startColumnSql} ASC, s.name, s.id ASC",
            _ => $" ORDER BY {exactNameOrderSql}, {stableTieBreakers}",
        };
    }
}
