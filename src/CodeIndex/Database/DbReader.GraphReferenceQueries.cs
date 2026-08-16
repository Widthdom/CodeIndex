using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private enum GraphReferenceQueryShape
    {
        List,
        LimitedCount,
        TotalCount,
    }

    private sealed record GraphReferenceQueryRequest(
        string Query,
        int Limit,
        string? Lang,
        string? ReferenceKind,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests,
        bool Exact,
        bool RawKinds,
        bool IncludeQualifiedCommonCalls,
        bool IncludeMemberReads,
        long? IdentitySymbolId,
        IReadOnlyList<long>? CallerIdentitySymbolIds,
        bool ExcludeSelfReferences,
        int Offset);

    private sealed record GraphReferenceMatchPlan(
        bool AllowSqlLeafFallback,
        bool AllowQualifiedLeafFallback,
        bool AllowCSharpQualifiedContextMatch,
        bool UsesQualifiedName,
        string? CssScssVariableAlias);

    private sealed record GraphReferenceBuildContext(
        string SupportedLanguagePredicateSql,
        string IdentityFilterSql,
        GraphReferenceMatchPlan Match);

    private sealed record GraphReferenceRowLayout(
        int FirstLineOrdinal,
        int FirstColumnOrdinal,
        bool FirstColumnIsNullable,
        int? FirstLengthOrdinal,
        int ReferenceCountOrdinal,
        int ReferenceKindsOrdinal,
        int ReferenceKindCountsOrdinal,
        int ReferenceWeightOrdinal,
        int? SelfReferenceOrdinal,
        int? MutualRecursionOrdinal);

    private sealed record GraphReferenceDirectionSpec(
        string MatchColumnSql,
        string FoldedMatchColumnSql,
        string SqlLeafExactMatchSql,
        string SqlLeafFoldedMatchSql,
        string RankQueriedNameSql,
        bool UsesCSharpQualifiedContext,
        bool CountGroupsByReferenceKind,
        bool NormalizeBoundLanguage,
        string IdentityParameterName,
        GraphReferenceRowLayout RowLayout,
        Func<DbReader, string> BuildReferenceJoinSql,
        Func<DbReader, string> BuildSourcePredicateSql,
        Func<DbReader, GraphReferenceQueryRequest, string> BuildIdentityFilterSql,
        Func<DbReader, GraphReferenceQueryRequest, string> BuildPreNameFilterSql,
        Func<DbReader, GraphReferenceQueryRequest, string> BuildPostNameFilterSql,
        Func<DbReader, GraphReferenceQueryRequest, GraphReferenceMatchPlan, string> BuildQualifiedNameFilterSql,
        Func<DbReader, GraphReferenceQueryRequest, GraphReferenceBuildContext, string> BuildListSql,
        Action<DbReader, SqliteCommand, GraphReferenceQueryPlan> BindMatchParameters);

    private sealed record GraphReferenceQueryPlan(
        string Sql,
        GraphReferenceQueryShape Shape,
        GraphReferenceDirectionSpec Direction,
        GraphReferenceQueryRequest Request,
        GraphReferenceMatchPlan Match,
        IReadOnlyList<string> SupportedLanguages,
        bool BindIdentityParameter);

    private sealed record GraphReferenceRow(
        string Path,
        string? Lang,
        string? CallerKind,
        string? CallerName,
        string CalleeName,
        string ReferenceKind,
        IReadOnlyList<string> ReferenceKinds,
        IReadOnlyDictionary<string, int> ReferenceKindCounts,
        bool AggregateTruncated,
        double ReferenceWeightScore,
        int FirstLine,
        int? FirstColumn,
        int? FirstLength,
        int ReferenceCount,
        bool HasSelfReference,
        bool HasMutualRecursion);

    private static readonly GraphReferenceDirectionSpec CallerGraphReferenceDirection = new(
        MatchColumnSql: "r.symbol_name",
        FoldedMatchColumnSql: "r.symbol_name_folded",
        SqlLeafExactMatchSql: "r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE",
        SqlLeafFoldedMatchSql: "r.symbol_name_folded = @aliasQueryLeafFolded",
        RankQueriedNameSql: "r.symbol_name",
        UsesCSharpQualifiedContext: true,
        CountGroupsByReferenceKind: false,
        NormalizeBoundLanguage: true,
        IdentityParameterName: "@targetSymbolId",
        RowLayout: new GraphReferenceRowLayout(6, 7, false, null, 8, 9, 10, 11, 12, 13),
        BuildReferenceJoinSql: static reader => reader.ReferenceLineJoinSql("r"),
        BuildSourcePredicateSql: static _ => BuildCallerContainerPredicate("f", "r"),
        BuildIdentityFilterSql: static (reader, request) => reader.BuildCallerIdentityFilterSql(request),
        BuildPreNameFilterSql: static (reader, request) => reader.BuildCallerPreNameFilterSql(request),
        BuildPostNameFilterSql: static (reader, request) => reader.BuildCallerPostNameFilterSql(request),
        BuildQualifiedNameFilterSql: static (reader, request, _) => reader.BuildCallerQualifiedNameFilterSql(request),
        BuildListSql: static (reader, request, context) => reader.BuildCallerListSql(request, context),
        BindMatchParameters: static (reader, command, plan) => reader.BindCallerMatchParameters(command, plan));

    private static readonly GraphReferenceDirectionSpec CalleeGraphReferenceDirection = new(
        MatchColumnSql: "r.container_name",
        FoldedMatchColumnSql: "r.container_name_folded",
        SqlLeafExactMatchSql: "sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE",
        SqlLeafFoldedMatchSql: "sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded",
        RankQueriedNameSql: "r.container_name",
        UsesCSharpQualifiedContext: false,
        CountGroupsByReferenceKind: true,
        NormalizeBoundLanguage: false,
        IdentityParameterName: "@sourceSymbolId",
        RowLayout: new GraphReferenceRowLayout(6, 7, true, 8, 9, 10, 11, 12, null, null),
        BuildReferenceJoinSql: static _ => string.Empty,
        BuildSourcePredicateSql: static _ => "r.container_name IS NOT NULL",
        BuildIdentityFilterSql: static (reader, request) => reader.BuildCalleeIdentityFilterSql(request),
        BuildPreNameFilterSql: static (_, _) => string.Empty,
        BuildPostNameFilterSql: static (reader, request) => reader.BuildCalleePostNameFilterSql(request),
        BuildQualifiedNameFilterSql: static (reader, request, _) => reader.BuildCalleeQualifiedNameFilterSql(request),
        BuildListSql: static (reader, request, context) => reader.BuildCalleeListSql(request, context),
        BindMatchParameters: static (reader, command, plan) => reader.BindCalleeMatchParameters(command, plan));

    private static GraphReferenceQueryRequest CreateGraphReferenceQueryRequest(
        string query,
        int limit,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads,
        long? identitySymbolId = null,
        IReadOnlyList<long>? callerIdentitySymbolIds = null,
        bool excludeSelfReferences = false,
        int offset = 0)
        => new(
            query,
            limit,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            identitySymbolId,
            callerIdentitySymbolIds,
            excludeSelfReferences,
            offset);

    private GraphReferenceQueryPlan BuildGraphReferenceQueryPlan(
        GraphReferenceDirectionSpec direction,
        GraphReferenceQueryRequest request,
        GraphReferenceQueryShape shape,
        ReferenceRankMode rankMode = ReferenceRankMode.Weighted)
    {
        var supportedLanguages = GetWorkspaceSupportedReferenceLanguages()
            .OrderBy(static language => language, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        var supportedLanguagePredicateSql = BuildGraphSupportedLanguagePredicateSql(
            supportedLanguages,
            "f",
            "graphLang");
        var match = BuildGraphReferenceMatchPlan(direction, request);
        var identityFilterSql = direction.BuildIdentityFilterSql(this, request);
        var context = new GraphReferenceBuildContext(
            supportedLanguagePredicateSql,
            identityFilterSql,
            match);
        var sql = shape switch
        {
            GraphReferenceQueryShape.List => direction.BuildListSql(this, request, context),
            GraphReferenceQueryShape.LimitedCount => BuildGraphReferenceCountSql(direction, request, context, limited: true),
            GraphReferenceQueryShape.TotalCount => BuildGraphReferenceCountSql(direction, request, context, limited: false),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
        if (shape == GraphReferenceQueryShape.List)
            sql += $" ORDER BY {BuildReferenceRankOrderSql(rankMode, direction.RankQueriedNameSql)} LIMIT @limit OFFSET @offset";

        return new GraphReferenceQueryPlan(
            sql,
            shape,
            direction,
            request,
            match,
            supportedLanguages,
            BindIdentityParameter: identityFilterSql.Length > 0);
    }

    private GraphReferenceMatchPlan BuildGraphReferenceMatchPlan(
        GraphReferenceDirectionSpec direction,
        GraphReferenceQueryRequest request)
    {
        var usesQualifiedName = SqlNameResolver.HasQualifier(request.Query);
        var allowCSharpQualifiedContextMatch = direction.UsesCSharpQualifiedContext
            && usesQualifiedName
            && !HasQualifiedSymbolDefinition(
                request.Query,
                request.Lang,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);

        return new GraphReferenceMatchPlan(
            AllowSqlLeafFallbackForQuery(request.Query),
            HasSingleQualifiedSymbolDefinition(
                request.Query,
                request.Lang,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests),
            allowCSharpQualifiedContextMatch,
            usesQualifiedName,
            ComputeCssScssVariableAlias(request.Query));
    }

    private static string BuildGraphSupportedLanguagePredicateSql(
        IReadOnlyList<string> supportedLanguages,
        string fileAlias,
        string parameterPrefix)
    {
        if (supportedLanguages.Count == 0)
            return "1 = 0";

        var parameterNames = Enumerable.Range(0, supportedLanguages.Count)
            .Select(index => $"@{parameterPrefix}{index}");
        return $"{fileAlias}.lang IN ({string.Join(", ", parameterNames)})";
    }

    private string BuildGraphReferenceCountSql(
        GraphReferenceDirectionSpec direction,
        GraphReferenceQueryRequest request,
        GraphReferenceBuildContext context,
        bool limited)
    {
        var outerReferenceKindColumn = direction.CountGroupsByReferenceKind
            ? ", reference_kind"
            : string.Empty;
        var innerReferenceKindColumn = direction.CountGroupsByReferenceKind
            ? ",\n                       " + (request.RawKinds
                ? GetPreferredReferenceKindSql("r.reference_kind")
                : GetPreferredLogicalReferenceKindSql("r.reference_kind")) + " AS reference_kind"
            : string.Empty;
        var sql = @"
            SELECT path, lang, container_kind, container_name, symbol_name" + outerReferenceKindColumn + @"
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name" + innerReferenceKindColumn + @"
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + direction.BuildReferenceJoinSql(this) + @"
            WHERE " + direction.BuildSourcePredicateSql(this);
        sql += $" AND {context.SupportedLanguagePredicateSql}";

        sql += $" AND {GetCallableReferenceKindPredicateSql("r.reference_kind", request.ReferenceKind, "f.lang", request.IncludeMemberReads)}";
        AppendGraphReferenceTailFilters(ref sql, direction, request, context);
        sql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(request.RawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        sql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name" + outerReferenceKindColumn;
        if (limited)
            sql += " LIMIT @limit";

        return limited
            ? $"SELECT COUNT(*) FROM ({sql})"
            : $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({sql})";
    }

    private string BuildCallerListSql(
        GraphReferenceQueryRequest request,
        GraphReferenceBuildContext context)
    {
        var groupedReferenceKindSql = request.RawKinds
            ? GetGroupedCallerReferenceKindSql("r.reference_kind")
            : GetGroupedCallerLogicalReferenceKindSql("r.reference_kind");
        var groupedReferenceKindGroupSql = request.RawKinds
            ? GetRawReferenceKindSql("r.reference_kind")
            : GetLogicalReferenceKindSql("r.reference_kind");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
        var sql = @"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       " + groupedReferenceKindSql + @" AS reference_kind,
                       r.reference_kind AS raw_reference_kind,
                       " + groupedReferenceKindGroupSql + @" AS count_reference_kind,
                       COUNT(*) AS reference_count,
                       " + ReferenceWeightedScoreSql("r.reference_kind") + @" AS weighted_score,
                       (CAST(r.line AS INTEGER) * 4294967296 + r.column_number) AS location_key,
                       MAX(" + selfReferenceSql + @") AS is_self_reference,
                       MAX(" + mutualRecursionSql + @") AS is_mutual_recursion
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + ReferenceLineJoinSql("r") + @"
                WHERE " + BuildCallerContainerPredicate("f", "r") + @"
                  AND " + GetCallableReferenceKindPredicateSql("r.reference_kind", request.ReferenceKind, "f.lang", request.IncludeMemberReads) + @"
                  AND " + context.SupportedLanguagePredicateSql;
        AppendGraphReferenceTailFilters(ref sql, CallerGraphReferenceDirection, request, context);
        sql += @"
            GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, " + groupedReferenceKindGroupSql + @", r.reference_kind
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   " + (request.RawKinds ? GetGroupedCallerReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind")) + @" AS reference_kind,
                   (MIN(location_key) / 4294967296) AS first_line,
                   (MIN(location_key) % 4294967296) AS first_column,
                   SUM(r.reference_count) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds,
                   GROUP_CONCAT(r.count_reference_kind || ':' || r.reference_count) AS reference_kind_counts,
                   SUM(r.weighted_score) AS weighted_score,
                   MAX(r.is_self_reference) AS is_self_reference,
                   MAX(r.is_mutual_recursion) AS is_mutual_recursion
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name";
        return sql;
    }

    private string BuildCalleeListSql(
        GraphReferenceQueryRequest request,
        GraphReferenceBuildContext context)
    {
        var preferredCalleeKindSql = request.RawKinds
            ? GetPreferredReferenceKindSql("r.reference_kind")
            : GetPreferredLogicalReferenceKindSql("r.reference_kind");
        var calleeGroupKindSql = request.RawKinds
            ? GetRawReferenceKindSql("r.reference_kind")
            : GetLogicalReferenceKindSql("r.reference_kind");
        var referenceSpanLengthSql = _referenceColumns.Contains("span_length")
            ? "r.span_length"
            : "NULL";
        var sql = $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       {preferredCalleeKindSql} AS reference_kind,
                       r.reference_kind AS raw_reference_kind,
                       {calleeGroupKindSql} AS count_reference_kind,
                       COUNT(*) AS reference_count,
                       {ReferenceWeightedScoreSql("r.reference_kind")} AS weighted_score,
                       r.line,
                       r.column_number,
                       {referenceSpanLengthSql} AS span_length
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL
                  AND {GetCallableReferenceKindPredicateSql("r.reference_kind", request.ReferenceKind, "f.lang", request.IncludeMemberReads)}
                  AND {context.SupportedLanguagePredicateSql}";
        AppendGraphReferenceTailFilters(ref sql, CalleeGraphReferenceDirection, request, context);
        sql += $@"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {referenceSpanLengthSql}, r.reference_kind
            ),
            ranked_call_sites AS (
                SELECT logical_references.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY path, lang, container_kind, container_name, symbol_name, reference_kind
                           ORDER BY CASE WHEN column_number IS NULL THEN 1 ELSE 0 END,
                                    line,
                                    column_number,
                                    COALESCE(span_length, 0)
                       ) AS location_rank
                FROM logical_references
            )
            SELECT path, lang, container_kind, container_name, symbol_name,
                   reference_kind,
                   MAX(CASE WHEN location_rank = 1 THEN line END) AS first_line,
                   MAX(CASE WHEN location_rank = 1 THEN column_number END) AS first_column,
                   MAX(CASE WHEN location_rank = 1 THEN span_length END) AS first_length,
                   SUM(r.reference_count) AS reference_count,
                   GROUP_CONCAT(DISTINCT reference_kind) AS reference_kinds,
                   GROUP_CONCAT(r.count_reference_kind || ':' || r.reference_count) AS reference_kind_counts,
                   SUM(r.weighted_score) AS weighted_score
            FROM ranked_call_sites r
            GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";
        return sql;
    }

    private void AppendGraphReferenceTailFilters(
        ref string sql,
        GraphReferenceDirectionSpec direction,
        GraphReferenceQueryRequest request,
        GraphReferenceBuildContext context)
    {
        sql += context.IdentityFilterSql;
        sql += direction.BuildPreNameFilterSql(this, request);
        sql += BuildGraphReferenceNameFilterSql(direction, request, context.Match);
        sql += direction.BuildPostNameFilterSql(this, request);
        AppendPathFilters(ref sql, request.PathPatterns, request.ExcludePathPatterns, request.ExcludeTests);
    }

    private string BuildGraphReferenceNameFilterSql(
        GraphReferenceDirectionSpec direction,
        GraphReferenceQueryRequest request,
        GraphReferenceMatchPlan match)
    {
        if (match.UsesQualifiedName)
            return direction.BuildQualifiedNameFilterSql(this, request, match);

        var cssAliasScope = match.CssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (request.Exact && _foldReady)
        {
            var foldedMatchSql = BuildPersistedFoldedNameMatchSql(direction.FoldedMatchColumnSql, "@query");
            if (!match.AllowSqlLeafFallback)
                return $" AND {foldedMatchSql}";
            if (match.CssScssVariableAlias != null)
                return $" AND ({foldedMatchSql} OR ({direction.FoldedMatchColumnSql} = @queryCssScssVariableAlias{cssAliasScope}) OR (f.lang = 'sql' AND {direction.SqlLeafFoldedMatchSql}))";
            return $" AND ({foldedMatchSql} OR (f.lang = 'sql' AND {direction.SqlLeafFoldedMatchSql}))";
        }

        if (request.Exact)
        {
            if (!match.AllowSqlLeafFallback)
                return $" AND {direction.MatchColumnSql} = @query COLLATE NOCASE";
            if (match.CssScssVariableAlias != null)
                return $" AND ({direction.MatchColumnSql} = @query COLLATE NOCASE OR ({direction.MatchColumnSql} = @queryCssScssVariableAlias COLLATE NOCASE{cssAliasScope}) OR (f.lang = 'sql' AND {direction.SqlLeafExactMatchSql}))";
            return $" AND ({direction.MatchColumnSql} = @query COLLATE NOCASE OR (f.lang = 'sql' AND {direction.SqlLeafExactMatchSql}))";
        }

        if (match.CssScssVariableAlias != null)
            return $" AND ({direction.MatchColumnSql} LIKE @query ESCAPE '\\' OR ({direction.MatchColumnSql} = @queryCssScssVariableAlias COLLATE NOCASE{cssAliasScope}) OR (f.lang = 'sql' AND {direction.SqlLeafExactMatchSql}))";
        return $" AND ({direction.MatchColumnSql} LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND {direction.SqlLeafExactMatchSql}))";
    }

    private string BuildCallerQualifiedNameFilterSql(GraphReferenceQueryRequest request)
    {
        // A current identity-scoped query has already proved the target through the
        // reference candidate table. Reapplying the stored leaf spelling here would
        // incorrectly reject qualified overload families whose rows store only the leaf.
        // current な identity scope では candidate table が対象を証明済み。ここで保存済みの
        // leaf spelling を再照合すると、leaf のみを持つ修飾済み overload family を誤って落とす。
        if (request.CallerIdentitySymbolIds != null && request.Lang != null)
            return string.Empty;

        var folded = _foldReady;
        var like = !request.Exact;
        var contextSql = ReferenceContextSql("r");
        var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded, like);
        var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
        var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded);
        var nonSqlMatchSql = request.Exact
            ? folded
                ? BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")
                : "r.symbol_name = @query COLLATE NOCASE"
            : "r.symbol_name LIKE @query ESCAPE '\\'";
        var qualifiedNameFilterSql = $"(((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {nonSqlMatchSql}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        return request.CallerIdentitySymbolIds != null
            ? $" AND (f.lang = 'csharp' OR {qualifiedNameFilterSql})"
            : $" AND {qualifiedNameFilterSql}";
    }

    private string BuildCalleeQualifiedNameFilterSql(GraphReferenceQueryRequest request)
    {
        var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql(
            "r.container_name",
            "r.container_name_folded",
            folded: _foldReady);
        if (!request.Exact)
            return $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        if (_foldReady)
            return $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}) OR {qualifiedLeafFallbackSql})";
        return $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE) OR {qualifiedLeafFallbackSql})";
    }

    private string BuildCallerIdentityFilterSql(GraphReferenceQueryRequest request)
    {
        if (request.CallerIdentitySymbolIds == null || !HasTable("symbol_reference_candidates"))
            return string.Empty;

        var resolutionFilterSql = _referenceColumns.Contains("resolution_state")
            ? "r.resolution_state IN ('resolved', 'resolved_group')"
            : "1 = 0";
        if (request.CallerIdentitySymbolIds.Count == 0)
            return request.Lang == null
                ? " AND f.lang != 'csharp'"
                : " AND 1 = 0";

        var confirmedIdentitySql = "(" + resolutionFilterSql + @"
            AND EXISTS (
                    SELECT 1
                    FROM symbol_reference_candidates AS identity_candidate
                    WHERE identity_candidate.reference_id = r.id
                      AND identity_candidate.symbol_id IN (
                          SELECT CAST(value AS INTEGER)
                          FROM json_each(@callerTargetSymbolIdsJson)
                      )
                ))";
        return request.Lang == null
            ? " AND (f.lang != 'csharp' OR " + confirmedIdentitySql + ")"
            : " AND " + confirmedIdentitySql;
    }

    private string BuildCalleeIdentityFilterSql(GraphReferenceQueryRequest request)
        => request.IdentitySymbolId != null && _referenceColumns.Contains("source_symbol_id")
            ? " AND r.source_symbol_id = @sourceSymbolId"
            : string.Empty;

    private string BuildCallerPreNameFilterSql(GraphReferenceQueryRequest request)
    {
        if (!request.ExcludeSelfReferences)
            return string.Empty;
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference")
            ? "r.is_self_reference"
            : "0";
        return $" AND {selfReferenceSql} = 0";
    }

    private string BuildCallerPostNameFilterSql(GraphReferenceQueryRequest request)
    {
        var sql = string.Empty;
        if (request.Lang != null)
        {
            sql += IncludeAmbiguousMSourceForIdentityTarget(request.Lang, request.IdentitySymbolId)
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        sql += BuildCSharpBareMemberReferenceFilter(
            request.Query,
            request.Lang,
            "f",
            "r",
            request.IncludeQualifiedCommonCalls);
        return sql;
    }

    private string BuildCalleePostNameFilterSql(GraphReferenceQueryRequest request)
    {
        var sql = request.Lang != null
            ? " AND f.lang = @lang"
            : string.Empty;
        if (!request.IncludeQualifiedCommonCalls)
            sql += BuildCSharpQualifiedCommonCallNoiseFilter("f", "r");
        return sql;
    }

    private void BindGraphReferenceQueryPlan(SqliteCommand command, GraphReferenceQueryPlan plan)
    {
        for (var index = 0; index < plan.SupportedLanguages.Count; index++)
            SqliteCommandPolicy.Add(command, $"@graphLang{index}", plan.SupportedLanguages[index]);

        var request = plan.Request;
        var value = !request.Exact
            ? $"%{EscapeLikeQuery(request.Query)}%"
            : _foldReady
                ? FoldNameForLanguage(request.Query, request.Lang)
                : request.Query;
        if (request.Exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(command, "@query", request.Query, request.Lang);
        else
            SqliteCommandPolicy.Add(command, "@query", value);
        SqliteCommandPolicy.Add(command, "@aliasQuery", request.Query);
        plan.Direction.BindMatchParameters(this, command, plan);

        if (RequiresReferenceKindParameter(request.ReferenceKind))
            SqliteCommandPolicy.Add(command, "@referenceKind", request.ReferenceKind);
        if (request.Lang != null)
        {
            var language = plan.Direction.NormalizeBoundLanguage
                ? NormalizeQueryLanguage(request.Lang)
                : request.Lang;
            SqliteCommandPolicy.Add(command, "@lang", language);
        }
        if (plan.BindIdentityParameter)
        {
            if (request.CallerIdentitySymbolIds != null)
            {
                var symbolIdValues = request.CallerIdentitySymbolIds
                    .Select(static symbolId => symbolId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ToList();
                SqliteCommandPolicy.Add(
                    command,
                    "@callerTargetSymbolIdsJson",
                    JsonStringListCodec.Serialize(symbolIdValues));
            }
            else
            {
                SqliteCommandPolicy.Add(command, plan.Direction.IdentityParameterName, request.IdentitySymbolId!.Value);
            }
        }
        AddPathFilterParameters(command, request.PathPatterns, request.ExcludePathPatterns);
        if (plan.Shape != GraphReferenceQueryShape.TotalCount)
            SqliteCommandPolicy.Add(command, "@limit", request.Limit);
        if (plan.Shape == GraphReferenceQueryShape.List)
            SqliteCommandPolicy.Add(command, "@offset", Math.Max(0, request.Offset));
    }

    private void BindCallerMatchParameters(SqliteCommand command, GraphReferenceQueryPlan plan)
    {
        var request = plan.Request;
        AddQualifiedGraphQueryParameters(
            command,
            request.Query,
            plan.Match.AllowQualifiedLeafFallback,
            plan.Match.AllowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(command, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(request.Query)) ?? SqlNameResolver.GetLeafName(request.Query));
        AddCssScssVariableAliasParameter(command, request, plan.Match.CssScssVariableAlias);
        if (plan.Shape == GraphReferenceQueryShape.List)
            AddGraphReferenceRankingParameters(command, request.Query);
    }

    private void BindCalleeMatchParameters(SqliteCommand command, GraphReferenceQueryPlan plan)
    {
        var request = plan.Request;
        var normalizedQuery = SqlNameResolver.NormalizeQualifiedName(request.Query);
        SqliteCommandPolicy.Add(command, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(request.Query)) ?? SqlNameResolver.GetLeafName(request.Query));
        SqliteCommandPolicy.Add(command, "@aliasQueryNormalized", normalizedQuery);
        SqliteCommandPolicy.Add(command, "@aliasQueryNormalizedFolded", NameFold.Fold(normalizedQuery) ?? normalizedQuery);
        SqliteCommandPolicy.Add(command, "@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(request.Query));
        if (plan.Shape == GraphReferenceQueryShape.List)
        {
            AddCssScssVariableAliasParameter(command, request, plan.Match.CssScssVariableAlias);
            AddGraphReferenceRankingParameters(command, request.Query);
            AddQualifiedGraphQueryParameters(command, request.Query, plan.Match.AllowQualifiedLeafFallback);
        }
        else
        {
            AddQualifiedGraphQueryParameters(command, request.Query, plan.Match.AllowQualifiedLeafFallback);
            AddCssScssVariableAliasParameter(command, request, plan.Match.CssScssVariableAlias);
        }
    }

    private void AddCssScssVariableAliasParameter(
        SqliteCommand command,
        GraphReferenceQueryRequest request,
        string? cssScssVariableAlias)
    {
        if (cssScssVariableAlias == null)
            return;
        var aliasParameter = request.Exact && _foldReady
            ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
            : cssScssVariableAlias;
        SqliteCommandPolicy.Add(command, "@queryCssScssVariableAlias", aliasParameter);
    }

    private static void AddGraphReferenceRankingParameters(SqliteCommand command, string query)
    {
        SqliteCommandPolicy.Add(command, "@rawQuery", query);
        SqliteCommandPolicy.Add(command, "@rankingQuery", query.Trim());
    }

    private static string ReferenceWeightedScoreSql(string columnSql) => $@"
        SUM(CASE {columnSql}
            WHEN 'instantiate' THEN 3.0
            WHEN 'generic_type_argument' THEN 0.5
            WHEN 'call' THEN 1.0
            WHEN 'subscribe' THEN 0.1
            WHEN 'unsubscribe' THEN 0.1
            WHEN 'razor_event_binding' THEN 0.1
            ELSE 0.0
        END)";

    private static string BuildReferenceRankOrderSql(
        ReferenceRankMode rankMode,
        string queriedNameSql)
        => string.Join(
            ", ",
            ReferenceRankRecipes.Get(rankMode).Select(dimension => dimension switch
            {
                ReferenceRankDimension.ReferenceWeightScoreDescending => "weighted_score DESC",
                ReferenceRankDimension.ReferenceCountDescending => "reference_count DESC",
                ReferenceRankDimension.ReferenceKindPriorityAscending =>
                    "CASE reference_kind WHEN 'instantiate' THEN 0 WHEN 'call' THEN 1 WHEN 'generic_type_argument' THEN 2 WHEN 'subscribe' THEN 3 ELSE 4 END",
                ReferenceRankDimension.ExactCaseMatchDescending =>
                    $"CASE WHEN {queriedNameSql} = @rawQuery THEN 0 ELSE 1 END",
                ReferenceRankDimension.ExactNameMatchDescending =>
                    $"CASE WHEN lower({queriedNameSql}) = lower(@rankingQuery) THEN 0 ELSE 1 END",
                ReferenceRankDimension.PathCategoryAscending => GetPathBucketOrderSql("r.path"),
                ReferenceRankDimension.PathAscending => "r.path",
                ReferenceRankDimension.FirstLineAscending => "first_line",
                ReferenceRankDimension.FirstColumnAscending => "first_column",
                ReferenceRankDimension.LanguageAscending => "r.lang",
                ReferenceRankDimension.ContainerKindAscending => "r.container_kind",
                ReferenceRankDimension.ContainerNameAscending => "r.container_name",
                ReferenceRankDimension.SymbolNameAscending => "r.symbol_name",
                ReferenceRankDimension.ReferenceKindAscending => "reference_kind",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
            }));

}
