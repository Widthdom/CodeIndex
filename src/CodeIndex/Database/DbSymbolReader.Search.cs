using System.Globalization;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const string GenericSymbolRankNamePenaltySqlLiteral = "0.01";
    private const string GenericSymbolRankNamesSql = "('add','all','any','append','appendline','average','build','call','combine','contains','convert','count','create','distinct','equal','equals','execute','exists','file','files','first','firstordefault','get','getboolean','getbyte','getbytes','getchar','getchars','getdatetime','getdecimal','getdouble','getfieldvalue','getfloat','getguid','getint16','getint32','getint64','getordinal','getstring','gettemppath','getvalue','getvalues','groupby','handle','id','invoke','isdbnull','key','kind','last','lastordefault','length','line','list','load','name','orderby','orderbydescending','parse','path','process','read','resolve','run','set','single','singleordefault','skip','start','stop','sum','take','text','thenby','thenbydescending','tolist','tostring','tryparse','type','update','value','values','write')";

    private sealed class NormalizedSymbolSearchQueryList : List<string>
    {
        public NormalizedSymbolSearchQueryList(IEnumerable<string> queries)
            : base(queries)
        {
        }
    }

    private static IReadOnlyList<string>? NormalizeSymbolSearchQueries(IReadOnlyList<string>? queries, string? lang, bool exact)
    {
        if (queries == null)
            return null;
        if (queries is NormalizedSymbolSearchQueryList)
            return queries;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();
        foreach (var query in queries)
        {
            var value = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact) ?? query ?? string.Empty;
            if (value.Length == 0 || !seen.Add(value))
                continue;
            normalized.Add(value);
        }

        return new NormalizedSymbolSearchQueryList(normalized);
    }

    /// <summary>
    /// Escape LIKE wildcards (%, _) in user input to prevent unintended pattern matching.
    /// ユーザー入力のLIKEワイルドカード（%, _）をエスケープして意図しないパターンマッチを防止。
    /// </summary>
    /// <summary>
    /// Return all distinct symbol kinds present in the index.
    /// インデックス内の全シンボル種別を返す。
    /// </summary>
    /// <summary>
    /// Return symbol kind counts for status display.
    /// ステータス表示用のシンボル種別カウントを返す。
    /// </summary>
    public Dictionary<string, long> GetSymbolKindCounts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM symbols GROUP BY kind ORDER BY COUNT(*) DESC";
        var counts = new Dictionary<string, long>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }

    public List<string> GetDistinctKinds()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT kind FROM symbols ORDER BY kind";
        var kinds = new List<string>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            kinds.Add(reader.GetString(0));
        return kinds;
    }

    /// <summary>
    /// Search symbols by name pattern, optionally filtered by kind and language.
    /// シンボルを名前パターンで検索（種別・言語でフィルタ可能）。
    /// </summary>
    public List<SymbolResult> SearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name, int? startLine = null, int? endLine = null, bool groupPartials = false, int offset = 0)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return SearchSymbols(normalizedQuery == null ? null : new[] { normalizedQuery }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, sortMode, startLine, endLine, groupPartials, offset);
    }

    public int CountSearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return CountSearchSymbols(normalizedQuery == null ? null : new[] { normalizedQuery }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
    }

    public bool AnySearchSymbols(IReadOnlyList<string>? queries, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var validQueries = NormalizeSymbolSearchQueries(queries, lang, exact);
        if (validQueries == null || validQueries.Count == 0)
            return CountSearchSymbols(validQueries, 1, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact) > 0;

        foreach (var query in validQueries)
        {
            if (CountSearchSymbols([query], 1, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact) > 0)
                return true;
        }

        return false;
    }

    private string BuildQualifiedSymbolMatchSql(string parameterStem, bool useFoldedName, string symbolAlias = "s", string fileAlias = "f")
    {
        var containerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", containerNameSql, symbolAlias);
        var nameMatchSql = useFoldedName
            ? $"{symbolAlias}.name_folded = @{parameterStem}LeafFolded"
            : $"{symbolAlias}.name = @{parameterStem}Leaf COLLATE NOCASE";
        return $@"({fileAlias}.lang = 'csharp'
                  AND {nameMatchSql}
                  AND ({containerNameSql} = @{parameterStem}Container COLLATE NOCASE
                       OR {containerQualifiedNameSql} = @{parameterStem}Container COLLATE NOCASE
                       OR {containerQualifiedNameSql} COLLATE NOCASE LIKE @{parameterStem}ContainerSuffixLike ESCAPE '\'))";
    }

    private static string GetQualifiedQueryContainer(string query)
    {
        var normalized = SqlNameResolver.NormalizeQualifiedName(query);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot > 0 ? normalized[..lastDot] : string.Empty;
    }

    private static string GetQualifiedQuerySuffix(string query)
    {
        var normalized = SqlNameResolver.NormalizeQualifiedName(query);
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot <= 0)
            return normalized;
        var previousDot = normalized.LastIndexOf('.', lastDot - 1);
        return previousDot >= 0 ? normalized[(previousDot + 1)..] : normalized;
    }

    private static void AddQualifiedSymbolQueryParameters(SqliteCommand cmd, string parameterStem, string query)
    {
        var container = GetQualifiedQueryContainer(query);
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}Container", container);
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}ContainerSuffixLike", $"%.{EscapeLikeQuery(container)}");
    }

    private bool HasSingleQualifiedSymbolDefinition(string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (!SqlNameResolver.HasQualifier(query))
            return false;

        var matches = SearchSymbols(query, 2, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: false);
        if (matches.Count != 1)
            return false;

        var leafMatches = SearchSymbols(SqlNameResolver.GetLeafName(query), 2, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: true);
        return leafMatches.Count == 1;
    }

    private bool HasQualifiedSymbolDefinition(string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (!SqlNameResolver.HasQualifier(query))
            return false;

        return SearchSymbols(query, 1, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: false).Count > 0;
    }

    private static string BuildQualifiedContextMatchSql(string contextSql, string columnSql, bool folded, bool like)
    {
        var functionName = (folded, like) switch
        {
            (true, true) => "sql_context_like_name_folded_at",
            (true, false) => "sql_context_has_name_folded_at",
            (false, true) => "sql_context_like_name_at",
            _ => "sql_context_has_name_at",
        };
        return $"({functionName}({contextSql}, @aliasQuery, {columnSql}) = 1 OR {functionName}({contextSql}, @aliasQuerySuffix, {columnSql}) = 1)";
    }

    private static string BuildQualifiedLeafFallbackSql(string nameSql, string foldedNameSql, bool folded)
        => folded
            ? $"(@allowQualifiedLeafFallback = 1 AND f.lang = 'csharp' AND {foldedNameSql} = @aliasQueryLeafFolded)"
            : $"(@allowQualifiedLeafFallback = 1 AND f.lang = 'csharp' AND {nameSql} = @aliasQueryLeaf COLLATE NOCASE)";

    private static string BuildCSharpQualifiedContextFallbackSql(string qualifiedContextSql)
        => $"(@allowCSharpQualifiedContextMatch = 1 AND f.lang = 'csharp' AND {qualifiedContextSql})";

    private static void AddQualifiedGraphQueryParameters(SqliteCommand cmd, string query, bool allowLeafFallback, bool allowCSharpContextMatch = false)
    {
        SqliteCommandPolicy.Add(cmd, "@aliasQuerySuffix", GetQualifiedQuerySuffix(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeaf", SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@allowQualifiedLeafFallback", allowLeafFallback ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@allowCSharpQualifiedContextMatch", allowCSharpContextMatch ? 1 : 0);
    }

    public int CountSearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var validQueries = NormalizeSymbolSearchQueries(queries, lang, exact);
        if (validQueries != null && validQueries.Count > 1)
            return SearchSymbols(validQueries, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters).Count;

        using var cmd = _conn.CreateCommand();

        var innerSql = @"
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE 1=1";

        if (validQueries != null && validQueries.Count == 1)
        {
            var allowLeafFallback = !SqlNameResolver.HasQualifier(validQueries[0]);
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(validQueries[0])
                ? BuildQualifiedSymbolMatchSql("query0", _foldReady)
                : null;
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(validQueries[0], lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(validQueries[0]) : default;
            innerSql += exact
                ? rustQualifiedParts.QualifiedPath != null
                    ? _foldReady
                        ? " AND ((s.container_qualified_name = @query0RustContainer COLLATE NOCASE OR s.container_name = @query0RustContainer COLLATE NOCASE) AND s.name_folded = @query0RustLeafFolded)"
                        : " AND ((s.container_qualified_name = @query0RustContainer COLLATE NOCASE OR s.container_name = @query0RustContainer COLLATE NOCASE) AND s.name = @query0RustLeaf COLLATE NOCASE)"
                    : _foldReady
                        ? allowLeafFallback
                            ? " AND (s.name_folded = @query0 OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query0SegmentCount AND sql_normalize_name_folded(s.name) = @query0NormalizedFolded) OR sql_leaf_name_folded(s.name) = @query0LeafFolded)))"
                            : $" AND (s.name_folded = @query0 OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query0SegmentCount AND sql_normalize_name_folded(s.name) = @query0NormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                        : allowLeafFallback
                            ? " AND (s.name = @query0 COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query0SegmentCount AND sql_normalize_name(s.name) = @query0Normalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @query0Leaf COLLATE NOCASE)))"
                            : $" AND (s.name = @query0 COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query0SegmentCount AND sql_normalize_name(s.name) = @query0Normalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                : $" AND (s.name LIKE @query0 ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @query0NormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
        }
        if (kind != null)
            innerSql += " AND s.kind = @kind";
        if (lang != null)
            innerSql += SymbolLanguageFileIdFilter;
        if (since != null && _fileColumns.Contains("modified"))
            innerSql += " AND f.modified >= @since";
        AppendPathFilters(ref innerSql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref innerSql, visibilityFilters, excludeVisibilityFilters);
        innerSql += " LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({innerSql})";
        if (validQueries != null && validQueries.Count == 1)
        {
            var value = validQueries[0];
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(value, lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(value) : default;
            var paramValue = !exact
                ? $"%{EscapeLikeQuery(value)}%"
                : _foldReady
                    ? NameFold.Fold(value) ?? value
                    : value;
            SqliteCommandPolicy.Add(cmd, "@query0", paramValue);
            SqliteCommandPolicy.Add(cmd, "@query0Normalized", SqlNameResolver.NormalizeQualifiedName(value));
            SqliteCommandPolicy.Add(cmd, "@query0NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(value)) ?? SqlNameResolver.NormalizeQualifiedName(value));
            SqliteCommandPolicy.Add(cmd, "@query0Leaf", SqlNameResolver.GetLeafName(value));
            SqliteCommandPolicy.Add(cmd, "@query0LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(value)) ?? SqlNameResolver.GetLeafName(value));
            SqliteCommandPolicy.Add(cmd, "@query0SegmentCount", SqlNameResolver.GetSegmentCount(value));
            SqliteCommandPolicy.Add(cmd, "@query0NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(value))}%");
            if (SqlNameResolver.HasQualifier(value))
                AddQualifiedSymbolQueryParameters(cmd, "query0", value);
            if (rustQualifiedParts.QualifiedPath != null)
            {
                SqliteCommandPolicy.Add(cmd, "@query0RustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@query0RustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@query0RustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
            }
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(cmd, "@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountSearchSymbolsTotal(string? query = null, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, bool groupPartials = false)
    {
        return CountSearchSymbolsTotal(query == null ? null : new[] { NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact) ?? query ?? string.Empty }, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, groupPartials);
    }

    public QueryCountResult CountSearchSymbolsTotal(IReadOnlyList<string>? queries, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, bool groupPartials = false)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        using var cmd = _conn.CreateCommand();

        var logicalPartialKeySql = LogicalPartialSymbolGrouper.BuildSqlKeyExpression(
            "f.lang",
            "s.kind",
            "s.name",
            "s.id",
            GetSymbolColumnSql("signature"),
            GetSymbolColumnSql("container_name"),
            GetSymbolColumnSql("container_qualified_name"),
            GetSymbolColumnSql("family_key"));
        var countSql = groupPartials
            ? $"COUNT(DISTINCT ({logicalPartialKeySql}))"
            : "COUNT(*)";
        var sql = $@"
            SELECT {countSql}, COUNT(DISTINCT f.path)
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE 1=1";

        var effectiveQueries = NormalizeSymbolSearchQueries(queries, lang, exact);
        if (effectiveQueries != null && effectiveQueries.Count > 0)
        {
            var orClauses = exact
                ? string.Join(" OR ", effectiveQueries.Select((queryValue, idx) =>
                {
                    var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(queryValue, lang, exact);
                    var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(queryValue) : default;
                    var allowLeafFallback = !SqlNameResolver.HasQualifier(queryValue);
                    var qualifiedSymbolClause = SqlNameResolver.HasQualifier(queryValue)
                        ? BuildQualifiedSymbolMatchSql($"query{idx}", _foldReady)
                        : null;
                    var swiftBacktickAlias = ComputeSwiftBacktickAlias(queryValue, lang);
                    var swiftBacktickClause = swiftBacktickAlias != null
                        ? _foldReady
                            ? $" OR s.name_folded = @query{idx}SwiftBacktickAlias"
                            : $" OR s.name = @query{idx}SwiftBacktickAlias COLLATE NOCASE"
                        : string.Empty;
                    if (rustQualifiedParts.QualifiedPath != null)
                        return _foldReady
                            ? $"((s.container_qualified_name = @query{idx}RustContainer COLLATE NOCASE OR s.container_name = @query{idx}RustContainer COLLATE NOCASE) AND s.name_folded = @query{idx}RustLeafFolded)"
                            : $"((s.container_qualified_name = @query{idx}RustContainer COLLATE NOCASE OR s.container_name = @query{idx}RustContainer COLLATE NOCASE) AND s.name = @query{idx}RustLeaf COLLATE NOCASE)";
                    return _foldReady
                        ? allowLeafFallback
                            ? $"(s.name_folded = @query{idx}{swiftBacktickClause} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name_folded(s.name) = @query{idx}NormalizedFolded) OR sql_leaf_name_folded(s.name) = @query{idx}LeafFolded)))"
                            : $"(s.name_folded = @query{idx}{swiftBacktickClause} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name_folded(s.name) = @query{idx}NormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                        : allowLeafFallback
                            ? $"(s.name = @query{idx} COLLATE NOCASE{swiftBacktickClause} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name(s.name) = @query{idx}Normalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @query{idx}Leaf COLLATE NOCASE)))"
                            : $"(s.name = @query{idx} COLLATE NOCASE{swiftBacktickClause} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name(s.name) = @query{idx}Normalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
                }))
                : string.Join(" OR ", effectiveQueries.Select((queryValue, idx) =>
                {
                    var qualifiedSymbolClause = SqlNameResolver.HasQualifier(queryValue)
                        ? BuildQualifiedSymbolMatchSql($"query{idx}", _foldReady)
                        : null;
                    return $"(s.name LIKE @query{idx} ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @query{idx}NormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
                }));
            sql += $" AND ({orClauses})";
        }
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);

        cmd.CommandText = sql;
        if (effectiveQueries != null)
        {
            for (int i = 0; i < effectiveQueries.Count; i++)
            {
                var value = effectiveQueries[i];
                var paramValue = !exact
                    ? $"%{EscapeLikeQuery(value)}%"
                    : _foldReady
                        ? NameFold.Fold(value) ?? value
                        : value;
                SqliteCommandPolicy.Add(cmd, $"@query{i}", paramValue);
                SqliteCommandPolicy.Add(cmd, $"@query{i}Normalized", SqlNameResolver.NormalizeQualifiedName(value));
                SqliteCommandPolicy.Add(cmd, $"@query{i}NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(value)) ?? SqlNameResolver.NormalizeQualifiedName(value));
                SqliteCommandPolicy.Add(cmd, $"@query{i}Leaf", SqlNameResolver.GetLeafName(value));
                SqliteCommandPolicy.Add(cmd, $"@query{i}LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(value)) ?? SqlNameResolver.GetLeafName(value));
                SqliteCommandPolicy.Add(cmd, $"@query{i}SegmentCount", SqlNameResolver.GetSegmentCount(value));
                SqliteCommandPolicy.Add(cmd, $"@query{i}NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(value))}%");
                if (SqlNameResolver.HasQualifier(value))
                    AddQualifiedSymbolQueryParameters(cmd, $"query{i}", value);
                var swiftBacktickAlias = ComputeSwiftBacktickAlias(value, lang);
                if (swiftBacktickAlias != null)
                {
                    SqliteCommandPolicy.Add(cmd, $"@query{i}SwiftBacktickAlias", _foldReady
                        ? NameFold.Fold(swiftBacktickAlias) ?? swiftBacktickAlias
                        : swiftBacktickAlias);
                }
            }
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(cmd, "@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead()
            ? new QueryCountResult(reader.GetInt32(0), reader.GetInt32(1))
            : new QueryCountResult(0, 0);
    }

    /// <summary>
    /// Search symbols by one or more name patterns (OR-joined). Empty/null list returns all symbols matching other filters.
    /// When <paramref name="exact"/> is true, names are matched case-insensitively for equality instead of substring.
    /// 複数名前パターン（OR結合）でシンボルを検索。空/null なら他フィルタに一致する全シンボルを返す。
    /// <paramref name="exact"/> が true の場合、部分一致ではなく大文字小文字を無視した完全一致になる。
    /// </summary>
    public List<SymbolResult> SearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name, int? startLine = null, int? endLine = null, bool groupPartials = false, int offset = 0)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        // Multi-name queries: run one search per name to guarantee per-name candidate coverage
        // (a common/earlier-sorting name cannot starve others out of the candidate pool), then
        // round-robin interleave the per-name results under a single global `limit` cap so the
        // public `limit` contract stays "Max total results", not per-name.
        // 複数名指定: 名前ごとに独立検索して候補プールを確保した上で、round-robin で統合し、
        // 最終的に全体で `limit` 件に収める。`limit` は従来どおり「合計の上限」。
        var validQueries = NormalizeSymbolSearchQueries(queries, lang, exact);
        if (validQueries != null && validQueries.Count > 1)
        {
            var requestedPrefix = checked(limit + Math.Max(0, offset));
            var perName = new List<List<SymbolResult>>(validQueries.Count);
            foreach (var q in validQueries)
                perName.Add(SearchSymbols(new[] { q! }, requestedPrefix, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, sortMode, startLine, endLine, groupPartials));

            var seen = new HashSet<(string Path, int Line, string Name, string Kind)>();
            var merged = new List<SymbolResult>();
            var cursors = new int[perName.Count];
            bool advanced;
            do
            {
                advanced = false;
                for (int i = 0; i < perName.Count && merged.Count < requestedPrefix; i++)
                {
                    while (cursors[i] < perName[i].Count)
                    {
                        var r = perName[i][cursors[i]++];
                        if (seen.Add((r.Path, r.Line, r.Name, r.Kind)))
                        {
                            merged.Add(r);
                            advanced = true;
                            break;
                        }
                    }
                }
            } while (advanced && merged.Count < requestedPrefix);
            return merged.Skip(Math.Max(0, offset)).Take(limit).ToList();
        }

        using var cmd = _conn.CreateCommand();

        var startLineSql = GetSymbolColumnSql("start_line", "s.line");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line");
        var bodyStartLineSql = GetSymbolColumnSql("body_start_line");
        var bodyEndLineSql = GetSymbolColumnSql("body_end_line");
        var signatureSql = GetSymbolColumnSql("signature");
        var containerKindSql = GetSymbolColumnSql("container_kind");
        var containerNameSql = GetSymbolColumnSql("container_name");
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var familyKeySql = GetSymbolColumnSql("family_key");
        var visibilitySql = GetSymbolColumnSql("visibility");
        var returnTypeSql = GetSymbolColumnSql("return_type");
        var startColumnSql = GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)");
        var sizeLinesSql = $"CASE WHEN ({endLineSql}) >= ({startLineSql}) THEN ({endLineSql}) - ({startLineSql}) + 1 ELSE 1 END";
        var includeRankSignals = sortMode != SymbolSortMode.Name && _hasReferencesTable;
        // Keep aggregate grouping aligned with the NOCASE joins below. Binary grouping would emit
        // one aggregate row per case variant and multiply the same physical s.id before LIMIT/OFFSET (#4753).
        // 集計側と下記 NOCASE JOIN の照合順序を揃え、大小文字 variant による同一 s.id の増殖を防ぐ。
        var symbolRankJoin = includeRankSignals
            ? $@"
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
             AND symbol_defs.symbol_name = s.name COLLATE NOCASE"
            : string.Empty;
        var genericNamePenaltySql = includeRankSignals ? GetGenericSymbolRankNamePenaltySql("s.name") : "1.0";
        var definitionSitesSql = includeRankSignals ? "COALESCE(symbol_defs.definition_sites, 1)" : "CAST(1 AS INTEGER)";
        var csharpConservativeRankSignalSql = includeRankSignals
            ? $"(f.lang = 'csharp' AND (s.kind = 'property' OR ({definitionSitesSql}) > 1 OR lower(s.name) IN {GenericSymbolRankNamesSql}))"
            : "0";
        var referenceCountSql = includeRankSignals
            ? $"CASE WHEN {csharpConservativeRankSignalSql} THEN COALESCE(symbol_file_rank.reference_count, 0) ELSE COALESCE(symbol_rank.reference_count, 0) END"
            : "CAST(0 AS INTEGER)";
        var hotspotScoreSql = includeRankSignals
            ? $"CASE WHEN {csharpConservativeRankSignalSql} THEN COALESCE(symbol_file_rank.hotspot_score, 0.0) ELSE COALESCE(symbol_rank.hotspot_score, 0.0) END"
            : "CAST(0.0 AS REAL)";
        var definitionDilutionSql = $"CASE WHEN ({definitionSitesSql}) > 1 THEN CAST(({definitionSitesSql}) * ({definitionSitesSql}) AS REAL) ELSE 1.0 END";
        var structuralRankPenaltySql = includeRankSignals ? $"CASE WHEN s.kind IN ('property', 'enum') AND ({sizeLinesSql}) <= 1 THEN 0.1 ELSE 1.0 END" : "1.0";
        var rankingReferenceScoreSql = includeRankSignals ? $"(({referenceCountSql}) * ({genericNamePenaltySql}) * ({structuralRankPenaltySql}) / ({definitionDilutionSql}))" : referenceCountSql;
        var rankingHotspotScoreSql = includeRankSignals ? $"(({hotspotScoreSql}) * ({genericNamePenaltySql}) * ({structuralRankPenaltySql}) / ({definitionDilutionSql}))" : hotspotScoreSql;
        var cappedRankingReferenceScoreSql = $"CASE WHEN ({rankingReferenceScoreSql}) > 100.0 THEN 100.0 ELSE ({rankingReferenceScoreSql}) END";
        var cappedRankingHotspotScoreSql = $"CASE WHEN ({rankingHotspotScoreSql}) > 150.0 THEN 150.0 ELSE ({rankingHotspotScoreSql}) END";
        var complexityScoreSql = $@"(({sizeLinesSql} * 16.0) + ({cappedRankingReferenceScoreSql} * 0.75) + ({cappedRankingHotspotScoreSql} * 0.35) + CASE
                       WHEN {visibilitySql} IN ('public', 'pub', 'open', 'export') THEN 8.0
                       WHEN {visibilitySql} IN ('protected', 'internal', 'protected internal') THEN 4.0
                       ELSE 0.0
                   END)";
        var logicalPartialKeySql = LogicalPartialSymbolGrouper.BuildSqlKeyExpression(
            "f.lang",
            "s.kind",
            "s.name",
            "s.id",
            signatureSql,
            containerNameSql,
            containerQualifiedNameSql,
            familyKeySql);
        var exactNameOrderSql = "CASE " +
            "WHEN @preferLiteralExactMatch = 1 AND s.name = @rawQuery THEN 0 " +
            "WHEN @preferLiteralNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name(s.name) = @rawQueryNormalized THEN 1 " +
            "WHEN @preferCaseInsensitiveExactMatch = 1 AND s.name = @rawQuery COLLATE NOCASE THEN 2 " +
            "WHEN @preferCaseInsensitiveNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name_folded(s.name) = @rawQueryNormalizedFolded THEN 3 " +
            "WHEN @preferCaseInsensitiveSqlLeafMatch = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @rawQueryLeafFolded THEN 4 " +
            "ELSE 5 END";

        var sql = $@"
            SELECT f.path, f.lang, s.kind, {GetSymbolColumnSql("sub_kind")} AS sub_kind, s.name, s.line,
                   {startLineSql} AS start_line,
                   {GetSymbolColumnSql("start_column")} AS start_column,
                   {endLineSql} AS end_line,
                   {bodyStartLineSql} AS body_start_line,
                   {bodyEndLineSql} AS body_end_line,
                   {signatureSql} AS signature,
                   {containerKindSql} AS container_kind,
                   {containerNameSql} AS container_name,
                   {visibilitySql} AS visibility,
                   {returnTypeSql} AS return_type,
                   {referenceCountSql} AS reference_count,
                   {hotspotScoreSql} AS hotspot_score,
                   {rankingReferenceScoreSql} AS ranking_reference_score,
                   {rankingHotspotScoreSql} AS ranking_hotspot_score,
                   {genericNamePenaltySql} AS generic_name_penalty,
                   {structuralRankPenaltySql} AS structural_rank_penalty,
                   {definitionSitesSql} AS definition_sites,
                   {sizeLinesSql} AS size_lines,
                   {complexityScoreSql} AS complexity_score,
                   {containerQualifiedNameSql} AS container_qualified_name,
                   {logicalPartialKeySql} AS logical_partial_key,
                   s.id AS symbol_id,
                   {exactNameOrderSql} AS exact_name_order,
                   {PathBucketOrder} AS path_bucket,
                   {VisibilityOrder} AS visibility_rank,
                   {startColumnSql} AS stable_start_column
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            {symbolRankJoin}
            WHERE 1=1";

        var effectiveQueries = validQueries;
        if (effectiveQueries != null && effectiveQueries.Count > 0)
        {
            // --exact: Unicode-aware equality when FoldReady (#86), else ASCII COLLATE NOCASE.
            // Fold path: `s.name_folded = @qFolded` (indexed by idx_symbols_name_folded), query
            // value is pre-folded in .NET with NameFold.Fold so Ä vs ä / 全角 vs 半角 match.
            // Fallback: `s.name = @q COLLATE NOCASE` (indexed by idx_symbols_name_nocase). Both
            // paths stay SARGable. Using `lower(col)` would force a full scan per name.
            // --exact: FoldReady なら Unicode 折り畳み経路、未 ready ならレガシー NOCASE 経路へ fallback。
            var orClauses = exact
                ? string.Join(" OR ", effectiveQueries.Select((queryValue, idx) =>
                {
                    var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(queryValue, lang, exact);
                    var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(queryValue) : default;
                    var allowLeafFallback = !SqlNameResolver.HasQualifier(queryValue);
                    var qualifiedSymbolClause = SqlNameResolver.HasQualifier(queryValue)
                        ? BuildQualifiedSymbolMatchSql($"query{idx}", _foldReady)
                        : null;
                    var swiftBacktickAlias = ComputeSwiftBacktickAlias(queryValue, lang);
                    var swiftBacktickClause = swiftBacktickAlias != null
                        ? _foldReady
                            ? $" OR s.name_folded = @query{idx}SwiftBacktickAlias"
                            : $" OR s.name = @query{idx}SwiftBacktickAlias COLLATE NOCASE"
                        : string.Empty;
                    if (rustQualifiedParts.QualifiedPath != null)
                        return _foldReady
                            ? $"((s.container_qualified_name = @query{idx}RustContainer COLLATE NOCASE OR s.container_name = @query{idx}RustContainer COLLATE NOCASE) AND s.name_folded = @query{idx}RustLeafFolded)"
                            : $"((s.container_qualified_name = @query{idx}RustContainer COLLATE NOCASE OR s.container_name = @query{idx}RustContainer COLLATE NOCASE) AND s.name = @query{idx}RustLeaf COLLATE NOCASE)";
                    return _foldReady
                        ? allowLeafFallback
                            ? $"(s.name_folded = @query{idx}{swiftBacktickClause} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name_folded(s.name) = @query{idx}NormalizedFolded) OR sql_leaf_name_folded(s.name) = @query{idx}LeafFolded)))"
                            : $"(s.name_folded = @query{idx}{swiftBacktickClause} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name_folded(s.name) = @query{idx}NormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                        : allowLeafFallback
                            ? $"(s.name = @query{idx} COLLATE NOCASE{swiftBacktickClause} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name(s.name) = @query{idx}Normalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @query{idx}Leaf COLLATE NOCASE)))"
                            : $"(s.name = @query{idx} COLLATE NOCASE{swiftBacktickClause} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @query{idx}SegmentCount AND sql_normalize_name(s.name) = @query{idx}Normalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
                }))
                : string.Join(" OR ", effectiveQueries.Select((queryValue, idx) =>
                {
                    var qualifiedSymbolClause = SqlNameResolver.HasQualifier(queryValue)
                        ? BuildQualifiedSymbolMatchSql($"query{idx}", _foldReady)
                        : null;
                    return $"(s.name LIKE @query{idx} ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @query{idx}NormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
                }));
            sql += $" AND ({orClauses})";
        }
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        if (startLine != null)
            sql += " AND s.line >= @startLine";
        if (endLine != null)
            sql += " AND s.line <= @endLine";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        if (groupPartials)
            sql = BuildLogicalPartialSymbolQuery(sql, sortMode);
        else
            sql += BuildSymbolSortOrderBy(sortMode, exactNameOrderSql, referenceCountSql, hotspotScoreSql, rankingReferenceScoreSql, rankingHotspotScoreSql, sizeLinesSql, complexityScoreSql, startColumnSql);
        sql += " LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        if (effectiveQueries != null)
        {
            for (int idx = 0; idx < effectiveQueries.Count; idx++)
            {
                string paramValue;
                var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(effectiveQueries[idx], lang, exact);
                var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(effectiveQueries[idx]) : default;
                if (!exact)
                    paramValue = $"%{EscapeLikeQuery(effectiveQueries[idx])}%";
                else if (_foldReady)
                    paramValue = NameFold.Fold(effectiveQueries[idx]) ?? effectiveQueries[idx];
                else
                    paramValue = effectiveQueries[idx];
                SqliteCommandPolicy.Add(cmd, $"@query{idx}", paramValue);
                SqliteCommandPolicy.Add(cmd, $"@query{idx}Normalized", SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]));
                SqliteCommandPolicy.Add(cmd, $"@query{idx}NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx])) ?? SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]));
                SqliteCommandPolicy.Add(cmd, $"@query{idx}Leaf", SqlNameResolver.GetLeafName(effectiveQueries[idx]));
                SqliteCommandPolicy.Add(cmd, $"@query{idx}LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(effectiveQueries[idx])) ?? SqlNameResolver.GetLeafName(effectiveQueries[idx]));
                SqliteCommandPolicy.Add(cmd, $"@query{idx}SegmentCount", SqlNameResolver.GetSegmentCount(effectiveQueries[idx]));
                SqliteCommandPolicy.Add(cmd, $"@query{idx}NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]))}%");
                if (SqlNameResolver.HasQualifier(effectiveQueries[idx]))
                    AddQualifiedSymbolQueryParameters(cmd, $"query{idx}", effectiveQueries[idx]);
                if (rustQualifiedParts.QualifiedPath != null)
                {
                    SqliteCommandPolicy.Add(cmd, $"@query{idx}RustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                    SqliteCommandPolicy.Add(cmd, $"@query{idx}RustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                    SqliteCommandPolicy.Add(cmd, $"@query{idx}RustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
                }
                var swiftBacktickAlias = ComputeSwiftBacktickAlias(effectiveQueries[idx], lang);
                if (swiftBacktickAlias != null)
                {
                    SqliteCommandPolicy.Add(cmd, $"@query{idx}SwiftBacktickAlias", _foldReady
                        ? NameFold.Fold(swiftBacktickAlias) ?? swiftBacktickAlias
                        : swiftBacktickAlias);
                }
            }
        }
        var preferLiteralExactMatch = effectiveQueries != null && effectiveQueries.Count == 1;
        var preferCaseInsensitiveExactMatch = effectiveQueries != null && effectiveQueries.Count == 1;
        var preferSqlLeafMatch = preferCaseInsensitiveExactMatch && !SqlNameResolver.HasQualifier(effectiveQueries![0]);
        SqliteCommandPolicy.Add(cmd, "@preferLiteralExactMatch", preferLiteralExactMatch ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@preferLiteralNormalizedSqlMatch", preferLiteralExactMatch ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@preferCaseInsensitiveExactMatch", preferCaseInsensitiveExactMatch ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@preferCaseInsensitiveNormalizedSqlMatch", preferCaseInsensitiveExactMatch ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@preferCaseInsensitiveSqlLeafMatch", preferSqlLeafMatch ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@rawQuery", preferLiteralExactMatch ? effectiveQueries![0] : string.Empty);
        SqliteCommandPolicy.Add(cmd, "@rawQueryNormalized", preferLiteralExactMatch ? SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0]) : string.Empty);
        SqliteCommandPolicy.Add(cmd, "@rawQueryNormalizedFolded", preferLiteralExactMatch ? NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0])) ?? SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0]) : string.Empty);
        SqliteCommandPolicy.Add(cmd, "@rawQueryLeaf", preferLiteralExactMatch ? SqlNameResolver.GetLeafName(effectiveQueries![0]) : string.Empty);
        SqliteCommandPolicy.Add(cmd, "@rawQueryLeafFolded", preferLiteralExactMatch ? NameFold.Fold(SqlNameResolver.GetLeafName(effectiveQueries![0])) ?? SqlNameResolver.GetLeafName(effectiveQueries![0]) : string.Empty);
        SqliteCommandPolicy.Add(cmd, "@rawQuerySegmentCount", preferLiteralExactMatch ? SqlNameResolver.GetSegmentCount(effectiveQueries![0]) : 0);
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(cmd, "@since", since.Value);
        if (startLine != null)
            SqliteCommandPolicy.Add(cmd, "@startLine", startLine.Value);
        if (endLine != null)
            SqliteCommandPolicy.Add(cmd, "@endLine", endLine.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

        var includeRankingMetadata = sortMode != SymbolSortMode.Name;
        var sortModeName = sortMode.ToString().ToLowerInvariant();
        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var definitionSites = Convert.ToInt32(reader.GetInt64(22));
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Kind = reader.GetString(2),
                SubKind = GetNullableString(reader, 3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                StartColumn = ResolveSymbolIdentifierStartColumn(
                    GetNullableInt32(reader, 7),
                    GetNullableString(reader, 11),
                    reader.GetString(4)),
                EndLine = GetInt32OrFallback(reader, 8, 5),
                BodyStartLine = GetNullableInt32(reader, 9),
                BodyEndLine = GetNullableInt32(reader, 10),
                Signature = GetNullableString(reader, 11),
                ContainerKind = GetNullableString(reader, 12),
                ContainerName = GetNullableString(reader, 13),
                ContainerQualifiedName = GetNullableString(reader, 25),
                LogicalPartialKey = GetNullableString(reader, 26),
                Visibility = GetNullableString(reader, 14),
                ReturnType = GetNullableString(reader, 15),
                SortMode = includeRankingMetadata ? sortModeName : null,
                ReferenceCount = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(16)) : null,
                HotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(17), 3) : null,
                RankingReferenceScore = includeRankingMetadata ? Math.Round(reader.GetDouble(18), 3) : null,
                RankingHotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(19), 3) : null,
                GenericNamePenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(20), 3) : null,
                StructuralRankPenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(21), 3) : null,
                DefinitionSites = includeRankingMetadata || (groupPartials && definitionSites > 1) ? definitionSites : null,
                SizeLines = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(23)) : null,
                ComplexityScore = includeRankingMetadata ? Math.Round(reader.GetDouble(24), 3) : null,
                SymbolId = reader.GetInt64(27),
            });
        }
        return results;
    }

    private static int? ResolveSymbolIdentifierStartColumn(int? declarationStartColumn, string? signature, string name)
    {
        if (!declarationStartColumn.HasValue || string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(name))
            return declarationStartColumn;

        var firstLineEnd = signature.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd >= 0 ? signature[..firstLineEnd] : signature;
        var relativeColumn = firstLine.IndexOf(name, StringComparison.Ordinal);
        return relativeColumn >= 0 ? declarationStartColumn.Value + relativeColumn : declarationStartColumn;
    }

    private static string GetGenericSymbolRankNamePenaltySql(string nameSql)
        => $"CASE WHEN lower({nameSql}) IN {GenericSymbolRankNamesSql} THEN {GenericSymbolRankNamePenaltySqlLiteral} ELSE 1.0 END";

    private static string BuildLogicalPartialSymbolQuery(string matchingSymbolsSql, SymbolSortMode sortMode)
    {
        var orderBy = BuildLogicalPartialSortOrderBy(sortMode);
        return $@"
            WITH matching_symbols AS (
                {matchingSymbolsSql}
            ),
            logical_symbols AS (
                SELECT matching_symbols.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY logical_partial_key
                           ORDER BY path COLLATE BINARY, start_line, stable_start_column, symbol_id
                       ) AS logical_row_number,
                       COUNT(*) OVER (PARTITION BY logical_partial_key) AS logical_definition_sites,
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
                       MIN(visibility_rank) OVER (PARTITION BY logical_partial_key) AS logical_visibility_rank
                FROM matching_symbols
            )
            SELECT path, lang, kind, sub_kind, name, line,
                   start_line, start_column, end_line,
                   body_start_line, body_end_line, signature,
                   container_kind, container_name, visibility, return_type,
                   logical_reference_count, logical_hotspot_score,
                   logical_ranking_reference_score, logical_ranking_hotspot_score,
                   logical_generic_name_penalty, logical_structural_rank_penalty,
                   logical_definition_sites, logical_size_lines, logical_complexity_score,
                   container_qualified_name, logical_partial_key, symbol_id
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

    private static string? NormalizeSymbolSearchQuery(string? query, string? lang, bool exact = false)
    {
        if (!string.IsNullOrWhiteSpace(lang) && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase))
            return NormalizeRustSymbolSearchQuery(query, exact);

        if (!string.IsNullOrWhiteSpace(lang) && string.Equals(lang, "javascript", StringComparison.OrdinalIgnoreCase))
            return NormalizeJavaScriptSymbolSearchQuery(query);

        // Terraform dotted prefixes (var.X / local.X / module.X / data.TYPE.X) are stored as bare names in
        // the references and symbols tables. Strip the prefix so queries pasted from HCL still resolve.
        // Terraform の dotted prefix（var.X / local.X / module.X / data.TYPE.X）は参照/シンボルの bare 名で格納されるため、
        // HCL からそのまま貼り付けたクエリでも解決できるよう prefix を取り除く。
        var terraformNormalized = NormalizeTerraformDottedQuery(query, lang);
        if (terraformNormalized != null)
            return terraformNormalized;

        return NormalizeCSharpVerbatimQuery(query, lang);
    }

    private static readonly Regex TerraformVarLocalModuleQueryRegex = new(
        @"^(?:var|local|module)\.(?<name>[A-Za-z_]\w*)(?:\..*)?$",
        RegexOptions.Compiled);

    private static readonly Regex TerraformDataQueryRegex = new(
        @"^data\.[A-Za-z_]\w*\.(?<name>[A-Za-z_]\w*)(?:\..*)?$",
        RegexOptions.Compiled);

    private static string? NormalizeTerraformDottedQuery(string? query, string? lang)
    {
        if (!string.IsNullOrWhiteSpace(lang)
            && !string.Equals(lang, "terraform", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(query))
            return null;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return null;

        var simpleMatch = TerraformVarLocalModuleQueryRegex.Match(trimmed);
        if (simpleMatch.Success)
            return simpleMatch.Groups["name"].Value;

        var dataMatch = TerraformDataQueryRegex.Match(trimmed);
        if (dataMatch.Success)
            return dataMatch.Groups["name"].Value;

        return null;
    }

    private static string? ComputeSwiftBacktickAlias(string? query, string? lang)
    {
        if (!string.Equals(lang, "swift", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(query))
            return null;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return null;

        if (trimmed.IndexOfAny(['`', ':', '/', '<', '>', '(', ')', '[', ']', ' ']) >= 0)
            return null;

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot < 0)
            return $"`{trimmed}`";

        if (lastDot == 0 || lastDot == trimmed.Length - 1)
            return null;

        var prefix = trimmed[..(lastDot + 1)];
        var leaf = trimmed[(lastDot + 1)..];
        if (leaf.IndexOf('.') >= 0)
            return null;

        return $"{prefix}`{leaf}`";
    }

    private static string? NormalizeJavaScriptSymbolSearchQuery(string? query)
    {
        if (query == null)
            return null;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return null;

        var commonJsPrefixLength = 0;
        if (trimmed.StartsWith("module.exports", StringComparison.Ordinal))
        {
            var nextIndex = "module.exports".Length;
            if (trimmed.Length > nextIndex && trimmed[nextIndex] is '.' or '[')
                commonJsPrefixLength = nextIndex;
        }
        else if (trimmed.StartsWith("exports", StringComparison.Ordinal))
        {
            var nextIndex = "exports".Length;
            if (trimmed.Length > nextIndex && trimmed[nextIndex] is '.' or '[')
                commonJsPrefixLength = nextIndex;
        }

        if (commonJsPrefixLength == 0)
            return trimmed;

        trimmed = trimmed[commonJsPrefixLength..];
        if (trimmed.Length == 0)
            return null;

        trimmed = trimmed.TrimStart();
        if (trimmed.StartsWith(".", StringComparison.Ordinal))
            trimmed = trimmed[1..].TrimStart();

        var bracketLeaf = NormalizeJavaScriptBracketLeaf(trimmed);
        if (bracketLeaf != null)
            return bracketLeaf;

        var leafIndex = trimmed.LastIndexOf('.');
        if (leafIndex >= 0)
            trimmed = trimmed[(leafIndex + 1)..];

        bracketLeaf = NormalizeJavaScriptBracketLeaf(trimmed);
        if (bracketLeaf != null)
            return bracketLeaf;

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeJavaScriptBracketLeaf(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']')
            return null;

        var inner = trimmed[1..^1].Trim();
        if (inner.Length < 2)
            return null;

        var quote = inner[0];
        if (quote is not '\'' and not '"')
            return null;

        if (inner[^1] != quote)
            return null;

        var leaf = inner[1..^1].Trim();
        return leaf.Length == 0 ? null : leaf;
    }

    private static string? NormalizeRustSymbolSearchQuery(string? query, bool exact = false)
    {
        if (query == null)
            return null;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return null;

        var macroQuery = trimmed;
        var isMacroQuery = macroQuery.EndsWith("!", StringComparison.Ordinal);
        if (isMacroQuery)
            macroQuery = macroQuery[..^1].TrimEnd();

        if (macroQuery.Length == 0)
            return null;

        if (exact && isMacroQuery && macroQuery.Contains("::", StringComparison.Ordinal))
            return NormalizeRustQualifiedMacroQuery(macroQuery);

        var leafIndex = macroQuery.LastIndexOf("::", StringComparison.Ordinal);
        if (leafIndex >= 0)
            macroQuery = macroQuery[(leafIndex + 2)..].Trim();

        if (macroQuery.StartsWith("r#", StringComparison.Ordinal))
            macroQuery = macroQuery[2..];

        return macroQuery.Length == 0 ? null : macroQuery;
    }

    private static string? NormalizeRustQualifiedMacroQuery(string query)
    {
        var segments = query
            .Split("::", StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment => segment.StartsWith("r#", StringComparison.Ordinal) ? segment[2..] : segment)
            .ToList();

        return segments.Count == 0 ? null : string.Join("::", segments);
    }

    private static bool ShouldPreserveRustQualifiedExactQuery(string? query, string? lang, bool exact)
    {
        return exact
            && !string.IsNullOrWhiteSpace(lang)
            && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(query)
            && query.Contains("::", StringComparison.Ordinal);
    }

    private static string? NormalizeSymbolSearchQueryForSymbolSearch(string? query, string? lang, bool exact)
    {
        if (ShouldPreserveRustQualifiedExactQuery(query, lang, exact))
            return query?.Trim();

        return NormalizeSymbolSearchQuery(query, lang) ?? query;
    }

    private static (string? QualifiedPath, string? ContainerPath, string? LeafName) NormalizeRustQualifiedExactQueryParts(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return (null, null, null);

        if (trimmed.EndsWith("!", StringComparison.Ordinal))
            trimmed = trimmed[..^1].TrimEnd();

        var normalized = NormalizeRustQualifiedMacroQuery(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
            return (null, null, null);

        normalized = normalized.Replace("::", ".");
        while (normalized.StartsWith("crate.", StringComparison.Ordinal)
            || normalized.StartsWith("self.", StringComparison.Ordinal)
            || normalized.StartsWith("super.", StringComparison.Ordinal))
        {
            var dotIndex = normalized.IndexOf('.');
            if (dotIndex < 0 || dotIndex == normalized.Length - 1)
                break;

            normalized = normalized[(dotIndex + 1)..];
        }
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot < 0)
            return (normalized, string.Empty, normalized);

        return (normalized, normalized[..lastDot], normalized[(lastDot + 1)..]);
    }

}
