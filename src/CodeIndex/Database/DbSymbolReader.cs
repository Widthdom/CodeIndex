using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

/// <summary>
/// Symbol query operations: search, definitions, outline, analyze (partial class split from DbReader.cs).
/// シンボルクエリ操作: 検索、定義、アウトライン、分析（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    internal const int DefinitionBodyMaxLines = 20;
    internal const int DefinitionBodyMaxRequestedLines = 1_000;
    internal const int DefinitionBodyMaxBytes = 16 * 1024;

    private const string GenericHotspotNamePenaltySqlLiteral = "0.35";
    private const string GenericHotspotNamesSql = "('add','append','build','call','combine','convert','create','execute','get','getstring','getvalue','getvalues','handle','invoke','load','parse','process','read','resolve','run','set','start','stop','tolist','tostring','tryparse','update','write')";
    private const string GenericSymbolRankNamePenaltySqlLiteral = "0.01";
    private const string GenericSymbolRankNamesSql = "('add','all','any','append','appendline','average','build','call','combine','contains','convert','count','create','distinct','equal','equals','execute','exists','file','files','first','firstordefault','get','getboolean','getbyte','getbytes','getchar','getchars','getdatetime','getdecimal','getdouble','getfieldvalue','getfloat','getguid','getint16','getint32','getint64','getordinal','getstring','gettemppath','getvalue','getvalues','groupby','handle','id','invoke','isdbnull','key','kind','last','lastordefault','length','line','list','load','name','orderby','orderbydescending','parse','path','process','read','resolve','run','set','single','singleordefault','skip','start','stop','sum','take','text','thenby','thenbydescending','tolist','tostring','tryparse','type','update','value','values','write')";

    private const int GroupedHotspotPathSampleLimit = 20;
    private const string SymbolLanguageFileIdFilter = " AND s.file_id IN (SELECT id FROM files WHERE lang = @lang)";
    private const int QueryOutputSignatureMaxChars = 512;
    private const string QueryOutputSignatureTruncationSuffix = "...";
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

    private void AppendVisibilityFilters(ref string sql, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) IN ({SqliteDynamicSql.BuildParameterList("visibility", expandedVisibilityFilters.Count)})";
        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) NOT IN ({SqliteDynamicSql.BuildParameterList("excludeVisibility", expandedExcludeVisibilityFilters.Count)})";
    }

    private static void AddVisibilityFilterParameters(SqliteCommand cmd, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "visibility", expandedVisibilityFilters, SqliteType.Text, "visibility filters");

        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "excludeVisibility", expandedExcludeVisibilityFilters, SqliteType.Text, "visibility filters");
    }

    private static void EnsureVisibilityFilterParameterBudget(IReadOnlyCollection<string>? visibilityFilters, IReadOnlyCollection<string>? excludeVisibilityFilters)
        => SqliteDynamicSql.EnsureParameterBudget((visibilityFilters?.Count ?? 0) + (excludeVisibilityFilters?.Count ?? 0), "visibility filters");

    private static List<string> ExpandVisibilityFilterValues(IReadOnlyList<string> filters)
    {
        var expanded = new List<string>();
        foreach (var filter in filters)
        {
            string[] aliases = filter switch
            {
                "public" => ["public", "pub", "open", "export"],
                "private" => ["private", "fileprivate"],
                _ => [filter],
            };

            foreach (var alias in aliases)
            {
                if (!expanded.Contains(alias, StringComparer.Ordinal))
                    expanded.Add(alias);
            }
        }

        return expanded;
    }

    private static string BuildSameFilePrivateUseExclusionSql(string symbolAlias, string fileAlias, string visibilitySql, string startLineSql, string endLineSql)
    {
        return $@"
              AND NOT (
                  {fileAlias}.lang = 'csharp'
                  AND {visibilitySql} IN ('private', 'fileprivate')
                  AND {symbolAlias}.name <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM chunks same_file_chunk
                      WHERE same_file_chunk.file_id = {symbolAlias}.file_id
                        AND csharp_identifier_occurrence_count(same_file_chunk.content, {symbolAlias}.name) > 0
                        AND (
                            same_file_chunk.end_line < {startLineSql}
                            OR same_file_chunk.start_line > {endLineSql}
                            OR csharp_identifier_occurrence_count(same_file_chunk.content, {symbolAlias}.name) > 1
                        )
                  )
              )";
    }

    private string BuildCSharpPartialContainingTypeUseExclusionSql(string symbolAlias, string fileAlias, string visibilitySql)
    {
        var containerKindSql = GetSymbolColumnSql("container_kind", "''", symbolAlias);
        var containerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", containerNameSql, symbolAlias);
        var ownContainerNameSql = GetSymbolColumnSql("container_name", "''", "partial_own_type");
        var ownSignatureSql = GetSymbolColumnSql("signature", "''", "partial_own_type");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "partial_peer_type");
        var peerSignatureSql = GetSymbolColumnSql("signature", "''", "partial_peer_type");
        var ownQualifiedNameSql = $@"CASE
                            WHEN {ownContainerNameSql} <> '' THEN {ownContainerNameSql} || '.' || partial_own_type.name
                            ELSE partial_own_type.name
                        END";
        var peerQualifiedNameSql = $@"CASE
                            WHEN {peerContainerNameSql} <> '' THEN {peerContainerNameSql} || '.' || partial_peer_type.name
                            ELSE partial_peer_type.name
                        END";

        return $@"
              AND NOT (
                  {fileAlias}.lang = 'csharp'
                  AND {visibilitySql} IN ('private', 'fileprivate')
                  AND {symbolAlias}.name <> ''
                  AND {containerKindSql} IN ('class', 'struct', 'interface')
                  AND {containerNameSql} <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM symbols partial_own_type
                      WHERE partial_own_type.file_id = {symbolAlias}.file_id
                        AND partial_own_type.kind = {containerKindSql}
                        AND partial_own_type.name = {containerNameSql}
                        AND lower({ownSignatureSql}) LIKE '%partial%'
                        AND (
                            {containerQualifiedNameSql} = ''
                            OR {containerQualifiedNameSql} = partial_own_type.name
                            OR {containerQualifiedNameSql} = {ownQualifiedNameSql}
                        )
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM symbols partial_peer_type
                      JOIN files partial_peer_file ON partial_peer_file.id = partial_peer_type.file_id
                      JOIN chunks partial_peer_chunk ON partial_peer_chunk.file_id = partial_peer_type.file_id
                      WHERE partial_peer_file.lang = 'csharp'
                        AND partial_peer_type.file_id <> {symbolAlias}.file_id
                        AND partial_peer_type.kind = {containerKindSql}
                        AND partial_peer_type.name = {containerNameSql}
                        AND lower({peerSignatureSql}) LIKE '%partial%'
                        AND (
                            {containerQualifiedNameSql} = ''
                            OR {containerQualifiedNameSql} = partial_peer_type.name
                            OR {containerQualifiedNameSql} = {peerQualifiedNameSql}
                        )
                        AND csharp_identifier_occurrence_count(partial_peer_chunk.content, {symbolAlias}.name) > 0
                      LIMIT 1
                  )
              )";
    }

    private static string GetHotspotReferenceWeightSql(string referenceKindSql) => $@"
        CASE {referenceKindSql}
            WHEN 'call' THEN 1.0
            WHEN 'instantiate' THEN 1.0
            WHEN 'subscribe' THEN 0.3
            WHEN 'friend' THEN 0.3
            WHEN 'type_reference' THEN 0.1
            ELSE 0.0
        END";

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
    public List<SymbolResult> SearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return SearchSymbols(normalizedQuery == null ? null : new[] { normalizedQuery }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, sortMode);
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

    public QueryCountResult CountSearchSymbolsTotal(string? query = null, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        return CountSearchSymbolsTotal(query == null ? null : new[] { NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact) ?? query ?? string.Empty }, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
    }

    public QueryCountResult CountSearchSymbolsTotal(IReadOnlyList<string>? queries, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        using var cmd = _conn.CreateCommand();

        var sql = @"
            SELECT COUNT(*), COUNT(DISTINCT path)
            FROM (
                SELECT f.path AS path
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
        sql += ")";

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
    public List<SymbolResult> SearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name)
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
            var perName = new List<List<SymbolResult>>(validQueries.Count);
            foreach (var q in validQueries)
                perName.Add(SearchSymbols(new[] { q! }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, sortMode));

            var seen = new HashSet<(string Path, int Line, string Name, string Kind)>();
            var merged = new List<SymbolResult>();
            var cursors = new int[perName.Count];
            bool advanced;
            do
            {
                advanced = false;
                for (int i = 0; i < perName.Count && merged.Count < limit; i++)
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
            } while (advanced && merged.Count < limit);
            return merged;
        }

        using var cmd = _conn.CreateCommand();

        var startLineSql = GetSymbolColumnSql("start_line", "s.line");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line");
        var bodyStartLineSql = GetSymbolColumnSql("body_start_line");
        var bodyEndLineSql = GetSymbolColumnSql("body_end_line");
        var signatureSql = GetSymbolColumnSql("signature");
        var containerKindSql = GetSymbolColumnSql("container_kind");
        var containerNameSql = GetSymbolColumnSql("container_name");
        var visibilitySql = GetSymbolColumnSql("visibility");
        var returnTypeSql = GetSymbolColumnSql("return_type");
        var startColumnSql = GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)");
        var sizeLinesSql = $"CASE WHEN ({endLineSql}) >= ({startLineSql}) THEN ({endLineSql}) - ({startLineSql}) + 1 ELSE 1 END";
        var includeRankSignals = sortMode != SymbolSortMode.Name && _hasReferencesTable;
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
                GROUP BY rf.lang, sr.symbol_name
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
                GROUP BY sr.file_id, sr.symbol_name
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
                GROUP BY df.lang, ds.name
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

        var sql = $@"
            SELECT f.path, f.lang, s.kind, {GetSymbolColumnSql("sub_kind")} AS sub_kind, s.name, s.line,
                   {startLineSql} AS start_line,
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
                   {complexityScoreSql} AS complexity_score
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
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        var exactNameOrderSql = "CASE " +
            "WHEN @preferLiteralExactMatch = 1 AND s.name = @rawQuery THEN 0 " +
            "WHEN @preferLiteralNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name(s.name) = @rawQueryNormalized THEN 1 " +
            "WHEN @preferCaseInsensitiveExactMatch = 1 AND s.name = @rawQuery COLLATE NOCASE THEN 2 " +
            "WHEN @preferCaseInsensitiveNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name_folded(s.name) = @rawQueryNormalizedFolded THEN 3 " +
            "WHEN @preferCaseInsensitiveSqlLeafMatch = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @rawQueryLeafFolded THEN 4 " +
            "ELSE 5 END";
        sql += BuildSymbolSortOrderBy(sortMode, exactNameOrderSql, referenceCountSql, hotspotScoreSql, rankingReferenceScoreSql, rankingHotspotScoreSql, sizeLinesSql, complexityScoreSql, startColumnSql);
        sql += " LIMIT @limit";

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
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);

        var includeRankingMetadata = sortMode != SymbolSortMode.Name;
        var sortModeName = sortMode.ToString().ToLowerInvariant();
        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Kind = reader.GetString(2),
                SubKind = GetNullableString(reader, 3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                EndLine = GetInt32OrFallback(reader, 7, 5),
                BodyStartLine = GetNullableInt32(reader, 8),
                BodyEndLine = GetNullableInt32(reader, 9),
                Signature = GetNullableString(reader, 10),
                ContainerKind = GetNullableString(reader, 11),
                ContainerName = GetNullableString(reader, 12),
                Visibility = GetNullableString(reader, 13),
                ReturnType = GetNullableString(reader, 14),
                SortMode = includeRankingMetadata ? sortModeName : null,
                ReferenceCount = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(15)) : null,
                HotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(16), 3) : null,
                RankingReferenceScore = includeRankingMetadata ? Math.Round(reader.GetDouble(17), 3) : null,
                RankingHotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(18), 3) : null,
                GenericNamePenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(19), 3) : null,
                StructuralRankPenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(20), 3) : null,
                DefinitionSites = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(21)) : null,
                SizeLines = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(22)) : null,
                ComplexityScore = includeRankingMetadata ? Math.Round(reader.GetDouble(23), 3) : null,
            });
        }
        return results;
    }

    private static string GetGenericHotspotNamePenaltySql(string nameSql)
        => $"CASE WHEN lower({nameSql}) IN {GenericHotspotNamesSql} THEN {GenericHotspotNamePenaltySqlLiteral} ELSE 1.0 END";

    private static string GetGenericSymbolRankNamePenaltySql(string nameSql)
        => $"CASE WHEN lower({nameSql}) IN {GenericSymbolRankNamesSql} THEN {GenericSymbolRankNamePenaltySqlLiteral} ELSE 1.0 END";

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

    /// <summary>
    /// Resolve symbol definitions with reconstructed excerpts.
    /// シンボル定義を抜粋付きで解決する。
    /// </summary>
    public List<DefinitionResult> GetDefinitions(string query, int limit = 20, string? kind = null, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, int? bodyStartLine = null, int? bodyLineCount = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        var symbols = SearchSymbols(query, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
        var results = new List<DefinitionResult>();

        foreach (var symbol in symbols)
        {
            var definition = BuildDefinitionResult(symbol, includeBody, bodyStartLine, bodyLineCount);
            if (definition != null)
                results.Add(definition);
        }

        return results;
    }

    private DefinitionResult? BuildDefinitionResult(
        SymbolResult symbol,
        bool includeBody,
        int? bodyStartLine = null,
        int? bodyLineCount = null)
    {
        var definitionExcerpt = GetExcerpt(symbol.Path, symbol.StartLine, symbol.EndLine);
        if (definitionExcerpt == null)
            return null;

        string? bodyContent = null;
        int? bodyContentStartLine = null;
        int? bodyContentEndLine = null;
        int? bodyContentNextStartLine = null;
        var bodyContentTruncated = false;
        int? bodyRequestedStartLine = null;
        int? bodyRequestedEndLine = null;
        int? bodyEffectiveStartLine = null;
        int? bodyEffectiveEndLine = null;
        var bodyContentTruncationReasons = new List<string>();
        ExcerptRecoveryHint? bodyContentRecovery = null;
        if (includeBody && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
        {
            var requestedBodyLines = Math.Clamp(
                bodyLineCount ?? DefinitionBodyMaxLines,
                1,
                DefinitionBodyMaxRequestedLines);
            var effectiveBodyStartLine = Math.Clamp(
                bodyStartLine ?? symbol.BodyStartLine.Value,
                symbol.BodyStartLine.Value,
                symbol.BodyEndLine.Value);
            var cappedBodyEndLine = Math.Min(
                symbol.BodyEndLine.Value,
                effectiveBodyStartLine + requestedBodyLines - 1);
            var bodyExcerpt = GetExcerpt(symbol.Path, effectiveBodyStartLine, cappedBodyEndLine);
            if (bodyExcerpt != null)
            {
                bodyRequestedStartLine = symbol.BodyStartLine.Value;
                bodyRequestedEndLine = symbol.BodyEndLine.Value;
                bodyEffectiveStartLine = bodyExcerpt.StartLine;
                bodyEffectiveEndLine = bodyExcerpt.EndLine;
                bodyContent = bodyExcerpt.Content;
                bodyContentStartLine = bodyExcerpt.StartLine;
                bodyContentEndLine = bodyExcerpt.EndLine;
                bodyContentTruncationReasons.AddRange(bodyExcerpt.ContentTruncationReasons);
                bodyContentRecovery = bodyExcerpt.ContentRecovery;
                if (cappedBodyEndLine < symbol.BodyEndLine.Value)
                {
                    bodyContentTruncated = true;
                    AddBodyContentTruncationReason(bodyContentTruncationReasons, "body_line_cap");
                    var recoveryStartLine = cappedBodyEndLine + 1;
                    var recoveryEndLine = Math.Min(symbol.BodyEndLine.Value, recoveryStartLine + DefinitionBodyMaxLines - 1);
                    bodyContentNextStartLine = recoveryStartLine;
                    bodyContentRecovery ??= FileExcerptResult.CreateRecoveryHint(symbol.Path, recoveryStartLine, recoveryEndLine);
                }
                bodyContentTruncated |= bodyExcerpt.ContentTruncated;
                var byteClamp = ClampDefinitionBodyBytes(bodyContent);
                bodyContent = byteClamp.Content;
                if (byteClamp.Truncated)
                {
                    bodyContentTruncated = true;
                    AddBodyContentTruncationReason(bodyContentTruncationReasons, "body_byte_cap");
                    if (byteClamp.ReturnedLineCount > 0)
                    {
                        bodyContentEndLine = Math.Min(
                            bodyExcerpt.EndLine,
                            bodyExcerpt.StartLine + byteClamp.ReturnedLineCount - 1);
                        var nextStartLine = bodyContentEndLine.Value + 1;
                        if (nextStartLine <= symbol.BodyEndLine.Value)
                            bodyContentNextStartLine = nextStartLine;
                    }
                    else
                    {
                        bodyContentEndLine = bodyExcerpt.StartLine;
                        var nextStartLine = bodyExcerpt.StartLine + 1;
                        if (nextStartLine <= symbol.BodyEndLine.Value)
                            bodyContentNextStartLine = nextStartLine;
                    }
                    bodyContentRecovery = FileExcerptResult.CreateRecoveryHint(symbol.Path, bodyExcerpt.StartLine, bodyExcerpt.EndLine);
                }
            }
        }

        return new DefinitionResult
        {
            Path = symbol.Path,
            Lang = symbol.Lang,
            Kind = symbol.Kind,
            SubKind = symbol.SubKind,
            Name = symbol.Name,
            Line = symbol.Line,
            StartLine = symbol.StartLine,
            EndLine = symbol.EndLine,
            BodyStartLine = symbol.BodyStartLine,
            BodyEndLine = symbol.BodyEndLine,
            Signature = symbol.Signature,
            ContainerKind = symbol.ContainerKind,
            ContainerName = symbol.ContainerName,
            Visibility = symbol.Visibility,
            ReturnType = symbol.ReturnType,
            Disambiguator = BuildDefinitionDisambiguator(symbol),
            Content = definitionExcerpt.Content,
            BodyContent = bodyContent,
            BodyContentStartLine = bodyContentStartLine,
            BodyContentEndLine = bodyContentEndLine,
            BodyContentNextStartLine = bodyContentNextStartLine,
            BodyContentTruncated = bodyContentTruncated,
            BodyRequestedStartLine = bodyRequestedStartLine,
            BodyRequestedEndLine = bodyRequestedEndLine,
            BodyEffectiveStartLine = bodyEffectiveStartLine,
            BodyEffectiveEndLine = bodyEffectiveEndLine,
            BodyContentTruncationReasons = bodyContentTruncationReasons.Count > 0 ? bodyContentTruncationReasons : null,
            BodyContentRecovery = bodyContentRecovery,
            Complexity = bodyContent != null && !bodyContentTruncated
                ? SymbolExtractor.EstimateComplexity(bodyContent)
                : null,
        };
    }

    private static void AddBodyContentTruncationReason(List<string> reasons, string reason)
    {
        if (!reasons.Any(existing => string.Equals(existing, reason, StringComparison.Ordinal)))
            reasons.Add(reason);
    }

    private static (string Content, bool Truncated, int ReturnedLineCount) ClampDefinitionBodyBytes(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= DefinitionBodyMaxBytes)
            return (content, false, CountReturnedBodyLines(content));

        var byteCount = DefinitionBodyMaxBytes;
        while (byteCount > 0 && IsUtf8ContinuationByte(bytes[byteCount]))
            byteCount--;

        var clamped = Encoding.UTF8.GetString(bytes, 0, byteCount);
        return (
            clamped,
            true,
            CountReturnedBodyLines(clamped));
    }

    private static bool IsUtf8ContinuationByte(byte value) => (value & 0xC0) == 0x80;

    private static int CountReturnedBodyLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var lineBreaks = 0;
        foreach (var ch in content)
        {
            if (ch == '\n')
                lineBreaks++;
        }

        return content[^1] == '\n'
            ? lineBreaks
            : lineBreaks + 1;
    }

    private static string? BuildDefinitionDisambiguator(SymbolResult symbol)
    {
        if (!string.Equals(symbol.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return null;

        var signature = symbol.Signature;
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        if (signature.Contains(" partial ", StringComparison.Ordinal)
            || signature.Contains("partial class ", StringComparison.Ordinal)
            || signature.Contains("partial struct ", StringComparison.Ordinal)
            || signature.Contains("partial interface ", StringComparison.Ordinal))
            return "partial-" + (symbol.Kind ?? "definition");

        if (signature.Contains("(this ", StringComparison.Ordinal)
            || signature.Contains(", this ", StringComparison.Ordinal))
        {
            var receiver = ExtractExtensionReceiver(signature);
            return receiver == null ? "extension-method" : $"extension-method-on({receiver})";
        }

        if (symbol.Kind == "function")
        {
            var parameters = ExtractParameterTypeList(signature);
            if (parameters != null)
                return $"overload({parameters})";
        }

        return null;
    }

    private static string? ExtractExtensionReceiver(string signature)
    {
        var parameters = ExtractParameters(signature);
        if (parameters == null)
            return null;

        var firstParameter = parameters.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        const string ThisPrefix = "this ";
        if (!firstParameter.StartsWith(ThisPrefix, StringComparison.Ordinal))
            return null;

        var withoutThis = firstParameter[ThisPrefix.Length..].Trim();
        var parts = withoutThis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static string? ExtractParameterTypeList(string signature)
    {
        var parameters = ExtractParameters(signature);
        if (parameters == null)
            return null;
        if (string.IsNullOrWhiteSpace(parameters))
            return "";

        var types = parameters
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ExtractParameterType)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .ToList();
        return types.Count > 0 ? string.Join(", ", types) : null;
    }

    private static string? ExtractParameters(string signature)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;
        return signature.Substring(open + 1, close - open - 1).Trim();
    }

    private static string ExtractParameterType(string parameter)
    {
        var tokens = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return string.Empty;
        var start = tokens[0] is "this" or "ref" or "out" or "in" or "params" ? 1 : 0;
        if (start >= tokens.Length)
            return string.Empty;
        var end = Math.Max(start + 1, tokens.Length - 1);
        return string.Join(" ", tokens[start..end]);
    }

    public QueryCountResult CountDefinitionsTotal(string query, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        using var cmd = _conn.CreateCommand();

        var sql = $@"
            SELECT COUNT(*), COUNT(DISTINCT path)
            FROM (
                SELECT f.path AS path
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE 1=1";

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(normalizedQuery, lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(normalizedQuery) : default;
            var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedQuery);
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(normalizedQuery)
                ? BuildQualifiedSymbolMatchSql("query", _foldReady)
                : null;
            sql += exact
                ? rustQualifiedParts.QualifiedPath != null
                    ? _foldReady
                        ? " AND ((s.container_qualified_name = @queryRustContainer COLLATE NOCASE OR s.container_name = @queryRustContainer COLLATE NOCASE) AND s.name_folded = @queryRustLeafFolded)"
                        : " AND ((s.container_qualified_name = @queryRustContainer COLLATE NOCASE OR s.container_name = @queryRustContainer COLLATE NOCASE) AND s.name = @queryRustLeaf COLLATE NOCASE)"
                    : _foldReady
                        ? allowLeafFallback
                            ? " AND (s.name_folded = @query OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                            : $" AND (s.name_folded = @query OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                        : allowLeafFallback
                            ? " AND (s.name = @query COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @queryLeaf COLLATE NOCASE)))"
                            : $" AND (s.name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                : $" AND (s.name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @queryNormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
        }
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += $@"
                  AND EXISTS (
                      SELECT 1
                      FROM chunks c
                      WHERE c.file_id = s.file_id
                        AND c.end_line >= {GetSymbolColumnSql("start_line", "s.line")}
                        AND c.start_line <= {GetSymbolColumnSql("end_line", "s.line")}
                  )
            )";

        cmd.CommandText = sql;
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(normalizedQuery, lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(normalizedQuery) : default;
            var paramValue = !exact
                ? $"%{EscapeLikeQuery(normalizedQuery)}%"
                : _foldReady
                    ? NameFold.Fold(normalizedQuery) ?? normalizedQuery
                    : normalizedQuery;
            SqliteCommandPolicy.Add(cmd, "@query", paramValue);
            SqliteCommandPolicy.Add(cmd, "@queryNormalized", SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(normalizedQuery)) ?? SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryLeaf", SqlNameResolver.GetLeafName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(normalizedQuery)) ?? SqlNameResolver.GetLeafName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@querySegmentCount", SqlNameResolver.GetSegmentCount(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryNormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(normalizedQuery))}%");
            if (SqlNameResolver.HasQualifier(normalizedQuery))
                AddQualifiedSymbolQueryParameters(cmd, "query", normalizedQuery);
            if (rustQualifiedParts.QualifiedPath != null)
            {
                SqliteCommandPolicy.Add(cmd, "@queryRustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@queryRustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@queryRustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
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

    /// <summary>
    /// Get nearby symbols in the same file ordered by proximity to a focus line.
    /// 同一ファイル内の近傍シンボルを、注目行からの近さ順で取得する。
    /// </summary>
    public List<SymbolResult> GetNearbySymbols(string path, int focusLine, int limit = 10, string? excludeName = null, int? excludeStartLine = null)
    {
        using var cmd = _conn.CreateCommand();

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
            WHERE f.path = @path";

        if (excludeName != null && excludeStartLine != null)
            sql += " AND NOT (s.name = @excludeName AND " + GetSymbolColumnSql("start_line", "s.line") + " = @excludeStartLine)";

        sql += " ORDER BY CASE WHEN @focusLine BETWEEN " + GetSymbolColumnSql("start_line", "s.line") + " AND " + GetSymbolColumnSql("end_line", "s.line") + " THEN 0 ELSE abs(" + GetSymbolColumnSql("start_line", "s.line") + " - @focusLine) END, " + GetSymbolColumnSql("start_line", "s.line") + " LIMIT @limit";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@focusLine", focusLine);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        if (excludeName != null && excludeStartLine != null)
        {
            SqliteCommandPolicy.Add(cmd, "@excludeName", excludeName);
            SqliteCommandPolicy.Add(cmd, "@excludeStartLine", excludeStartLine.Value);
        }

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Kind = reader.GetString(2),
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = GetInt32OrFallback(reader, 5, 4),
                EndLine = GetInt32OrFallback(reader, 6, 4),
                BodyStartLine = GetNullableInt32(reader, 7),
                BodyEndLine = GetNullableInt32(reader, 8),
                Signature = GetNullableString(reader, 9),
                ContainerKind = GetNullableString(reader, 10),
                ContainerName = GetNullableString(reader, 11),
                Visibility = GetNullableString(reader, 12),
                ReturnType = GetNullableString(reader, 13),
            });
        }

        return results;
    }

    public SymbolAnalysisResult AnalyzeFileLine(
        string path,
        int line,
        int limit = 10,
        string? lang = null,
        bool includeBody = false,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth,
        int? bodyStartLine = null,
        int? bodyLineCount = null,
        string? kind = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        using var txn = _conn.BeginTransaction(deferred: true);

        var query = $"{path}:{line.ToString(CultureInfo.InvariantCulture)}";
        var file = GetFileByPath(path);
        var freshness = GetWorkspaceFreshness();
        var graphLanguage = lang ?? file?.Lang;
        List<SymbolResult> symbolsAtLine = file == null
            ? []
            : GetSymbolsAtLine(path, line, Math.Max(limit, 1), kind, lang);
        var primarySymbol = symbolsAtLine.FirstOrDefault();
        var primaryLineDefinition = primarySymbol == null
            ? null
            : BuildDefinitionResult(primarySymbol, includeBody, bodyStartLine, bodyLineCount);
        List<DefinitionResult> definitions = primaryLineDefinition == null ? [] : [primaryLineDefinition];
        var primaryDefinition = definitions.FirstOrDefault();
        var hasSupportedGraphDefinition = primaryDefinition != null
            && ReferenceExtractor.SupportsSymbolGraph(primaryDefinition.Lang, primaryDefinition.Kind, primaryDefinition.ContainerKind) == true;
        var baseGraphSupported = graphLanguage == null
            ? (bool?)null
            : ReferenceExtractor.SupportsLanguage(graphLanguage);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReasonWithUnsupportedEnumMemberGap(
            graphLanguage,
            baseGraphSupported,
            hasUnsupportedEnumMember: false,
            hasSupportedGraphDefinition);
        var references = primaryDefinition != null
            ? SearchReferences(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, maxLineWidth)
            : [];
        var callers = primaryDefinition != null
            ? GetCallers(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
            : [];
        var callees = primaryDefinition != null
            ? GetCallees(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
            : [];
        var nearbySymbols = file != null
            ? GetNearbySymbols(
                path,
                line,
                Math.Min(limit, 10),
                primaryDefinition?.Name,
                primaryDefinition?.StartLine)
            : [];
        ApplyQueryOutputSignatureLimits(definitions);
        ApplyQueryOutputSignatureLimits(nearbySymbols);

        var result = new SymbolAnalysisResult
        {
            Query = query,
            File = file,
            WorkspaceIndexedAt = freshness.IndexedAt,
            WorkspaceLatestModified = freshness.LatestModified,
            GraphLanguage = graphLanguage,
            GraphSupported = baseGraphSupported,
            GraphSupportReason = graphSupportReason,
            Definitions = definitions,
            NearbySymbols = nearbySymbols,
            References = references,
            Callers = callers,
            Callees = callees,
            GraphTableAvailable = _hasReferencesTable,
        };
        txn.Commit();
        return result;
    }

    private List<SymbolResult> GetSymbolsAtLine(string path, int line, int limit, string? kind, string? lang)
    {
        using var cmd = _conn.CreateCommand();

        var startLineSql = GetSymbolColumnSql("start_line", "s.line");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line");
        var bodyStartLineSql = GetSymbolColumnSql("body_start_line");
        var bodyEndLineSql = GetSymbolColumnSql("body_end_line");
        var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {startLineSql} AS start_line,
                   {endLineSql} AS end_line,
                   {bodyStartLineSql} AS body_start_line,
                   {bodyEndLineSql} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND @line BETWEEN {startLineSql} AND {endLineSql}";
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += " AND f.lang = @lang";
        sql += $@"
            ORDER BY
                CASE WHEN {startLineSql} = @line THEN 0 ELSE 1 END,
                ({endLineSql} - {startLineSql}),
                CASE WHEN {bodyStartLineSql} IS NOT NULL
                       AND {bodyEndLineSql} IS NOT NULL
                       AND @line BETWEEN {bodyStartLineSql} AND {bodyEndLineSql}
                     THEN 0 ELSE 1 END,
                {startLineSql} DESC
            LIMIT @limit";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@line", line);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(ReadSymbolResult(reader));

        return results;
    }

    private static SymbolResult ReadSymbolResult(SqliteDataReader reader)
        => new()
        {
            Path = reader.GetString(0),
            Lang = GetNullableString(reader, 1),
            Kind = reader.GetString(2),
            Name = reader.GetString(3),
            Line = reader.GetInt32(4),
            StartLine = GetInt32OrFallback(reader, 5, 4),
            EndLine = GetInt32OrFallback(reader, 6, 4),
            BodyStartLine = GetNullableInt32(reader, 7),
            BodyEndLine = GetNullableInt32(reader, 8),
            Signature = GetNullableString(reader, 9),
            ContainerKind = GetNullableString(reader, 10),
            ContainerName = GetNullableString(reader, 11),
            Visibility = GetNullableString(reader, 12),
            ReturnType = GetNullableString(reader, 13),
        };

    /// <summary>
    /// Bundle definition, graph, and local file context for one symbol query.
    /// 単一シンボルクエリ向けに、定義・グラフ・ローカル文脈をまとめて返す。
    /// </summary>
    public SymbolAnalysisResult AnalyzeSymbol(string query, int limit = 10, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? bodyStartLine = null, int? bodyLineCount = null, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
        {
            var workspaceFreshness = GetWorkspaceFreshness();
            return new SymbolAnalysisResult
            {
                Query = query,
                WorkspaceIndexedAt = workspaceFreshness.IndexedAt,
                WorkspaceLatestModified = workspaceFreshness.LatestModified,
                GraphTableAvailable = _hasReferencesTable,
            };
        }

        lang = DbReader.NormalizeQueryLanguage(lang);
        var normalizedQuery = NormalizeSymbolSearchQuery(query, lang) ?? query;
        // Propagate `exact` to every bundled sub-query so the one-round-trip AI workflow
        // (`inspect` / MCP `analyze_symbol`) keeps the same precision contract as the leaf
        // commands. Without this, `inspect Run --exact` would still pull RunAsync/RunImpact
        // into references / callers / callees. See codex review of #83.
        // `exact` は bundle 内のすべての sub-query に伝播させ、leaf コマンドと precision を揃える。
        //
        // Issue #180: wrap the multi-statement bundle in one DEFERRED transaction so every
        // sub-query (definitions / file metadata / freshness / references / callers /
        // callees / nearby symbols) resolves against the same WAL snapshot. Without this,
        // a concurrent writer mid-indexing can make the bundle report callers for an old
        // symbol layout alongside a file row that already reflects the new one.
        // Issue #180: bundle 内の全 sub-query を 1 つの DEFERRED transaction でまとめ、
        // definitions / file / freshness / references / callers / callees / nearby symbols
        // が同じ WAL snapshot を参照するようにする。
        using var txn = _conn.BeginTransaction(deferred: true);
        var definitionLimit = Math.Min(limit, 5);
        var definitions = PrioritizeSourceDefinitions(GetDefinitions(normalizedQuery, definitionLimit, kind: kind, lang, includeBody, pathPatterns, excludePathPatterns, excludeTests, since: null, exact, bodyStartLine: bodyStartLine, bodyLineCount: bodyLineCount));
        DefinitionResult? primaryDefinition = definitions
            .FirstOrDefault(definition => ReferenceExtractor.SupportsLanguage(definition.Lang) == true && !IsCSharpEnumMemberDefinition(definition))
            ?? definitions.FirstOrDefault(definition => ReferenceExtractor.SupportsLanguage(definition.Lang) == true)
            ?? definitions.FirstOrDefault();
        if (exact)
            definitions = BuildAnalysisDefinitions(primaryDefinition, definitions, definitionLimit);
        var file = primaryDefinition != null ? GetFileByPath(primaryDefinition.Path) : null;
        var freshness = GetWorkspaceFreshness();
        var hasGraphApplicableFiles = HasGraphApplicableFiles(lang, pathPatterns, excludePathPatterns, excludeTests);
        var graphLanguage = lang ?? file?.Lang;
        const bool hasUnsupportedEnumMember = false;
        var hasSupportedGraphDefinition = exact
            ? HasExactGraphSupportedDefinition(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests)
            : definitions.Any(definition => ReferenceExtractor.SupportsSymbolGraph(definition.Lang, definition.Kind, definition.ContainerKind) == true);
        var baseGraphSupported = graphLanguage == null
            ? (bool?)null
            : ReferenceExtractor.SupportsLanguage(graphLanguage);
        bool? graphSupported = baseGraphSupported;
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReasonWithUnsupportedEnumMemberGap(
            graphLanguage,
            graphSupported,
            hasUnsupportedEnumMember,
            hasSupportedGraphDefinition);
        var unsupportedSymbolKind = hasUnsupportedEnumMember ? "enum_member" : null;
        var references = SearchReferences(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact, maxLineWidth);
        var callers = GetCallers(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact);
        var callees = GetCallees(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact);
        var sqlGraphRelevant = IsSqlLanguage(lang)
            || IsSqlLanguage(graphLanguage)
            || ContainsSqlLanguage(definitions.Select(definition => definition.Lang))
            || ContainsSqlLanguage(references.Select(reference => reference.Lang))
            || ContainsSqlLanguage(callers.Select(caller => caller.Lang))
            || ContainsSqlLanguage(callees.Select(callee => callee.Lang));
        var exactSignal = exact
            ? GetAnalyzeSymbolExactQuerySignal(
                includeGraphSignal: hasGraphApplicableFiles,
                includeSqlGraphContractSignal: sqlGraphRelevant,
                lang: lang,
                pathPatterns: pathPatterns,
                excludePathPatterns: excludePathPatterns,
                excludeTests: excludeTests)
            : (ExactQuerySignal?)null;
        var relaxedSymbols = exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
            ? SearchSymbols(normalizedQuery, Math.Max(limit, 5), kind: null, lang, pathPatterns, excludePathPatterns, excludeTests, since: null, exact: false)
            : null;
        var exactZeroHint = exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
            ? ExactZeroHintResult.FromRelaxedMatches(
                relaxedSymbols!.Count,
                relaxedSymbols.Select(result => result.Name))
            : null;
        var nearbySymbols = primaryDefinition != null
            ? GetNearbySymbols(primaryDefinition.Path, primaryDefinition.StartLine, Math.Min(limit, 10), primaryDefinition.Name, primaryDefinition.StartLine)
            : [];
        ApplyQueryOutputSignatureLimits(definitions);
        ApplyQueryOutputSignatureLimits(nearbySymbols);

        var result = new SymbolAnalysisResult
        {
            Query = query,
            File = file,
            WorkspaceIndexedAt = freshness.IndexedAt,
            WorkspaceLatestModified = freshness.LatestModified,
            GraphLanguage = graphLanguage,
            GraphSupported = graphSupported,
            GraphSupportReason = graphSupportReason,
            GraphDegraded = hasUnsupportedEnumMember ? true : null,
            UnsupportedSymbolKind = unsupportedSymbolKind,
            Definitions = definitions,
            NearbySymbols = nearbySymbols,
            References = references,
            Callers = callers,
            Callees = callees,
            GraphTableAvailable = _hasReferencesTable,
            ExactZeroHint = exactZeroHint,
            ExactIndexAvailable = exactSignal?.ExactIndexAvailable,
            ExactHasMissingIndex = exactSignal?.HasMissingIndex,
            ExactHasMissingTable = exactSignal?.HasMissingTable,
            DegradedReason = exactSignal?.DegradedReason,
        };
        txn.Commit();
        return result;
    }

    private static List<DefinitionResult> PrioritizeSourceDefinitions(List<DefinitionResult> definitions)
    {
        if (definitions.Count <= 1)
            return definitions;

        return definitions
            .Select((definition, index) => (definition, index))
            .OrderBy(item => SearchMatchClassifier.IsLikelyTestPath(item.definition.Path) ? 1 : 0)
            .ThenBy(item => item.index)
            .Select(item => item.definition)
            .ToList();
    }

    public HashSet<string> GetUnsupportedExactGraphSymbolKinds(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (HasExactUnsupportedCSharpEnumMember(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests))
            kinds.Add("enum_member");
        return kinds;
    }

    public bool HasExactUnsupportedCSharpEnumMember(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return false;
    }

    public bool HasExactGraphSupportedDefinition(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return GetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests) != null;
    }

    public string? GetExactGraphSupportedDefinitionLanguage(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return TryGetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests, preferNonEnumMember: true)
            ?? TryGetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests, preferNonEnumMember: false);
    }

    private string? TryGetExactGraphSupportedDefinitionLanguage(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool preferNonEnumMember)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "supportedGraphLang");
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedQuery);
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                : "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @queryLeaf COLLATE NOCASE)))"
                : "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE))";

        var sql = @"
            SELECT f.lang
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE " + nameCondition + @"
              AND " + supportedLangFilter;
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (preferNonEnumMember)
            sql += " AND NOT (f.lang = 'csharp' AND s.kind = 'enum' AND "
                + GetSymbolColumnSql("container_kind", "''")
                + " = 'enum')";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@queryRaw", query);
        SqliteCommandPolicy.Add(cmd, "@queryFolded", NameFold.Fold(query) ?? query);
        SqliteCommandPolicy.Add(cmd, "@queryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@queryLeaf", SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@querySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : (string?)value;
    }

    public bool HasFilteredCSharpEnumSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (lang != null && !string.Equals(lang, "csharp", StringComparison.Ordinal))
            return false;
        if (kind != null && !string.Equals(kind, "enum", StringComparison.Ordinal))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = @"
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'enum'";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";
        cmd.CommandText = sql;
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var value = cmd.ExecuteScalar();
        return value != null && value != DBNull.Value;
    }

    private static bool IsCSharpEnumMemberDefinition(DefinitionResult definition)
    {
        return string.Equals(definition.Lang, "csharp", StringComparison.Ordinal)
            && string.Equals(definition.Kind, "enum", StringComparison.Ordinal)
            && string.Equals(definition.ContainerKind, "enum", StringComparison.Ordinal);
    }

    private static List<DefinitionResult> BuildAnalysisDefinitions(DefinitionResult? primaryDefinition, List<DefinitionResult> definitions, int limit)
    {
        if (primaryDefinition == null || limit <= 0)
            return definitions;

        var ordered = definitions
            .Where(definition => !IsSameDefinition(definition, primaryDefinition))
            .Prepend(primaryDefinition)
            .Take(limit)
            .ToList();
        return ordered;
    }

    private static bool IsSameDefinition(DefinitionResult left, DefinitionResult right)
    {
        return string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && left.StartLine == right.StartLine
            && left.EndLine == right.EndLine
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal);
    }

    private static void ApplyQueryOutputSignatureLimits(IEnumerable<SymbolResult> symbols)
    {
        foreach (var symbol in symbols)
            ApplyQueryOutputSignatureLimit(symbol);
    }

    private static void ApplyQueryOutputSignatureLimit(SymbolResult symbol)
    {
        if (!TryTruncateQueryOutputSignature(symbol.Signature, out var signature, out var originalLength))
            return;

        symbol.Signature = signature;
        symbol.SignatureTruncated = true;
        symbol.SignatureOriginalLength = originalLength;
    }

    private static bool TryTruncateQueryOutputSignature(string? signature, out string? truncatedSignature, out int? originalLength)
    {
        truncatedSignature = signature;
        originalLength = null;
        if (signature == null || signature.Length <= QueryOutputSignatureMaxChars)
            return false;

        originalLength = signature.Length;
        truncatedSignature = signature[..(QueryOutputSignatureMaxChars - QueryOutputSignatureTruncationSuffix.Length)]
            + QueryOutputSignatureTruncationSuffix;
        return true;
    }

    /// <summary>
    /// Find symbols with the most references (hotspots — heavily used code).
    /// Counts total reference volume across the codebase for names that stay unambiguous within
    /// the active language/kind candidate set. Path and test filters only decide which logical
    /// target rows are returned, not whether a name is considered globally ambiguous. When
    /// multiple logical targets still share the same name, falls back to conservative in-target
    /// file counts; rows that collapse to one logical target family (same container or top-level
    /// file) are grouped because bare-name references cannot disambiguate the true target symbol.
    /// 最も多く参照されるシンボルを検索する（ホットスポット — 多用されるコード）。
    /// active な言語/種別候補集合の中で名前が曖昧でないシンボルは codebase 全体の参照数を数える。
    /// path/test フィルタは返す logical target 行だけを絞り、名前の曖昧性判定には使わない。
    /// 複数の logical target が同名を共有する場合は bare-name 参照で真の対象を特定できないため
    /// 保守的な in-target file 件数へフォールバックし、1 つの logical target family に収まる行は集約する。
    /// </summary>
    public List<SymbolHotspotResult> GetSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var genericNamePenaltySql = GetGenericHotspotNamePenaltySql("gr.name");
        var sql = query.Sql + @"
            SELECT gr.name, rc.ref_count, rc.ref_score,
                   (rc.ref_score * (" + genericNamePenaltySql + @")) AS ranking_score,
                   (" + genericNamePenaltySql + @") AS generic_name_penalty,
                   gr.kind, gr.path, gr.lang, gr.line,
                   gr.visibility, gr.container_name
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0
            ORDER BY ranking_score DESC,
                     rc.ref_score DESC,
                     rc.ref_count DESC,
                     gr.path COLLATE BINARY ASC,
                     gr.line ASC,
                     gr.name COLLATE BINARY ASC,
                     gr.kind COLLATE BINARY ASC,
                     gr.symbol_id ASC
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);

        var results = new List<SymbolHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolHotspotResult
            {
                Symbol = new SymbolResult
                {
                    Name = reader.GetString(0),
                    Kind = reader.GetString(5),
                    Path = reader.GetString(6),
                    Lang = GetNullableString(reader, 7),
                    Line = reader.GetInt32(8),
                    Visibility = GetNullableString(reader, 9),
                    ContainerName = GetNullableString(reader, 10),
                },
                ReferenceCount = reader.GetInt32(1),
                ReferenceScore = reader.GetDouble(2),
                RankingScore = reader.GetDouble(3),
                GenericNamePenalty = reader.GetDouble(4),
            });
        }
        return results;
    }

    public List<FileHotspotResult> GetFileSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var genericNamePenaltySql = GetGenericHotspotNamePenaltySql("gr.name");
        var fileSymbolCountSql = "MAX(fsc.symbol_count)";
        var structuralRankPenaltySql = GetFileHotspotStructuralRankPenaltySql("COUNT(*)");
        var sql = query.Sql + @"
            SELECT gr.path,
                   gr.lang,
                   SUM(rc.ref_count) AS ref_count,
                   SUM(rc.ref_score) AS ref_score,
                   (SUM(rc.ref_score * (" + genericNamePenaltySql + @")) * (" + structuralRankPenaltySql + @")) AS ranking_score,
                   CASE
                       WHEN SUM(rc.ref_score) > 0
                           THEN SUM(rc.ref_score * (" + genericNamePenaltySql + @")) / SUM(rc.ref_score)
                       ELSE 1.0
                   END AS generic_name_penalty,
                   (" + structuralRankPenaltySql + @") AS structural_rank_penalty,
                   " + fileSymbolCountSql + @" AS symbol_count
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            JOIN file_symbol_counts fsc
              ON fsc.path = gr.path
             AND fsc.lang_key = COALESCE(gr.lang, '')
            WHERE rc.ref_count > 0
            GROUP BY gr.path, gr.lang
            ORDER BY ranking_score DESC,
                     ref_score DESC,
                     ref_count DESC,
                     gr.path COLLATE BINARY ASC
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);

        var results = new List<FileHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new FileHotspotResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                ReferenceCount = reader.GetInt32(2),
                ReferenceScore = reader.GetDouble(3),
                RankingScore = reader.GetDouble(4),
                GenericNamePenalty = reader.GetDouble(5),
                StructuralRankPenalty = reader.GetDouble(6),
                SymbolCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    private static string GetFileHotspotStructuralRankPenaltySql(string symbolCountSql)
        => $@"CASE
                WHEN {symbolCountSql} <= 2 THEN 0.1
                WHEN {symbolCountSql} <= 8 THEN 0.35
                ELSE 1.0
            END";

    public HotspotCountResult CountSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   COUNT(DISTINCT gr.path)
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    public HotspotCountResult CountFileSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   COUNT(*)
            FROM (
                SELECT gr.path,
                       gr.lang
                FROM grouped_rows gr
                JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
                WHERE rc.ref_count > 0
                GROUP BY gr.path, gr.lang
            ) file_groups";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    private SymbolHotspotRowsQuery BuildSymbolHotspotRowsQuery(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var containerNameSql = GetSymbolColumnSql("container_name");
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var familyKeySql = GetSymbolColumnSql("family_key");
        var hotspotFamilyLangs = _hotspotFamilyReadyLanguages
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var familyLangConditionSql = hotspotFamilyLangs.Count > 0
            ? $"f.lang IN ({string.Join(",", hotspotFamilyLangs.Select((_, i) => $"@hotspotFamilyLang{i}"))})"
            : "0";
        var familyTargetKeySql = hotspotFamilyLangs.Count > 0
            ? $@"CASE
                    WHEN {familyLangConditionSql}
                     AND COALESCE({familyKeySql}, '') <> ''
                        THEN 'family|' || COALESCE(f.lang, '') || '|' || COALESCE(s.kind, '') || '|' || {familyKeySql}
                    ELSE NULL
                END"
            : "NULL";
        var containerTargetKeySql = $@"CASE
                    WHEN COALESCE({containerQualifiedNameSql}, '') <> ''
                        THEN 'container|' || CAST(s.file_id AS TEXT) || '|' || COALESCE(s.kind, '') || '|' || {containerQualifiedNameSql}
                    ELSE NULL
                END";
        var csharpFunctionDefinitionGateSql = _symbolColumns.Contains("body_start_line")
            && _symbolColumns.Contains("body_end_line")
            && _symbolColumns.Contains("signature")
            && _symbolColumns.Contains("container_kind")
            ? @"
                  AND NOT (
                      f.lang = 'csharp'
                      AND s.kind = 'function'
                      AND s.container_kind = 'function'
                      AND (
                          (s.body_start_line IS NULL AND s.body_end_line IS NULL)
                          OR (s.container_kind = 'function' AND COALESCE(s.signature, '') LIKE '%.' || s.name || '(%')
                      )
                  )"
            : string.Empty;
        // Ambiguity is computed from the unscoped language/kind candidate set so `--path`
        // cannot hide an out-of-scope duplicate and accidentally promote a same-name symbol
        // back to codebase-wide counting. Cross-file grouping is allowed only when the
        // extractor persisted an authoritative family key on a DB that is stamped as fully
        // current for hotspot-family semantics (currently partial-type families). Same-file
        // same-container overloads can still share one conservative target key, but only
        // unique names or authoritative families may promote to codebase-wide counts.
        // 曖昧性は path 非依存の候補集合で判定し、`--path` で隠れた重複定義が一意扱いに
        // 戻ってしまうことを防ぐ。cross-file の集約は current な hotspot-family semantics で
        // fully-ready と判定された DB 上の正式な family key のみに限定し、same-file の
        // same-container overload は保守的な target として扱いつつ、codebase-wide 集計への
        // 昇格は一意名か authoritative family のみに限定する。
        var sql = $@"
            WITH all_candidate_symbols AS (
                SELECT s.id, s.file_id, s.name, s.kind, f.path, f.lang, s.line,
                       {GetSymbolColumnSql("visibility")} AS visibility,
                       {containerNameSql} AS container_name,
                       CASE
                           WHEN {familyTargetKeySql} IS NOT NULL
                               THEN {familyTargetKeySql}
                           WHEN {containerTargetKeySql} IS NOT NULL
                               THEN {containerTargetKeySql}
                           ELSE 'file|' || CAST(s.file_id AS TEXT)
                       END AS logical_target_key,
                       COALESCE({familyTargetKeySql}, {containerTargetKeySql}) AS count_safe_key
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.kind NOT IN ('import', 'namespace')" + csharpFunctionDefinitionGateSql;

        var graphLangs = ReferenceExtractor.GetSupportedLanguages().ToList();
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";
        if (kind != null)
            sql += " AND s.kind = @kind";
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);

        sql += @"
            ),
            name_cardinality AS (
                SELECT lang,
                       name,
                       COUNT(*) AS defs,
                       COUNT(DISTINCT logical_target_key) AS target_groups,
                       COUNT(DISTINCT count_safe_key) AS count_safe_groups,
                       COUNT(count_safe_key) AS count_safe_defs
                FROM all_candidate_symbols
                GROUP BY lang, name
            ),
            filtered_candidates AS (
                SELECT id,
                       file_id,
                       name,
                       kind,
                       path,
                       lang,
                       line,
                       visibility,
                       container_name,
                       logical_target_key
                FROM all_candidate_symbols
                WHERE 1 = 1";
        if (pathPatterns != null && pathPatterns.Count > 0)
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathColumnFilterPredicate("path", "pathPattern", i, pathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND NOT {BuildPathColumnFilterPredicate("path", "excludePathPattern", i, excludePathPatterns[i])}";
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", "path")}";
        sql += @"
            ),
            grouped_candidates AS (
                SELECT MIN(id) AS symbol_id,
                       name,
                       kind,
                       logical_target_key
                FROM filtered_candidates
                GROUP BY logical_target_key, name, kind
            ),
            grouped_metadata AS (
                SELECT logical_target_key,
                       name,
                       kind,
                       CASE
                           WHEN COUNT(DISTINCT COALESCE(visibility, '')) = 1 THEN MIN(visibility)
                           ELSE NULL
                       END AS visibility,
                       CASE
                           WHEN COUNT(DISTINCT COALESCE(container_name, '')) = 1 THEN MIN(container_name)
                           ELSE NULL
                       END AS container_name
                FROM filtered_candidates
                GROUP BY logical_target_key, name, kind
            ),
            grouped_rows AS (
                SELECT gc.symbol_id,
                       gc.name,
                       gc.kind,
                       fc.path,
                       fc.lang,
                       fc.line,
                       gm.visibility,
                       gm.container_name,
                       gc.logical_target_key
                FROM grouped_candidates gc
                JOIN filtered_candidates fc ON fc.id = gc.symbol_id
                JOIN grouped_metadata gm
                 ON gm.logical_target_key = gc.logical_target_key
                 AND gm.name = gc.name
                 AND gm.kind = gc.kind
            ),
            logical_references AS (
                SELECT sr.file_id,
                       rf.lang,
                       sr.symbol_name AS raw_symbol_name,
                       " + BuildLogicalReferenceNameExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_segment_count,
                       " + BuildLogicalReferenceLeafFallbackAllowedExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS allow_leaf_fallback,
                       sr.line,
                       sr.column_number,
                       " + GetLogicalReferenceKindSql("sr.reference_kind") + @" AS logical_reference_kind,
                       " + GetHotspotReferenceWeightSql("sr.reference_kind") + @" AS reference_weight
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                WHERE sr.reference_kind IN " + CallGraphReferenceKindsSql + @"
                GROUP BY rf.lang, sr.file_id, raw_symbol_name, symbol_name, symbol_segment_count, allow_leaf_fallback, sr.line, sr.column_number, logical_reference_kind
            ),
            global_exact_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, symbol_name, symbol_segment_count
            ),
            global_leaf_reference_counts AS (
                SELECT lang,
                       raw_symbol_name,
                       symbol_name AS resolved_symbol_name,
                       symbol_segment_count AS resolved_symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                WHERE allow_leaf_fallback = 1
                GROUP BY lang, raw_symbol_name, resolved_symbol_name, resolved_symbol_segment_count
            ),
            file_target_cardinality AS (
                SELECT lang,
                       file_id,
                       name,
                       kind,
                       COUNT(DISTINCT logical_target_key) AS target_count
                FROM filtered_candidates
                GROUP BY lang, file_id, name, kind
            ),
            conservative_target_files AS (
                SELECT DISTINCT lang,
                       file_id,
                       name,
                       kind,
                       logical_target_key
                FROM filtered_candidates
            ),
            file_reference_counts_exact AS (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            file_reference_counts_leaf AS (
                SELECT lang,
                       file_id,
                       raw_symbol_name,
                       symbol_name AS resolved_symbol_name,
                       symbol_segment_count AS resolved_symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                WHERE allow_leaf_fallback = 1
                GROUP BY lang, file_id, raw_symbol_name, resolved_symbol_name, resolved_symbol_segment_count
            ),
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(COALESCE(frc_exact.ref_count, 0) + COALESCE(frc_leaf.ref_count, 0)) AS ref_count,
                       SUM(COALESCE(frc_exact.ref_score, 0.0) + COALESCE(frc_leaf.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts_exact frc_exact
                  ON frc_exact.lang = ctf.lang
                 AND frc_exact.file_id = ctf.file_id
                 AND (
                         (ctf.lang != 'sql' AND frc_exact.symbol_name = ctf.name)
                      OR (ctf.lang = 'sql' AND (
                             (frc_exact.symbol_segment_count = sql_segment_count(ctf.name) AND frc_exact.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE)
                      ))
                  )
                LEFT JOIN file_reference_counts_leaf frc_leaf
                  ON frc_leaf.lang = ctf.lang
                 AND frc_leaf.file_id = ctf.file_id
                 AND ctf.lang = 'sql'
                 AND sql_segment_count(ctf.name) > 1
                 AND frc_leaf.raw_symbol_name = sql_leaf_name(ctf.name) COLLATE NOCASE
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_resolved
                        WHERE fc_resolved.lang = ctf.lang
                          AND sql_segment_count(fc_resolved.name) = frc_leaf.resolved_symbol_segment_count
                          AND sql_normalize_name(fc_resolved.name) = frc_leaf.resolved_symbol_name COLLATE NOCASE
                    )
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_exact
                        WHERE fc_exact.lang = ctf.lang
                          AND sql_segment_count(fc_exact.name) = 1
                          AND sql_normalize_name(fc_exact.name) = frc_leaf.raw_symbol_name COLLATE NOCASE
                    )
                GROUP BY ctf.logical_target_key, ctf.name, ctf.kind
            ),
            reference_counts AS (
                SELECT gr.symbol_id,
                       CASE
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc.ref_count, 0) + COALESCE(glrc.ref_count, 0)
                            ELSE COALESCE(crc.ref_count, 0)
                        END AS ref_count,
                       CASE
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc.ref_score, 0.0) + COALESCE(glrc.ref_score, 0.0)
                            ELSE COALESCE(crc.ref_score, 0.0)
                        END AS ref_score
                FROM grouped_rows gr
                JOIN name_cardinality nc
                  ON nc.lang = gr.lang
                  AND nc.name = gr.name
                LEFT JOIN global_exact_reference_counts gerc
                  ON gerc.lang = gr.lang
                 AND (
                         (gr.lang != 'sql' AND gerc.symbol_name = gr.name)
                      OR (gr.lang = 'sql' AND (
                             (gerc.symbol_segment_count = sql_segment_count(gr.name) AND gerc.symbol_name = sql_normalize_name(gr.name) COLLATE NOCASE)
                      ))
                  )
                LEFT JOIN global_leaf_reference_counts glrc
                  ON glrc.lang = gr.lang
                 AND gr.lang = 'sql'
                 AND sql_segment_count(gr.name) > 1
                 AND glrc.raw_symbol_name = sql_leaf_name(gr.name) COLLATE NOCASE
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_resolved
                        WHERE fc_resolved.lang = gr.lang
                          AND sql_segment_count(fc_resolved.name) = glrc.resolved_symbol_segment_count
                          AND sql_normalize_name(fc_resolved.name) = glrc.resolved_symbol_name COLLATE NOCASE
                    )
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_exact
                        WHERE fc_exact.lang = gr.lang
                          AND sql_segment_count(fc_exact.name) = 1
                          AND sql_normalize_name(fc_exact.name) = glrc.raw_symbol_name COLLATE NOCASE
                    )
                LEFT JOIN conservative_reference_counts crc
                  ON crc.logical_target_key = gr.logical_target_key
                 AND crc.name = gr.name
                 AND crc.kind = gr.kind
            ),
            file_symbol_counts AS (
                SELECT path,
                       COALESCE(lang, '') AS lang_key,
                       COUNT(*) AS symbol_count
                FROM filtered_candidates
                GROUP BY path, COALESCE(lang, '')
            )";
        return new SymbolHotspotRowsQuery(sql, graphLangs, hotspotFamilyLangs);
    }

    private static void AddSymbolHotspotParameters(SqliteCommand command, SymbolHotspotRowsQuery query, int? limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (limit.HasValue)
            SqliteCommandPolicy.Add(command, "@limit", limit.Value);
        if (lang != null)
            SqliteCommandPolicy.Add(command, "@lang", lang);
        else
        {
            for (int i = 0; i < query.GraphLanguages.Count; i++)
                SqliteCommandPolicy.Add(command, $"@gl{i}", query.GraphLanguages[i]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(command, "@kind", kind);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(command, visibilityFilters, excludeVisibilityFilters);
        for (int i = 0; i < query.HotspotFamilyLanguages.Count; i++)
            SqliteCommandPolicy.Add(command, $"@hotspotFamilyLang{i}", query.HotspotFamilyLanguages[i]);
    }

    private sealed record SymbolHotspotRowsQuery(string Sql, List<string> GraphLanguages, List<string> HotspotFamilyLanguages);

    private static HotspotCountResult ExecuteHotspotCountSummary(SqliteCommand command)
    {
        using var reader = command.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return new HotspotCountResult(0, 0);

        var count = Convert.ToInt32(reader.GetValue(0));
        var fileCount = Convert.ToInt32(reader.GetValue(1));
        var definitionSiteTotal = reader.FieldCount > 2 && !reader.IsDBNull(2)
            ? Convert.ToInt32(reader.GetValue(2))
            : 0;
        return new HotspotCountResult(count, fileCount, definitionSiteTotal);
    }


}
