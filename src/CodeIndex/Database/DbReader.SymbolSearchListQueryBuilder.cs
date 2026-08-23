namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record SymbolSearchColumnSql(
        string StartLine,
        string EndLine,
        string BodyStartLine,
        string BodyEndLine,
        string Signature,
        string ContainerKind,
        string ContainerName,
        string ContainerQualifiedName,
        string FamilyKey,
        string Visibility,
        string ReturnType,
        string StartColumn,
        string SizeLines);

    private sealed record SymbolSearchRankingSql(
        string Join,
        string GenericNamePenalty,
        string DefinitionSites,
        string ReferenceCount,
        string HotspotScore,
        string RankingReferenceScore,
        string RankingHotspotScore,
        string StructuralRankPenalty,
        string ComplexityScore,
        string ExactNameOrder);

    private string BuildSymbolSearchListSql(SymbolSearchQueryPlan plan)
    {
        var columns = BuildSymbolSearchColumnSql();
        var includeRankSignals =
            plan.SortMode != SymbolSortMode.Name && _hasReferencesTable;
        var canonical = LogicalPartialQuerySql.Build(
            this,
            columns.Signature,
            columns.ContainerName,
            columns.ContainerQualifiedName,
            columns.FamilyKey,
            columns.ReturnType,
            columns.BodyStartLine,
            columns.BodyEndLine);
        var ranking = BuildSymbolSearchRankingSql(
            columns,
            canonical.LogicalPartialKey,
            includeRankSignals);
        var sql = BuildSymbolSearchSelectSql(columns, ranking, canonical);
        sql += SymbolSearchQueryPredicateBuilder.BuildFull(this, plan);
        SymbolSearchQueryPredicateBuilder.AppendFilters(
            this,
            ref sql,
            plan,
            includeLineRange: true);
        if (plan.GroupPartials)
        {
            sql = BuildLogicalPartialSymbolQuery(sql, plan);
        }
        else
        {
            sql += BuildSymbolSortOrderBy(
                plan.SortMode,
                ranking.ExactNameOrder,
                ranking.ReferenceCount,
                ranking.HotspotScore,
                ranking.RankingReferenceScore,
                ranking.RankingHotspotScore,
                columns.SizeLines,
                ranking.ComplexityScore,
                columns.StartColumn);
        }

        return sql + " LIMIT @limit OFFSET @offset";
    }

    private SymbolSearchColumnSql BuildSymbolSearchColumnSql()
    {
        var startLine = GetSymbolColumnSql("start_line", "s.line");
        var endLine = GetSymbolColumnSql("end_line", "s.line");
        return new SymbolSearchColumnSql(
            startLine,
            endLine,
            GetSymbolColumnSql("body_start_line"),
            GetSymbolColumnSql("body_end_line"),
            GetSymbolColumnSql("signature"),
            GetSymbolColumnSql("container_kind"),
            GetSymbolColumnSql("container_name"),
            GetSymbolColumnSql("container_qualified_name"),
            GetSymbolColumnSql("family_key"),
            GetSymbolColumnSql("visibility"),
            GetSymbolColumnSql("return_type"),
            GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)"),
            $"CASE WHEN ({endLine}) >= ({startLine}) THEN ({endLine}) - ({startLine}) + 1 ELSE 1 END");
    }

    private SymbolSearchRankingSql BuildSymbolSearchRankingSql(
        SymbolSearchColumnSql columns,
        string logicalPartialKeySql,
        bool includeRankSignals)
    {
        var genericPenalty = includeRankSignals
            ? GetGenericSymbolRankNamePenaltySql("s.name")
            : "1.0";
        var definitionSites = includeRankSignals
            ? "COALESCE(symbol_defs.definition_sites, 1)"
            : "CAST(1 AS INTEGER)";
        var conservativeSignal = includeRankSignals
            ? $"(f.lang = 'csharp' AND (s.kind = 'property' OR ({definitionSites}) > 1 OR lower(s.name) IN {GenericSymbolRankNamesSql}))"
            : "0";
        var useCSharpIdentityRank = includeRankSignals && CanUseCSharpIdentityHotspotCounts();
        var csharpPartialIdentitySignal = useCSharpIdentityRank
            ? $"(f.lang = 'csharp' AND ({logicalPartialKeySql}) LIKE 'family:%')"
            : "0";
        var fallbackReferenceCount = $"CASE WHEN {conservativeSignal} THEN COALESCE(symbol_file_rank.reference_count, 0) ELSE COALESCE(symbol_rank.reference_count, 0) END";
        var fallbackHotspotScore = $"CASE WHEN {conservativeSignal} THEN COALESCE(symbol_file_rank.hotspot_score, 0.0) ELSE COALESCE(symbol_rank.hotspot_score, 0.0) END";
        var referenceCount = includeRankSignals
            ? useCSharpIdentityRank
                ? $"CASE WHEN {csharpPartialIdentitySignal} THEN COALESCE(symbol_identity_rank.reference_count, 0) ELSE {fallbackReferenceCount} END"
                : fallbackReferenceCount
            : "CAST(0 AS INTEGER)";
        var hotspotScore = includeRankSignals
            ? useCSharpIdentityRank
                ? $"CASE WHEN {csharpPartialIdentitySignal} THEN COALESCE(symbol_identity_rank.hotspot_score, 0.0) ELSE {fallbackHotspotScore} END"
                : fallbackHotspotScore
            : "CAST(0.0 AS REAL)";
        var dilution = $"CASE WHEN ({definitionSites}) > 1 THEN CAST(({definitionSites}) * ({definitionSites}) AS REAL) ELSE 1.0 END";
        var structuralPenalty = includeRankSignals
            ? $"CASE WHEN s.kind IN ('property', 'enum') AND ({columns.SizeLines}) <= 1 THEN 0.1 ELSE 1.0 END"
            : "1.0";
        var rankingReference = includeRankSignals
            ? $"(({referenceCount}) * ({genericPenalty}) * ({structuralPenalty}) / ({dilution}))"
            : referenceCount;
        var rankingHotspot = includeRankSignals
            ? $"(({hotspotScore}) * ({genericPenalty}) * ({structuralPenalty}) / ({dilution}))"
            : hotspotScore;
        var cappedReference = $"CASE WHEN ({rankingReference}) > 100.0 THEN 100.0 ELSE ({rankingReference}) END";
        var cappedHotspot = $"CASE WHEN ({rankingHotspot}) > 150.0 THEN 150.0 ELSE ({rankingHotspot}) END";
        var complexity = $@"(({columns.SizeLines} * 16.0) + ({cappedReference} * 0.75) + ({cappedHotspot} * 0.35) + CASE
                       WHEN {columns.Visibility} IN ('public', 'pub', 'open', 'export') THEN 8.0
                       WHEN {columns.Visibility} IN ('protected', 'internal', 'protected internal') THEN 4.0
                       ELSE 0.0
                   END)";
        return new SymbolSearchRankingSql(
            BuildSymbolRankJoin(includeRankSignals, useCSharpIdentityRank, logicalPartialKeySql),
            genericPenalty,
            definitionSites,
            referenceCount,
            hotspotScore,
            rankingReference,
            rankingHotspot,
            structuralPenalty,
            complexity,
            BuildExactSymbolNameOrderSql());
    }

    private string BuildSymbolRankJoin(
        bool includeRankSignals,
        bool useCSharpIdentityRank,
        string logicalPartialKeySql)
    {
        if (!includeRankSignals)
            return string.Empty;

        var identityJoin = useCSharpIdentityRank
            ? $@"
            LEFT JOIN (
                SELECT identity_site.lang,
                       identity_site.target_symbol_key,
                       COUNT(*) AS reference_count,
                       SUM(identity_site.reference_score) AS hotspot_score
                FROM (
                    SELECT rf.lang,
                           sr.target_symbol_key,
                           sr.file_id,
                           sr.line,
                           sr.column_number,
                           {GetLogicalReferenceKindSql("sr.reference_kind")} AS logical_reference_kind,
                           MAX({GetHotspotReferenceWeightSql("sr.reference_kind")}) AS reference_score
                    FROM symbol_references sr
                    JOIN files rf ON rf.id = sr.file_id
                    WHERE rf.lang = 'csharp'
                      AND sr.target_symbol_key IS NOT NULL
                      AND sr.resolution_state IN ('resolved', 'resolved_group')
                      AND (sr.reference_kind IN {CallGraphReferenceKindsSql}
                           OR sr.reference_kind = 'type_reference')
                    GROUP BY rf.lang,
                             sr.target_symbol_key,
                             sr.file_id,
                             sr.line,
                             sr.column_number,
                             logical_reference_kind
                ) identity_site
                GROUP BY identity_site.lang, identity_site.target_symbol_key
            ) symbol_identity_rank
              ON symbol_identity_rank.lang = f.lang
             AND symbol_identity_rank.target_symbol_key = ({logicalPartialKeySql})"
            : string.Empty;
        return $@"
            LEFT JOIN (
                SELECT rf.lang AS lang,
                       sr.symbol_name AS symbol_name,
                       COUNT(*) AS reference_count,
                       SUM({GetHotspotReferenceWeightSql("sr.reference_kind")}) AS hotspot_score
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id
                WHERE sr.reference_kind IN {CallGraphReferenceKindsSql}
                  AND sr.symbol_name IS NOT NULL
                  AND sr.symbol_name <> ''
                GROUP BY rf.lang, sr.symbol_name COLLATE NOCASE
            ) symbol_rank
              ON symbol_rank.lang = f.lang
             AND symbol_rank.symbol_name = s.name COLLATE NOCASE
            LEFT JOIN (
                SELECT sr.file_id AS file_id,
                       sr.symbol_name AS symbol_name,
                       COUNT(*) AS reference_count,
                       SUM({GetHotspotReferenceWeightSql("sr.reference_kind")}) AS hotspot_score
                FROM symbol_references sr
                WHERE sr.reference_kind IN {CallGraphReferenceKindsSql}
                  AND sr.symbol_name IS NOT NULL
                  AND sr.symbol_name <> ''
                GROUP BY sr.file_id, sr.symbol_name COLLATE NOCASE
            ) symbol_file_rank
              ON symbol_file_rank.file_id = s.file_id
             AND symbol_file_rank.symbol_name = s.name COLLATE NOCASE
            LEFT JOIN (
                SELECT df.lang AS lang,
                       ds.name AS symbol_name,
                       COUNT(*) AS definition_sites
                FROM symbols ds
                JOIN files df ON df.id = ds.file_id
                WHERE ds.name IS NOT NULL
                  AND ds.name <> ''
                GROUP BY df.lang, ds.name COLLATE NOCASE
            ) symbol_defs
              ON symbol_defs.lang = f.lang
             AND symbol_defs.symbol_name = s.name COLLATE NOCASE
            {identityJoin}";
    }

    private string BuildSymbolSearchSelectSql(
        SymbolSearchColumnSql columns,
        SymbolSearchRankingSql ranking,
        LogicalPartialCanonicalSql canonical)
    {
        return $@"
            SELECT f.path, f.lang, s.kind, {GetSymbolColumnSql("sub_kind")} AS sub_kind, s.name, s.line,
                   {columns.StartLine} AS start_line,
                   {GetSymbolColumnSql("start_column")} AS start_column,
                   {columns.EndLine} AS end_line,
                   {columns.BodyStartLine} AS body_start_line,
                   {columns.BodyEndLine} AS body_end_line,
                   {columns.Signature} AS signature,
                   {columns.ContainerKind} AS container_kind,
                   {columns.ContainerName} AS container_name,
                   {columns.Visibility} AS visibility,
                   {columns.ReturnType} AS return_type,
                   {ranking.ReferenceCount} AS reference_count,
                   {ranking.HotspotScore} AS hotspot_score,
                   {ranking.RankingReferenceScore} AS ranking_reference_score,
                   {ranking.RankingHotspotScore} AS ranking_hotspot_score,
                   {ranking.GenericNamePenalty} AS generic_name_penalty,
                   {ranking.StructuralRankPenalty} AS structural_rank_penalty,
                   {ranking.DefinitionSites} AS definition_sites,
                   {columns.SizeLines} AS size_lines,
                   {ranking.ComplexityScore} AS complexity_score,
                   {columns.ContainerQualifiedName} AS container_qualified_name,
                   {canonical.LogicalPartialKey} AS logical_partial_key,
                   s.id AS symbol_id,
                   {ranking.ExactNameOrder} AS exact_name_order,
                   {PathBucketOrder} AS path_bucket,
                   {VisibilityOrder} AS visibility_rank,
                   {columns.StartColumn} AS stable_start_column,
                   {canonical.PrimaryRank} AS canonical_primary_rank,
                   {canonical.Generated} AS canonical_generated_rank,
                   {canonical.SemanticScore} AS canonical_semantic_score,
                   {canonical.DeclarationIdentity} AS canonical_declaration_identity,
                   {GetSymbolColumnSql("identifier_start_column")} AS identifier_start_column
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            {ranking.Join}
            WHERE 1=1";
    }

    private static string BuildExactSymbolNameOrderSql()
    {
        return "CASE " +
            "WHEN @preferLiteralExactMatch = 1 AND s.name = @rawQuery THEN 0 " +
            "WHEN @preferLiteralNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name(s.name) = @rawQueryNormalized THEN 1 " +
            "WHEN @preferCaseInsensitiveExactMatch = 1 AND s.name = @rawQuery COLLATE NOCASE THEN 2 " +
            "WHEN @preferCaseInsensitiveNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name_folded(s.name) = @rawQueryNormalizedFolded THEN 3 " +
            "WHEN @preferCaseInsensitiveSqlLeafMatch = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @rawQueryLeafFolded THEN 4 " +
            "ELSE 5 END";
    }
}
