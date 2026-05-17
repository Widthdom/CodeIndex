using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Find callers for a referenced symbol.
    /// 指定シンボルを呼び出している呼び出し元を探す。
    /// </summary>
    public List<CallerResult> GetCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CallerResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var supportedLangPredicate = BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang");

        var groupedReferenceKindSql = rawKinds
            ? GetGroupedCallerReferenceKindSql("r.reference_kind")
            : GetGroupedCallerLogicalReferenceKindSql("r.reference_kind");
        var groupedReferenceKindGroupSql = rawKinds
            ? GetRawReferenceKindSql("r.reference_kind")
            : GetLogicalReferenceKindSql("r.reference_kind");
        var sql = referenceKind == null
            ? @"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       " + groupedReferenceKindSql + @" AS reference_kind,
                       " + ReferenceKindCountSql("r.reference_kind", "call") + @" AS call_count,
                       " + ReferenceKindCountSql("r.reference_kind", "instantiate") + @" AS instantiate_count,
                       " + ReferenceKindCountSql("r.reference_kind", "subscribe") + @" AS subscribe_count,
                       " + ReferenceWeightedScoreSql("r.reference_kind") + @" AS weighted_score,
                       r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
                WHERE " + callerContainerPredicate + @"
                  AND r.reference_kind IN " + CallGraphReferenceKindsSql + @"
                  AND " + supportedLangPredicate
            : @"
            SELECT f.path, f.lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, r.symbol_name,
                   r.reference_kind, MIN(r.line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds,
                   " + ReferenceKindCountSql("r.reference_kind", "call") + @" AS call_count,
                   " + ReferenceKindCountSql("r.reference_kind", "instantiate") + @" AS instantiate_count,
                   " + ReferenceKindCountSql("r.reference_kind", "subscribe") + @" AS subscribe_count,
                   " + ReferenceWeightedScoreSql("r.reference_kind") + @" AS weighted_score
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
            WHERE " + BuildCallerContainerPredicate("f", "r");
        if (referenceKind != null)
            sql += " AND " + supportedLangPredicate;

        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        else
            sql += NonInvocationReferenceKindsExclusion;
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            sql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            sql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            sql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            sql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
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
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
            GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, " + groupedReferenceKindGroupSql + @"
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   " + (rawKinds ? GetGroupedCallerReferenceKindSql("r.reference_kind") : "MIN(r.reference_kind)") + @" AS reference_kind,
                   MIN(line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds,
                   SUM(r.call_count) AS call_count,
                   SUM(r.instantiate_count) AS instantiate_count,
                   SUM(r.subscribe_count) AS subscribe_count,
                   SUM(r.weighted_score) AS weighted_score
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name";
        }
        else
        {
            sql += " GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name";
        }
        sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.symbol_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN lower(r.symbol_name) = lower(@rankingQuery) THEN 0 ELSE 1 END, {BuildReferenceRankOrderSql(rankMode)}, {(referenceKind == null ? "r.path" : "f.path")}, first_line LIMIT @limit";

        cmd.CommandText = sql;
        string callersQueryParam;
        if (!exact)
            callersQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            callersQueryParam = NameFold.Fold(query) ?? query;
        else
            callersQueryParam = query;
        cmd.Parameters.AddWithValue("@query", callersQueryParam);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact ? query : string.Empty);
        cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kinds = ParseDistinctReferenceKinds(GetNullableString(reader, 8), primaryKind);
            var counts = BuildReferenceKindCounts(reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11));
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
                ReferenceWeightScore = reader.GetDouble(12),
                FirstLine = reader.GetInt32(6),
                ReferenceCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    public int CountCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false)
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

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
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
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCallersTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false)
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

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
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
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Find callees used by a caller/container symbol.
    /// 呼び出し元シンボルが使っている呼び出し先を探す。
    /// </summary>
    public List<CalleeResult> GetCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted)
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
        var sql = referenceKind == null
            ? $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       {preferredCalleeKindSql} AS reference_kind,
                       {ReferenceKindCountSql("r.reference_kind", "call")} AS call_count,
                       {ReferenceKindCountSql("r.reference_kind", "instantiate")} AS instantiate_count,
                       {ReferenceKindCountSql("r.reference_kind", "subscribe")} AS subscribe_count,
                       {ReferenceWeightedScoreSql("r.reference_kind")} AS weighted_score,
                       r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL
                  AND r.reference_kind IN {CallGraphReferenceKindsSql}
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}"
            : @"
            SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                   r.reference_kind, MIN(r.line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds,
                   " + ReferenceKindCountSql("r.reference_kind", "call") + @" AS call_count,
                   " + ReferenceKindCountSql("r.reference_kind", "instantiate") + @" AS instantiate_count,
                   " + ReferenceKindCountSql("r.reference_kind", "subscribe") + @" AS subscribe_count,
                   " + ReferenceWeightedScoreSql("r.reference_kind") + @" AS weighted_score
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            WHERE r.container_name IS NOT NULL";
        if (referenceKind != null)
            sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        else
            sql += NonInvocationReferenceKindsExclusion;
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            sql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            sql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
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
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number
            )
            SELECT path, lang, container_kind, container_name, symbol_name,
                   reference_kind, MIN(line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT reference_kind) AS reference_kinds,
                   SUM(r.call_count) AS call_count,
                   SUM(r.instantiate_count) AS instantiate_count,
                   SUM(r.subscribe_count) AS subscribe_count,
                   SUM(r.weighted_score) AS weighted_score
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";
        }
        else
        {
            sql += " GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind";
        }
        sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.container_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN lower(r.container_name) = lower(@rankingQuery) THEN 0 ELSE 1 END, {BuildReferenceRankOrderSql(rankMode)}, {(referenceKind == null ? "r.path" : "f.path")}, first_line LIMIT @limit";

        cmd.CommandText = sql;
        string calleesQueryParam;
        if (!exact)
            calleesQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            calleesQueryParam = NameFold.Fold(query) ?? query;
        else
            calleesQueryParam = query;
        cmd.Parameters.AddWithValue("@query", calleesQueryParam);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact ? query : string.Empty);
        cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CalleeResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kinds = ParseDistinctReferenceKinds(GetNullableString(reader, 8), primaryKind);
            var counts = BuildReferenceKindCounts(reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11));
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
                ReferenceWeightScore = reader.GetDouble(12),
                FirstLine = reader.GetInt32(6),
                ReferenceCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    public int CountCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false)
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
                       " + (referenceKind == null
                           ? (rawKinds ? GetPreferredReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind"))
                           : "r.reference_kind") + @" AS reference_kind
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
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
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCalleesTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false)
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
                       " + (referenceKind == null
                           ? (rawKinds ? GetPreferredReferenceKindSql("r.reference_kind") : GetPreferredLogicalReferenceKindSql("r.reference_kind"))
                           : "r.reference_kind") + @" AS reference_kind
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
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
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {(rawKinds ? GetRawReferenceKindSql("r.reference_kind") : GetLogicalReferenceKindSql("r.reference_kind"))}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    private static string ReferenceKindCountSql(string columnSql, string kind) =>
        $"SUM(CASE WHEN {columnSql} = '{kind}' THEN 1 ELSE 0 END)";

    private static string ReferenceWeightedScoreSql(string columnSql) => $@"
        SUM(CASE {columnSql}
            WHEN 'instantiate' THEN 3.0
            WHEN 'call' THEN 1.0
            WHEN 'subscribe' THEN 0.1
            ELSE 0.0
        END)";

    private static string BuildReferenceRankOrderSql(ReferenceRankMode rankMode) => rankMode switch
    {
        ReferenceRankMode.Count => "reference_count DESC",
        ReferenceRankMode.Kind => "CASE reference_kind WHEN 'instantiate' THEN 0 WHEN 'invoke' THEN 0 WHEN 'call' THEN 1 WHEN 'subscribe' THEN 2 WHEN 'event' THEN 2 ELSE 3 END, reference_count DESC",
        _ => "weighted_score DESC, reference_count DESC",
    };

    private static IReadOnlyDictionary<string, int> BuildReferenceKindCounts(int callCount, int instantiateCount, int subscribeCount)
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["call"] = callCount,
            ["instantiate"] = instantiateCount,
            ["subscribe"] = subscribeCount,
        };
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
        cmd.Parameters.AddWithValue("@name", normalizedSymbolName);
        cmd.Parameters.AddWithValue("@normalizedName", normalizedName);
        cmd.Parameters.AddWithValue("@normalizedNameFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        cmd.Parameters.AddWithValue("@leafName", leafName);
        cmd.Parameters.AddWithValue("@leafNameFolded", NameFold.Fold(leafName) ?? leafName);
        cmd.Parameters.AddWithValue("@segmentCount", segmentCount);
        cmd.Parameters.AddWithValue("@allowLeafFallback", allowLeafFallback ? 1 : 0);
        if (_foldReady)
            cmd.Parameters.AddWithValue("@nameFolded", NameFold.Fold(normalizedSymbolName) ?? normalizedSymbolName);
        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead() ? reader.GetString(0) : symbolName;
    }

    /// <summary>
    /// Find exact-match callers for BFS traversal. Uses per-row case sensitivity
    /// and filters to graph-supported languages only (preventing stale edges from
    /// unsupported languages leaking into results on pre-upgrade databases).
    /// BFS 走査用の完全一致 caller 検索。行ごとの case sensitivity 判定、
    /// かつ graph 対応言語のみにフィルタ（アップグレード前 DB の古いエッジ漏れを防止）。
    /// </summary>
    private List<CallerResult> GetCallersExact(string symbolName, int limit, int offset = 0, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "callerLang");

        // Exact caller matching mirrors the leaf `--exact` readers: folded equality when
        // FoldReady, ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // ResolveSymbolName() already normalizes the root symbol first, so this catches
        // caller rows whose stored callee casing differs from the resolved definition.
        // caller 側も leaf `--exact` と同じく FoldReady なら folded equality、legacy DB では
        // `COLLATE NOCASE` fallback。definition と caller 行の casing 差もここで吸収する。
        var allowSqlLeafFallback = !SqlNameResolver.HasQualifier(symbolName);
        var nameCondition = _foldReady
            ? allowSqlLeafFallback
                ? @"
              AND (r.symbol_name_folded = @symbolNameFolded OR (f.lang = 'sql' AND r.symbol_name_folded = @symbolNameLeafFolded))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_folded_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @symbolNameFolded))"
            : allowSqlLeafFallback
                ? @"
              AND (r.symbol_name = @symbolName COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@symbolName) COLLATE NOCASE))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @symbolName COLLATE NOCASE))";

        // impact BFS must share the call-graph contract with `callers`/`callees`/`hotspots`,
        // so event subscriptions (`Click += OnClick`) also participate in the transitive
        // caller chain. Metadata edges (`attribute`, `annotation`) stay excluded.
        // impact の BFS は `callers`/`callees`/`hotspots` と同じ call-graph 契約を共有し、
        // `subscribe` エッジ（`Click += OnClick` 等）も推移 caller に含める。`attribute` /
        // `annotation` のような metadata エッジは引き続き除外する。
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var sql = $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id{referenceLineJoin}
                WHERE {callerContainerPredicate}
                  AND r.reference_kind IN {CallGraphReferenceKindsSql}
                  AND {supportedLangFilter}
                  {nameCondition}";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   MIN(line) AS first_line, COUNT(*) AS reference_count
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name";
        sql += $" ORDER BY {GetPathBucketOrderSql("r.path")}, reference_count DESC, r.path, COALESCE(r.container_name, ''), COALESCE(r.container_kind, ''), r.symbol_name, first_line LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@symbolName", symbolName);
        cmd.Parameters.AddWithValue("@symbolNameLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        if (_foldReady)
            cmd.Parameters.AddWithValue("@symbolNameFolded", NameFold.Fold(symbolName) ?? symbolName);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

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
                FirstLine = reader.GetInt32(5),
                ReferenceCount = reader.GetInt32(6),
            });
        }
        return results;
    }

    // Per-result cap on the number of distinct shortest paths surfaced by impact --with-paths.
    // Each call chain row may carry multiple converging paths from the resolved root through
    // distinct intermediates; the cap keeps JSON output bounded for diamond-heavy graphs and
    // is signaled by ImpactResult.PathsTruncated when exceeded.
    // impact --with-paths が 1 caller につき保持する経路数の上限。ダイヤモンド型で多経路が
    // 収束する場合に JSON 膨張を抑える役割があり、超過時は PathsTruncated で通知する。
    private const int DefaultImpactPathsPerResult = 10;

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
    public (List<ImpactResult> Results, bool Truncated, string? TruncatedReason, string TerminationReason, List<ImpactCycleResult> Cycles) GetTransitiveCallers(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool withPaths = false, int maxPathsPerResult = DefaultImpactPathsPerResult)
    {
        // Resolve the symbol name through definitions first so case-mismatched queries
        // like "run" find the actual "Run" symbol. Falls back to user input if not found.
        // 定義を通じてシンボル名を解決し、"run" → "Run" のようなケース違いを補正する。
        // 見つからなければユーザ入力をフォールバック使用。
        var resolvedName = ResolveSymbolName(symbolName, lang);
        var rootDefinitionPaths = ResolveImpactDefinitions(resolvedName, lang, pathPatterns, excludePathPatterns, excludeTests)
            .Select(definition => definition.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<ImpactResult>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Symbol, int Depth)>();
        queue.Enqueue((resolvedName, 0));
        visited.Add(resolvedName);
        var truncated = false;
        var maxDepthReached = false;
        var cycles = new List<ImpactCycleResult>();
        var cycleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // truncatedReason tracks the *strongest* signal observed: safety_cap wins over
        // user_limit because it tells callers that raising --limit alone will not help
        // (the input graph is likely pathological). See Issue #1533.
        // truncatedReason は強い方の信号を保持する: safety_cap は --limit を緩和しても解消しない
        // ことを示すため、user_limit より優先する (#1533)。
        string? truncatedReason = null;
        // Safety cap to prevent infinite loops on pathological graphs / 病的グラフでの無限ループ防止
        const int maxFetchIterations = 1000;

        // Path tracking state is allocated only when callers opt in. We collapse parent edges by
        // caller name (rather than path:name) so that diamond chains converging on the same name
        // across files share the same node when emitting paths — the issue example asks for
        // ["foo", "B", "A"], not file-qualified entries.
        // path 追跡は opt-in 時のみ確保する。同名 caller が複数ファイルにあっても経路上は同名
        // ノードとして畳む（issue #1536 の例 ["foo", "B", "A"] と整合）。
        Dictionary<string, HashSet<string>> parentsByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> cycleParentsByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> depthByName = new(StringComparer.OrdinalIgnoreCase)
        {
            [resolvedName] = 0,
        };
        var resultIndicesByName = withPaths
            ? new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
            : null;

        while (queue.Count > 0 && results.Count < limit)
        {
            var (currentSymbol, depth) = queue.Dequeue();
            if (depth > maxDepth)
                break;

            // Fetch callers in pages, filtering out already-visited before counting toward limit.
            // This prevents diamond graphs from hiding reachable callers behind visited duplicates.
            // ページングで caller を取得し、visited フィルタ後にカウント。
            // ダイヤモンド型グラフで到達可能な caller が visited 重複に隠れるのを防止。
            var needed = limit - results.Count;
            var offset = 0;
            const int pageSize = 200;
            var fetchIterations = 0;

            while (results.Count < limit && fetchIterations < maxFetchIterations)
            {
                fetchIterations++;
                var page = GetCallersExact(currentSymbol, pageSize, offset, lang, pathPatterns, excludePathPatterns, excludeTests);

                if (page.Count == 0)
                    break; // No more callers for this symbol / このシンボルの caller は尽きた

                foreach (var caller in page)
                {
                    if (results.Count >= limit)
                    {
                        truncated = true;
                        truncatedReason ??= ImpactTruncatedReasons.UserLimit;
                        break;
                    }

                    var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                    if (IsCycleEdge(callerName, currentSymbol, cycleParentsByName))
                        AddImpactCycle(cycles, cycleKeys, BuildCycleMembers(callerName, currentSymbol, cycleParentsByName));
                    if (string.Equals(callerName, resolvedName, StringComparison.OrdinalIgnoreCase)
                        && (rootDefinitionPaths.Count == 0 || rootDefinitionPaths.Contains(caller.Path)))
                        continue;
                    var key = $"{caller.Path}:{callerName}";
                    if (!cycleParentsByName.TryGetValue(callerName, out var cycleParentSet))
                    {
                        cycleParentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        cycleParentsByName[callerName] = cycleParentSet;
                    }
                    cycleParentSet.Add(currentSymbol);

                    if (!visited.Add(key))
                    {
                        // Same-depth convergence: record the additional parent so path
                        // enumeration can discover this alternate route. Other-depth re-arrivals
                        // are intentionally dropped — BFS already keeps the shortest route.
                        // 同 depth で再到達した場合のみ親辺を追加し、別 depth の到達は破棄。
                        // BFS により最短経路だけが残る。
                        if (withPaths
                            && depthByName.TryGetValue(callerName, out var existingDepth)
                            && existingDepth == depth + 1)
                        {
                            parentsByName[callerName].Add(currentSymbol);
                        }
                        continue;
                    }

                    results.Add(new ImpactResult
                    {
                        Path = caller.Path,
                        Lang = caller.Lang,
                        CallerKind = caller.CallerKind,
                        CallerName = caller.CallerName,
                        CalleeName = caller.CalleeName,
                        Depth = depth + 1,
                        FirstLine = caller.FirstLine,
                        ReferenceCount = caller.ReferenceCount,
                    });

                    if (withPaths)
                    {
                        if (!depthByName.ContainsKey(callerName))
                            depthByName[callerName] = depth + 1;
                        if (!resultIndicesByName!.TryGetValue(callerName, out var idxList))
                        {
                            idxList = new List<int>();
                            resultIndicesByName[callerName] = idxList;
                        }
                        idxList.Add(results.Count - 1);
                    }
                    else if (!depthByName.ContainsKey(callerName))
                    {
                        depthByName[callerName] = depth + 1;
                    }
                    if (!parentsByName.TryGetValue(callerName, out var parentSet))
                    {
                        parentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        parentsByName[callerName] = parentSet;
                    }
                    parentSet.Add(currentSymbol);

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
                        queue.Enqueue((caller.CallerName, depth + 1));
                    }
                    else if (caller.CallerName != null
                             && caller.CallerName != SyntheticTopLevelCallerName
                             && depth + 1 == maxDepth)
                    {
                        maxDepthReached |= InspectBoundaryCallers(
                            caller.CallerName,
                            resolvedName,
                            rootDefinitionPaths,
                            visited,
                            cycleParentsByName,
                            cycles,
                            cycleKeys,
                            lang,
                            pathPatterns,
                            excludePathPatterns,
                            excludeTests);
                    }
                }

                offset += page.Count;

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

        if (queue.Count > 0 && results.Count >= limit)
        {
            truncated = true;
            truncatedReason ??= ImpactTruncatedReasons.UserLimit;
        }

        if (withPaths)
        {
            var effectiveCap = maxPathsPerResult > 0 ? maxPathsPerResult : DefaultImpactPathsPerResult;
            foreach (var (callerName, indices) in resultIndicesByName!)
            {
                var (paths, more) = EnumerateImpactPaths(callerName, parentsByName, resolvedName, effectiveCap);
                foreach (var idx in indices)
                {
                    results[idx].Paths = paths;
                    results[idx].PathsTruncated = more;
                }
            }
        }

        var terminationReason = truncatedReason switch
        {
            ImpactTruncatedReasons.SafetyCap => ImpactTerminationReasons.SafetyCap,
            ImpactTruncatedReasons.UserLimit => ImpactTerminationReasons.RowLimitTruncated,
            _ when cycles.Count > 0 => ImpactTerminationReasons.CycleDetected,
            _ when maxDepthReached => ImpactTerminationReasons.MaxDepthReached,
            _ => ImpactTerminationReasons.Completed,
        };

        return (results, truncated, truncatedReason, terminationReason, cycles);
    }

    private bool InspectBoundaryCallers(
        string symbolName,
        string resolvedName,
        HashSet<string> rootDefinitionPaths,
        HashSet<string> visited,
        Dictionary<string, HashSet<string>> cycleParentsByName,
        List<ImpactCycleResult> cycles,
        HashSet<string> cycleKeys,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        const int pageSize = 200;
        var offset = 0;
        while (true)
        {
            var page = GetCallersExact(symbolName, pageSize, offset, lang, pathPatterns, excludePathPatterns, excludeTests);
            if (page.Count == 0)
                return false;

            foreach (var caller in page)
            {
                var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                if (IsCycleEdge(callerName, symbolName, cycleParentsByName))
                    AddImpactCycle(cycles, cycleKeys, BuildCycleMembers(callerName, symbolName, cycleParentsByName));
                var isRoot = string.Equals(callerName, resolvedName, StringComparison.OrdinalIgnoreCase)
                    && (rootDefinitionPaths.Count == 0 || rootDefinitionPaths.Contains(caller.Path));
                if (isRoot)
                    continue;

                if (!cycleParentsByName.TryGetValue(callerName, out var cycleParentSet))
                {
                    cycleParentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    cycleParentsByName[callerName] = cycleParentSet;
                }
                cycleParentSet.Add(symbolName);

                var key = $"{caller.Path}:{callerName}";
                if (!visited.Contains(key))
                    return true;
            }

            if (page.Count < pageSize)
                return false;
            offset += page.Count;
        }
    }

    private static bool IsCycleEdge(
        string callerName,
        string currentSymbol,
        Dictionary<string, HashSet<string>> parentsByName)
    {
        if (string.Equals(callerName, currentSymbol, StringComparison.OrdinalIgnoreCase))
            return true;
        return HasAncestor(currentSymbol, callerName, parentsByName);
    }

    private static bool HasAncestor(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByName)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
                continue;
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!parentsByName.TryGetValue(current, out var parents))
                continue;
            foreach (var parent in parents)
                stack.Push(parent);
        }
        return false;
    }

    private static List<string> BuildCycleMembers(
        string callerName,
        string currentSymbol,
        Dictionary<string, HashSet<string>> parentsByName)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryBuildAncestorPath(currentSymbol, callerName, parentsByName, members))
        {
            members.Add(callerName);
            members.Add(currentSymbol);
        }
        var result = members.ToList();
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool TryBuildAncestorPath(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByName,
        HashSet<string> members)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return TryBuildAncestorPathCore(node, target, parentsByName, members, seen);
    }

    private static bool TryBuildAncestorPathCore(
        string node,
        string target,
        Dictionary<string, HashSet<string>> parentsByName,
        HashSet<string> members,
        HashSet<string> seen)
    {
        if (!seen.Add(node))
            return false;
        members.Add(node);
        if (string.Equals(node, target, StringComparison.OrdinalIgnoreCase))
            return true;
        if (parentsByName.TryGetValue(node, out var parents))
        {
            foreach (var parent in parents)
            {
                if (TryBuildAncestorPathCore(parent, target, parentsByName, members, seen))
                    return true;
            }
        }

        members.Remove(node);
        return false;
    }

    private static void AddImpactCycle(
        List<ImpactCycleResult> cycles,
        HashSet<string> cycleKeys,
        List<string> members)
    {
        if (members.Count == 0)
            return;
        var key = string.Join("\u001F", members);
        if (!cycleKeys.Add(key))
            return;
        cycles.Add(new ImpactCycleResult { Members = members });
    }

    private static (List<List<string>> Paths, bool Truncated) EnumerateImpactPaths(
        string callerName,
        Dictionary<string, HashSet<string>> parentsByName,
        string resolvedRoot,
        int maxPathsPerResult)
    {
        // DFS upward from `callerName` to `resolvedRoot` through parent edges. The Stack<T>
        // enumerator yields top-first, so a stack of [callerName, ..., resolvedRoot] (pushed
        // bottom→top) materializes directly to [resolvedRoot, ..., callerName] without an
        // explicit reverse — matching the order in the issue example ["foo", "B", "A"].
        // 親辺を辿って callerName → resolvedRoot を DFS で列挙する。Stack<T> の列挙は top→bottom
        // なので、push 順 [callerName, ..., resolvedRoot] がそのまま [resolvedRoot, ..., callerName]
        // で取り出せる（issue #1536 の例 ["foo", "B", "A"] に一致）。
        var paths = new List<List<string>>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncatedRef = new bool[1];

        stack.Push(callerName);
        onStack.Add(callerName);
        Dfs(callerName);
        stack.Pop();
        onStack.Remove(callerName);
        return (paths, truncatedRef[0]);

        void Dfs(string node)
        {
            if (string.Equals(node, resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(stack.ToList());
                return;
            }
            if (!parentsByName.TryGetValue(node, out var parents))
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

    /// <summary>
    /// Analyze impact for a query by combining transitive callers with symbol-resolution
    /// metadata and a class-like file-dependency fallback when symbol-level callers are absent.
    /// The <paramref name="maxDepth"/> bound is inclusive (callers at depth 1..N are returned);
    /// <c>maxDepth: 0</c> short-circuits to symbol resolution only.
    /// impact 用に caller BFS と解決メタデータを束ね、class 系で caller 不在なら
    /// file dependency をフォールバックとして返す。<paramref name="maxDepth"/> は inclusive で
    /// N 指定時は depth 1〜N の caller を返し、<c>maxDepth: 0</c> は symbol 解決のみで終了する。
    /// </summary>
    public ImpactAnalysisResult AnalyzeImpact(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool withPaths = false)
    {
        lang = NormalizeQueryLanguage(lang);
        var resolvedName = ResolveSymbolName(symbolName, lang);
        var definitions = ResolveImpactDefinitions(resolvedName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var definitionPaths = definitions
            .Select(d => d.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasMultipleDefinitions = definitions.Count > 1;
        var fallbackDefinitions = definitions
            .Where(d => IsPreciseImpactFallbackKind(d.Kind))
            .ToList();
        var fallbackDefinitionPaths = fallbackDefinitions
            .Select(d => d.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasMultipleFallbackDefinitions = fallbackDefinitions.Count > 1;
        var hasMultipleFallbackDefinitionFiles = fallbackDefinitionPaths.Count > 1;
        var hasClassLikeDefinitions = fallbackDefinitions.Count > 0;

        if (maxDepth <= 0)
        {
            return new ImpactAnalysisResult
            {
                Query = symbolName,
                ResolvedName = resolvedName,
                ImpactMode = "none",
                Heuristic = false,
                MaxDepth = maxDepth,
                DefinitionCount = definitions.Count,
                DefinitionFileCount = definitionPaths.Count,
                HintCount = 0,
                HasClassLikeDefinitions = hasClassLikeDefinitions,
                HasMultipleDefinitions = hasMultipleDefinitions,
                HasMultipleDefinitionFiles = definitionPaths.Count > 1,
                Definitions = definitions,
                Callers = [],
                FileImpacts = [],
                Truncated = false,
                TruncatedReason = null,
                TerminationReason = ImpactTerminationReasons.Completed,
                CycleDetected = false,
                Cycles = null,
                GraphTableAvailable = _hasReferencesTable,
                ZeroResultReason = definitions.Count == 0 ? "no_matching_definition" : "depth_zero",
                Suggestion = definitions.Count == 0
                    ? "Try `cdidx definition <symbol>` to confirm the indexed name."
                    : "Use `cdidx impact <symbol> --depth 1` or higher to traverse callers.",
            };
        }

        var (callers, truncated, truncatedReason, terminationReason, cycles) = GetTransitiveCallers(symbolName, maxDepth, limit, lang, pathPatterns, excludePathPatterns, excludeTests, withPaths);

        var impactMode = "callers";
        var fileImpacts = new List<FileDependencyResult>();
        string? zeroResultReason = null;
        string? suggestion = null;
        var heuristic = false;

        if (callers.Count == 0)
        {
            impactMode = "none";

            if (_hasReferencesTable)
            {
                if (definitions.Count > 0 && definitions.All(d => IsNonCallableImpactKind(d.Kind)))
                {
                    zeroResultReason = "non_callable_symbol_kind";
                    suggestion = "Try `cdidx definition <symbol>` and then run `impact` on a specific callable member instead.";
                }
                else if (hasMultipleFallbackDefinitions)
                {
                    zeroResultReason = hasMultipleFallbackDefinitionFiles ? "multiple_definition_files" : "multiple_definitions";
                    suggestion = BuildImpactSuggestion(fallbackDefinitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: hasMultipleFallbackDefinitionFiles);
                }
                else if (fallbackDefinitions.Count == 1)
                {
                    var fallbackNames = ResolveImpactFallbackNames(fallbackDefinitions[0]);
                    var (hintResults, hintTruncated) = GetFileDependencyHintsToResolvedType(fallbackDefinitions[0], fallbackNames, limit, lang, pathPatterns, excludePathPatterns, excludeTests);
                    fileImpacts = hintResults;
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
                    if (fileImpacts.Count > 0)
                    {
                        impactMode = "file_dependency_hints";
                        heuristic = true;
                        suggestion = "These file-level dependents are heuristic only; confirm with `cdidx deps --path <definition-path> --reverse` and a member-level `impact` query.";
                    }
                    else
                    {
                        zeroResultReason = "class_symbol_no_symbol_callers";
                        suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: false, hasMultipleDefinitionFiles: false);
                    }
                }
                else if (hasMultipleDefinitions)
                {
                    zeroResultReason = definitionPaths.Count > 1 ? "multiple_definition_files" : "multiple_definitions";
                    suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: definitionPaths.Count > 1);
                }
                else if (definitions.Count == 0)
                {
                    zeroResultReason = "no_matching_definition";
                    suggestion = "Try `cdidx definition <symbol>` to confirm the indexed name.";
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
            DefinitionCount = definitions.Count,
            DefinitionFileCount = definitionPaths.Count,
            HintCount = fileImpacts.Count,
            HasClassLikeDefinitions = hasClassLikeDefinitions,
            HasMultipleDefinitions = hasMultipleDefinitions,
            HasMultipleDefinitionFiles = definitionPaths.Count > 1,
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
            Suggestion = suggestion,
        };
    }

    private List<SymbolResult> ResolveImpactDefinitions(string resolvedName, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(resolvedName);
        var leafName = SqlNameResolver.GetLeafName(resolvedName);
        var segmentCount = SqlNameResolver.GetSegmentCount(resolvedName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(resolvedName);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactDefLang");
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @resolvedNameFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded) OR sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded)))"
                : "(s.name_folded = @resolvedNameFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @resolvedNameLeaf COLLATE NOCASE)))"
                : "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE))";
        var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE {nameCondition}
              AND {supportedLangFilter}";

        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @" ORDER BY CASE
                     WHEN s.name = @resolvedName THEN 0
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized THEN 1
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded THEN 2
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @resolvedNameLeaf THEN 3
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded THEN 4
                     ELSE 5
                   END, " + $"{PathBucketOrder}, {VisibilityOrder}, s.name, f.path, s.line LIMIT @limit";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@resolvedName", resolvedName);
        cmd.Parameters.AddWithValue("@resolvedNameNormalized", normalizedName);
        cmd.Parameters.AddWithValue("@resolvedNameNormalizedFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        cmd.Parameters.AddWithValue("@resolvedNameLeaf", leafName);
        cmd.Parameters.AddWithValue("@resolvedNameLeafFolded", NameFold.Fold(leafName) ?? leafName);
        cmd.Parameters.AddWithValue("@resolvedNameSegmentCount", segmentCount);
        cmd.Parameters.AddWithValue("@allowLeafFallback", allowLeafFallback ? 1 : 0);
        if (_foldReady)
            cmd.Parameters.AddWithValue("@resolvedNameFolded", NameFold.Fold(resolvedName) ?? resolvedName);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", 50);

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = reader.GetString(1),
                Kind = reader.GetString(2),
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = !reader.IsDBNull(5) ? reader.GetInt32(5) : reader.GetInt32(4),
                EndLine = !reader.IsDBNull(6) ? reader.GetInt32(6) : reader.GetInt32(4),
                BodyStartLine = !reader.IsDBNull(7) ? reader.GetInt32(7) : null,
                BodyEndLine = !reader.IsDBNull(8) ? reader.GetInt32(8) : null,
                Signature = !reader.IsDBNull(9) ? reader.GetString(9) : null,
                ContainerKind = !reader.IsDBNull(10) ? reader.GetString(10) : null,
                ContainerName = !reader.IsDBNull(11) ? reader.GetString(11) : null,
                Visibility = !reader.IsDBNull(12) ? reader.GetString(12) : null,
                ReturnType = !reader.IsDBNull(13) ? reader.GetString(13) : null,
            });
        }

        return results;
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

    private List<string> ResolveImpactFallbackNames(SymbolResult definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Path) || string.IsNullOrWhiteSpace(definition.Name))
            return new List<string>();

        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactSafeNameLang");
        cmd.CommandText = @"
            SELECT DISTINCT s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @targetPath
              AND " + supportedLangFilter + @"
              AND (
                    (s.name = @containerName AND s.kind = @containerKind)
                    OR s.container_name = @containerName
                  )
            ORDER BY s.name";
        cmd.Parameters.AddWithValue("@targetPath", definition.Path);
        cmd.Parameters.AddWithValue("@containerName", definition.Name);
        cmd.Parameters.AddWithValue("@containerKind", definition.Kind);

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

    private (List<FileDependencyResult> Results, bool Truncated) GetFileDependencyHintsToResolvedType(SymbolResult definition, IReadOnlyList<string> fallbackNames, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(definition.Path) || fallbackNames.Count == 0)
            return (new List<FileDependencyResult>(), false);

        using var cmd = _conn.CreateCommand();
        var innerSql = @"
                SELECT src.id AS source_file_id, src.path AS source_path, @impactTargetPath AS target_path,
                       r.symbol_name AS symbol_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id
                WHERE src.path != @impactTargetPath";
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
        var nameClauses = new List<string>(fallbackNames.Count);
        for (int i = 0; i < fallbackNames.Count; i++)
            nameClauses.Add($"r.symbol_name = @impactFallbackName{i}");
        innerSql += " AND (" + string.Join(" OR ", nameClauses) + ")";

        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"src.path LIKE @pathPattern{i} ESCAPE '\\'");
            innerSql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                innerSql += $" AND src.path NOT LIKE @excludePath{i} ESCAPE '\\'";
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
            cmd.Parameters.AddWithValue("@lang", lang);
        cmd.Parameters.AddWithValue("@impactTargetPath", definition.Path);
        for (int i = 0; i < fallbackNames.Count; i++)
            cmd.Parameters.AddWithValue($"@impactFallbackName{i}", fallbackNames[i]);
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

        var truncated = filtered.Count > limit;
        if (truncated)
            filtered.RemoveRange(limit, filtered.Count - limit);

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
            cmd.Parameters.AddWithValue("@metadataAmbigLangFilter", lang);
        }
        // Path / exclude-path parameters share the same glob-aware LIKE
        // translation as the rest of the reader. Plain text keeps substring
        // behavior, while `*` / `?` become wildcards. Passing the raw CLI
        // value here would let `--path src/A/*.cs` stay literal and undercount
        // ambiguous targets, so centralize the conversion in
        // BuildPathLikePattern.
        // path / exclude-path のパラメータは reader 全体で共通の glob 対応
        // LIKE 変換を使う。ワイルドカードを含まない文字列は従来どおり部分文字列、
        // `*` / `?` はワイルドカードとして扱う。CLI の生値をそのまま渡すと
        // `--path src/A/*.cs` がリテラル扱いのままになり、曖昧性の件数を誤って
        // 数え込むため、変換は BuildPathLikePattern に集約する。
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
            {
                ors.Add($"f.path LIKE @metadataAmbigPath{i} ESCAPE '\\'");
                cmd.Parameters.AddWithValue($"@metadataAmbigPath{i}", BuildPathLikePattern(pathPatterns[i]));
            }
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
            {
                sql += $" AND f.path NOT LIKE @metadataAmbigExcludePath{i} ESCAPE '\\'";
                cmd.Parameters.AddWithValue($"@metadataAmbigExcludePath{i}", BuildPathLikePattern(excludePathPatterns[i]));
            }
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition}";
        sql += ")";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@metadataAmbigName", definition.Name);
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
        cmd.Parameters.AddWithValue("@fileId", fileId);

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
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.Parameters.AddWithValue("@typeName", typeName);
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

    private static bool IsNonCallableImpactKind(string? kind) =>
        kind is "namespace" or "import";

    private static bool IsPreciseImpactFallbackKind(string? kind)
    {
        return kind is "class" or "struct" or "interface";
    }

    private static string BuildImpactSuggestion(IReadOnlyList<string> definitionPaths, bool hasClassLikeDefinitions, bool hasMultipleDefinitions, bool hasMultipleDefinitionFiles)
    {
        if (hasClassLikeDefinitions)
        {
            if (hasMultipleDefinitionFiles)
                return "Try `cdidx deps --path <definition-path> --reverse` for each definition file or query a member symbol instead.";
            if (hasMultipleDefinitions)
                return "Try a fully qualified or member symbol query, or inspect the overlapping definitions with `cdidx definition <symbol> --body`.";
            if (definitionPaths.Count > 0)
                return $"Try `cdidx deps --path {definitionPaths[0]} --reverse` or query a member symbol instead.";
        }

        if (hasMultipleDefinitions)
            return "Try a more specific symbol name or inspect each definition file with `cdidx definition <symbol> --body`.";

        return "Try `cdidx definition <symbol>` to confirm the indexed symbol and then query a more specific callable member.";
    }

    private static string BuildGraphSupportReason(string? graphLanguage, bool? graphSupported)
    {
        return ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported)
            ?? "Call-graph support could not be determined because no language filter or matching definition was available.";
    }
}
