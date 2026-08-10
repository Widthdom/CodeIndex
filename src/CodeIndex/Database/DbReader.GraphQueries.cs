using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static readonly string CSharpCommonQualifiedMemberCallNamesSql = string.Join(
        ", ",
        CSharpReferenceExtractor.CommonQualifiedMemberCallNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'"));

    private string BuildCSharpBareMemberReferenceFilter(
        string query,
        string? lang,
        string fileAlias,
        string referenceAlias,
        bool includeQualifiedCommonCalls)
    {
        if (includeQualifiedCommonCalls
            || !ShouldFilterCSharpQualifiedCommonBareMemberQuery(query, lang))
            return string.Empty;

        return BuildCSharpQualifiedCommonCallNoiseFilter(fileAlias, referenceAlias);
    }

    private string BuildCSharpQualifiedCommonCallNoiseFilter(
        string fileAlias,
        string referenceAlias)
    {
        // Legacy read-only indexes cannot run the migrations that added resolution
        // evidence. Preserve their pre-filter graph behavior instead of emitting SQL
        // against columns they do not have.
        // 読み取り専用の旧 index は resolution evidence 列を追加できないため、
        // 存在しない列を参照せず従来の graph 結果へフォールバックする。
        if (!_referenceColumns.Contains("target_qualifier")
            || !_referenceColumns.Contains("resolution_state"))
        {
            return string.Empty;
        }

        return $" AND NOT ({fileAlias}.lang = 'csharp' AND {referenceAlias}.reference_kind = 'call' AND {referenceAlias}.symbol_name IN ({CSharpCommonQualifiedMemberCallNamesSql}) AND {referenceAlias}.target_qualifier IS NOT NULL AND COALESCE({referenceAlias}.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group'))";
    }

    private static bool ShouldFilterCSharpQualifiedCommonBareMemberQuery(string query, string? lang)
    {
        return (lang == null || string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            && !SqlNameResolver.HasQualifier(query);
    }

    /// <summary>
    /// Find callers for a referenced symbol.
    /// 指定シンボルを呼び出している呼び出し元を探す。
    /// </summary>
    public List<CallerResult> GetCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted, bool excludeSelfReferences = false, int offset = 0, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => GetCallersCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawKinds, rankMode, excludeSelfReferences, offset, includeQualifiedCommonCalls, includeMemberReads, targetSymbolId: null);

    private List<CallerResult> GetCallersForCandidate(DefinitionResult definition, int limit, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, int offset = 0)
        => GetCallersCore(definition.Name, limit, definition.Lang, referenceKind: null, pathPatterns, excludePathPatterns, excludeTests, exact: true, rawKinds: false, ReferenceRankMode.Weighted, excludeSelfReferences: false, offset, includeQualifiedCommonCalls: false, includeMemberReads: false, targetSymbolId: definition.SymbolId);

    private int CountCallersForCandidate(DefinitionResult definition, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (definition.SymbolId is not long symbolId || !HasTable("symbol_reference_candidates"))
            return 0;

        return CountCallersTotalCore(
            definition.Name,
            definition.Lang,
            referenceKind: null,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds: false,
            includeQualifiedCommonCalls: false,
            includeMemberReads: false,
            symbolId).Count;
    }

    private List<CallerResult> GetCallersCore(string query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, bool rawKinds, ReferenceRankMode rankMode, bool excludeSelfReferences, int offset, bool includeQualifiedCommonCalls, bool includeMemberReads, long? targetSymbolId)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CallerResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var supportedLangPredicate = BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang");

        var groupedReferenceKindSql = rawKinds
            ? GetGroupedCallerReferenceKindSql("r.reference_kind")
            : GetGroupedCallerLogicalReferenceKindSql("r.reference_kind");
        var groupedReferenceKindGroupSql = rawKinds
            ? GetRawReferenceKindSql("r.reference_kind")
            : GetLogicalReferenceKindSql("r.reference_kind");
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
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
                WHERE " + callerContainerPredicate + @"
                  AND " + GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads) + @"
                  AND " + supportedLangPredicate;
        if (targetSymbolId != null && HasTable("symbol_reference_candidates"))
        {
            // Candidate membership alone is not an edge: an ambiguous reference can list
            // several possible targets. Only authoritative resolution states may contribute
            // callers to a candidate-specific inspect bundle.
            // candidate membership だけでは edge ではない。ambiguous reference は複数の
            // 候補を持ち得るため、candidate 別 inspect bundle の callers には authoritative
            // な resolution state だけを採用する。
            sql += _referenceColumns.Contains("resolution_state")
                ? " AND r.resolution_state IN ('resolved', 'resolved_group')"
                : " AND 1 = 0";
            sql += @"
                AND EXISTS (
                    SELECT 1
                    FROM symbol_reference_candidates AS identity_candidate
                    WHERE identity_candidate.reference_id = r.id
                      AND identity_candidate.symbol_id = @targetSymbolId
                )";
        }
        if (excludeSelfReferences)
            sql += $" AND {selfReferenceSql} = 0";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowCSharpQualifiedContextMatch = SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            sql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && exact)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            sql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            sql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            sql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
        else if (exact)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            sql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
        {
            sql += IncludeAmbiguousMSourceForIdentityTarget(lang, targetSymbolId)
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        sql += BuildCSharpBareMemberReferenceFilter(
            query,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @"
            GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, " + groupedReferenceKindGroupSql + @", r.reference_kind
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   " + (rawKinds ? GetGroupedCallerReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind")) + @" AS reference_kind,
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
        sql += $" ORDER BY {BuildReferenceRankOrderSql(rankMode, "r.symbol_name")} LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        string callersQueryParam;
        if (!exact)
            callersQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            callersQueryParam = FoldNameForLanguage(query, lang);
        else
            callersQueryParam = query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", callersQueryParam);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        SqliteCommandPolicy.Add(cmd, "@rawQuery", query);
        SqliteCommandPolicy.Add(cmd, "@rankingQuery", query.Trim());
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        if (targetSymbolId != null && HasTable("symbol_reference_candidates"))
            SqliteCommandPolicy.Add(cmd, "@targetSymbolId", targetSymbolId.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kindAggregate = TruncateReferenceKindAggregate(GetNullableString(reader, 9), out var kindsTruncated);
            var countAggregate = TruncateReferenceKindAggregate(GetNullableString(reader, 10), out var countsTruncated);
            var kinds = ParseDistinctReferenceKinds(kindAggregate, primaryKind);
            var counts = ParseReferenceKindCounts(countAggregate, primaryKind, reader.GetInt32(8));
            results.Add(new CallerResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = primaryKind,
                ReferenceKinds = kinds,
                HasMixedReferenceKinds = kinds.Count > 1,
                ReferenceKindCounts = counts,
                AggregateTruncated = kindsTruncated || countsTruncated,
                ReferenceWeightScore = reader.GetDouble(11),
                FirstLine = reader.GetInt32(6),
                FirstColumn = reader.GetInt32(7),
                ReferenceCount = reader.GetInt32(8),
                HasSelfReference = reader.GetInt32(12) != 0,
                HasMutualRecursion = reader.GetInt32(13) != 0,
            });
        }
        return results;
    }

    public int CountCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var groupedSql = @"
            SELECT path, lang, container_kind, container_name, symbol_name
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
            WHERE " + BuildCallerContainerPredicate("f", "r");
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        groupedSql += $" AND {GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads)}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowCSharpQualifiedContextMatch = SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && exact)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        groupedSql += BuildCSharpBareMemberReferenceFilter(
            query,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? FoldNameForLanguage(query, lang)
                : query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", value);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCallersTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => CountCallersTotalCore(
            query,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            targetSymbolId: null);

    private QueryCountResult CountCallersTotalCore(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads,
        long? targetSymbolId)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        var groupedSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
                WHERE " + BuildCallerContainerPredicate("f", "r");
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        groupedSql += $" AND {GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads)}";
        if (targetSymbolId != null && HasTable("symbol_reference_candidates"))
        {
            groupedSql += _referenceColumns.Contains("resolution_state")
                ? " AND r.resolution_state IN ('resolved', 'resolved_group')"
                : " AND 1 = 0";
            groupedSql += @"
                AND EXISTS (
                    SELECT 1
                    FROM symbol_reference_candidates AS identity_candidate
                    WHERE identity_candidate.reference_id = r.id
                      AND identity_candidate.symbol_id = @targetSymbolId
                )";
        }
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowCSharpQualifiedContextMatch = SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        if (useSqlQualifiedContextMatch && exact && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && exact)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch && _foldReady)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContextMatch)
        {
            var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
            var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
        {
            groupedSql += IncludeAmbiguousMSourceForIdentityTarget(lang, targetSymbolId)
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        groupedSql += BuildCSharpBareMemberReferenceFilter(
            query,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
              ? FoldNameForLanguage(query, lang)
                : query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", value);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        if (targetSymbolId != null)
            SqliteCommandPolicy.Add(cmd, "@targetSymbolId", targetSymbolId.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Find callees used by a caller/container symbol.
    /// 呼び出し元シンボルが使っている呼び出し先を探す。
    /// </summary>
    public List<CalleeResult> GetCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted, int offset = 0, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => GetCalleesCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawKinds, rankMode, offset, includeQualifiedCommonCalls, includeMemberReads, sourceSymbolId: null);

    private List<CalleeResult> GetCalleesForCandidate(DefinitionResult definition, int limit, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, int offset = 0)
        => GetCalleesCore(definition.Name, limit, definition.Lang, referenceKind: null, pathPatterns, excludePathPatterns, excludeTests, exact: true, rawKinds: false, ReferenceRankMode.Weighted, offset, includeQualifiedCommonCalls: false, includeMemberReads: false, sourceSymbolId: definition.SymbolId);

    private int CountCalleesForCandidate(DefinitionResult definition, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (definition.SymbolId is not long symbolId || !_referenceColumns.Contains("source_symbol_id"))
            return 0;

        return CountCalleesTotalCore(
            definition.Name,
            definition.Lang,
            referenceKind: null,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds: false,
            includeQualifiedCommonCalls: false,
            includeMemberReads: false,
            symbolId).Count;
    }

    private List<CalleeResult> GetCalleesCore(string query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, bool rawKinds, ReferenceRankMode rankMode, int offset, bool includeQualifiedCommonCalls, bool includeMemberReads, long? sourceSymbolId)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CalleeResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return new List<CalleeResult>();
        using var cmd = _conn.CreateCommand();

        var preferredCalleeKindSql = rawKinds
            ? GetPreferredReferenceKindSql("r.reference_kind")
            : GetPreferredLogicalReferenceKindSql("r.reference_kind");
        var calleeGroupKindSql = rawKinds
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
                  AND {GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads)}
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";
        if (sourceSymbolId != null && _referenceColumns.Contains("source_symbol_id"))
            sql += " AND r.source_symbol_id = @sourceSymbolId";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            sql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}) OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            sql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE) OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            sql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            sql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}";
        else if (exact)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            sql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (!includeQualifiedCommonCalls)
            sql += BuildCSharpQualifiedCommonCallNoiseFilter("f", "r");
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
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
        sql += $" ORDER BY {BuildReferenceRankOrderSql(rankMode, "r.container_name")} LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        string calleesQueryParam;
        if (!exact)
            calleesQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            calleesQueryParam = FoldNameForLanguage(query, lang);
        else
            calleesQueryParam = query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", calleesQueryParam);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        SqliteCommandPolicy.Add(cmd, "@rawQuery", query);
        SqliteCommandPolicy.Add(cmd, "@rankingQuery", query.Trim());
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback);
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (sourceSymbolId != null && _referenceColumns.Contains("source_symbol_id"))
            SqliteCommandPolicy.Add(cmd, "@sourceSymbolId", sourceSymbolId.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

        var results = new List<CalleeResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kindAggregate = TruncateReferenceKindAggregate(GetNullableString(reader, 10), out var kindsTruncated);
            var countAggregate = TruncateReferenceKindAggregate(GetNullableString(reader, 11), out var countsTruncated);
            var kinds = ParseDistinctReferenceKinds(kindAggregate, primaryKind);
            var counts = ParseReferenceKindCounts(countAggregate, primaryKind, reader.GetInt32(9));
            results.Add(new CalleeResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = primaryKind,
                ReferenceKinds = kinds,
                HasMixedReferenceKinds = kinds.Count > 1,
                ReferenceKindCounts = counts,
                AggregateTruncated = kindsTruncated || countsTruncated,
                ReferenceWeightScore = reader.GetDouble(12),
                FirstLine = reader.GetInt32(6),
                FirstColumn = GetNullableInt32(reader, 7),
                FirstLength = GetNullableInt32(reader, 8),
                ReferenceCount = reader.GetInt32(9),
            });
        }
        return results;
    }

    public int CountCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var groupedSql = @"
            SELECT path, lang, container_kind, container_name, symbol_name, reference_kind
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name,
                       " + (rawKinds ? GetPreferredReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind")) + @" AS reference_kind
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        groupedSql += $" AND {GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads)}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}) OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE) OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            groupedSql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            groupedSql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        if (!includeQualifiedCommonCalls)
            groupedSql += BuildCSharpQualifiedCommonCallNoiseFilter("f", "r");
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? FoldNameForLanguage(query, lang)
                : query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", value);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback);
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCalleesTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => CountCalleesTotalCore(
            query,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            sourceSymbolId: null);

    private QueryCountResult CountCalleesTotalCore(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads,
        long? sourceSymbolId)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var groupedSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name,
                       " + (rawKinds ? GetPreferredReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind")) + @" AS reference_kind
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        groupedSql += $" AND {GetCallableReferenceKindPredicateSql("r.reference_kind", referenceKind, "f.lang", includeMemberReads)}";
        if (sourceSymbolId != null && _referenceColumns.Contains("source_symbol_id"))
            groupedSql += " AND r.source_symbol_id = @sourceSymbolId";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            groupedSql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}) OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            groupedSql += $" AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE) OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch && _foldReady)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: true);
            groupedSql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (useSqlQualifiedContainerMatch)
        {
            var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.container_name", "r.container_name_folded", folded: false);
            groupedSql += $" AND (r.container_name LIKE @query ESCAPE '\\' OR {qualifiedLeafFallbackSql})";
        }
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : $" AND ({BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")} OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : $" AND {BuildPersistedFoldedNameMatchSql("r.container_name_folded", "@query")}";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        if (!includeQualifiedCommonCalls)
            groupedSql += BuildCSharpQualifiedCommonCallNoiseFilter("f", "r");
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? FoldNameForLanguage(query, lang)
                : query;
        if (exact && _foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
        else
            SqliteCommandPolicy.Add(cmd, "@query", value);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback);
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
        }
        if (RequiresReferenceKindParameter(referenceKind))
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (sourceSymbolId != null)
            SqliteCommandPolicy.Add(cmd, "@sourceSymbolId", sourceSymbolId.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
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

    private static IReadOnlyDictionary<string, int> ParseReferenceKindCounts(string? aggregate, string primaryKind, int fallbackCount)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        counts["call"] = 0;
        counts["instantiate"] = 0;
        counts["subscribe"] = 0;
        if (!string.IsNullOrWhiteSpace(aggregate))
        {
            foreach (var entry in aggregate.Split(','))
            {
                var separator = entry.LastIndexOf(':');
                if (separator <= 0 || separator == entry.Length - 1)
                    continue;
                var kind = entry[..separator].Trim();
                if (kind.Length == 0 || !int.TryParse(entry[(separator + 1)..], out var count))
                    continue;
                counts[kind] = counts.TryGetValue(kind, out var existing)
                    ? existing + count
                    : count;
            }
        }
        if (counts.Count == 0 && !string.IsNullOrEmpty(primaryKind))
            counts[primaryKind] = fallbackCount;
        return counts;
    }

    /// <summary>
    /// Resolve a user-provided symbol name to its actual indexed casing via definition lookup.
    /// Prefers exact-case match, then falls back to case-insensitive. Only considers
    /// graph-supported languages. Returns the original input if no match is found.
    /// ユーザ入力のシンボル名を定義検索で実際のインデックス済みケーシングに解決する。
    /// 完全一致を優先し、なければ大文字小文字無視でフォールバック。graph 対応言語のみ対象。
    /// 見つからなければ元の入力をそのまま返す。
    /// </summary>
    private string ResolveSymbolName(string symbolName, string? lang)
    {
        var normalizedSymbolName = NormalizeCSharpVerbatimQuery(symbolName, lang) ?? symbolName;
        // Exact lookup mirrors the leaf `--exact` readers: folded equality when FoldReady,
        // ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // No path/test filters — definitions outside caller scope must still be found.
        // Only considers graph-supported languages to avoid resolving to unsupported ones.
        // FoldReady なら folded equality、legacy DB では ASCII `COLLATE NOCASE` にフォールバック。
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(normalizedSymbolName);
        var leafName = SqlNameResolver.GetLeafName(normalizedSymbolName);
        var segmentCount = SqlNameResolver.GetSegmentCount(normalizedSymbolName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedSymbolName);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "resolveLang");
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded) OR sql_leaf_name_folded(s.name) = @leafNameFolded)))"
                : "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded))"
            : allowLeafFallback
                ? "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE) OR sql_leaf_name(s.name) = @leafName COLLATE NOCASE)))"
                : "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE))";
        var csharpExplicitInterfaceClause = allowLeafFallback
            ? BuildCSharpExplicitInterfaceShortAliasMatchSql("name")
            : BuildCSharpExplicitInterfaceIdentityMatchSql("name");
        nameCondition = $"({nameCondition} OR {csharpExplicitInterfaceClause})";
        cmd.CommandText = @"SELECT s.name FROM symbols s JOIN files f ON s.file_id = f.id
                            WHERE " + nameCondition + @"
                              AND " + supportedLangFilter + @"
                            ORDER BY CASE
                                         WHEN s.name = @name THEN 0
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName THEN 1
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded THEN 2
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @leafName THEN 3
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @leafNameFolded THEN 4
                                         ELSE 5
                                     END LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@name", normalizedSymbolName);
        SqliteCommandPolicy.Add(cmd, "@normalizedName", normalizedName);
        SqliteCommandPolicy.Add(cmd, "@normalizedNameFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        SqliteCommandPolicy.Add(cmd, "@leafName", leafName);
        SqliteCommandPolicy.Add(cmd, "@leafNameFolded", NameFold.Fold(leafName) ?? leafName);
        SqliteCommandPolicy.Add(cmd, "@nameLeaf", leafName);
        SqliteCommandPolicy.Add(cmd, "@nameLeafFolded", NameFold.Fold(leafName) ?? leafName);
        SqliteCommandPolicy.Add(cmd, "@segmentCount", segmentCount);
        SqliteCommandPolicy.Add(cmd, "@allowLeafFallback", allowLeafFallback ? 1 : 0);
        AddCSharpExplicitInterfaceIdentityQueryParameter(cmd, "name", normalizedSymbolName);
        if (_foldReady)
            SqliteCommandPolicy.Add(cmd, "@nameFolded", NameFold.Fold(normalizedSymbolName) ?? normalizedSymbolName);
        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead() ? reader.GetString(0) : symbolName;
    }

    /// <summary>
    /// Find exact-match callers for BFS traversal. Uses per-row case sensitivity
    /// and filters to graph-supported languages only (preventing stale edges from
    /// unsupported languages leaking into results on pre-upgrade databases). The
    /// SQL query applies the requested LIMIT/OFFSET so callers do not materialize
    /// a larger intermediate page than they asked for.
    /// BFS 走査用の完全一致 caller 検索。行ごとの case sensitivity 判定、
    /// かつ graph 対応言語のみにフィルタ（アップグレード前 DB の古いエッジ漏れを防止）。
    /// SQL 側で要求された LIMIT/OFFSET を適用し、呼び出し側が要求以上の中間ページを
    /// materialize しないようにする。
    /// </summary>
    private List<CallerResult> GetCallersExact(string symbolName, int limit, int offset = 0, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool includeAmbiguousMSource = false, bool includeMemberReads = false)
        => GetCallersExactCore(symbolName, limit, offset, lang, pathPatterns, excludePathPatterns, excludeTests, targetSymbolIds: null, includeAmbiguousMSource, includeMemberReads);

    private List<CallerResult> GetCallersExactForTarget(string symbolName, long targetSymbolId, int limit, int offset, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool includeAmbiguousMSource = false, bool includeMemberReads = false)
        => GetCallersExactCore(symbolName, limit, offset, lang, pathPatterns, excludePathPatterns, excludeTests, [targetSymbolId], includeAmbiguousMSource, includeMemberReads);

    private List<CallerResult> GetCallersExactForTargets(string symbolName, IReadOnlyList<long> targetSymbolIds, int limit, int offset, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool includeAmbiguousMSource = false, bool includeMemberReads = false)
        => GetCallersExactCore(symbolName, limit, offset, lang, pathPatterns, excludePathPatterns, excludeTests, targetSymbolIds, includeAmbiguousMSource, includeMemberReads);

    private List<CallerResult> GetCallersExactCore(string symbolName, int limit, int offset, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<long>? targetSymbolIds, bool includeAmbiguousMSource, bool includeMemberReads)
    {
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
        var sourceSymbolIdSql = _referenceColumns.Contains("source_symbol_id") ? "r.source_symbol_id" : "NULL";
        var hasIdentityTargetScope = targetSymbolIds is { Count: > 0 }
                                     && _referenceColumns.Contains("target_symbol_id")
                                     && _referenceColumns.Contains("resolution_state")
                                     && HasTable("symbol_reference_candidates");
        const string targetSymbolIdsSql = "SELECT CAST(value AS INTEGER) FROM json_each(@targetSymbolIdsJson)";
        var targetSymbolIdSql = hasIdentityTargetScope
            ? $@"CASE
                    WHEN r.resolution_state = 'resolved'
                         AND EXISTS (
                             SELECT 1
                             FROM symbol_reference_candidates projected_identity_candidate
                             WHERE projected_identity_candidate.reference_id = r.id
                               AND projected_identity_candidate.symbol_id IN ({targetSymbolIdsSql})
                         )
                    THEN r.target_symbol_id
                    ELSE NULL
                END"
            : _referenceColumns.Contains("target_symbol_id") ? "r.target_symbol_id" : "NULL";

        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "callerLang");

        // Exact caller matching mirrors the leaf `--exact` readers: folded equality when
        // FoldReady, ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // ResolveSymbolName() already normalizes the root symbol first, so this catches
        // caller rows whose stored callee casing differs from the resolved definition.
        // caller 側も leaf `--exact` と同じく FoldReady なら folded equality、legacy DB では
        // `COLLATE NOCASE` fallback。definition と caller 行の casing 差もここで吸収する。
        var allowSqlLeafFallback = !SqlNameResolver.HasQualifier(symbolName);
        var allowCSharpQualifiedContextMatch = SqlNameResolver.HasQualifier(symbolName)
            && !HasQualifiedSymbolDefinition(symbolName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(symbolName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var polymorphicCSharpSymbolNames = lang is null or "csharp"
            ? GetCSharpPolymorphicDispatchSymbolNames(symbolName)
            : [];
        var polymorphicNameCondition = polymorphicCSharpSymbolNames.Count == 0
            ? string.Empty
            : _foldReady
                ? " OR (f.lang = 'csharp' AND r.symbol_name_folded IN (" + string.Join(", ", polymorphicCSharpSymbolNames.Select((_, i) => $"@polymorphicSymbolNameFolded{i}")) + "))"
                : " OR (f.lang = 'csharp' AND r.symbol_name COLLATE NOCASE IN (" + string.Join(", ", polymorphicCSharpSymbolNames.Select((_, i) => $"@polymorphicSymbolName{i}")) + "))";
        var unscopedPolymorphicNameCondition = hasIdentityTargetScope
            ? string.Empty
            : polymorphicNameCondition;
        var nameCondition = _foldReady
            ? allowSqlLeafFallback
                ? @"
              AND (" + BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@symbolNameFolded") + " OR (f.lang = 'sql' AND r.symbol_name_folded = @symbolNameLeafFolded)" + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_folded_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND " + BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@symbolNameFolded") + ") OR " + BuildCSharpQualifiedContextFallbackSql(BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false)) + " OR " + BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true) + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
            : allowSqlLeafFallback
                ? @"
              AND (r.symbol_name = @symbolName COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@symbolName) COLLATE NOCASE)" + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @symbolName COLLATE NOCASE) OR " + BuildCSharpQualifiedContextFallbackSql(BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false)) + " OR " + BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false) + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))";
        // Resolved rows must match the requested canonical target. Resolution groups can match
        // a candidate for conservative traversal, but only uniquely resolved rows project that
        // candidate as an actual cycle edge. Unresolved/ambiguous rows retain the historical
        // name-based traversal and likewise keep null target IDs.
        // resolved 行は要求された正規 target と一致させる。resolution group は保守的 traversal
        // の候補にはできるが、一意に resolved した行だけが実際の cycle edge として候補 ID を
        // 公開する。unresolved/ambiguous 行も従来の名前ベース探索に残し、target ID は null にする。
        var targetCondition = hasIdentityTargetScope
            ? $@"
              AND (
                  EXISTS (
                      SELECT 1
                      FROM symbol_reference_candidates identity_candidate
                      WHERE identity_candidate.reference_id = r.id
                        AND identity_candidate.symbol_id IN ({targetSymbolIdsSql})
                        AND r.resolution_state IN ('resolved', 'resolved_group')
                  )
                  OR (
                      COALESCE(r.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group')
                      " + nameCondition + @"
                  )" + unscopedPolymorphicNameCondition + @"
              )"
            : nameCondition;
        // impact BFS must share the call-graph contract with `callers`/`callees`/`hotspots`,
        // so event subscriptions (`Click += OnClick`) also participate in the transitive
        // caller chain. Metadata edges (`attribute`, `annotation`) stay excluded.
        // impact の BFS は `callers`/`callees`/`hotspots` と同じ call-graph 契約を共有し、
        // `subscribe` エッジ（`Click += OnClick` 等）も推移 caller に含める。`attribute` /
        // `annotation` のような metadata エッジは引き続き除外する。
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var sql = $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind, r.line,
                       {sourceSymbolIdSql} AS source_symbol_id,
                       {targetSymbolIdSql} AS target_symbol_id,
                       MAX({selfReferenceSql}) AS is_self_reference,
                       MAX({mutualRecursionSql}) AS is_mutual_recursion
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id{referenceLineJoin}
                WHERE {callerContainerPredicate}
                  AND (r.reference_kind IN {CallGraphReferenceKindsSql}{(includeMemberReads ? " OR r.reference_kind = 'member_read'" : string.Empty)})
                  AND {supportedLangFilter}
                  {targetCondition}";
        if (lang != null)
        {
            sql += includeAmbiguousMSource
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        sql += BuildCSharpBareMemberReferenceFilter(
            symbolName,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls: false);
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind, r.file_id, r.line, r.column_number, source_symbol_id, target_symbol_id
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind,
                   CASE WHEN lang = 'solution' AND reference_kind = 'project_reference' THEN path
                        ELSE " + BuildCallerNameProjectionSql("r") + @" END AS container_name,
                   symbol_name,
                   reference_kind, MIN(line) AS first_line, COUNT(*) AS reference_count,
                   MAX(is_self_reference) AS is_self_reference,
                   MAX(is_mutual_recursion) AS is_mutual_recursion,
                   source_symbol_id,
                   CASE
                       WHEN COUNT(DISTINCT COALESCE(target_symbol_id, -1)) = 1
                       THEN MIN(target_symbol_id)
                       ELSE NULL
                   END AS target_symbol_id,
                   GROUP_CONCAT(DISTINCT target_symbol_id) AS target_symbol_ids
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind, source_symbol_id";
        sql += $" ORDER BY {GetPathBucketOrderSql("r.path")}, reference_count DESC, r.path, COALESCE(r.container_name, ''), COALESCE(r.container_kind, ''), r.symbol_name, reference_kind, first_line, COALESCE(source_symbol_id, -1) LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@symbolName", symbolName);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", symbolName);
        AddQualifiedGraphQueryParameters(cmd, symbolName, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        SqliteCommandPolicy.Add(cmd, "@symbolNameLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        if (_foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@symbolNameFolded", symbolName, lang);
        for (var i = 0; i < polymorphicCSharpSymbolNames.Count; i++)
        {
            if (_foldReady)
                SqliteCommandPolicy.Add(cmd, $"@polymorphicSymbolNameFolded{i}", NameFold.Fold(polymorphicCSharpSymbolNames[i]) ?? polymorphicCSharpSymbolNames[i]);
            else
                SqliteCommandPolicy.Add(cmd, $"@polymorphicSymbolName{i}", polymorphicCSharpSymbolNames[i]);
        }
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (hasIdentityTargetScope)
        {
            var targetSymbolIdValues = targetSymbolIds!
                .Select(static symbolId => symbolId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            SqliteCommandPolicy.Add(cmd, "@targetSymbolIdsJson", JsonStringListCodec.Serialize(targetSymbolIdValues));
        }
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@offset", offset);

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new CallerResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = reader.GetString(5),
                ReferenceKinds = [reader.GetString(5)],
                ReferenceKindCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [reader.GetString(5)] = reader.GetInt32(7),
                },
                FirstLine = reader.GetInt32(6),
                ReferenceCount = reader.GetInt32(7),
                HasSelfReference = reader.GetInt32(8) != 0,
                HasMutualRecursion = reader.GetInt32(9) != 0,
                CallerSymbolId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                CalleeSymbolId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                CalleeSymbolIds = reader.IsDBNull(12)
                    ? Array.Empty<long>()
                    : reader.GetString(12)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(long.Parse)
                        .Order()
                        .ToArray(),
            });
        }
        return results;
    }

    private static string BuildImpactVisitedKey(
        CallerResult caller,
        string callerName,
        bool useCanonicalIdentity,
        bool deduplicateLogicalNodes = false)
    {
        var identity = useCanonicalIdentity && caller.CallerSymbolId is long callerSymbolId
            ? $"id:{callerSymbolId}"
            : $"{caller.Path}:{callerName}";
        return deduplicateLogicalNodes ? identity : $"{identity}:{caller.ReferenceKind}";
    }

    private static string BuildImpactTraversalNodeKey(long? symbolId, string name)
        => symbolId is long canonicalSymbolId ? $"id:{canonicalSymbolId}" : $"name:{name}";

    // Per-result cap on the number of distinct shortest paths surfaced by impact --with-paths.
    // Each call chain row may carry multiple converging paths from the resolved root through
    // distinct intermediates; the cap keeps JSON output bounded for diamond-heavy graphs and
    // is signaled by ImpactResult.PathsTruncated when exceeded.
    // impact --with-paths が 1 caller につき保持する経路数の上限。ダイヤモンド型で多経路が
    // 収束する場合に JSON 膨張を抑える役割があり、超過時は PathsTruncated で通知する。
    private const int DefaultImpactPathsPerResult = 10;
    internal const int DefaultImpactGraphStateEntryBudget = 10_000;
    internal const int DefaultImpactPartialFamilyMemberBudget = 10_000;
    internal int ImpactPartialFamilyMemberBudget { get; set; } = DefaultImpactPartialFamilyMemberBudget;
    internal const int ImpactBoundaryCallerProbeBudget = 512;
    private const int ImpactBoundaryCallerProbePageSize = 64;

    /// <summary>
    /// Compute transitive callers of a symbol using BFS with exact matching.
    /// Returns each unique caller in the call chain with its depth from the root symbol.
    /// The <paramref name="maxDepth"/> bound is inclusive: when <paramref name="maxDepth"/> is N,
    /// callers at depth 1 through N are returned (so a chain A→B→C→D queried against D with
    /// <c>maxDepth: 2</c> yields C at depth 1 and B at depth 2). Truncation is signaled via the
    /// Truncated property in results. When Truncated is true, TruncatedReason distinguishes
    /// user_limit (raise <c>--limit</c>) from safety_cap (pathological graph). See Issue #1533.
    /// When <paramref name="withPaths"/> is true, each ImpactResult is populated with the
    /// distinct shortest call paths from the resolved root through any intermediates to that
    /// caller (issue #1536); converging diamond chains surface every shortest route up to
    /// <paramref name="maxPathsPerResult"/>.
    /// 完全一致の BFS でシンボルの推移的呼び出し元を算出。各呼び出し元とルートシンボルからの深さを返す。
    /// <paramref name="maxDepth"/> は inclusive で、N を指定すると depth 1〜N の caller を返す
    /// (例: A→B→C→D のチェーンで D を <c>maxDepth: 2</c> 検索すると C(depth=1) と B(depth=2) を返す)。
    /// 結果が切り詰められた場合は Truncated フラグで通知し、TruncatedReason で
    /// user_limit (--limit 到達、緩和で増える) と safety_cap (病的グラフ、--limit 緩和では解消しない) を区別する (#1533)。
    /// <paramref name="withPaths"/> を true にすると、各 caller に対してルートからの推移経路
    /// （ダイヤモンド収束時は複数）を <paramref name="maxPathsPerResult"/> 件まで付与する（issue #1536）。
    /// </summary>
    public (List<ImpactResult> Results, bool Truncated, string? TruncatedReason, string TerminationReason, List<ImpactCycleResult> Cycles) GetTransitiveCallers(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool withPaths = false, int maxPathsPerResult = DefaultImpactPathsPerResult, int resultOffset = 0, bool includeMemberReads = false)
    {
        // Resolve the symbol name through definitions first so case-mismatched queries
        // like "run" find the actual "Run" symbol. Falls back to user input if not found.
        // 定義を通じてシンボル名を解決し、"run" → "Run" のようなケース違いを補正する。
        // 見つからなければユーザ入力をフォールバック使用。
        var resolvedName = ResolveSymbolName(symbolName, lang);
        var hasResolvedIdentityGraph = _referenceIdentityContractCurrent;
        var canResolveQualifiedCSharpIdentity =
            hasResolvedIdentityGraph
            && SqlNameResolver.HasQualifier(symbolName)
            && lang is null or "csharp";
        var rootDefinitionLimit = canResolveQualifiedCSharpIdentity
            ? DefaultImpactGraphStateEntryBudget
            : limit;
        var rootDefinitionResolution = ResolveImpactDefinitions(symbolName, rootDefinitionLimit, lang, pathPatterns, excludePathPatterns, excludeTests);
        if (rootDefinitionResolution.Definitions.Count == 0
            && !string.Equals(symbolName, resolvedName, StringComparison.Ordinal))
        {
            rootDefinitionResolution = ResolveImpactDefinitions(resolvedName, rootDefinitionLimit, lang, pathPatterns, excludePathPatterns, excludeTests);
        }
        var rootDefinitions = rootDefinitionResolution.Definitions;
        var rootDefinitionPaths = rootDefinitions
            .Select(definition => definition.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isLogicalPartialFamilyRoot =
            hasResolvedIdentityGraph
            && rootDefinitionResolution.LogicalCount == 1
            && rootDefinitions.Count == 1
            && rootDefinitions[0].Lang == "csharp"
            && rootDefinitions[0].PartialFamilyId != null
            && rootDefinitionResolution.PhysicalSymbolIds.Count > 0;
        var qualifiedCSharpRootSymbolIds =
            (canResolveQualifiedCSharpIdentity || isLogicalPartialFamilyRoot)
            && rootDefinitions.Count > 0
            && rootDefinitions.All(definition => definition.Lang == "csharp")
            && rootDefinitions.All(definition => definition.SymbolId != null)
            && (isLogicalPartialFamilyRoot || rootDefinitionResolution.LogicalCount == rootDefinitions.Count)
                ? rootDefinitionResolution.PhysicalSymbolIds.ToHashSet()
                : [];
        if (hasResolvedIdentityGraph
            && rootDefinitionPaths.Count > 1
            && qualifiedCSharpRootSymbolIds.Count == 0)
        {
            return ([], false, null, ImpactTerminationReasons.Completed, []);
        }
        var ambiguousMRootSymbolId = hasResolvedIdentityGraph
                                     && rootDefinitions.Count == 1
                                     && lang is "matlab" or "objc"
                                     && string.Equals(rootDefinitions[0].Lang, lang, StringComparison.Ordinal)
            ? rootDefinitions[0].SymbolId
            : null;
        var identityRootSymbolIds = qualifiedCSharpRootSymbolIds.Count > 0
            ? qualifiedCSharpRootSymbolIds
            : ambiguousMRootSymbolId is long ambiguousRootSymbolId
                ? [ambiguousRootSymbolId]
                : [];
        var singleIdentityRootSymbolId = identityRootSymbolIds.Count == 1
            ? identityRootSymbolIds.Single()
            : (long?)null;
        var includeAmbiguousMSource = ambiguousMRootSymbolId != null;

        var results = new List<ImpactResult>();
        resultOffset = Math.Max(0, resultOffset);
        var resultWindowEnd = checked(resultOffset + limit);
        var discoveredResultCount = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootTraversalNodeKey = identityRootSymbolIds.Count > 1
            ? $"identity:{NameFold.Fold(symbolName) ?? symbolName}"
            : BuildImpactTraversalNodeKey(singleIdentityRootSymbolId, resolvedName);
        var queue = new Queue<(string Symbol, long? SymbolId, IReadOnlyList<long>? TargetSymbolIds, string NodeKey, int Depth)>();
        if (isLogicalPartialFamilyRoot)
        {
            queue.Enqueue((resolvedName, null, identityRootSymbolIds.Order().ToArray(), rootTraversalNodeKey, 0));
        }
        else if (identityRootSymbolIds.Count > 0)
        {
            foreach (var identityRootSymbolId in identityRootSymbolIds.Order())
                queue.Enqueue((resolvedName, identityRootSymbolId, null, rootTraversalNodeKey, 0));
        }
        else
        {
            queue.Enqueue((resolvedName, null, null, rootTraversalNodeKey, 0));
        }
        visited.Add(resolvedName);
        // A partial-family root cap is reported independently on ImpactAnalysisResult.
        // It must not masquerade as a traversal/result cap, because raising --limit does
        // not expand the family root and the BFS may otherwise have completed normally.
        // partial family の root 上限は ImpactAnalysisResult で独立して報告する。
        // --limit 由来の traversal truncation と混同せず、通常完了した BFS を
        // safety_cap 扱いしない。
        var truncated = !isLogicalPartialFamilyRoot
                        && qualifiedCSharpRootSymbolIds.Count > 0
                        && rootDefinitionResolution.PhysicalSymbolIdsTruncated;
        var maxDepthReached = false;
        var cycles = new List<ImpactCycleResult>();
        var cycleKeys = new HashSet<string>(StringComparer.Ordinal);
        var cycleNodesByKey = new Dictionary<string, ImpactCycleMemberResult>(StringComparer.Ordinal);
        // truncatedReason tracks the *strongest* signal observed: safety_cap wins over
        // user_limit because it tells callers that raising --limit alone will not help
        // (the input graph is likely pathological). See Issue #1533.
        // truncatedReason は強い方の信号を保持する: safety_cap は --limit を緩和しても解消しない
        // ことを示すため、user_limit より優先する (#1533)。
        string? truncatedReason = truncated ? ImpactTruncatedReasons.SafetyCap : null;
        // Safety cap to prevent infinite loops on pathological graphs / 病的グラフでの無限ループ防止
        const int maxFetchIterations = 1000;
        var graphStateEntryBudget = GetImpactGraphStateEntryBudget(resultWindowEnd);
        var graphStateBudgetHit = false;
        var boundaryProbeBudgetHit = false;

        // Traversal state uses canonical symbol IDs when they are available. Display names are
        // applied only while materializing output, so consecutive same-name symbols remain
        // distinct path nodes (issue #4847) while legacy graphs retain name-keyed behavior.
        // traversal state は利用可能なら正規 symbol ID をキーにする。表示名への変換は出力時
        // だけ行い、同名 symbol が連続する経路も別ノードとして保持する (#4847)。
        Dictionary<string, HashSet<string>> parentsByNodeKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> cycleParentsByKey = new(StringComparer.Ordinal);
        Dictionary<string, int> depthByNodeKey = new(StringComparer.OrdinalIgnoreCase)
        {
            [rootTraversalNodeKey] = 0,
        };
        var resultIndicesByNodeKey = withPaths
            ? new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
            : null;
        var resultIndexByVisitedKey = isLogicalPartialFamilyRoot
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : null;
        var pathNodesByKey = withPaths
            ? new Dictionary<string, ImpactPathNode>(StringComparer.OrdinalIgnoreCase)
            : null;
        if (withPaths)
        {
            var rootPathNode = ResolveImpactPathNode(
                resolvedName,
                singleIdentityRootSymbolId,
                kind: null,
                lang,
                referencePath: null,
                referenceLine: null);
            if (isLogicalPartialFamilyRoot)
            {
                var representative = rootDefinitions[0];
                rootPathNode.SymbolId = null;
                rootPathNode.Name = resolvedName;
                rootPathNode.Kind = representative.Kind;
                rootPathNode.Lang = representative.Lang;
                rootPathNode.DefinitionPath = representative.Path;
                rootPathNode.DefinitionLine = representative.Line;
                rootPathNode.Container = representative.ContainerQualifiedName ?? representative.ContainerName;
                rootPathNode.PartialFamilyId = representative.PartialFamilyId;
                rootPathNode.LogicalTargetKey = $"partial|{representative.PartialFamilyId}";
            }
            pathNodesByKey![rootTraversalNodeKey] = rootPathNode;
        }

        while (queue.Count > 0 && discoveredResultCount < resultWindowEnd && !graphStateBudgetHit && !boundaryProbeBudgetHit)
        {
            var (currentSymbol, currentSymbolId, currentTargetSymbolIds, currentNodeKey, depth) = queue.Dequeue();

            // Fetch callers in pages, filtering out already-visited before counting toward limit.
            // This prevents diamond graphs from hiding reachable callers behind visited duplicates.
            // ページングで caller を取得し、visited フィルタ後にカウント。
            // ダイヤモンド型グラフで到達可能な caller が visited 重複に隠れるのを防止。
            var needed = resultWindowEnd - discoveredResultCount;
            var pageOffset = 0;
            var pageSize = Math.Max(1, needed + 1);
            var fetchIterations = 0;

            while (discoveredResultCount < resultWindowEnd && fetchIterations < maxFetchIterations && !graphStateBudgetHit && !boundaryProbeBudgetHit)
            {
                fetchIterations++;
                var page = currentTargetSymbolIds is { Count: > 0 }
                    ? GetCallersExactForTargets(currentSymbol, currentTargetSymbolIds, pageSize, pageOffset, lang, pathPatterns, excludePathPatterns, excludeTests, includeAmbiguousMSource, includeMemberReads)
                    : currentSymbolId is long targetSymbolId
                        ? GetCallersExactForTarget(currentSymbol, targetSymbolId, pageSize, pageOffset, lang, pathPatterns, excludePathPatterns, excludeTests, includeAmbiguousMSource, includeMemberReads)
                        : GetCallersExact(currentSymbol, pageSize, pageOffset, lang, pathPatterns, excludePathPatterns, excludeTests, includeAmbiguousMSource, includeMemberReads);

                if (page.Count == 0)
                    break; // No more callers for this symbol / このシンボルの caller は尽きた

                foreach (var caller in page)
                {
                    if (discoveredResultCount >= resultWindowEnd)
                    {
                        truncated = true;
                        truncatedReason ??= ImpactTruncatedReasons.UserLimit;
                        break;
                    }

                    var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                    var callerSymbolId = hasResolvedIdentityGraph ? caller.CallerSymbolId : null;
                    var calleeSymbolId = hasResolvedIdentityGraph ? caller.CalleeSymbolId : null;
                    var cycleEdges = BuildImpactCycleEdges(
                        caller,
                        callerName,
                        currentSymbol,
                        hasResolvedIdentityGraph,
                        isLogicalPartialFamilyRoot ? identityRootSymbolIds : null,
                        rootTraversalNodeKey,
                        resolvedName);
                    foreach (var cycleEdge in cycleEdges)
                    {
                        RegisterImpactCycleNode(cycleNodesByKey, cycleEdge.Caller);
                        RegisterImpactCycleNode(cycleNodesByKey, cycleEdge.Callee);
                        if (IsCycleEdge(cycleEdge.Caller.Key, cycleEdge.Callee.Key, cycleParentsByKey))
                            AddImpactCycle(cycles, cycleKeys, BuildCycleMembers(cycleEdge.Caller.Key, cycleEdge.Callee.Key, cycleParentsByKey), cycleNodesByKey);
                    }
                    if (IsImpactRootCaller(caller, callerName, resolvedName, rootDefinitionPaths, identityRootSymbolIds))
                        continue;
                    var callerNodeKey = BuildImpactTraversalNodeKey(callerSymbolId, callerName);
                    var key = BuildImpactVisitedKey(
                        caller,
                        callerName,
                        hasResolvedIdentityGraph,
                        deduplicateLogicalNodes: isLogicalPartialFamilyRoot);
                    foreach (var cycleEdge in cycleEdges)
                    {
                        if (!cycleParentsByKey.TryGetValue(cycleEdge.Caller.Key, out var cycleParentSet))
                        {
                            cycleParentSet = new HashSet<string>(StringComparer.Ordinal);
                            cycleParentsByKey[cycleEdge.Caller.Key] = cycleParentSet;
                        }
                        cycleParentSet.Add(cycleEdge.Callee.Key);
                    }
                    if (ImpactGraphStateEntryCount(parentsByNodeKey, cycleParentsByKey, depthByNodeKey, resultIndicesByNodeKey) > graphStateEntryBudget)
                    {
                        graphStateBudgetHit = true;
                        truncated = true;
                        truncatedReason = ImpactTruncatedReasons.GraphStateBudget;
                        break;
                    }

                    if (!visited.Add(key))
                    {
                        if (resultIndexByVisitedKey != null
                            && resultIndexByVisitedKey.TryGetValue(key, out var existingResultIndex))
                        {
                            MergeImpactReferenceEvidence(results[existingResultIndex], caller);
                        }
                        // Same-depth convergence: record the additional parent so path
                        // enumeration can discover this alternate route. Other-depth re-arrivals
                        // are intentionally dropped — BFS already keeps the shortest route.
                        // 同 depth で再到達した場合のみ親辺を追加し、別 depth の到達は破棄。
                        // BFS により最短経路だけが残る。
                        if (withPaths
                            && depthByNodeKey.TryGetValue(callerNodeKey, out var existingDepth)
                            && existingDepth == depth + 1)
                        {
                            parentsByNodeKey[callerNodeKey].Add(currentNodeKey);
                            if (ImpactGraphStateEntryCount(parentsByNodeKey, cycleParentsByKey, depthByNodeKey, resultIndicesByNodeKey) > graphStateEntryBudget)
                            {
                                graphStateBudgetHit = true;
                                truncated = true;
                                truncatedReason = ImpactTruncatedReasons.GraphStateBudget;
                                break;
                            }
                        }
                        continue;
                    }

                    var includeInPage = discoveredResultCount >= resultOffset;
                    var resultIndex = -1;
                    if (includeInPage)
                    {
                        results.Add(new ImpactResult
                        {
                            Path = caller.Path,
                            Lang = caller.Lang,
                            CallerKind = caller.CallerKind,
                            CallerName = caller.CallerName,
                            CalleeName = caller.CalleeName,
                            CallerSymbolId = callerSymbolId,
                            CalleeSymbolId = calleeSymbolId,
                            Depth = depth + 1,
                            FirstLine = caller.FirstLine,
                            ReferenceCount = caller.ReferenceCount,
                            ReferenceKind = caller.ReferenceKind,
                            ReferenceKinds = caller.ReferenceKinds,
                            ReferenceKindCounts = caller.ReferenceKindCounts,
                        });
                        resultIndex = results.Count - 1;
                        resultIndexByVisitedKey?.Add(key, resultIndex);
                    }
                    discoveredResultCount++;

                    if (withPaths)
                    {
                        pathNodesByKey!.TryAdd(
                            callerNodeKey,
                            ResolveImpactPathNode(
                                callerName,
                                callerSymbolId,
                                caller.CallerKind,
                                caller.Lang ?? lang,
                                caller.Path,
                                caller.FirstLine));
                        if (!depthByNodeKey.ContainsKey(callerNodeKey))
                            depthByNodeKey[callerNodeKey] = depth + 1;
                        if (includeInPage)
                        {
                            if (!resultIndicesByNodeKey!.TryGetValue(callerNodeKey, out var idxList))
                            {
                                idxList = new List<int>();
                                resultIndicesByNodeKey[callerNodeKey] = idxList;
                            }
                            idxList.Add(resultIndex);
                        }
                    }
                    else if (!depthByNodeKey.ContainsKey(callerNodeKey))
                    {
                        depthByNodeKey[callerNodeKey] = depth + 1;
                    }
                    if (!parentsByNodeKey.TryGetValue(callerNodeKey, out var parentSet))
                    {
                        parentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        parentsByNodeKey[callerNodeKey] = parentSet;
                    }
                    parentSet.Add(currentNodeKey);
                    if (ImpactGraphStateEntryCount(parentsByNodeKey, cycleParentsByKey, depthByNodeKey, resultIndicesByNodeKey) > graphStateEntryBudget)
                    {
                        graphStateBudgetHit = true;
                        truncated = true;
                        truncatedReason = ImpactTruncatedReasons.GraphStateBudget;
                        break;
                    }

                    // Only recurse if the just-added caller (at depth + 1) is strictly below
                    // maxDepth, so that the next BFS step can reach depth + 2 ≤ maxDepth.
                    // This keeps the maxDepth bound inclusive of depth = maxDepth results.
                    // 追加した caller (depth + 1) が maxDepth より小さいときだけ再帰し、
                    // 次の BFS で depth + 2 ≤ maxDepth まで到達できるようにする。
                    // これにより maxDepth は inclusive な上限として機能する。
                    if (caller.CallerName != null
                        && caller.CallerName != SyntheticTopLevelCallerName
                        && depth + 1 < maxDepth)
                    {
                        queue.Enqueue((caller.CallerName, callerSymbolId, null, callerNodeKey, depth + 1));
                    }
                    else if (caller.CallerName != null
                             && caller.CallerName != SyntheticTopLevelCallerName
                             && depth + 1 == maxDepth)
                    {
                        var boundaryInspection = InspectBoundaryCallersCore(
                            caller.CallerName,
                            callerSymbolId,
                            resolvedName,
                            rootDefinitionPaths,
                            identityRootSymbolIds,
                            visited,
                            cycleParentsByKey,
                            cycleNodesByKey,
                            cycles,
                            cycleKeys,
                            hasResolvedIdentityGraph,
                            lang,
                            pathPatterns,
                            excludePathPatterns,
                            excludeTests,
                            includeAmbiguousMSource,
                            includeMemberReads,
                            isLogicalPartialFamilyRoot ? identityRootSymbolIds : null,
                            rootTraversalNodeKey,
                            resolvedName);
                        maxDepthReached |= boundaryInspection.HasUnvisitedCaller;
                        if (boundaryInspection.ProbeBudgetHit)
                        {
                            boundaryProbeBudgetHit = true;
                            truncated = true;
                            truncatedReason = ImpactTruncatedReasons.BoundaryProbeBudget;
                            break;
                        }
                    }
                }

                pageOffset += page.Count;

                // If this page was full, there might be more — continue paging
                // ページが満杯なら、まだある可能性 — ページングを継続
                if (page.Count < pageSize)
                    break;
            }

            // If fetch iteration cap was hit, mark as truncated / フェッチ反復上限に達した場合も truncated
            if (fetchIterations >= maxFetchIterations)
            {
                truncated = true;
                truncatedReason = ImpactTruncatedReasons.SafetyCap;
            }
        }

        if (queue.Count > 0 && discoveredResultCount >= resultWindowEnd)
        {
            truncated = true;
            truncatedReason ??= ImpactTruncatedReasons.UserLimit;
        }

        if (withPaths)
        {
            var effectiveCap = maxPathsPerResult > 0 ? maxPathsPerResult : DefaultImpactPathsPerResult;
            foreach (var (callerNodeKey, indices) in resultIndicesByNodeKey!)
            {
                var (pathKeys, more) = EnumerateImpactPaths(callerNodeKey, parentsByNodeKey, rootTraversalNodeKey, effectiveCap);
                var paths = pathKeys
                    .Select(path => path.Select(nodeKey => pathNodesByKey![nodeKey].Name).ToList())
                    .ToList();
                foreach (var idx in indices)
                {
                    results[idx].Paths = paths;
                    results[idx].PathDetails = BuildImpactPathDetails(pathKeys, pathNodesByKey!, results[idx]);
                    results[idx].PathsTruncated = more;
                }
            }
        }

        var terminationReason = truncatedReason switch
        {
            ImpactTruncatedReasons.GraphStateBudget => ImpactTerminationReasons.GraphStateBudget,
            ImpactTruncatedReasons.BoundaryProbeBudget => ImpactTerminationReasons.BoundaryProbeBudget,
            ImpactTruncatedReasons.SafetyCap => ImpactTerminationReasons.SafetyCap,
            ImpactTruncatedReasons.UserLimit => ImpactTerminationReasons.RowLimitTruncated,
            _ when cycles.Count > 0 => ImpactTerminationReasons.CycleDetected,
            _ when maxDepthReached => ImpactTerminationReasons.MaxDepthReached,
            _ => ImpactTerminationReasons.Completed,
        };

        return (results, truncated, truncatedReason, terminationReason, cycles);
    }

    private static int GetImpactGraphStateEntryBudget(int limit)
    {
        var limitScaled = Math.Max(1, limit) * 200;
        return Math.Max(1024, Math.Min(DefaultImpactGraphStateEntryBudget, limitScaled));
    }

    private static void MergeImpactReferenceEvidence(ImpactResult result, CallerResult caller)
    {
        var counts = result.ReferenceKindCounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (kind, count) in caller.ReferenceKindCounts)
        {
            counts[kind] = counts.TryGetValue(kind, out var existingCount)
                ? Math.Max(existingCount, count)
                : count;
        }

        result.ReferenceKindCounts = counts;
        result.ReferenceKinds = counts.Keys.Order(StringComparer.Ordinal).ToArray();
        result.ReferenceCount = counts.Values.Sum();
        result.FirstLine = Math.Min(result.FirstLine, caller.FirstLine);
    }

    private static int ImpactGraphStateEntryCount(
        Dictionary<string, HashSet<string>> parentsByNodeKey,
        Dictionary<string, HashSet<string>> cycleParentsByNodeKey,
        Dictionary<string, int> depthByNodeKey,
        Dictionary<string, List<int>>? resultIndicesByNodeKey)
    {
        var count = depthByNodeKey.Count + parentsByNodeKey.Count + cycleParentsByNodeKey.Count + (resultIndicesByNodeKey?.Count ?? 0);
        foreach (var parents in parentsByNodeKey.Values)
            count += parents.Count;
        foreach (var parents in cycleParentsByNodeKey.Values)
            count += parents.Count;
        if (resultIndicesByNodeKey != null)
            foreach (var indices in resultIndicesByNodeKey.Values)
                count += indices.Count;
        return count;
    }

    private readonly record struct ImpactBoundaryInspection(bool HasUnvisitedCaller, bool ProbeBudgetHit);
    private readonly record struct ImpactCycleNode(string Key, long? SymbolId, string Name);
    private readonly record struct ImpactCycleEdge(ImpactCycleNode Caller, ImpactCycleNode Callee);

    private static void RegisterImpactCycleNode(
        Dictionary<string, ImpactCycleMemberResult> nodesByKey,
        ImpactCycleNode node)
        => nodesByKey.TryAdd(node.Key, new ImpactCycleMemberResult
        {
            SymbolId = node.SymbolId,
            Name = node.Name,
        });

    private static ImpactCycleNode? BuildImpactCycleNode(long? symbolId, string name, bool hasResolvedIdentityGraph)
    {
        if (!hasResolvedIdentityGraph)
            return new ImpactCycleNode($"name:{NameFold.Fold(name) ?? name}", null, name);
        if (symbolId is long canonicalSymbolId)
            return new ImpactCycleNode($"id:{canonicalSymbolId}", canonicalSymbolId, name);
        return null;
    }

    private static List<ImpactCycleEdge> BuildImpactCycleEdges(
        CallerResult caller,
        string callerName,
        string calleeName,
        bool hasResolvedIdentityGraph,
        IReadOnlySet<long>? logicalRootSymbolIds = null,
        string? logicalRootKey = null,
        string? logicalRootName = null)
    {
        var callerNode = NormalizeImpactCycleRootNode(
            BuildImpactCycleNode(caller.CallerSymbolId, callerName, hasResolvedIdentityGraph),
            logicalRootSymbolIds,
            logicalRootKey,
            logicalRootName);
        if (callerNode is not { } canonicalCaller)
            return [];

        if (!hasResolvedIdentityGraph)
        {
            var legacyCallee = BuildImpactCycleNode(symbolId: null, calleeName, hasResolvedIdentityGraph: false)!.Value;
            return [new ImpactCycleEdge(canonicalCaller, legacyCallee)];
        }

        var calleeSymbolIds = caller.CalleeSymbolIds.Count > 0
            ? caller.CalleeSymbolIds
            : caller.CalleeSymbolId is long calleeSymbolId
                ? [calleeSymbolId]
                : Array.Empty<long>();
        return calleeSymbolIds
            .Distinct()
            .Order()
            .Select(calleeSymbolId => new ImpactCycleEdge(
                canonicalCaller,
                NormalizeImpactCycleRootNode(
                    new ImpactCycleNode($"id:{calleeSymbolId}", calleeSymbolId, calleeName),
                    logicalRootSymbolIds,
                    logicalRootKey,
                    logicalRootName)!.Value))
            .ToList();
    }

    private static ImpactCycleNode? NormalizeImpactCycleRootNode(
        ImpactCycleNode? node,
        IReadOnlySet<long>? logicalRootSymbolIds,
        string? logicalRootKey,
        string? logicalRootName)
    {
        if (node is not { SymbolId: long symbolId }
            || logicalRootSymbolIds is not { Count: > 0 }
            || !logicalRootSymbolIds.Contains(symbolId))
        {
            return node;
        }

        return new ImpactCycleNode(
            logicalRootKey ?? "logical-partial-root",
            SymbolId: null,
            logicalRootName ?? node.Value.Name);
    }

    private static bool IsImpactRootCaller(
        CallerResult caller,
        string callerName,
        string resolvedName,
        HashSet<string> rootDefinitionPaths,
        IReadOnlySet<long>? identityRootSymbolIds)
    {
        if (identityRootSymbolIds is { Count: > 0 }
            && caller.CallerSymbolId is long callerSymbolId)
        {
            return identityRootSymbolIds.Contains(callerSymbolId);
        }
        return string.Equals(callerName, resolvedName, StringComparison.OrdinalIgnoreCase)
               && (rootDefinitionPaths.Count == 0 || rootDefinitionPaths.Contains(caller.Path));
    }

    private ImpactBoundaryInspection InspectBoundaryCallers(
        string symbolName,
        long? symbolId,
        string resolvedName,
        HashSet<string> rootDefinitionPaths,
        IReadOnlySet<long>? identityRootSymbolIds,
        HashSet<string> visited,
        Dictionary<string, HashSet<string>> cycleParentsByKey,
        Dictionary<string, ImpactCycleMemberResult> cycleNodesByKey,
        List<ImpactCycleResult> cycles,
        HashSet<string> cycleKeys,
        bool hasResolvedIdentityGraph,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool includeAmbiguousMSource,
        bool includeMemberReads)
        => InspectBoundaryCallersCore(
            symbolName,
            symbolId,
            resolvedName,
            rootDefinitionPaths,
            identityRootSymbolIds,
            visited,
            cycleParentsByKey,
            cycleNodesByKey,
            cycles,
            cycleKeys,
            hasResolvedIdentityGraph,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            includeAmbiguousMSource,
            includeMemberReads,
            logicalRootSymbolIds: null,
            logicalRootKey: null,
            logicalRootName: null);

    private ImpactBoundaryInspection InspectBoundaryCallersCore(
        string symbolName,
        long? symbolId,
        string resolvedName,
        HashSet<string> rootDefinitionPaths,
        IReadOnlySet<long>? identityRootSymbolIds,
        HashSet<string> visited,
        Dictionary<string, HashSet<string>> cycleParentsByKey,
        Dictionary<string, ImpactCycleMemberResult> cycleNodesByKey,
        List<ImpactCycleResult> cycles,
        HashSet<string> cycleKeys,
        bool hasResolvedIdentityGraph,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool includeAmbiguousMSource,
        bool includeMemberReads,
        IReadOnlySet<long>? logicalRootSymbolIds,
        string? logicalRootKey,
        string? logicalRootName)
    {
        var offset = 0;
        var probes = 0;
        while (true)
        {
            if (probes >= ImpactBoundaryCallerProbeBudget)
                return new ImpactBoundaryInspection(HasUnvisitedCaller: true, ProbeBudgetHit: true);

            var pageSize = Math.Min(ImpactBoundaryCallerProbePageSize, ImpactBoundaryCallerProbeBudget - probes);
            var page = symbolId is long targetSymbolId
                ? GetCallersExactForTarget(symbolName, targetSymbolId, pageSize, offset, lang, pathPatterns, excludePathPatterns, excludeTests, includeAmbiguousMSource, includeMemberReads)
                : GetCallersExact(symbolName, pageSize, offset, lang, pathPatterns, excludePathPatterns, excludeTests, includeAmbiguousMSource, includeMemberReads);
            if (page.Count == 0)
                return new ImpactBoundaryInspection(HasUnvisitedCaller: false, ProbeBudgetHit: false);
            probes += page.Count;

            foreach (var caller in page)
            {
                var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                var cycleEdges = BuildImpactCycleEdges(
                    caller,
                    callerName,
                    symbolName,
                    hasResolvedIdentityGraph,
                    logicalRootSymbolIds,
                    logicalRootKey,
                    logicalRootName);
                foreach (var cycleEdge in cycleEdges)
                {
                    RegisterImpactCycleNode(cycleNodesByKey, cycleEdge.Caller);
                    RegisterImpactCycleNode(cycleNodesByKey, cycleEdge.Callee);
                    if (IsCycleEdge(cycleEdge.Caller.Key, cycleEdge.Callee.Key, cycleParentsByKey))
                        AddImpactCycle(cycles, cycleKeys, BuildCycleMembers(cycleEdge.Caller.Key, cycleEdge.Callee.Key, cycleParentsByKey), cycleNodesByKey);
                }
                var isRoot = IsImpactRootCaller(caller, callerName, resolvedName, rootDefinitionPaths, identityRootSymbolIds);
                if (isRoot)
                    continue;

                foreach (var cycleEdge in cycleEdges)
                {
                    if (!cycleParentsByKey.TryGetValue(cycleEdge.Caller.Key, out var cycleParentSet))
                    {
                        cycleParentSet = new HashSet<string>(StringComparer.Ordinal);
                        cycleParentsByKey[cycleEdge.Caller.Key] = cycleParentSet;
                    }
                    cycleParentSet.Add(cycleEdge.Callee.Key);
                }

                var key = BuildImpactVisitedKey(
                    caller,
                    callerName,
                    hasResolvedIdentityGraph,
                    deduplicateLogicalNodes: logicalRootSymbolIds is not null);
                if (!visited.Contains(key))
                    return new ImpactBoundaryInspection(HasUnvisitedCaller: true, ProbeBudgetHit: false);
            }

            if (page.Count < pageSize)
                return new ImpactBoundaryInspection(HasUnvisitedCaller: false, ProbeBudgetHit: false);
            offset += page.Count;
        }
    }

    private static bool IsCycleEdge(
        string callerKey,
        string currentKey,
        Dictionary<string, HashSet<string>> parentsByKey)
    {
        if (string.Equals(callerKey, currentKey, StringComparison.Ordinal))
            return true;
        return HasAncestor(currentKey, callerKey, parentsByKey);
    }

    private static bool HasAncestor(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByKey)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
                continue;
            if (string.Equals(current, target, StringComparison.Ordinal))
                return true;
            if (!parentsByKey.TryGetValue(current, out var parents))
                continue;
            foreach (var parent in parents)
                stack.Push(parent);
        }
        return false;
    }

    private static List<string> BuildCycleMembers(
        string callerKey,
        string currentKey,
        Dictionary<string, HashSet<string>> parentsByKey)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (!TryBuildAncestorPath(currentKey, callerKey, parentsByKey, members))
        {
            members.Add(callerKey);
            members.Add(currentKey);
        }
        var result = members.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static bool TryBuildAncestorPath(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByKey,
        HashSet<string> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return TryBuildAncestorPathCore(node, target, parentsByKey, members, seen);
    }

    private static bool TryBuildAncestorPathCore(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByKey,
        HashSet<string> members,
        HashSet<string> seen)
    {
        if (!seen.Add(node))
            return false;
        members.Add(node);
        if (string.Equals(node, target, StringComparison.Ordinal))
            return true;
        if (parentsByKey.TryGetValue(node, out var parents))
        {
            foreach (var parent in parents)
            {
                if (TryBuildAncestorPathCore(parent, target, parentsByKey, members, seen))
                    return true;
            }
        }

        members.Remove(node);
        return false;
    }

    private static void AddImpactCycle(
        List<ImpactCycleResult> cycles,
        HashSet<string> cycleKeys,
        List<string> memberKeys,
        IReadOnlyDictionary<string, ImpactCycleMemberResult> nodesByKey)
    {
        if (memberKeys.Count == 0)
            return;
        var key = string.Join("\u001F", memberKeys);
        if (!cycleKeys.Add(key))
            return;
        var identities = memberKeys
            .Select(memberKey => nodesByKey[memberKey])
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.SymbolId)
            .Select(node => new ImpactCycleMemberResult
            {
                SymbolId = node.SymbolId,
                Name = node.Name,
            })
            .ToList();
        cycles.Add(new ImpactCycleResult
        {
            Members = identities.Select(identity => identity.Name).ToList(),
            MemberIdentities = identities.Any(identity => identity.SymbolId != null) ? identities : null,
        });
    }

    private static (List<List<string>> Paths, bool Truncated) EnumerateImpactPaths(
        string callerNodeKey,
        Dictionary<string, HashSet<string>> parentsByNodeKey,
        string resolvedRootNodeKey,
        int maxPathsPerResult)
    {
        // DFS upward through canonical node keys. The Stack<T> enumerator yields top-first,
        // so the materialized key path is already ordered [resolvedRoot, ..., caller].
        // 正規 node key の親辺を DFS で辿る。Stack<T> は top-first で列挙されるため、
        // key path はそのまま [resolvedRoot, ..., caller] 順になる。
        var paths = new List<List<string>>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncatedRef = new bool[1];

        stack.Push(callerNodeKey);
        onStack.Add(callerNodeKey);
        Dfs(callerNodeKey);
        stack.Pop();
        onStack.Remove(callerNodeKey);
        return (paths, truncatedRef[0]);

        void Dfs(string node)
        {
            if (string.Equals(node, resolvedRootNodeKey, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(stack.ToList());
                return;
            }
            if (!parentsByNodeKey.TryGetValue(node, out var parents))
                return;
            foreach (var p in parents)
            {
                if (onStack.Contains(p))
                    continue;
                // Only mark truncated when the cap forces us to *skip* a still-unexplored parent;
                // hitting cap exactly as the foreach drains naturally is not a truncation.
                // 残りの parent を探索できなくなった瞬間にのみ truncated を立てる。foreach が
                // 自然に終わるタイミングと一致しただけでは truncation 扱いしない。
                if (paths.Count >= maxPathsPerResult)
                {
                    truncatedRef[0] = true;
                    return;
                }
                stack.Push(p);
                onStack.Add(p);
                Dfs(p);
                stack.Pop();
                onStack.Remove(p);
            }
        }
    }

    private ImpactPathNode ResolveImpactPathNode(string name, long? symbolId, string? kind, string? lang, string? referencePath, int? referenceLine)
    {
        var node = TryResolveImpactPathNodeDefinition(name, symbolId, kind, lang, referencePath)
            ?? new ImpactPathNode
            {
                SymbolId = symbolId,
                Name = name,
                Kind = kind,
                Lang = lang,
            };
        node.ReferencePath = referencePath;
        node.ReferenceLine = referenceLine;
        return node;
    }

    private ImpactPathNode? TryResolveImpactPathNodeDefinition(string name, long? symbolId, string? kind, string? lang, string? preferredPath)
    {
        if (!_symbolColumns.Contains("name") || !_symbolColumns.Contains("kind"))
            return null;

        using var cmd = _conn.CreateCommand();
        var containerNameSql = GetSymbolColumnSql("container_name");
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var familyKeySql = GetSymbolColumnSql("family_key");
        var namePredicate = _foldReady && _symbolColumns.Contains("name_folded")
            ? "s.name_folded = @nameFolded"
            : "s.name = @name COLLATE NOCASE";

        cmd.CommandText = $@"
            SELECT s.id,
                   f.path,
                   f.lang,
                   s.kind,
                   s.name,
                   s.line,
                   {containerNameSql} AS container_name,
                   {containerQualifiedNameSql} AS container_qualified_name,
                   {familyKeySql} AS family_key,
                   s.file_id,
                   COUNT(*) OVER () AS matching_definition_count
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE ((@symbolId IS NOT NULL AND s.id = @symbolId)
                   OR (@symbolId IS NULL AND {namePredicate}))
              AND s.kind NOT IN ('import', 'namespace')
              AND (@kind IS NULL OR s.kind = @kind)
              AND (@lang IS NULL OR f.lang = @lang)
            ORDER BY CASE WHEN @preferredPath IS NOT NULL AND f.path = @preferredPath THEN 0 ELSE 1 END,
                     f.path,
                     s.line
            LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        SqliteCommandPolicy.AddNullableInt64(cmd, "@symbolId", symbolId);
        if (_foldReady && _symbolColumns.Contains("name_folded"))
            SqliteCommandPolicy.Add(cmd, "@nameFolded", NameFold.Fold(name) ?? name);
        SqliteCommandPolicy.AddNullableText(cmd, "@kind", kind);
        SqliteCommandPolicy.AddNullableText(cmd, "@lang", lang);
        SqliteCommandPolicy.AddNullableText(cmd, "@preferredPath", preferredPath);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        var definitionSymbolId = reader.GetInt64(0);
        var definitionPath = reader.GetString(1);
        var definitionLang = GetNullableString(reader, 2);
        var definitionKind = reader.GetString(3);
        var definitionName = reader.GetString(4);
        var definitionLine = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var containerName = GetNullableString(reader, 6);
        var containerQualifiedName = GetNullableString(reader, 7);
        var familyKey = GetNullableString(reader, 8);
        var fileId = reader.GetInt64(9);
        var matchingDefinitionCount = reader.GetInt64(10);

        return new ImpactPathNode
        {
            SymbolId = symbolId ?? (matchingDefinitionCount == 1 ? definitionSymbolId : null),
            Name = definitionName,
            Kind = definitionKind,
            Lang = definitionLang,
            DefinitionPath = definitionPath,
            DefinitionLine = definitionLine,
            Container = containerName,
            FamilyKey = familyKey,
            LogicalTargetKey = BuildImpactPathLogicalTargetKey(definitionLang, definitionKind, familyKey, containerQualifiedName, fileId),
        };
    }

    private static string BuildImpactPathLogicalTargetKey(string? lang, string kind, string? familyKey, string? containerQualifiedName, long fileId)
    {
        if (!string.IsNullOrWhiteSpace(familyKey))
            return $"family|{lang ?? string.Empty}|{kind}|{familyKey}";
        if (!string.IsNullOrWhiteSpace(containerQualifiedName))
            return $"container|{fileId}|{kind}|{containerQualifiedName}";
        return $"file|{fileId}";
    }

    private static List<List<ImpactPathNode>> BuildImpactPathDetails(
        List<List<string>> pathKeys,
        IReadOnlyDictionary<string, ImpactPathNode> nodesByKey,
        ImpactResult result)
    {
        var details = new List<List<ImpactPathNode>>(pathKeys.Count);
        foreach (var path in pathKeys)
        {
            var detailPath = new List<ImpactPathNode>(path.Count);
            for (var i = 0; i < path.Count; i++)
            {
                var nodeKey = path[i];
                var isResultNode = i == path.Count - 1;
                if (!nodesByKey.TryGetValue(nodeKey, out var node))
                    node = new ImpactPathNode { Name = nodeKey };
                detailPath.Add(isResultNode
                    ? CloneImpactPathNodeForResult(node, result)
                    : CloneImpactPathNode(node));
            }
            details.Add(detailPath);
        }
        return details;
    }

    private static ImpactPathNode CloneImpactPathNodeForResult(ImpactPathNode node, ImpactResult result)
    {
        var clone = CloneImpactPathNode(node);
        clone.Kind ??= result.CallerKind;
        clone.Lang ??= result.Lang;
        clone.ReferencePath = result.Path;
        clone.ReferenceLine = result.FirstLine;
        return clone;
    }

    private static ImpactPathNode CloneImpactPathNode(ImpactPathNode node)
        => new()
        {
            SymbolId = node.SymbolId,
            Name = node.Name,
            Kind = node.Kind,
            Lang = node.Lang,
            DefinitionPath = node.DefinitionPath,
            DefinitionLine = node.DefinitionLine,
            Container = node.Container,
            FamilyKey = node.FamilyKey,
            PartialFamilyId = node.PartialFamilyId,
            LogicalTargetKey = node.LogicalTargetKey,
            ReferencePath = node.ReferencePath,
            ReferenceLine = node.ReferenceLine,
        };

    /// <summary>
    /// Analyze impact for a query by combining transitive callers with symbol-resolution
    /// metadata and a class-like file-dependency fallback when symbol-level callers are absent.
    /// The <paramref name="maxDepth"/> bound is inclusive (callers at depth 1..N are returned);
    /// <c>maxDepth: 0</c> short-circuits to symbol resolution only.
    /// impact 用に caller BFS と解決メタデータを束ね、class 系で caller 不在なら
    /// file dependency をフォールバックとして返す。<paramref name="maxDepth"/> は inclusive で
    /// N 指定時は depth 1〜N の caller を返し、<c>maxDepth: 0</c> は symbol 解決のみで終了する。
    /// </summary>
    public ImpactAnalysisResult AnalyzeImpact(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool withPaths = false, int offset = 0, string? responseCollection = null, bool includeMemberReads = false)
    {
        lang = NormalizeQueryLanguage(lang);
        var resolvedName = ResolveSymbolName(symbolName, lang);
        var definitionOffset = string.Equals(responseCollection, "definitions", StringComparison.Ordinal) ? offset : 0;
        var definitionResolution = ResolveImpactDefinitions(symbolName, limit, lang, pathPatterns, excludePathPatterns, excludeTests, definitionOffset);
        if (definitionResolution.Definitions.Count == 0
            && !string.Equals(symbolName, resolvedName, StringComparison.Ordinal))
        {
            definitionResolution = ResolveImpactDefinitions(resolvedName, limit, lang, pathPatterns, excludePathPatterns, excludeTests, definitionOffset);
        }
        var definitions = definitionResolution.Definitions;
        var indexedPathComparer = GetIndexedPathComparer();
        var definitionPaths = definitions
            .Select(d => d.Path)
            .Distinct(indexedPathComparer)
            .ToList();
        var hasMultipleDefinitions = definitionResolution.PhysicalCount > 1;
        var fallbackDefinitions = definitionResolution.SinglePreciseDefinition != null
            ? [definitionResolution.SinglePreciseDefinition]
            : definitions.Where(d => IsPreciseImpactFallbackKind(d.Kind)).ToList();
        var fallbackDefinitionPaths = fallbackDefinitions
            .Select(d => d.Path)
            .Distinct(indexedPathComparer)
            .ToList();
        var hasMultipleFallbackDefinitions = definitionResolution.PreciseLogicalDefinitionCount > 1;
        var hasMultipleFallbackDefinitionFiles = definitionResolution.PreciseDefinitionFileCount > 1;
        var hasClassLikeDefinitions = definitionResolution.PreciseDefinitionCount > 0;
        var logicalPartialFamilyDefinition = _referenceIdentityContractCurrent
                                             && definitionResolution.LogicalCount == 1
                                             && definitions.Count == 1
                                             && definitions[0].Lang == "csharp"
                                             && definitions[0].PartialFamilyId != null
            ? definitions[0]
            : null;
        var traversalRootScope = logicalPartialFamilyDefinition != null
            ? "logical_partial_family"
            : "symbol";
        var partialFamilyMemberCount = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalCount
            : (int?)null;
        var partialFamilyMemberRootCount = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalSymbolIds.Count
            : (int?)null;
        var partialFamilyMemberRootLimit = logicalPartialFamilyDefinition != null
            ? Math.Max(1, ImpactPartialFamilyMemberBudget)
            : (int?)null;
        var partialFamilyMemberRootTruncated = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalSymbolIdsTruncated
            : (bool?)null;
        var partialFamilyMemberRootOmitted = logicalPartialFamilyDefinition != null
            ? Math.Max(0, definitionResolution.PhysicalCount - definitionResolution.PhysicalSymbolIds.Count)
            : (int?)null;

        if (maxDepth <= 0)
        {
            return new ImpactAnalysisResult
            {
                Query = symbolName,
                ResolvedName = resolvedName,
                ImpactMode = "none",
                Heuristic = false,
                MaxDepth = maxDepth,
                DefinitionCount = definitionResolution.PhysicalCount,
                DefinitionFileCount = definitionResolution.PhysicalFileCount,
                LogicalDefinitionCount = definitionResolution.LogicalCount,
                HintCount = 0,
                HasClassLikeDefinitions = hasClassLikeDefinitions,
                HasMultipleDefinitions = hasMultipleDefinitions,
                HasMultipleDefinitionFiles = definitionResolution.PhysicalFileCount > 1,
                TraversalRootScope = traversalRootScope,
                TraversalPartialFamilyId = logicalPartialFamilyDefinition?.PartialFamilyId,
                PartialFamilyMemberCount = partialFamilyMemberCount,
                PartialFamilyMemberRootCount = partialFamilyMemberRootCount,
                PartialFamilyMemberRootLimit = partialFamilyMemberRootLimit,
                PartialFamilyMemberRootTruncated = partialFamilyMemberRootTruncated,
                PartialFamilyMemberRootOmitted = partialFamilyMemberRootOmitted,
                Definitions = definitions,
                Callers = [],
                FileImpacts = [],
                Truncated = false,
                TruncatedReason = null,
                TerminationReason = ImpactTerminationReasons.Completed,
                CycleDetected = false,
                Cycles = null,
                GraphTableAvailable = _hasReferencesTable,
                ZeroResultReason = definitionResolution.PhysicalCount == 0 ? "no_matching_definition" : "depth_requested_zero",
                ImpactFailureChain = definitionResolution.PhysicalCount == 0
                    ? ["definition_not_found", "depth_requested_zero"]
                    : ["depth_requested_zero"],
                SuggestionType = definitionResolution.PhysicalCount == 0 ? "resolution" : "precondition",
                Suggestion = definitionResolution.PhysicalCount == 0
                    ? "Try `cdidx definition <symbol>` to confirm the indexed name."
                    : "Use `cdidx impact <symbol> --max-hops 1` or higher to traverse callers.",
            };
        }

        var callerOffset = responseCollection is null || string.Equals(responseCollection, "callers", StringComparison.Ordinal)
            ? offset
            : 0;
        var (callers, truncated, truncatedReason, terminationReason, cycles) = GetTransitiveCallers(symbolName, maxDepth, limit, lang, pathPatterns, excludePathPatterns, excludeTests, withPaths, resultOffset: callerOffset, includeMemberReads: includeMemberReads);
        var callerExistsBeforeOffset = false;
        if (callers.Count == 0 && callerOffset > 0)
        {
            var callerProbe = GetTransitiveCallers(symbolName, maxDepth, 1, lang, pathPatterns, excludePathPatterns, excludeTests, withPaths: false, resultOffset: 0, includeMemberReads: includeMemberReads);
            callerExistsBeforeOffset = callerProbe.Results.Count > 0;
        }

        var impactMode = "callers";
        var fileImpacts = new List<FileDependencyResult>();
        string? zeroResultReason = null;
        List<string>? impactFailureChain = null;
        string? suggestionType = null;
        string? suggestion = null;
        var heuristic = false;

        if (callers.Count == 0 && !callerExistsBeforeOffset)
        {
            impactMode = "none";
            impactFailureChain = [];

            if (!_hasReferencesTable)
            {
                zeroResultReason = "graph_unavailable";
                impactFailureChain.Add("graph_unavailable");
                suggestionType = "precondition";
                suggestion = "Re-index with the current `cdidx` so symbol reference graph data is available.";
            }
            else
            {
                if (definitionResolution.PhysicalCount > 0
                    && definitionResolution.NonCallableDefinitionCount == definitionResolution.PhysicalCount)
                {
                    zeroResultReason = "non_callable_symbol_kind";
                    impactFailureChain.Add("callable_filter_fails");
                    suggestionType = "resolution";
                    suggestion = "Try `cdidx definition <symbol>` and then run `impact` on a specific callable member instead.";
                }
                else if (hasMultipleFallbackDefinitions)
                {
                    zeroResultReason = hasMultipleFallbackDefinitionFiles ? "multiple_definition_files" : "multiple_definitions";
                    impactFailureChain.Add(zeroResultReason);
                    suggestionType = "resolution";
                    suggestion = BuildImpactSuggestion(fallbackDefinitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: hasMultipleFallbackDefinitionFiles, lang);
                }
                else if (fallbackDefinitions.Count == 1)
                {
                    var fallbackNames = ResolveImpactFallbackNames(
                        fallbackDefinitions[0],
                        logicalPartialFamilyDefinition != null
                            ? definitionResolution.PhysicalDefinitionPaths
                            : null);
                    var fileImpactOffset = responseCollection is null || string.Equals(responseCollection, "file_impacts", StringComparison.Ordinal)
                        ? offset
                        : 0;
                    var (hintResults, hintTruncated) = GetFileDependencyHintsToResolvedType(
                        fallbackDefinitions[0],
                        fallbackNames,
                        limit,
                        lang,
                        pathPatterns,
                        excludePathPatterns,
                        excludeTests,
                        fileImpactOffset,
                        logicalPartialFamilyDefinition != null
                            ? definitionResolution.PhysicalDefinitionPaths
                            : null);
                    fileImpacts = hintResults;
                    var hintExistsBeforeOffset = false;
                    if (fileImpacts.Count == 0 && fileImpactOffset > 0)
                    {
                        var hintProbe = GetFileDependencyHintsToResolvedType(
                            fallbackDefinitions[0],
                            fallbackNames,
                            1,
                            lang,
                            pathPatterns,
                            excludePathPatterns,
                            excludeTests,
                            0,
                            logicalPartialFamilyDefinition != null
                                ? definitionResolution.PhysicalDefinitionPaths
                                : null);
                        hintExistsBeforeOffset = hintProbe.Results.Count > 0;
                    }
                    if (hintTruncated)
                    {
                        truncated = true;
                        // Heuristic hints can only be capped by the user-supplied --limit, so this
                        // path never escalates to safety_cap. Leave any pre-existing reason
                        // (e.g. safety_cap propagated from the caller BFS above) intact since it
                        // is the stronger signal. Issue #1533.
                        // ヒント側の truncation は --limit による cap のみ。caller BFS で
                        // safety_cap が立っていればそちらを優先する (#1533)。
                        truncatedReason ??= ImpactTruncatedReasons.UserLimit;
                    }
                    if (fileImpacts.Count > 0 || hintExistsBeforeOffset)
                    {
                        impactMode = "file_dependency_hints";
                        heuristic = true;
                        suggestion = "These file-level dependents are heuristic only; confirm with `cdidx deps --path <definition-path> --reverse` and a member-level `impact` query.";
                    }
                    else
                    {
                        zeroResultReason = "class_symbol_no_symbol_callers";
                        impactFailureChain.Add("no_callers");
                        suggestionType = "traversal";
                        suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: false, hasMultipleDefinitionFiles: false, lang);
                    }
                }
                else if (hasMultipleDefinitions && logicalPartialFamilyDefinition == null)
                {
                    zeroResultReason = definitionResolution.PhysicalFileCount > 1 ? "multiple_definition_files" : "multiple_definitions";
                    impactFailureChain.Add(zeroResultReason);
                    suggestionType = "resolution";
                    suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: definitionResolution.PhysicalFileCount > 1, lang);
                }
                else if (definitionResolution.PhysicalCount == 0)
                {
                    zeroResultReason = "no_matching_definition";
                    impactFailureChain.Add("definition_not_found");
                    suggestionType = "resolution";
                    suggestion = "Try `cdidx definition <symbol>` to confirm the indexed name.";
                }
                else
                {
                    impactFailureChain.Add("no_callers");
                    suggestionType = "traversal";
                }
            }
        }

        return new ImpactAnalysisResult
        {
            Query = symbolName,
            ResolvedName = resolvedName,
            ImpactMode = impactMode,
            Heuristic = heuristic,
            MaxDepth = maxDepth,
            DefinitionCount = definitionResolution.PhysicalCount,
            DefinitionFileCount = definitionResolution.PhysicalFileCount,
            LogicalDefinitionCount = definitionResolution.LogicalCount,
            HintCount = fileImpacts.Count,
            HasClassLikeDefinitions = hasClassLikeDefinitions,
            HasMultipleDefinitions = hasMultipleDefinitions,
            HasMultipleDefinitionFiles = definitionResolution.PhysicalFileCount > 1,
            TraversalRootScope = traversalRootScope,
            TraversalPartialFamilyId = logicalPartialFamilyDefinition?.PartialFamilyId,
            PartialFamilyMemberCount = partialFamilyMemberCount,
            PartialFamilyMemberRootCount = partialFamilyMemberRootCount,
            PartialFamilyMemberRootLimit = partialFamilyMemberRootLimit,
            PartialFamilyMemberRootTruncated = partialFamilyMemberRootTruncated,
            PartialFamilyMemberRootOmitted = partialFamilyMemberRootOmitted,
            Definitions = definitions,
            Callers = callers,
            FileImpacts = fileImpacts,
            Truncated = truncated,
            TruncatedReason = truncated ? truncatedReason : null,
            TerminationReason = terminationReason,
            CycleDetected = cycles.Count > 0,
            Cycles = cycles.Count > 0 ? cycles : null,
            GraphTableAvailable = _hasReferencesTable,
            ZeroResultReason = zeroResultReason,
            ImpactFailureChain = impactFailureChain is { Count: > 0 } ? impactFailureChain : null,
            SuggestionType = suggestionType,
            Suggestion = suggestion,
        };
    }

    private sealed record ImpactDefinitionResolution(
        List<SymbolResult> Definitions,
        int PhysicalCount,
        int PhysicalFileCount,
        int LogicalCount,
        int PreciseDefinitionCount,
        int PreciseLogicalDefinitionCount,
        int PreciseDefinitionFileCount,
        int NonCallableDefinitionCount,
        SymbolResult? SinglePreciseDefinition,
        HashSet<long> PhysicalSymbolIds,
        HashSet<string> PhysicalDefinitionPaths,
        bool PhysicalSymbolIdsTruncated);

    private ImpactDefinitionResolution ResolveImpactDefinitions(
        string resolvedName,
        int representativeLimit,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int representativeOffset = 0)
    {
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(resolvedName);
        var leafName = SqlNameResolver.GetLeafName(resolvedName);
        var segmentCount = SqlNameResolver.GetSegmentCount(resolvedName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(resolvedName);
        EnsureCSharpCallableTypeKinds(lang, [leafName], exact: true);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactDefLang");
        var signatureSql = GetSymbolColumnSql("signature");
        var returnTypeSql = GetSymbolColumnSql("return_type");
        var bodyStartLineSql = GetSymbolColumnSql("body_start_line");
        var bodyEndLineSql = GetSymbolColumnSql("body_end_line");
        var startColumnSql = GetSymbolColumnSql("start_column");
        var identifierStartColumnSql = GetSymbolColumnSql("identifier_start_column");
        var logicalPartialKeySql = LogicalPartialSymbolGrouper.BuildSqlKeyExpression(
            "f.lang",
            "s.kind",
            "s.name",
            "s.id",
            "f.path",
            signatureSql,
            GetSymbolColumnSql("container_name"),
            GetSymbolColumnSql("container_qualified_name"),
            GetSymbolColumnSql("family_key"),
            returnTypeSql,
            GetSymbolColumnSql("is_partial_declaration"),
            _hotspotFamilyReadyLanguages.Contains("csharp"));
        var generatedSql = _fileColumns.Contains("generated")
            ? "CASE WHEN COALESCE(f.generated, 0) <> 0 OR codeindex_generated_file_name(f.path) THEN 1 ELSE 0 END"
            : "CASE WHEN codeindex_generated_file_name(f.path) THEN 1 ELSE 0 END";
        var canonicalPrimaryRankSql = LogicalPartialSymbolGrouper.BuildSqlPrimaryRankExpression(
            "s.kind",
            bodyStartLineSql,
            bodyEndLineSql);
        var canonicalSemanticScoreSql = LogicalPartialSymbolGrouper.BuildSqlSemanticScoreExpression(
            signatureSql,
            "s.kind",
            GetSymbolColumnSql("declaration_semantic_score"));
        var fallbackCanonicalDeclarationIdentitySql = BuildCanonicalDeclarationIdentitySql(signatureSql);
        var canonicalDeclarationIdentitySql = $"CASE WHEN s.kind IN ('function', 'test.method') THEN COALESCE(csharp_partial_callable_identity({signatureSql}, s.name, {returnTypeSql}), {fallbackCanonicalDeclarationIdentitySql}) ELSE {fallbackCanonicalDeclarationIdentitySql} END";
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? $"({BuildPersistedFoldedNameMatchSql("s.name_folded", "@resolvedNameFolded")} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded) OR sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded)))"
                : $"({BuildPersistedFoldedNameMatchSql("s.name_folded", "@resolvedNameFolded")} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @resolvedNameLeaf COLLATE NOCASE)))"
                : "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE))";
        var csharpExplicitInterfaceClause = allowLeafFallback
            ? BuildCSharpExplicitInterfaceShortAliasMatchSql("resolvedName")
            : BuildCSharpExplicitInterfaceIdentityMatchSql("resolvedName");
        nameCondition = $"({nameCondition} OR {csharpExplicitInterfaceClause})";
        if (SqlNameResolver.HasQualifier(resolvedName))
        {
            var containerNameSql = GetSymbolColumnSql("container_name", "''");
            var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", containerNameSql);
            var csharpLeafCondition = _foldReady
                ? "s.name_folded = @resolvedNameLeafFolded"
                : "s.name = @resolvedNameLeaf COLLATE NOCASE";
            nameCondition = $"({nameCondition} OR (f.lang = 'csharp' AND {csharpLeafCondition} AND ({containerNameSql} = @resolvedNameContainer COLLATE NOCASE OR {containerQualifiedNameSql} = @resolvedNameContainer COLLATE NOCASE OR {containerQualifiedNameSql} COLLATE NOCASE LIKE @resolvedNameContainerSuffix ESCAPE '\\')))";
        }
        var matchOrderSql = @"CASE
                     WHEN s.name = @resolvedName THEN 0
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized THEN 1
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded THEN 2
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @resolvedNameLeaf THEN 3
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded THEN 4
                     ELSE 5
                   END";
        var matchingSql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {startColumnSql} AS start_column,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {bodyStartLineSql} AS body_start_line,
                   {bodyEndLineSql} AS body_end_line,
                   {signatureSql} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {returnTypeSql} AS return_type,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {logicalPartialKeySql} AS logical_partial_key,
                   s.id AS symbol_id,
                   {matchOrderSql} AS match_order,
                   {PathBucketOrder} AS path_bucket,
                   {VisibilityOrder} AS visibility_rank,
                   CASE WHEN s.kind IN ('class', 'struct', 'interface') THEN 1 ELSE 0 END AS is_precise,
                   CASE WHEN s.kind IN ('namespace', 'import') THEN 1 ELSE 0 END AS is_non_callable,
                   {canonicalPrimaryRankSql} AS canonical_primary_rank,
                   {generatedSql} AS canonical_generated_rank,
                   {canonicalSemanticScoreSql} AS canonical_semantic_score,
                   {canonicalDeclarationIdentitySql} AS canonical_declaration_identity,
                   COALESCE({GetSymbolColumnSql("start_column")}, 2147483647) AS stable_start_column,
                   {identifierStartColumnSql} AS identifier_start_column
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE {nameCondition}
              AND {supportedLangFilter}";

        if (lang != null)
            matchingSql += " AND f.lang = @lang";
        AppendPathFilters(ref matchingSql, pathPatterns, excludePathPatterns, excludeTests);
        var pathDistinctSql = ReferenceEquals(GetIndexedPathComparer(), StringComparer.Ordinal)
            ? "COUNT(DISTINCT path)"
            : "COUNT(DISTINCT path COLLATE NOCASE)";
        var precisePathDistinctSql = ReferenceEquals(GetIndexedPathComparer(), StringComparer.Ordinal)
            ? "COUNT(DISTINCT CASE WHEN is_precise = 1 THEN path END)"
            : "COUNT(DISTINCT CASE WHEN is_precise = 1 THEN path END COLLATE NOCASE)";
        const string canonicalRepresentativeOrder = "canonical_primary_rank, canonical_generated_rank, canonical_semantic_score DESC, canonical_declaration_identity COLLATE BINARY, path COLLATE BINARY, start_line, stable_start_column, symbol_id";
        const string resultOrder = "match_order, path_bucket, visibility_rank, name, path COLLATE BINARY, line, symbol_id";
        var sql = $@"
            WITH matching_definitions AS (
                {matchingSql}
            ),
            ranked_definitions AS (
                SELECT matching_definitions.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY {canonicalRepresentativeOrder}
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
            ),
            logical_definitions AS (
                SELECT *
                FROM family_metadata_definitions
                WHERE logical_row_number = 1
            ),
            requested_definitions AS (
                SELECT logical_partial_key, 1 AS requested_row
                FROM logical_definitions
                ORDER BY {resultOrder}
                LIMIT @definitionLimit OFFSET @definitionOffset
            ),
            single_precise_definition AS (
                SELECT logical_partial_key, 0 AS requested_row
                FROM logical_definitions
                WHERE is_precise = 1
                ORDER BY {resultOrder}
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
            )
            SELECT logical.path, logical.lang, logical.kind, logical.name, logical.line,
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
            ORDER BY {resultOrder}";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@resolvedName", resolvedName);
        SqliteCommandPolicy.Add(cmd, "@resolvedNameNormalized", normalizedName);
        SqliteCommandPolicy.Add(cmd, "@resolvedNameNormalizedFolded", FoldNameForLanguage(normalizedName, lang));
        SqliteCommandPolicy.Add(cmd, "@resolvedNameLeaf", leafName);
        SqliteCommandPolicy.Add(cmd, "@resolvedNameLeafFolded", FoldNameForLanguage(leafName, lang));
        SqliteCommandPolicy.Add(cmd, "@resolvedNameSegmentCount", segmentCount);
        SqliteCommandPolicy.Add(cmd, "@allowLeafFallback", allowLeafFallback ? 1 : 0);
        AddCSharpExplicitInterfaceIdentityQueryParameter(cmd, "resolvedName", resolvedName);
        if (SqlNameResolver.HasQualifier(resolvedName))
        {
            var container = GetQualifiedQueryContainer(resolvedName);
            SqliteCommandPolicy.Add(cmd, "@resolvedNameContainer", container);
            SqliteCommandPolicy.Add(cmd, "@resolvedNameContainerSuffix", $"%.{EscapeLikeQuery(container)}");
        }
        if (_foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@resolvedNameFolded", resolvedName, lang);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        SqliteCommandPolicy.Add(cmd, "@definitionLimit", Math.Max(1, representativeLimit));
        SqliteCommandPolicy.Add(cmd, "@definitionOffset", Math.Max(0, representativeOffset));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        var results = new List<SymbolResult>();
        SymbolResult? preciseDefinition = null;
        var physicalCount = 0;
        var physicalFileCount = 0;
        var logicalCount = 0;
        var preciseCount = 0;
        var preciseLogicalCount = 0;
        var preciseFileCount = 0;
        var nonCallableCount = 0;
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var definitionSites = reader.GetInt32(18);
            var result = new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = reader.GetString(1),
                Kind = reader.GetString(2),
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = !reader.IsDBNull(5) ? reader.GetInt32(5) : reader.GetInt32(4),
                StartColumn = !reader.IsDBNull(29)
                    ? reader.GetInt32(29)
                    : ResolveSymbolIdentifierStartColumn(
                        !reader.IsDBNull(6) ? reader.GetInt32(6) : null,
                        !reader.IsDBNull(10) ? reader.GetString(10) : null,
                        reader.GetString(3),
                        reader.GetString(2)),
                EndLine = !reader.IsDBNull(7) ? reader.GetInt32(7) : reader.GetInt32(4),
                BodyStartLine = !reader.IsDBNull(8) ? reader.GetInt32(8) : null,
                BodyEndLine = !reader.IsDBNull(9) ? reader.GetInt32(9) : null,
                Signature = !reader.IsDBNull(10) ? reader.GetString(10) : null,
                ContainerKind = !reader.IsDBNull(11) ? reader.GetString(11) : null,
                ContainerName = !reader.IsDBNull(12) ? reader.GetString(12) : null,
                ContainerQualifiedName = !reader.IsDBNull(15) ? reader.GetString(15) : null,
                LogicalPartialKey = !reader.IsDBNull(16) ? reader.GetString(16) : null,
                Visibility = !reader.IsDBNull(13) ? reader.GetString(13) : null,
                ReturnType = !reader.IsDBNull(14) ? reader.GetString(14) : null,
                SymbolId = reader.GetInt64(17),
                DefinitionSites = definitionSites > 1 ? definitionSites : null,
            };
            if (definitionSites > 1)
            {
                result.PartialFamilyId = LogicalPartialSymbolGrouper.BuildPartialFamilyId(result.LogicalPartialKey!);
                result.RepresentativeReason = reader.GetString(26);
                result.FamilyMembers = ReadPartialFamilyMembers(reader.GetString(27), result);
                result.FamilyMembersTruncated = reader.GetInt64(28) != 0;
            }
            physicalCount = reader.GetInt32(20);
            physicalFileCount = reader.GetInt32(21);
            logicalCount = reader.GetInt32(22);
            preciseCount = reader.GetInt32(23);
            preciseFileCount = reader.GetInt32(24);
            nonCallableCount = reader.GetInt32(25);
            preciseLogicalCount = reader.GetInt32(30);
            if (IsPreciseImpactFallbackKind(result.Kind))
                preciseDefinition ??= result;
            if (reader.GetInt32(19) == 1)
                results.Add(result);
        }

        // Identity-scoped C# graph traversal keeps the representative-only
        // definition payload, but retain every selected physical family ID internally so a
        // call resolved to a partial declaration reaches the same graph as its implementation.
        // identity-scoped C# のグラフ探索では definition の出力は代表1件のまま
        // としつつ、partial 宣言側へ解決された call も実装側と同じグラフへ到達できるよう、
        // 選択された family の全 physical ID を内部的に保持する。
        reader.Dispose();
        var physicalSymbolIds = new HashSet<long>();
        var physicalDefinitionPaths = new HashSet<string>(GetIndexedPathComparer());
        var physicalSymbolIdsTruncated = false;
        foreach (var definition in results)
        {
            AddPhysicalDefinition(definition.SymbolId!.Value, definition.Path);
            if (physicalSymbolIdsTruncated || definition.DefinitionSites is not > 1)
                continue;

            if (!definition.FamilyMembersTruncated)
            {
                foreach (var member in definition.FamilyMembers ?? [])
                {
                    if (member.SymbolId is long memberSymbolId)
                        AddPhysicalDefinition(memberSymbolId, member.Path);
                    if (physicalSymbolIdsTruncated)
                        break;
                }
                continue;
            }

            var (familyMembers, familyIdsTruncated) = ResolveImpactPhysicalFamilyMembers(
                definition,
                logicalPartialKeySql,
                lang,
                pathPatterns,
                excludePathPatterns,
                excludeTests);
            foreach (var familyMember in familyMembers)
            {
                AddPhysicalDefinition(familyMember.SymbolId, familyMember.Path);
                if (physicalSymbolIdsTruncated)
                    break;
            }
            physicalSymbolIdsTruncated |= familyIdsTruncated;
        }

        return new ImpactDefinitionResolution(
            results,
            physicalCount,
            physicalFileCount,
            logicalCount,
            preciseCount,
            preciseLogicalCount,
            preciseFileCount,
            nonCallableCount,
            preciseLogicalCount == 1 ? preciseDefinition : null,
            physicalSymbolIds,
            physicalDefinitionPaths,
            physicalSymbolIdsTruncated);

        void AddPhysicalDefinition(long symbolId, string path)
        {
            if (physicalSymbolIds.Contains(symbolId))
                return;
            if (physicalSymbolIds.Count >= Math.Max(1, ImpactPartialFamilyMemberBudget))
            {
                physicalSymbolIdsTruncated = true;
                return;
            }
            physicalSymbolIds.Add(symbolId);
            physicalDefinitionPaths.Add(path);
        }
    }

    private (List<(long SymbolId, string Path)> Members, bool Truncated) ResolveImpactPhysicalFamilyMembers(
        SymbolResult definition,
        string logicalPartialKeySql,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactFamilyLang");
        var familyKindPredicate = definition.Kind is "function" or "test.method"
            ? "s.kind IN ('function', 'test.method')"
            : "s.kind = @familyKind";
        var sql = $@"
            SELECT s.id, f.path
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = @familyLang
              AND {familyKindPredicate}
              AND s.name = @familyName COLLATE BINARY
              AND ({logicalPartialKeySql}) = @logicalPartialKey
              AND {supportedLangFilter}";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " ORDER BY s.id LIMIT @familyMemberLimit";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@familyLang", definition.Lang!);
        if (definition.Kind is not ("function" or "test.method"))
            SqliteCommandPolicy.Add(cmd, "@familyKind", definition.Kind);
        SqliteCommandPolicy.Add(cmd, "@familyName", definition.Name);
        SqliteCommandPolicy.Add(cmd, "@logicalPartialKey", definition.LogicalPartialKey!);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        var familyMemberBudget = Math.Max(1, ImpactPartialFamilyMemberBudget);
        SqliteCommandPolicy.Add(cmd, "@familyMemberLimit", familyMemberBudget + 1);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var members = new List<(long SymbolId, string Path)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (members.Count >= familyMemberBudget)
                return (members, true);
            members.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return (members, false);
    }

    // C# convention: a class `FooAttribute` is used in source as `[Foo]`, so the reference
    // site is stored with `symbol_name = "Foo"`. When a user queries with the class name
    // (`references FooAttribute`, `inspect FooAttribute`, `analyze_symbol("FooAttribute")`),
    // return the suffix-stripped form as an alias so the query still reaches the idiomatic
    // use site. Only applies for C# scope — other languages do not share the convention.
    // C# の規約: クラス `FooAttribute` はソース中で `[Foo]` として使われるため、参照サイトは
    // `symbol_name = "Foo"` で保存される。ユーザーがクラス名で問い合わせたとき
    // (`references FooAttribute` 等) でも慣用的な利用サイトに到達できるよう、
    // suffix を外した別名を返す。C# 以外の言語ではこの規約を持たないので適用しない。
    private static string? ComputeCSharpAttributeSuffixAlias(string? query, string? lang, string? referenceKind)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (lang != null && !lang.Equals("csharp", StringComparison.OrdinalIgnoreCase)) return null;
        // Only metadata lookups should apply the suffix alias: ordinary call-graph
        // queries (`--kind call` / `instantiate` / `subscribe`) must not match `Foo()`
        // call rows when the user typed `FooAttribute`. When `referenceKind` is null,
        // the SQL side additionally constrains the alias clause to attribute rows only.
        // metadata 参照の問い合わせ時だけ alias を適用する: `--kind call` などの call-graph
        // クエリは `FooAttribute` と入力されたときに `Foo()` の call 行に一致してはならない。
        // referenceKind が null のときは SQL 側でも alias 節を attribute 行に限定する。
        if (referenceKind != null && !referenceKind.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            return null;
        const string suffix = "Attribute";
        // Case-insensitive suffix detection so `references myauditattribute` and
        // `inspect MyAuditATTRIBUTE` still produce the `MyAudit` alias, matching the
        // NOCASE / folded contract of the surrounding exact/substring query paths.
        // 大文字小文字を無視して suffix を検出することで、`myauditattribute` や
        // `MyAuditATTRIBUTE` のような形でも alias を生成できる。
        if (!query!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        if (query.Length <= suffix.Length) return null;
        return query.Substring(0, query.Length - suffix.Length);
    }

    // CSS/SCSS convention: Sass variables are stored without the leading `$`, so queries
    // that keep the sigil should still reach the canonical symbol/reference rows.
    // CSS/SCSS の規約: Sass 変数は先頭の `$` を外した形で保存されるため、sigil 付きの
    // クエリでも canonical な symbol/reference 行に到達できるようにする。
    private static string? ComputeCssScssVariableAlias(string? query)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '$')
            return null;
        if (query.Length <= 1)
            return null;
        return query[1..];
    }

    private List<string> ResolveImpactFallbackNames(
        SymbolResult definition,
        IReadOnlySet<string>? physicalDefinitionPaths = null)
    {
        if (string.IsNullOrWhiteSpace(definition.Path) || string.IsNullOrWhiteSpace(definition.Name))
            return new List<string>();

        var definitionPaths = physicalDefinitionPaths is { Count: > 0 }
            ? physicalDefinitionPaths.Order(StringComparer.Ordinal).ToList()
            : [definition.Path];
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactSafeNameLang");
        cmd.CommandText = @"
            SELECT DISTINCT s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path IN (SELECT value FROM json_each(@targetPathsJson))
              AND " + supportedLangFilter + @"
              AND (
                    (s.name = @containerName AND s.kind = @containerKind)
                    OR s.container_name = @containerName
                  )
            ORDER BY s.name";
        SqliteCommandPolicy.Add(cmd, "@targetPathsJson", JsonStringListCodec.Serialize(definitionPaths));
        SqliteCommandPolicy.Add(cmd, "@containerName", definition.Name);
        SqliteCommandPolicy.Add(cmd, "@containerKind", definition.Kind);

        var results = new List<string>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(reader.GetString(0));

        // C# attribute naming convention: a class `FooAttribute` is used as `[Foo]` in source,
        // so reference sites are stored with symbol_name `Foo`. Add the suffix-stripped alias
        // for the resolved definition itself so impact on `FooAttribute` can find metadata-only
        // usage sites. Only the resolved definition's own name gets the alias — applying the
        // strip to every same-file fallback name (e.g. a nested `BarAttribute` inside the file
        // that defines `FooAttribute`) would let `impact FooAttribute` falsely report `[Bar]`
        // usages as part of `FooAttribute`'s blast radius.
        // C# の属性命名規約: クラス `FooAttribute` はソースで `[Foo]` として使われ、参照サイトは
        // symbol_name `Foo` で保存される。`FooAttribute` への impact でも metadata 参照サイトを
        // 見つけられるよう、*解決済み定義自身* にのみサフィックスを外した別名を追加する。
        // same-file fallback 名全体（例: `FooAttribute` と同一ファイルに nested で存在する
        // `BarAttribute`）にまで strip を適用すると、`impact FooAttribute` が `[Bar]` 利用を
        // 誤って `FooAttribute` の影響範囲として報告してしまうため、定義自身だけに限定する。
        if (string.Equals(definition.Lang, "csharp", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(definition.Name) &&
            definition.Name.Length > "Attribute".Length &&
            definition.Name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            var stripped = definition.Name.Substring(0, definition.Name.Length - "Attribute".Length);
            if (stripped.Length > 0 && !results.Contains(stripped))
                results.Add(stripped);
        }

        return results;
    }

    private (List<FileDependencyResult> Results, bool Truncated) GetFileDependencyHintsToResolvedType(
        SymbolResult definition,
        IReadOnlyList<string> fallbackNames,
        int limit,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        int offset = 0,
        IReadOnlySet<string>? physicalDefinitionPaths = null)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(definition.Path) || fallbackNames.Count == 0)
            return (new List<FileDependencyResult>(), false);

        var definitionPaths = physicalDefinitionPaths is { Count: > 0 }
            ? physicalDefinitionPaths.Order(StringComparer.Ordinal).ToList()
            : [definition.Path];
        using var cmd = _conn.CreateCommand();
        var innerSql = @"
                SELECT src.id AS source_file_id, src.path AS source_path, @impactTargetPath AS target_path,
                       r.symbol_name AS symbol_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id
                WHERE src.path NOT IN (SELECT value FROM json_each(@impactTargetPathsJson))";
        // `impact` heuristic file hints intentionally include metadata-only reference
        // kinds (`attribute` / `annotation`). A rename or removal of `User` breaks
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` at compile time just
        // as surely as it breaks `new User()`, so file-level blast-radius analysis
        // must surface those sites as real dependencies. `callers` / `callees` still
        // reject metadata kinds at the CLI / MCP boundary because those commands model
        // the dynamic call graph, not the dependency graph.
        // `impact` の heuristic file hint は metadata-only な参照 (`attribute` /
        // `annotation`) も意図的に含める。`User` を rename / 削除すると
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` も compile-time で
        // 壊れるため、ファイル単位の blast-radius 分析ではそれらも本物の依存として
        // 出す必要がある。`callers` / `callees` は call graph を扱うので、metadata 種別
        // の拒否は引き続き CLI / MCP boundary 側で行う。
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "impactDepsLang")}";
        if (lang != null)
            innerSql += " AND src.lang = @lang";
        innerSql += " AND r.symbol_name IN (SELECT value FROM json_each(@impactFallbackNamesJson))";

        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathFilterPredicate("src", "pathPattern", i, pathPatterns[i]));
            innerSql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                innerSql += $" AND NOT {BuildPathFilterPredicate("src", "excludePathPattern", i, excludePathPatterns[i])}";
        }
        if (excludeTests)
            innerSql += $" AND NOT {TestPathCondition.Replace("f.path", "src.path")}";
        innerSql = "SELECT DISTINCT * FROM (" + innerSql + ")";

        cmd.CommandText = $@"
            SELECT source_file_id, source_path, target_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT symbol_name) AS symbols,
                   MAX(CASE WHEN logical_reference_kind IN ('attribute','annotation') THEN 1 ELSE 0 END) AS has_metadata_ref
            FROM ({innerSql}) edges
            GROUP BY source_file_id, source_path, target_path
            ORDER BY reference_count DESC, source_path, target_path";
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        SqliteCommandPolicy.Add(cmd, "@impactTargetPath", definition.Path);
        SqliteCommandPolicy.Add(cmd, "@impactTargetPathsJson", JsonStringListCodec.Serialize(definitionPaths));
        SqliteCommandPolicy.Add(cmd, "@impactFallbackNamesJson", JsonStringListCodec.Serialize(fallbackNames));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var candidates = new List<(long SourceFileId, bool HasMetadataRef, FileDependencyResult Edge)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            candidates.Add((
                reader.GetInt64(0),
                reader.GetInt32(5) == 1,
                new FileDependencyResult
                {
                    ResultKind = ImpactResultKinds.FileHeuristic,
                    SourcePath = reader.GetString(1),
                    TargetPath = reader.GetString(2),
                    ReferenceCount = reader.GetInt32(3),
                    Symbols = reader.GetString(4),
                }));
        }

        // Metadata references only carry the short use-site name (`Foo` for `[Foo]`,
        // `@Foo`). If multiple class-like definitions share the same unqualified name
        // across namespaces / packages (e.g. `A.MyAuditAttribute` and
        // `B.MyAuditAttribute`), we cannot uniquely attribute a `[MyAudit]` site to
        // either target. Skip the metadata evidence bypass in that ambiguous case so
        // `impact` does not over-report the blast radius of a rename / removal.
        // metadata 参照は use-site 側の短縮名 (`[Foo]` / `@Foo` の `Foo`) しか持た
        // ないため、namespace / package を跨いで同名の class-like 定義が複数存在
        // する場合、`[MyAudit]` 参照をどちらの target にも一意に紐付けられない。
        // そのような曖昧なケースでは metadata の evidence bypass を行わず、
        // `impact` が rename / 削除の影響範囲を過大報告しないようにする。
        var metadataBypassSafe = IsMetadataTargetUnambiguous(definition, lang, pathPatterns, excludePathPatterns, excludeTests);
        var evidenceCache = new Dictionary<long, bool>();
        var filtered = new List<FileDependencyResult>();
        foreach (var candidate in candidates)
        {
            // Evidence anchoring precedes the metadata bypass: an in-file `call` /
            // `instantiate` reference to `definition.Name` (or structured type evidence
            // such as a parameter or return-type token) pins the source/target pair
            // unambiguously, so the looser metadata widening is unnecessary. Falling
            // through to the bypass only when no such anchor exists keeps pure
            // attribute / annotation consumers visible without over-attributing edges
            // that the call graph already proves.
            // evidence anchoring を metadata bypass より先に評価する。`definition.Name`
            // への `call` / `instantiate` 参照、または structured type evidence
            // (引数 / return 型での出現) がファイル内にあれば source→target の関係は
            // 一意に固定されるので、より緩い metadata widening は不要。anchor が無い
            // ときだけ bypass にフォールスルーすることで、純粋な attribute / annotation
            // consumer の表示を維持しつつ、call graph で既に確定しているエッジを
            // 過剰に metadata 経由で広げないようにする。
            if (!evidenceCache.TryGetValue(candidate.SourceFileId, out var hasEvidence))
            {
                hasEvidence = SourceFileHasAnchorReferenceTo(candidate.SourceFileId, definition.Name)
                              || SourceFileHasStructuredTypeEvidence(candidate.SourceFileId, definition.Name);
                evidenceCache[candidate.SourceFileId] = hasEvidence;
            }
            if (hasEvidence)
            {
                filtered.Add(candidate.Edge);
                continue;
            }
            // Pure metadata-only consumers (`[MyAudit]` / `@Inject(User.class)`) legitimately
            // lack any anchor in the source file beyond the attribute / annotation use itself.
            // For those, bypass the evidence guard only when the class-like target is
            // unambiguous so deps/impact can still surface them without over-attributing
            // same-named targets in the ambiguous case.
            // anchor が一つも無い純粋な metadata consumer (`[MyAudit]` / `@Inject(User.class)`)
            // のみ、class-like target が一意な場合に限り evidence guard を skip して
            // 拾い上げる。曖昧なときは引き続き edge を落とし、同名 target への誤帰属を
            // 防ぐ。
            if (candidate.HasMetadataRef && metadataBypassSafe)
            {
                filtered.Add(candidate.Edge);
            }
        }

        offset = Math.Max(0, offset);
        var truncated = filtered.Count > checked(offset + limit);
        filtered = filtered.Skip(offset).Take(limit).ToList();

        return (filtered, truncated);
    }

    // Returns true when the metadata target name resolves to at most one class-like
    // symbol across the graph-supported languages. Ambiguous names (same unqualified
    // name under different namespaces / packages) must not trigger the metadata
    // evidence bypass because attribute / annotation reference rows only keep the
    // short name and cannot disambiguate between them.
    // graph 対応言語の中で class-like シンボルが高々 1 件しか存在しないときに true。
    // namespace / package を跨いで同名の class-like 定義が複数ある曖昧なケースでは
    // attribute / annotation 参照行が短縮名しか持たず区別できないため、metadata の
    // evidence bypass を許可しない。
    private bool IsMetadataTargetUnambiguous(
        SymbolResult definition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            return false;
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "metadataAmbigLang");
        // Count at symbol-identity level (path + line + name) rather than at path
        // level, so two same-named class-like definitions in the same source file
        // (e.g. `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }` both in one .cs file) still register as ambiguous.
        // DISTINCT f.path alone would collapse them to 1 and falsely trigger the
        // metadata bypass.
        // 曖昧性は path 単位ではなく symbol identity 単位 (path + line + name) で数える。
        // 同じ .cs ファイル内に別名前空間で同名の class-like が 2 つあるケース
        // (例: `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }`) でも ambiguity を 2 として検出できる。DISTINCT
        // f.path のままだと 1 に潰れ、metadata bypass が誤って有効化される。
        // For C# specifically, only count class-like definitions that are
        // plausible attribute metadata targets. We don't resolve base types
        // transitively at SQL time, so the best portable approximation is
        // "has an inheritance clause": any class declared with `: ...` is a
        // potential attribute type (direct `: Attribute`, indirect
        // `: BaseAudit` where BaseAudit itself derives from Attribute, or
        // any other `: Base` chain). A plain `class MyAuditAttribute { }`
        // with no `:` clause is not a valid `[MyAudit]` target at compile
        // time, so excluding it prevents the metadata bypass from being
        // falsely suppressed. We deliberately over-accept non-attribute
        // derived classes rather than under-accept indirectly-derived
        // attribute classes, because an invalid `[MyFoo]` against a
        // non-attribute class would fail to compile and therefore not
        // appear as a real reference. Other languages keep the broad
        // class-like candidate set because their metadata-target markers
        // don't match this signature shape.
        // C# は SQL 時点で基底型を遡れないため、「何かを継承している
        // class-like」を attribute 候補の近似として扱う。`: Attribute` の
        // 直接継承も、`: BaseAudit` のような中間基底経由の間接継承も、
        // 何らかの `: Base` があれば候補に含める。継承節の無い plain
        // `class MyAuditAttribute { }` だけを除外することで metadata
        // bypass の誤抑止を防ぐ。非 attribute を過剰に含めるが、無効な
        // `[MyFoo]` はコンパイルできないので実参照にはならず実害が無い。
        // 署名列が無い legacy DB では degrade して class 限定のみ使う。
        var metadataTargetKindExprF = BuildMetadataTargetKindExpr("f");
        var sql = $@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT f.path, s.line, s.name
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.name = @metadataAmbigName COLLATE NOCASE
                  AND {metadataTargetKindExprF}
                  AND {supportedLangFilter}";
        if (lang != null)
        {
            sql += " AND f.lang = @metadataAmbigLangFilter";
            SqliteCommandPolicy.Add(cmd, "@metadataAmbigLangFilter", lang);
        }
        // Path / exclude-path parameters share the same anchored path filter
        // predicate as the rest of the reader: plain values match an exact path
        // or subtree, while `*` / `?` keep glob-style LIKE matching.
        // path / exclude-path は reader 全体で共通の anchored path filter 条件を
        // 使う。ワイルドカードを含まない値は完全一致または配下に一致し、
        // `*` / `?` は glob 風の LIKE matching として扱う。
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathFilterPredicate("f", "metadataAmbigPath", i, pathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
            AddPathFilterParameterSet(cmd, "metadataAmbigPath", pathPatterns);
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND NOT {BuildPathFilterPredicate("f", "metadataAmbigExcludePath", i, excludePathPatterns[i])}";
            AddPathFilterParameterSet(cmd, "metadataAmbigExcludePath", excludePathPatterns);
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition}";
        sql += ")";
        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@metadataAmbigName", definition.Name);
        var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        // Require exactly one authoritative metadata target named `definition.Name`.
        // `count == 0` is also unsafe for the bypass — if no class-like symbol with
        // that name is a valid metadata target, then a `[Foo]` reference cannot
        // resolve to the passed-in definition either. `count <= 1` would let the
        // bypass fire with zero candidates and falsely attribute `[Foo]` sites to a
        // non-attribute definition (e.g. `class FooAttribute : BaseService` post
        // #435 iter 4 scope-aware resolver). Issue #435 codex review iter 4.
        // 1 件厳密一致のみ unambiguous とみなす。count=0 はメタデータターゲットが
        // 一つも無い状態であり、`[Foo]` が passed-in 定義へ解決する根拠も無いため
        // bypass は発動させない。`<= 1` だと #435 iter 4 のスコープ対応で非属性
        // 派生になったクラスに `[Foo]` 参照を誤帰属させる。
        return count == 1;
    }

    private bool SourceFileHasStructuredTypeEvidence(long fileId, string typeName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.name,
                   " + GetSymbolColumnSql("signature") + @" AS signature,
                   " + GetSymbolColumnSql("return_type") + @" AS return_type
            FROM symbols s
            WHERE s.file_id = @fileId";
        SqliteCommandPolicy.Add(cmd, "@fileId", fileId);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var symbolName = reader.GetString(0);
            var signature = !reader.IsDBNull(1) ? reader.GetString(1) : null;
            var returnType = !reader.IsDBNull(2) ? reader.GetString(2) : null;
            if (SymbolProvidesStructuredTypeEvidence(symbolName, signature, returnType, typeName))
                return true;
        }

        return false;
    }

    // A `call`, `instantiate`, `subscribe`, or `unsubscribe` reference to `typeName` inside
    // the source file is a stronger anchor than structured type evidence (signature /
    // return-type tokens). When such a reference exists, the source/target relationship
    // is pinned by the call graph itself, so `GetFileDependencyHintsToResolvedType` does
    // not need to widen via the looser metadata bypass. Symbol-name match is
    // intentionally exact (no suffix-strip alias) because callable references already
    // carry the authoritative name — applying the C# `[Foo]` → `FooAttribute` alias here
    // would let an unrelated `Foo()` method call anchor `impact FooAttribute` and
    // over-report blast radius (issue #1881).
    // `typeName` への `call` / `instantiate` / `subscribe` / `unsubscribe` 参照は signature /
    // return 型のトークンより強い anchor で、call graph 自体が source/target の関係を確定するため metadata bypass を
    // 経由した widening は不要になる。比較は厳密一致のみで行う：callable な参照は
    // 既に authoritative な名前を保持しているため、C# の `[Foo]` → `FooAttribute` のような
    // suffix alias を適用すると、無関係な `Foo()` 呼び出しが `impact FooAttribute` を
    // 不当に anchor してしまい blast radius を過大報告する (issue #1881)。
    private bool SourceFileHasAnchorReferenceTo(long fileId, string typeName)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(typeName))
            return false;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 1
            FROM symbol_references r
            WHERE r.file_id = @fileId
              AND r.symbol_name = @typeName
              AND r.reference_kind IN {ImpactAnchorReferenceKindsSql}
            LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@fileId", fileId);
        SqliteCommandPolicy.Add(cmd, "@typeName", typeName);
        return cmd.ExecuteScalar() != null;
    }

    private static bool SymbolProvidesStructuredTypeEvidence(string symbolName, string? signature, string? returnType, string typeName)
    {
        if (FoldedImpactNameEquals(returnType, typeName))
            return true;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        foreach (Match match in ImpactSignatureIdentifierRegex.Matches(signature))
        {
            var token = match.Value;
            if (FoldedImpactNameEquals(token, symbolName))
                continue;
            if (FoldedImpactNameEquals(token, typeName))
                return true;
        }

        return false;
    }

    private static bool FoldedImpactNameEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftFolded = NameFold.Fold(left) ?? left;
        var rightFolded = NameFold.Fold(right) ?? right;
        return string.Equals(leftFolded, rightFolded, StringComparison.Ordinal);
    }

    private static bool IsPreciseImpactFallbackKind(string? kind)
    {
        return kind is "class" or "struct" or "interface";
    }

    private static string BuildImpactSuggestion(IReadOnlyList<string> definitionPaths, bool hasClassLikeDefinitions, bool hasMultipleDefinitions, bool hasMultipleDefinitionFiles, string? lang)
    {
        var langHint = lang == null
            ? " Use `--lang <lang>` if the same name exists in multiple languages."
            : string.Empty;

        if (hasClassLikeDefinitions)
        {
            if (hasMultipleDefinitionFiles)
                return "Try `cdidx deps --path <definition-path> --reverse` for each definition file or query a member symbol instead." + langHint;
            if (hasMultipleDefinitions)
                return "Try a fully qualified or member symbol query, or inspect the overlapping definitions with `cdidx definition <symbol> --body`." + langHint;
            if (definitionPaths.Count > 0)
                return $"Try `cdidx deps --path {definitionPaths[0]} --reverse` or query a member symbol instead.";
        }

        if (hasMultipleDefinitions)
            return "Try a more specific symbol name or inspect each definition file with `cdidx definition <symbol> --body`." + langHint;

        return "Try `cdidx definition <symbol>` to confirm the indexed symbol and then query a more specific callable member.";
    }

    private static string BuildGraphSupportReason(string? graphLanguage, bool? graphSupported)
    {
        return ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported)
            ?? "Call-graph support could not be determined because no language filter or matching definition was available.";
    }
}
