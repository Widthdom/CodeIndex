using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Symbol query operations: search, definitions, outline, analyze (partial class split from DbReader.cs).
/// シンボルクエリ操作: 検索、定義、アウトライン、分析（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    private const string UnusedBucketLikelyPrivate = "likely_unused_private";
    private const string UnusedBucketMaybeNonPublic = "maybe_unused_nonpublic";
    private const string UnusedBucketPublicOrExported = "public_or_exported_no_refs";
    private const string UnusedBucketReflectionOrConfig = "reflection_or_config_suspect";
    private static readonly HashSet<string> ReflectionPropertyAttributeNames = new(StringComparer.Ordinal)
    {
        "jsonpropertyname",
        "jsonproperty",
        "jsoninclude",
        "datamember",
        "bsonelement",
        "bsonid",
        "xmlelement",
        "xmlattribute",
        "yamlmember",
        "column",
        "key",
        "required",
        "bindproperty",
        "parameter",
        "inject",
        "bindnever",
    };
    private static readonly HashSet<string> ReflectionTypeAttributeNames = new(StringComparer.Ordinal)
    {
        "serializable",
        "jsonserializable",
        "datacontract",
        "xmlroot",
        "protocontract",
        "messagepackobject",
        "table",
        "complextype",
        "owned",
        "keyless",
        "attributeusage",
    };
    private static readonly HashSet<string> ReflectionIgnoreAttributeNames = new(StringComparer.Ordinal)
    {
        "jsonignore",
        "ignoredatamember",
    };
    private static readonly HashSet<string> AttributeTargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "assembly",
        "module",
        "field",
        "event",
        "method",
        "param",
        "property",
        "return",
        "type",
    };
    private const int UnusedAttributeContextWindow = 16;
    private const int UnusedPublicOverfetchMultiplier = 16;
    private const int UnusedPublicOverfetchMinimum = 64;
    private const int UnusedPublicOverfetchMaximum = 1024;
    private const int UnusedPublicCandidateBudget = 2048;
    private const int GroupedHotspotPathSampleLimit = 20;
    private const string SymbolLanguageFileIdFilter = " AND s.file_id IN (SELECT id FROM files WHERE lang = @lang)";

    private sealed class UnusedCandidateSymbol
    {
        public long FileId { get; init; }
        public string Path { get; init; } = string.Empty;
        public string? Lang { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Line { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Signature { get; init; }
        public string? Visibility { get; init; }
        public string? ReturnType { get; init; }
        public string? ContainerKind { get; init; }
        public string? ContainerName { get; init; }
        public bool IsPublicOrExported { get; init; }
        public bool IsReflectionOrConfigSuspect { get; init; }
        public int ProvisionalBucketOrder { get; init; }
    }

    private readonly record struct UnusedCandidateChunk(int StartLine, int EndLine, string Content);

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
        if (visibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) IN ({string.Join(",", ExpandVisibilityFilterValues(visibilityFilters).Select((_, i) => $"@visibility{i}"))})";
        if (excludeVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) NOT IN ({string.Join(",", ExpandVisibilityFilterValues(excludeVisibilityFilters).Select((_, i) => $"@excludeVisibility{i}"))})";
    }

    private static void AddVisibilityFilterParameters(SqliteCommand cmd, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (visibilityFilters is { Count: > 0 })
        {
            var expanded = ExpandVisibilityFilterValues(visibilityFilters);
            for (int i = 0; i < expanded.Count; i++)
                cmd.Parameters.AddWithValue($"@visibility{i}", expanded[i]);
        }

        if (excludeVisibilityFilters is { Count: > 0 })
        {
            var expanded = ExpandVisibilityFilterValues(excludeVisibilityFilters);
            for (int i = 0; i < expanded.Count; i++)
                cmd.Parameters.AddWithValue($"@excludeVisibility{i}", expanded[i]);
        }
    }

    private static bool HasVisibilityFilters(IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
        => visibilityFilters is { Count: > 0 } || excludeVisibilityFilters is { Count: > 0 };

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
    public List<SymbolResult> SearchSymbols(string? query = null, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        return SearchSymbols(normalizedQuery == null ? null : new[] { normalizedQuery }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
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
        cmd.Parameters.AddWithValue($"@{parameterStem}Container", container);
        cmd.Parameters.AddWithValue($"@{parameterStem}ContainerSuffixLike", $"%.{EscapeLikeQuery(container)}");
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
        cmd.Parameters.AddWithValue("@aliasQuerySuffix", GetQualifiedQuerySuffix(query));
        cmd.Parameters.AddWithValue("@aliasQueryLeaf", SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@allowQualifiedLeafFallback", allowLeafFallback ? 1 : 0);
        cmd.Parameters.AddWithValue("@allowCSharpQualifiedContextMatch", allowCSharpContextMatch ? 1 : 0);
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
            cmd.Parameters.AddWithValue("@query0", paramValue);
            cmd.Parameters.AddWithValue("@query0Normalized", SqlNameResolver.NormalizeQualifiedName(value));
            cmd.Parameters.AddWithValue("@query0NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(value)) ?? SqlNameResolver.NormalizeQualifiedName(value));
            cmd.Parameters.AddWithValue("@query0Leaf", SqlNameResolver.GetLeafName(value));
            cmd.Parameters.AddWithValue("@query0LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(value)) ?? SqlNameResolver.GetLeafName(value));
            cmd.Parameters.AddWithValue("@query0SegmentCount", SqlNameResolver.GetSegmentCount(value));
            cmd.Parameters.AddWithValue("@query0NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(value))}%");
            if (SqlNameResolver.HasQualifier(value))
                AddQualifiedSymbolQueryParameters(cmd, "query0", value);
            if (rustQualifiedParts.QualifiedPath != null)
            {
                cmd.Parameters.AddWithValue("@query0RustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                cmd.Parameters.AddWithValue("@query0RustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                cmd.Parameters.AddWithValue("@query0RustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
            }
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        cmd.Parameters.AddWithValue("@limit", limit);

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
                cmd.Parameters.AddWithValue($"@query{i}", paramValue);
                cmd.Parameters.AddWithValue($"@query{i}Normalized", SqlNameResolver.NormalizeQualifiedName(value));
                cmd.Parameters.AddWithValue($"@query{i}NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(value)) ?? SqlNameResolver.NormalizeQualifiedName(value));
                cmd.Parameters.AddWithValue($"@query{i}Leaf", SqlNameResolver.GetLeafName(value));
                cmd.Parameters.AddWithValue($"@query{i}LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(value)) ?? SqlNameResolver.GetLeafName(value));
                cmd.Parameters.AddWithValue($"@query{i}SegmentCount", SqlNameResolver.GetSegmentCount(value));
                cmd.Parameters.AddWithValue($"@query{i}NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(value))}%");
                if (SqlNameResolver.HasQualifier(value))
                    AddQualifiedSymbolQueryParameters(cmd, $"query{i}", value);
                var swiftBacktickAlias = ComputeSwiftBacktickAlias(value, lang);
                if (swiftBacktickAlias != null)
                {
                    cmd.Parameters.AddWithValue($"@query{i}SwiftBacktickAlias", _foldReady
                        ? NameFold.Fold(swiftBacktickAlias) ?? swiftBacktickAlias
                        : swiftBacktickAlias);
                }
            }
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
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
    public List<SymbolResult> SearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
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
                perName.Add(SearchSymbols(new[] { q! }, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters));

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

        var sql = $@"
            SELECT f.path, f.lang, s.kind, {GetSymbolColumnSql("sub_kind")} AS sub_kind, s.name, s.line,
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
        sql += $" ORDER BY CASE " +
            "WHEN @preferLiteralExactMatch = 1 AND s.name = @rawQuery THEN 0 " +
            "WHEN @preferLiteralNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name(s.name) = @rawQueryNormalized THEN 1 " +
            "WHEN @preferCaseInsensitiveExactMatch = 1 AND s.name = @rawQuery COLLATE NOCASE THEN 2 " +
            "WHEN @preferCaseInsensitiveNormalizedSqlMatch = 1 AND f.lang = 'sql' AND sql_segment_count(s.name) = @rawQuerySegmentCount AND sql_normalize_name_folded(s.name) = @rawQueryNormalizedFolded THEN 3 " +
            "WHEN @preferCaseInsensitiveSqlLeafMatch = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @rawQueryLeafFolded THEN 4 " +
            "ELSE 5 END, " +
            $"{PathBucketOrder}, {VisibilityOrder}, s.name, f.path, s.line, " +
            $"{GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)")} ASC, s.id ASC LIMIT @limit";

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
                cmd.Parameters.AddWithValue($"@query{idx}", paramValue);
                cmd.Parameters.AddWithValue($"@query{idx}Normalized", SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]));
                cmd.Parameters.AddWithValue($"@query{idx}NormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx])) ?? SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]));
                cmd.Parameters.AddWithValue($"@query{idx}Leaf", SqlNameResolver.GetLeafName(effectiveQueries[idx]));
                cmd.Parameters.AddWithValue($"@query{idx}LeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(effectiveQueries[idx])) ?? SqlNameResolver.GetLeafName(effectiveQueries[idx]));
                cmd.Parameters.AddWithValue($"@query{idx}SegmentCount", SqlNameResolver.GetSegmentCount(effectiveQueries[idx]));
                cmd.Parameters.AddWithValue($"@query{idx}NormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(effectiveQueries[idx]))}%");
                if (SqlNameResolver.HasQualifier(effectiveQueries[idx]))
                    AddQualifiedSymbolQueryParameters(cmd, $"query{idx}", effectiveQueries[idx]);
                if (rustQualifiedParts.QualifiedPath != null)
                {
                    cmd.Parameters.AddWithValue($"@query{idx}RustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                    cmd.Parameters.AddWithValue($"@query{idx}RustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                    cmd.Parameters.AddWithValue($"@query{idx}RustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
                }
                var swiftBacktickAlias = ComputeSwiftBacktickAlias(effectiveQueries[idx], lang);
                if (swiftBacktickAlias != null)
                {
                    cmd.Parameters.AddWithValue($"@query{idx}SwiftBacktickAlias", _foldReady
                        ? NameFold.Fold(swiftBacktickAlias) ?? swiftBacktickAlias
                        : swiftBacktickAlias);
                }
            }
        }
        var preferLiteralExactMatch = effectiveQueries != null && effectiveQueries.Count == 1;
        var preferCaseInsensitiveExactMatch = effectiveQueries != null && effectiveQueries.Count == 1;
        var preferSqlLeafMatch = preferCaseInsensitiveExactMatch && !SqlNameResolver.HasQualifier(effectiveQueries![0]);
        cmd.Parameters.AddWithValue("@preferLiteralExactMatch", preferLiteralExactMatch ? 1 : 0);
        cmd.Parameters.AddWithValue("@preferLiteralNormalizedSqlMatch", preferLiteralExactMatch ? 1 : 0);
        cmd.Parameters.AddWithValue("@preferCaseInsensitiveExactMatch", preferCaseInsensitiveExactMatch ? 1 : 0);
        cmd.Parameters.AddWithValue("@preferCaseInsensitiveNormalizedSqlMatch", preferCaseInsensitiveExactMatch ? 1 : 0);
        cmd.Parameters.AddWithValue("@preferCaseInsensitiveSqlLeafMatch", preferSqlLeafMatch ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", preferLiteralExactMatch ? effectiveQueries![0] : string.Empty);
        cmd.Parameters.AddWithValue("@rawQueryNormalized", preferLiteralExactMatch ? SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0]) : string.Empty);
        cmd.Parameters.AddWithValue("@rawQueryNormalizedFolded", preferLiteralExactMatch ? NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0])) ?? SqlNameResolver.NormalizeQualifiedName(effectiveQueries![0]) : string.Empty);
        cmd.Parameters.AddWithValue("@rawQueryLeaf", preferLiteralExactMatch ? SqlNameResolver.GetLeafName(effectiveQueries![0]) : string.Empty);
        cmd.Parameters.AddWithValue("@rawQueryLeafFolded", preferLiteralExactMatch ? NameFold.Fold(SqlNameResolver.GetLeafName(effectiveQueries![0])) ?? SqlNameResolver.GetLeafName(effectiveQueries![0]) : string.Empty);
        cmd.Parameters.AddWithValue("@rawQuerySegmentCount", preferLiteralExactMatch ? SqlNameResolver.GetSegmentCount(effectiveQueries![0]) : 0);
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        cmd.Parameters.AddWithValue("@limit", limit);

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
            });
        }
        return results;
    }

    /// <summary>
    /// Resolve symbol definitions with reconstructed excerpts.
    /// シンボル定義を抜粋付きで解決する。
    /// </summary>
    public List<DefinitionResult> GetDefinitions(string query, int limit = 20, string? kind = null, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        var symbols = SearchSymbols(query, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
        var results = new List<DefinitionResult>();

        foreach (var symbol in symbols)
        {
            var definitionExcerpt = GetExcerpt(symbol.Path, symbol.StartLine, symbol.EndLine);
            if (definitionExcerpt == null)
                continue;

            string? bodyContent = null;
            if (includeBody && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            {
                bodyContent = GetExcerpt(symbol.Path, symbol.BodyStartLine.Value, symbol.BodyEndLine.Value)?.Content;
            }

            results.Add(new DefinitionResult
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
                Complexity = bodyContent != null ? SymbolExtractor.EstimateComplexity(bodyContent) : null,
            });
        }

        return results;
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
            cmd.Parameters.AddWithValue("@query", paramValue);
            cmd.Parameters.AddWithValue("@queryNormalized", SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            cmd.Parameters.AddWithValue("@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(normalizedQuery)) ?? SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            cmd.Parameters.AddWithValue("@queryLeaf", SqlNameResolver.GetLeafName(normalizedQuery));
            cmd.Parameters.AddWithValue("@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(normalizedQuery)) ?? SqlNameResolver.GetLeafName(normalizedQuery));
            cmd.Parameters.AddWithValue("@querySegmentCount", SqlNameResolver.GetSegmentCount(normalizedQuery));
            cmd.Parameters.AddWithValue("@queryNormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(normalizedQuery))}%");
            if (SqlNameResolver.HasQualifier(normalizedQuery))
                AddQualifiedSymbolQueryParameters(cmd, "query", normalizedQuery);
            if (rustQualifiedParts.QualifiedPath != null)
            {
                cmd.Parameters.AddWithValue("@queryRustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                cmd.Parameters.AddWithValue("@queryRustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                cmd.Parameters.AddWithValue("@queryRustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
            }
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
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
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@focusLine", focusLine);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (excludeName != null && excludeStartLine != null)
        {
            cmd.Parameters.AddWithValue("@excludeName", excludeName);
            cmd.Parameters.AddWithValue("@excludeStartLine", excludeStartLine.Value);
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

    /// <summary>
    /// Bundle definition, graph, and local file context for one symbol query.
    /// 単一シンボルクエリ向けに、定義・グラフ・ローカル文脈をまとめて返す。
    /// </summary>
    public SymbolAnalysisResult AnalyzeSymbol(string query, int limit = 10, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth)
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
        var definitions = GetDefinitions(normalizedQuery, definitionLimit, kind: null, lang, includeBody, pathPatterns, excludePathPatterns, excludeTests, since: null, exact);
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
        cmd.Parameters.AddWithValue("@queryRaw", query);
        cmd.Parameters.AddWithValue("@queryFolded", NameFold.Fold(query) ?? query);
        cmd.Parameters.AddWithValue("@queryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@queryLeaf", SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@querySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : (string?)value;
    }

    private bool HasExactDefinitionMatch(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string extraConditionSql,
        SqliteCommand? command = null)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        using var ownedCommand = command == null ? _conn.CreateCommand() : null;
        var cmd = command ?? ownedCommand!;
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedQuery);
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                : "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @queryLeaf COLLATE NOCASE)))"
                : "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE))";

        var sql = @"
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE " + nameCondition;
        if (lang != null)
            sql += " AND f.lang = @lang";
        sql += " AND " + extraConditionSql;
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@queryRaw", normalizedQuery);
        cmd.Parameters.AddWithValue("@queryFolded", NameFold.Fold(normalizedQuery) ?? normalizedQuery);
        cmd.Parameters.AddWithValue("@queryNormalized", SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
        cmd.Parameters.AddWithValue("@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(normalizedQuery)) ?? SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
        cmd.Parameters.AddWithValue("@queryLeaf", SqlNameResolver.GetLeafName(normalizedQuery));
        cmd.Parameters.AddWithValue("@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(normalizedQuery)) ?? SqlNameResolver.GetLeafName(normalizedQuery));
        cmd.Parameters.AddWithValue("@querySegmentCount", SqlNameResolver.GetSegmentCount(normalizedQuery));
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return cmd.ExecuteScalar() != null;
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

    /// <summary>
    /// Return a structured outline of symbols in a single file, ordered deterministically.
    /// 1ファイルのシンボルを決定的な順序の構造化アウトラインとして返す。
    /// </summary>
    public OutlineResult? GetOutline(string filePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, lang, lines FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", filePath);

        string? lang = null;
        int totalLines = 0;
        long fileId = 0;
        using (var reader = cmd.ExecuteTrackedReader())
        {
            if (!reader.TrackedRead())
                return null;
            fileId = reader.GetInt64(0);
            lang = GetNullableString(reader, 2);
            totalLines = reader.GetInt32(3);
        }

        var startColumnOrderSql = GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)");
        using var symCmd = _conn.CreateCommand();
        symCmd.CommandText = $@"
            SELECT s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            WHERE s.file_id = @fileId
            ORDER BY s.line ASC,
                     {startColumnOrderSql} ASC,
                     s.kind COLLATE BINARY ASC,
                     s.name COLLATE BINARY ASC,
                     s.id ASC";
        symCmd.Parameters.AddWithValue("@fileId", fileId);

        var symbols = new List<OutlineSymbol>();
        using (var reader = symCmd.ExecuteTrackedReader())
        {
            while (reader.TrackedRead())
            {
                var name = reader.GetString(1);
                var containerName = GetNullableString(reader, 9);
                var containerQualifiedName = GetNullableString(reader, 10);
                symbols.Add(new OutlineSymbol
                {
                    Kind = reader.GetString(0),
                    Name = name,
                    Line = reader.GetInt32(2),
                    StartLine = GetInt32OrFallback(reader, 3, 2),
                    EndLine = GetInt32OrFallback(reader, 4, 2),
                    BodyStartLine = GetNullableInt32(reader, 5),
                    BodyEndLine = GetNullableInt32(reader, 6),
                    Signature = GetNullableString(reader, 7),
                    ContainerKind = GetNullableString(reader, 8),
                    ContainerName = containerName,
                    Path = BuildOutlineSymbolPath(containerQualifiedName ?? containerName, name),
                    Visibility = GetNullableString(reader, 11),
                    ReturnType = GetNullableString(reader, 12),
                });
            }
        }

        PopulateOutlineDepths(symbols);
        PopulateOutlineDisplayNames(symbols, lang);

        return new OutlineResult
        {
            Path = filePath,
            Lang = lang,
            TotalLines = totalLines,
            SymbolCount = symbols.Count,
            Symbols = symbols,
        };
    }

    private static void PopulateOutlineDisplayNames(List<OutlineSymbol> symbols, string? lang)
    {
        foreach (var symbol in symbols)
        {
            symbol.DisplayName = BuildOutlineDisplayName(symbol, lang);
        }
    }

    private static string BuildOutlineDisplayName(OutlineSymbol symbol, string? lang)
    {
        if (IsCallableOutlineSymbol(symbol.Kind))
        {
            var compactSignature = TryBuildCompactCallableSignature(symbol.Name, symbol.Signature, lang);
            if (!string.IsNullOrWhiteSpace(compactSignature))
                return compactSignature!;

            return $"{symbol.Name}@{symbol.Line}";
        }

        return !string.IsNullOrWhiteSpace(symbol.Path)
            ? symbol.Path
            : symbol.Name;
    }

    private static string BuildOutlineSymbolPath(string? containerQualifiedName, string name)
    {
        return string.IsNullOrWhiteSpace(containerQualifiedName)
            ? name
            : $"{containerQualifiedName}.{name}";
    }

    private static bool IsCallableOutlineSymbol(string kind)
    {
        return kind is "function" or "operator" or "method" or "constructor";
    }

    private static string? TryBuildCompactCallableSignature(string name, string? signature, string? lang)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var openParen = FindCallableParameterOpenParen(signature, name);
        if (openParen < 0)
            return null;

        var closeParen = FindMatchingParen(signature, openParen);
        if (closeParen < 0)
            return null;

        var parameters = signature.Substring(openParen + 1, closeParen - openParen - 1);
        var parameterLabels = SplitTopLevelParameters(parameters)
            .Select(parameter => SimplifyParameterForOutline(parameter, lang))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter))
            .ToList();

        return $"{name}({string.Join(", ", parameterLabels)})";
    }

    private static int FindCallableParameterOpenParen(string signature, string name)
    {
        var searchStart = 0;
        while (searchStart < signature.Length)
        {
            var nameIndex = signature.IndexOf(name, searchStart, StringComparison.Ordinal);
            if (nameIndex < 0)
                return -1;

            var afterName = nameIndex + name.Length;
            var cursor = afterName;
            while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                cursor++;

            if (cursor < signature.Length && signature[cursor] == '(')
                return cursor;

            searchStart = afterName;
        }

        return -1;
    }

    private static int FindMatchingParen(string value, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevelParameters(string parameters)
    {
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var ch = parameters[i];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0) bracketDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    yield return parameters[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        var last = parameters[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static string SimplifyParameterForOutline(string parameter, string? lang)
    {
        var cleaned = Regex.Replace(parameter, @"\s*=\s*.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"^\[[^\]]+\]\s*", "").Trim();
        if (cleaned.Length == 0)
            return string.Empty;

        if (lang is "python")
        {
            if (cleaned is "self" or "cls" or "*" or "/")
                return string.Empty;

            var colonIndex = cleaned.IndexOf(':');
            if (colonIndex >= 0 && colonIndex + 1 < cleaned.Length)
                return cleaned[(colonIndex + 1)..].Trim();

            return cleaned.TrimStart('*');
        }

        var colon = cleaned.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < cleaned.Length)
            return cleaned[(colon + 1)..].Trim();

        cleaned = Regex.Replace(cleaned, @"^(params|ref|out|in|this|readonly)\s+", "", RegexOptions.IgnoreCase).Trim();
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            return cleaned;

        if (lang is "go")
            return parts[^1];

        return string.Join(" ", parts.Take(parts.Length - 1));
    }

    private static void PopulateOutlineDepths(List<OutlineSymbol> symbols)
    {
        var depthCache = new Dictionary<int, int>();
        var activeStack = new HashSet<int>();
        for (var i = 0; i < symbols.Count; i++)
            symbols[i].Depth = GetOutlineDepth(symbols, i, depthCache, activeStack);
    }

    private static int GetOutlineDepth(List<OutlineSymbol> symbols, int index, Dictionary<int, int> depthCache, HashSet<int> activeStack)
    {
        if (depthCache.TryGetValue(index, out var cachedDepth))
            return cachedDepth;

        if (!activeStack.Add(index))
            return 0;

        var symbol = symbols[index];
        var depth = 0;
        if (!string.IsNullOrEmpty(symbol.ContainerName))
        {
            var parentIndex = FindOutlineContainerIndex(symbols, index, symbol.ContainerName, symbol.ContainerKind);
            if (parentIndex >= 0)
                depth = GetOutlineDepth(symbols, parentIndex, depthCache, activeStack) + 1;
        }

        activeStack.Remove(index);
        depthCache[index] = depth;
        return depth;
    }

    private static int FindOutlineContainerIndex(List<OutlineSymbol> symbols, int childIndex, string containerName, string? containerKind)
    {
        var child = symbols[childIndex];
        var expectedContainerPath = GetOutlineContainerPath(child);
        for (var i = childIndex - 1; i >= 0; i--)
        {
            var candidate = symbols[i];
            if (!string.Equals(candidate.Name, containerName, StringComparison.Ordinal))
                continue;
            if (containerKind != null && !string.Equals(candidate.Kind, containerKind, StringComparison.Ordinal))
                continue;
            if (expectedContainerPath != null
                && !string.Equals(candidate.Path, expectedContainerPath, StringComparison.Ordinal))
            {
                continue;
            }
            if (candidate.Line > child.Line)
                continue;
            if (IsOutlineContainerMatch(candidate, child.Line))
                return i;
        }

        return -1;
    }

    private static string? GetOutlineContainerPath(OutlineSymbol symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Path) || string.IsNullOrWhiteSpace(symbol.Name))
            return null;

        var suffix = "." + symbol.Name;
        return symbol.Path.EndsWith(suffix, StringComparison.Ordinal)
            ? symbol.Path[..^suffix.Length]
            : null;
    }

    private static bool IsOutlineContainerMatch(OutlineSymbol candidate, int childLine)
    {
        if (candidate.StartLine <= childLine && candidate.EndLine >= childLine)
            return true;

        // file-scoped namespaces have no body range, so they do not enclose children by lines
        // even though they are the correct logical container.
        return candidate.Kind == "namespace"
            && candidate.BodyStartLine == null
            && candidate.BodyEndLine == null
            && candidate.Line <= childLine;
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
        var sql = query.Sql + @"
            SELECT gr.name, rc.ref_count, rc.ref_score,
                   gr.kind, gr.path, gr.lang, gr.line,
                   gr.visibility, gr.container_name
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0
            ORDER BY rc.ref_score DESC,
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
                    Kind = reader.GetString(3),
                    Path = reader.GetString(4),
                    Lang = GetNullableString(reader, 5),
                    Line = reader.GetInt32(6),
                    Visibility = GetNullableString(reader, 7),
                    ContainerName = GetNullableString(reader, 8),
                },
                ReferenceCount = reader.GetInt32(1),
                ReferenceScore = reader.GetDouble(2),
            });
        }
        return results;
    }

    public List<FileHotspotResult> GetFileSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT gr.path,
                   gr.lang,
                   SUM(rc.ref_count) AS ref_count,
                   COUNT(*) AS symbol_count
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0
            GROUP BY gr.path, gr.lang
            ORDER BY ref_count DESC,
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
                SymbolCount = reader.GetInt32(3),
            });
        }
        return results;
    }

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
                ors.Add($"path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND path NOT LIKE @excludePathPattern{i} ESCAPE '\\'";
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
            )";
        return new SymbolHotspotRowsQuery(sql, graphLangs, hotspotFamilyLangs);
    }

    private static void AddSymbolHotspotParameters(SqliteCommand command, SymbolHotspotRowsQuery query, int? limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (limit.HasValue)
            command.Parameters.AddWithValue("@limit", limit.Value);
        if (lang != null)
            command.Parameters.AddWithValue("@lang", lang);
        else
        {
            for (int i = 0; i < query.GraphLanguages.Count; i++)
                command.Parameters.AddWithValue($"@gl{i}", query.GraphLanguages[i]);
        }
        if (kind != null)
            command.Parameters.AddWithValue("@kind", kind);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(command, visibilityFilters, excludeVisibilityFilters);
        for (int i = 0; i < query.HotspotFamilyLanguages.Count; i++)
            command.Parameters.AddWithValue($"@hotspotFamilyLang{i}", query.HotspotFamilyLanguages[i]);
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

    private SymbolHotspotRowsQuery BuildGroupedSymbolHotspotRowsQuery(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
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
        var graphLangs = ReferenceExtractor.GetSupportedLanguages().ToList();
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
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND path NOT LIKE @excludePathPattern{i} ESCAPE '\\'";
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", "path")}";
        sql += @"
            ),
            logical_references AS (
                SELECT sr.file_id,
                       rf.lang,
                       " + BuildLogicalReferenceNameExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_segment_count,
                       sr.line,
                       sr.column_number,
                       " + GetLogicalReferenceKindSql("sr.reference_kind") + @" AS logical_reference_kind,
                       " + GetHotspotReferenceWeightSql("sr.reference_kind") + @" AS reference_weight
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                WHERE sr.reference_kind IN " + CallGraphReferenceKindsSql + @"
                GROUP BY rf.lang, sr.file_id, symbol_name, symbol_segment_count, sr.line, sr.column_number, logical_reference_kind
            ),
            global_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, symbol_name, symbol_segment_count
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
            file_reference_counts AS (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(COALESCE(frc.ref_count, 0)) AS ref_count,
                       SUM(COALESCE(frc.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts frc
                  ON frc.lang = ctf.lang
                 AND frc.file_id = ctf.file_id
                 AND (
                        (ctf.lang != 'sql' AND frc.symbol_name = ctf.name)
                     OR (ctf.lang = 'sql' AND (
                            (frc.symbol_segment_count = sql_segment_count(ctf.name) AND frc.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE)
                         OR (frc.symbol_segment_count = 1 AND frc.symbol_name = sql_leaf_name(ctf.name) COLLATE NOCASE)
                     ))
                 )
                GROUP BY ctf.logical_target_key, ctf.name, ctf.kind
            ),
            site_reference_counts AS (
                SELECT fc.id AS symbol_id,
                       CASE
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc.ref_count, 0)
                           ELSE COALESCE(crc.ref_count, 0)
                       END AS ref_count,
                       CASE
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc.ref_score, 0.0)
                           ELSE COALESCE(crc.ref_score, 0.0)
                       END AS ref_score
                FROM filtered_candidates fc
                JOIN name_cardinality nc
                  ON nc.lang = fc.lang
                 AND nc.name = fc.name
                LEFT JOIN global_reference_counts grc
                  ON grc.lang = fc.lang
                 AND (
                        (fc.lang != 'sql' AND grc.symbol_name = fc.name)
                     OR (fc.lang = 'sql' AND (
                            (grc.symbol_segment_count = sql_segment_count(fc.name) AND grc.symbol_name = sql_normalize_name(fc.name) COLLATE NOCASE)
                         OR (grc.symbol_segment_count = 1 AND grc.symbol_name = sql_leaf_name(fc.name) COLLATE NOCASE)
                     ))
                 )
                LEFT JOIN conservative_reference_counts crc
                  ON crc.logical_target_key = fc.logical_target_key
                 AND crc.name = fc.name
                 AND crc.kind = fc.kind
            ),
            hotspot_sites AS (
                SELECT fc.id AS symbol_id,
                       fc.name,
                       fc.kind,
                       fc.path,
                       fc.lang,
                       fc.line,
                       fc.visibility,
                       fc.container_name,
                       fc.logical_target_key,
                       src.ref_count,
                       src.ref_score
                FROM filtered_candidates fc
                JOIN site_reference_counts src ON src.symbol_id = fc.id
                WHERE src.ref_count > 0
            ),
            ranked_sites AS (
                SELECT hs.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY hs.name, hs.kind
                           ORDER BY hs.path, hs.line, COALESCE(hs.container_name, ''), COALESCE(hs.visibility, '')
                       ) AS rep_rank
                FROM hotspot_sites hs
            ),
            grouped_reference_counts AS (
                SELECT hs.name,
                       hs.kind,
                       SUM(hs.ref_count) AS ref_count,
                       SUM(hs.ref_score) AS ref_score
                FROM (
                    SELECT DISTINCT name,
                           kind,
                           logical_target_key,
                           ref_count,
                           ref_score
                    FROM hotspot_sites
                ) hs
                GROUP BY hs.name, hs.kind
            ),
            grouped AS (
                SELECT hs.name,
                       hs.kind,
                       MAX(grc.ref_count) AS ref_count,
                       MAX(grc.ref_score) AS ref_score,
                       COUNT(*) AS definition_sites
                FROM ranked_sites hs
                JOIN grouped_reference_counts grc
                  ON grc.name = hs.name
                 AND grc.kind = hs.kind
                GROUP BY hs.name, hs.kind
            )";

        return new SymbolHotspotRowsQuery(sql, graphLangs, hotspotFamilyLangs);
    }

    public HotspotCountResult CountGroupedSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildGroupedSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   (SELECT COUNT(DISTINCT path) FROM ranked_sites),
                   COALESCE(SUM(definition_sites), 0)
            FROM grouped";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    /// <summary>
    /// Return grouped hotspot rows collapsed by (name, kind) after the full filtered site set
    /// has been considered, keeping the representative site deterministic.
    /// フィルタ済みの全 definition site を見た上で、(name, kind) 単位に hotspot を集約して返す。
    /// 代表 site は決定的な順序で選ぶ。
    /// </summary>
    public List<GroupedHotspotResult> GetGroupedSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];

        var query = BuildGroupedSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT g.name, g.kind, g.ref_count, g.ref_score, g.definition_sites,
                   rep.path, rep.lang, rep.line, rep.visibility, rep.container_name,
                   (
                       SELECT GROUP_CONCAT(path, char(10))
                       FROM (
                           SELECT DISTINCT hs2.path AS path
                           FROM ranked_sites hs2
                           WHERE hs2.name = g.name
                             AND hs2.kind = g.kind
                           ORDER BY path
                           LIMIT @groupedPathSampleLimit
                       )
                   ) AS grouped_paths
            FROM grouped g
            JOIN ranked_sites rep
              ON rep.name = g.name
             AND rep.kind = g.kind
             AND rep.rep_rank = 1
            ORDER BY g.ref_score DESC, g.ref_count DESC, g.name, g.kind
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        cmd.Parameters.AddWithValue("@groupedPathSampleLimit", GroupedHotspotPathSampleLimit + 1);

        var results = new List<GroupedHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var paths = GetNullableString(reader, 10)?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [];
            var pathsTruncated = paths.Count > GroupedHotspotPathSampleLimit;
            if (pathsTruncated)
                paths = paths.Take(GroupedHotspotPathSampleLimit).ToList();
            results.Add(new GroupedHotspotResult
            {
                Symbol = new SymbolResult
                {
                    Name = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Path = reader.GetString(5),
                    Lang = GetNullableString(reader, 6),
                    Line = reader.GetInt32(7),
                    Visibility = GetNullableString(reader, 8),
                    ContainerName = GetNullableString(reader, 9),
                },
                ReferenceCount = reader.GetInt32(2),
                ReferenceScore = reader.GetDouble(3),
                DefinitionSites = reader.GetInt32(4),
                Paths = paths,
                PathsTruncated = pathsTruncated,
            });
        }

        return results;
    }

    /// <summary>
    /// Find symbols that have no matching references in the reference table (potential dead code).
    /// Only meaningful for graph-supported languages — unsupported languages are excluded by default.
    /// 参照テーブルに一致する参照がないシンボルを検索する（潜在的なデッドコード）。
    /// グラフ対応言語でのみ意味がある — 未対応言語はデフォルトで除外。
    /// </summary>
    private string BuildAmbiguousCSharpEnumMemberExclusionSql(
        string symbolAlias,
        string fileAlias,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var symbolContainerKindSql = GetSymbolColumnSql("container_kind", "''", symbolAlias);
        var symbolContainerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var symbolContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", symbolContainerNameSql, symbolAlias);
        var peerContainerKindSql = GetSymbolColumnSql("container_kind", "''", "s_peer");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "s_peer");
        var peerContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", peerContainerNameSql, "s_peer");
        var peerPathFiltersSql = BuildPathFiltersSql("f_peer", pathPatterns, excludePathPatterns, excludeTests);

        return $@"
                NOT (
                    {fileAlias}.lang = 'csharp'
                    AND {symbolAlias}.kind = 'enum'
                    AND {symbolContainerKindSql} = 'enum'
                    AND EXISTS (
                        SELECT 1
                        FROM symbols s_peer
                        JOIN files f_peer ON f_peer.id = s_peer.file_id
                        WHERE f_peer.lang = 'csharp'
                          {peerPathFiltersSql}
                          AND s_peer.kind = 'enum'
                          AND {peerContainerKindSql} = 'enum'
                          AND s_peer.name = {symbolAlias}.name
                          AND {peerContainerQualifiedNameSql} <> {symbolContainerQualifiedNameSql}
                    )
                )";
    }

    public List<UnusedSymbolResult> GetUnusedSymbols(
        int limit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? visibilityFilters = null,
        IReadOnlyList<string>? excludeVisibilityFilters = null,
        string? bucketFilter = null,
        string? minConfidence = null)
    {
        // Without symbol_references (legacy read-only DB), every symbol would appear unused,
        // which is a meaningless signal. Return empty rather than drowning the caller in noise.
        // symbol_references が無いレガシー read-only DB では全シンボルが未使用扱いになってしまうため、
        // ノイズを返すより空を返す。
        if (!_hasReferencesTable) return new List<UnusedSymbolResult>();
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            return [];
        // Restrict to graph-supported languages to avoid false positives
        // (unsupported languages have no references indexed, so all symbols appear unused)
        // グラフ対応言語に制限して偽陽性を防ぐ
        // （未対応言語は参照がインデックスされないため全シンボルが未使用に見える）
        if (HasEffectiveUnusedFilter(bucketFilter, minConfidence))
            return GetFilteredUnusedSymbols(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return GetUnusedSymbolsWithoutSqlResolver(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        var targetCount = Math.Max(limit, 1);
        var privateLike = FetchUnusedCandidates(targetCount, 0, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var maybeNonPublic = FetchUnusedCandidates(targetCount, 1, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var reflectionOrConfig = FetchUnusedCandidates(targetCount, 3, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var publicOrExported = new List<UnusedSymbolResult>();
        var publicBucketOffset = 0;
        var publicBatchSize = Math.Min(
            Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
            UnusedPublicOverfetchMaximum);
        var publicFetchBudget = Math.Max(targetCount, Math.Max(publicBatchSize, UnusedPublicCandidateBudget));
        var publicCandidatesFetched = 0;
        while ((publicOrExported.Count < targetCount || reflectionOrConfig.Count < targetCount)
            && publicCandidatesFetched < publicFetchBudget)
        {
            var batch = FetchUnusedCandidates(publicBatchSize, 2, publicBucketOffset, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
            if (batch.Count == 0)
                break;

            foreach (var candidate in batch)
            {
                if (candidate.UnusedBucket == UnusedBucketReflectionOrConfig)
                    reflectionOrConfig.Add(candidate);
                else
                    publicOrExported.Add(candidate);
            }

            publicBucketOffset += batch.Count;
            publicCandidatesFetched += batch.Count;
            if (batch.Count < publicBatchSize)
                break;
        }

        var merged = new List<UnusedSymbolResult>(privateLike.Count + maybeNonPublic.Count + publicOrExported.Count + reflectionOrConfig.Count);
        merged.AddRange(privateLike);
        merged.AddRange(maybeNonPublic);
        merged.AddRange(publicOrExported);
        merged.AddRange(reflectionOrConfig);
        return DiversifyUnusedResults(FilterUnusedResults(merged, bucketFilter, minConfidence), limit);
    }

    private List<UnusedSymbolResult> GetFilteredUnusedSymbols(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return GetFilteredUnusedSymbolsWithoutSqlResolver(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        var targetCount = Math.Max(limit, 1);
        var targetBuckets = GetTargetUnusedBuckets(bucketFilter, minConfidence);
        if (targetBuckets.Count == 0)
            return [];

        var resultsByBucket = CreateUnusedBucketResultLists();
        const int batchSize = UnusedPublicOverfetchMaximum;
        foreach (var provisionalBucket in GetRelevantUnusedProvisionalBuckets(targetBuckets))
        {
            var offset = 0;
            while (!AllTargetUnusedBucketsFilled(resultsByBucket, targetBuckets, targetCount))
            {
                var batch = FetchUnusedCandidates(batchSize, provisionalBucket, offset, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var result in batch)
                    AddFilteredUnusedResult(resultsByBucket, targetBuckets, targetCount, result, bucketFilter, minConfidence);

                if (batch.Count < batchSize)
                    break;
            }
        }

        var merged = OrderedUnusedBuckets
            .SelectMany(bucket => resultsByBucket[bucket])
            .ToList();
        return DiversifyUnusedResults(merged, limit);
    }

    private List<UnusedSymbolResult> GetUnusedSymbolsWithoutSqlResolver(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null,
        string? bucketFilter = null, string? minConfidence = null)
    {
        var targetCount = Math.Max(limit, 1);
        var publicFetchBudget = Math.Max(
            targetCount,
            Math.Max(
                Math.Min(
                    Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
                    UnusedPublicOverfetchMaximum),
                UnusedPublicCandidateBudget));
        var batchSize = Math.Min(
            Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
            UnusedPublicOverfetchMaximum);
        var publicOrExported = new List<UnusedSymbolResult>(targetCount);
        var chunksByFileId = new Dictionary<long, List<UnusedCandidateChunk>>();
        var privateLike = CollectUnusedCandidateBucket(targetCount, batchSize, 0, chunksByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var maybeNonPublic = CollectUnusedCandidateBucket(targetCount, batchSize, 1, chunksByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var reflectionOrConfig = CollectUnusedCandidateBucket(targetCount, batchSize, 3, chunksByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        CollectPublicUnusedCandidateBucket(targetCount, batchSize, publicFetchBudget, chunksByFileId,
            publicOrExported, reflectionOrConfig, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var merged = new List<UnusedSymbolResult>(privateLike.Count + maybeNonPublic.Count + publicOrExported.Count + reflectionOrConfig.Count);
        merged.AddRange(privateLike);
        merged.AddRange(maybeNonPublic);
        merged.AddRange(publicOrExported);
        merged.AddRange(reflectionOrConfig);
        return DiversifyUnusedResults(FilterUnusedResults(merged, bucketFilter, minConfidence), limit);
    }

    private List<UnusedSymbolResult> GetFilteredUnusedSymbolsWithoutSqlResolver(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        var targetCount = Math.Max(limit, 1);
        var targetBuckets = GetTargetUnusedBuckets(bucketFilter, minConfidence);
        if (targetBuckets.Count == 0)
            return [];

        var chunksByFileId = new Dictionary<long, List<UnusedCandidateChunk>>();
        var resultsByBucket = CreateUnusedBucketResultLists();
        const int batchSize = UnusedPublicOverfetchMaximum;
        foreach (var provisionalBucket in GetRelevantUnusedProvisionalBuckets(targetBuckets))
        {
            var offset = 0;
            while (!AllTargetUnusedBucketsFilled(resultsByBucket, targetBuckets, targetCount))
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, provisionalBucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasSameFilePrivateUse(candidate, chunksByFileId))
                        continue;

                    var result = CreateUnusedSymbolResult(candidate);
                    AddFilteredUnusedResult(resultsByBucket, targetBuckets, targetCount, result, bucketFilter, minConfidence);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        var merged = OrderedUnusedBuckets
            .SelectMany(bucket => resultsByBucket[bucket])
            .ToList();
        return DiversifyUnusedResults(merged, limit);
    }

    private List<UnusedSymbolResult> CollectUnusedCandidateBucket(int targetCount, int batchSize, int provisionalBucketOrder,
        Dictionary<long, List<UnusedCandidateChunk>> chunksByFileId, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var results = new List<UnusedSymbolResult>(targetCount);
        var offset = 0;
        while (results.Count < targetCount)
        {
            var batch = FetchUnusedCandidateSymbols(batchSize, offset, provisionalBucketOrder, kind, lang,
                pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
            if (batch.Count == 0)
                break;

            offset += batch.Count;
            foreach (var candidate in batch)
            {
                if (HasSameFilePrivateUse(candidate, chunksByFileId))
                    continue;

                results.Add(CreateUnusedSymbolResult(candidate));
                if (results.Count >= targetCount)
                    break;
            }

            if (batch.Count < batchSize)
                break;
        }

        return results;
    }

    private void CollectPublicUnusedCandidateBucket(int targetCount, int batchSize, int candidateBudget,
        Dictionary<long, List<UnusedCandidateChunk>> chunksByFileId,
        List<UnusedSymbolResult> publicOrExported, List<UnusedSymbolResult> reflectionOrConfig, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var offset = 0;
        var candidatesFetched = 0;
        while ((publicOrExported.Count < targetCount || reflectionOrConfig.Count < targetCount)
            && candidatesFetched < candidateBudget)
        {
            var batch = FetchUnusedCandidateSymbols(batchSize, offset, 2, kind, lang,
                pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
            if (batch.Count == 0)
                break;

            offset += batch.Count;
            foreach (var candidate in batch)
            {
                if (HasSameFilePrivateUse(candidate, chunksByFileId))
                    continue;

                candidatesFetched++;
                var result = CreateUnusedSymbolResult(candidate);
                if (result.UnusedBucket == UnusedBucketReflectionOrConfig)
                {
                    if (reflectionOrConfig.Count < targetCount)
                        reflectionOrConfig.Add(result);
                }
                else if (publicOrExported.Count < targetCount)
                {
                    publicOrExported.Add(result);
                }

                if ((publicOrExported.Count >= targetCount && reflectionOrConfig.Count >= targetCount)
                    || candidatesFetched >= candidateBudget)
                    break;
            }

            if (batch.Count < batchSize)
                break;
        }
    }

    private bool HasSameFilePrivateUse(UnusedCandidateSymbol candidate, Dictionary<long, List<UnusedCandidateChunk>> chunksByFileId)
    {
        if (!string.Equals(candidate.Lang, "csharp", StringComparison.Ordinal)
            || !IsPrivateLikeVisibility(candidate.Visibility)
            || candidate.Name.Length == 0
            || !_hasChunksTable
            || !HasTable("chunks"))
            return false;

        foreach (var chunk in GetUnusedCandidateChunks(candidate.FileId, chunksByFileId))
        {
            var occurrenceCount = DbContext.CountCSharpIdentifierOccurrences(chunk.Content, candidate.Name);
            if (occurrenceCount <= 0)
                continue;

            if (chunk.EndLine < candidate.StartLine
                || chunk.StartLine > candidate.EndLine
                || occurrenceCount > 1)
                return true;
        }

        return false;
    }

    private List<UnusedCandidateChunk> GetUnusedCandidateChunks(long fileId, Dictionary<long, List<UnusedCandidateChunk>> chunksByFileId)
    {
        if (chunksByFileId.TryGetValue(fileId, out var cached))
            return cached;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT start_line, end_line, content
            FROM chunks
            WHERE file_id = @fileId
            ORDER BY start_line, chunk_index
            """;
        cmd.Parameters.AddWithValue("@fileId", fileId);

        var chunks = new List<UnusedCandidateChunk>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            chunks.Add(new UnusedCandidateChunk(
                GetInt32OrFallback(reader, 0, 1),
                GetInt32OrFallback(reader, 1, 0),
                GetNullableString(reader, 2) ?? string.Empty));
        }

        chunksByFileId[fileId] = chunks;
        return chunks;
    }

    private IEnumerable<UnusedCandidateSymbol> FetchUnusedCandidateSymbols(int fetchLimit, int offset, int provisionalBucketOrder, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var graphLangs = ReferenceExtractor.GetSupportedLanguages()
            .Where(value => !IsSqlLanguage(value))
            .ToList();
        var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
        var signatureSql = $"lower({GetSymbolColumnSql("signature", "''")})";
        const string pathSql = "lower(f.path)";
        var isPublicOrExportedSql = $"{visibilitySql} IN ('public', 'open', 'pub', 'export')";
        var hasConfigContextSql = $@"(
                {pathSql} LIKE 'config/%'
                OR {pathSql} LIKE '%/config/%'
                OR {pathSql} LIKE 'settings/%'
                OR {pathSql} LIKE '%/settings/%'
                OR {pathSql} LIKE 'options/%'
                OR {pathSql} LIKE '%/options/%'
                OR {signatureSql} LIKE '%iconfiguration%'
                OR {signatureSql} LIKE '%configurationsection%'
                OR {signatureSql} LIKE '%ioptions%'
                OR {signatureSql} LIKE '%options<%'
            )";
        var isReflectionOrConfigSuspectSql = $@"(
                {isPublicOrExportedSql}
                AND s.kind = 'property'
                AND {hasConfigContextSql}
            )";
        var provisionalBucketOrderSql = $@"
            CASE
                WHEN {isReflectionOrConfigSuspectSql} THEN 3
                WHEN {isPublicOrExportedSql} THEN 2
                WHEN {visibilitySql} IN ('private', 'fileprivate') THEN 0
                ELSE 1
            END";

        var sql = $@"
            SELECT s.file_id, f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   CASE WHEN {isPublicOrExportedSql} THEN 1 ELSE 0 END AS is_public_or_exported,
                   CASE WHEN {isReflectionOrConfigSuspectSql} THEN 1 ELSE 0 END AS is_reflection_or_config_suspect,
                   {provisionalBucketOrderSql} AS provisional_bucket_order
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind NOT IN ('import', 'namespace')";
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";
        sql += """
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  WHERE sr.symbol_name IS NOT NULL
                    AND sr.symbol_name <> ''
                    AND sr.symbol_name = s.name
              )
            """;

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        sql += " AND (" + provisionalBucketOrderSql + ") = @provisionalBucketOrder";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += " ORDER BY f.path, s.line, s.name";
        sql += " LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        else
        {
            for (int i = 0; i < graphLangs.Count; i++)
                cmd.Parameters.AddWithValue($"@gl{i}", graphLangs[i]);
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@provisionalBucketOrder", provisionalBucketOrder);
        cmd.Parameters.AddWithValue("@limit", fetchLimit);
        cmd.Parameters.AddWithValue("@offset", offset);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            yield return new UnusedCandidateSymbol
            {
                FileId = reader.GetInt64(0),
                Path = reader.GetString(1),
                Lang = GetNullableString(reader, 2),
                Kind = reader.GetString(3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                EndLine = GetInt32OrFallback(reader, 7, 5),
                Signature = GetNullableString(reader, 8),
                Visibility = GetNullableString(reader, 9),
                ReturnType = GetNullableString(reader, 10),
                ContainerKind = GetNullableString(reader, 11),
                ContainerName = GetNullableString(reader, 12),
                IsPublicOrExported = reader.GetInt32(13) != 0,
                IsReflectionOrConfigSuspect = reader.GetInt32(14) != 0,
                ProvisionalBucketOrder = reader.GetInt32(15),
            };
        }
    }

    private UnusedSymbolResult CreateUnusedSymbolResult(UnusedCandidateSymbol candidate)
    {
        var isReflectionOrConfigSuspect = candidate.IsReflectionOrConfigSuspect;
        if (!isReflectionOrConfigSuspect && candidate.IsPublicOrExported)
            isReflectionOrConfigSuspect = HasReflectionAttributeContext(candidate.Kind, candidate.Path, candidate.StartLine);

        var classification = ClassifyUnusedSymbol(candidate.IsPublicOrExported, isReflectionOrConfigSuspect, candidate.Visibility);
        return new UnusedSymbolResult
        {
            Path = candidate.Path,
            Lang = candidate.Lang,
            Kind = candidate.Kind,
            Name = candidate.Name,
            Line = candidate.Line,
            StartLine = candidate.StartLine,
            EndLine = candidate.EndLine,
            Signature = candidate.Signature,
            Visibility = candidate.Visibility,
            ReturnType = candidate.ReturnType,
            ContainerKind = candidate.ContainerKind,
            ContainerName = candidate.ContainerName,
            UnusedBucket = classification.Bucket,
            UnusedConfidence = classification.Confidence,
            UnusedReason = classification.Reason,
        };
    }

    private List<UnusedSymbolResult> FetchUnusedCandidates(int fetchLimit, int provisionalBucketOrder, int offset, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();
        var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
        var signatureSql = $"lower({GetSymbolColumnSql("signature", "''")})";
        const string pathSql = "lower(f.path)";
        var isPublicOrExportedSql = $"{visibilitySql} IN ('public', 'open', 'pub', 'export')";
        var hasConfigContextSql = $@"(
                {pathSql} LIKE 'config/%'
                OR {pathSql} LIKE '%/config/%'
                OR {pathSql} LIKE 'settings/%'
                OR {pathSql} LIKE '%/settings/%'
                OR {pathSql} LIKE 'options/%'
                OR {pathSql} LIKE '%/options/%'
                OR {signatureSql} LIKE '%iconfiguration%'
                OR {signatureSql} LIKE '%configurationsection%'
                OR {signatureSql} LIKE '%ioptions%'
                OR {signatureSql} LIKE '%options<%'
            )";
        var isReflectionOrConfigSuspectSql = $@"(
                {isPublicOrExportedSql}
                AND s.kind = 'property'
                AND {hasConfigContextSql}
            )";
        var provisionalBucketOrderSql = $@"
            CASE
                WHEN {isReflectionOrConfigSuspectSql} THEN 3
                WHEN {isPublicOrExportedSql} THEN 2
                WHEN {visibilitySql} IN ('private', 'fileprivate') THEN 0
                ELSE 1
            END";

        var sql = $@"
            WITH unused_candidates AS (
                SELECT f.path, f.lang, s.kind, s.name, s.line,
                       {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                       {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                       {GetSymbolColumnSql("signature")} AS signature,
                       {GetSymbolColumnSql("visibility")} AS visibility,
                       {GetSymbolColumnSql("return_type")} AS return_type,
                       {GetSymbolColumnSql("container_kind")} AS container_kind,
                       {GetSymbolColumnSql("container_name")} AS container_name,
                       CASE WHEN {isPublicOrExportedSql} THEN 1 ELSE 0 END AS is_public_or_exported,
                       CASE WHEN {isReflectionOrConfigSuspectSql} THEN 1 ELSE 0 END AS is_reflection_or_config_suspect,
                       {provisionalBucketOrderSql} AS provisional_bucket_order
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.kind NOT IN ('import', 'namespace')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM symbol_references sr
                      JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                      WHERE sr.symbol_name = s.name
                         OR (f.lang = 'sql' AND rf.lang = 'sql' AND (
                                (sql_resolve_reference_segment_count_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number) = sql_segment_count(s.name)
                                 AND sql_reference_matches_target_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number, s.name) = 1)
                         OR (sql_segment_count(sr.symbol_name) = 1
                            AND sql_allow_leaf_fallback_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number) = 1
                            AND sr.symbol_name = sql_leaf_name(s.name) COLLATE NOCASE
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM symbols s_exact
                                    JOIN files f_exact ON f_exact.id = s_exact.file_id
                                    WHERE f_exact.lang = 'sql'
                                      AND sql_segment_count(s_exact.name) = sql_resolve_reference_segment_count_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number)
                                     AND sql_reference_matches_target_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number, s_exact.name) = 1
                                ))
                         ))
                  )";
        if (_hasChunksTable && HasTable("chunks"))
        {
            sql += BuildSameFilePrivateUseExclusionSql(
                "s",
                "f",
                visibilitySql,
                GetSymbolColumnSql("start_line", "s.line"),
                GetSymbolColumnSql("end_line", "s.line"));
        }
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += @"
            )
            SELECT path, lang, kind, name, line, start_line, end_line, signature, visibility,
                   return_type, container_kind, container_name, is_public_or_exported,
                   is_reflection_or_config_suspect
            FROM unused_candidates
            WHERE provisional_bucket_order = @bucketOrder
            ORDER BY path, line, name
            LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@bucketOrder", provisionalBucketOrder);
        cmd.Parameters.AddWithValue("@limit", fetchLimit);
        cmd.Parameters.AddWithValue("@offset", offset);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        else
        {
            var langList = graphLangs.ToList();
            for (int i = 0; i < langList.Count; i++)
                cmd.Parameters.AddWithValue($"@gl{i}", langList[i]);
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        var results = new List<UnusedSymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var path = reader.GetString(0);
            var kindValue = reader.GetString(2);
            var startLine = GetInt32OrFallback(reader, 5, 4);
            var isPublicOrExported = reader.GetInt32(12) != 0;
            var isReflectionOrConfigSuspect = reader.GetInt32(13) != 0;
            if (!isReflectionOrConfigSuspect && isPublicOrExported)
                isReflectionOrConfigSuspect = HasReflectionAttributeContext(kindValue, path, startLine);

            var visibility = GetNullableString(reader, 8);
            var classification = ClassifyUnusedSymbol(isPublicOrExported, isReflectionOrConfigSuspect, visibility);
            results.Add(new UnusedSymbolResult
            {
                Path = path,
                Lang = GetNullableString(reader, 1),
                Kind = kindValue,
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = startLine,
                EndLine = GetInt32OrFallback(reader, 6, 4),
                Signature = GetNullableString(reader, 7),
                Visibility = visibility,
                ReturnType = GetNullableString(reader, 9),
                ContainerKind = GetNullableString(reader, 10),
                ContainerName = GetNullableString(reader, 11),
                UnusedBucket = classification.Bucket,
                UnusedConfidence = classification.Confidence,
                UnusedReason = classification.Reason,
            });
        }

        return results;
    }

    private static List<UnusedSymbolResult> DiversifyUnusedResults(List<UnusedSymbolResult> results, int limit)
    {
        if (results.Count == 0 || limit <= 0)
            return results;

        var targetCount = Math.Min(limit, results.Count);
        var buckets = OrderedUnusedBuckets
            .ToDictionary(
                bucket => bucket,
                bucket => new Queue<UnusedSymbolResult>(results.Where(result => result.UnusedBucket == bucket)),
                StringComparer.Ordinal);

        var limited = new List<UnusedSymbolResult>(targetCount);
        bool advanced;
        do
        {
            advanced = false;
            foreach (var bucket in OrderedUnusedBuckets)
            {
                var queue = buckets[bucket];
                if (queue.Count == 0)
                    continue;

                limited.Add(queue.Dequeue());
                advanced = true;
                if (limited.Count >= targetCount)
                    return limited;
            }
        } while (advanced);

        return limited;
    }

    private static List<UnusedSymbolResult> FilterUnusedResults(IEnumerable<UnusedSymbolResult> results, string? bucketFilter, string? minConfidence)
        => results
            .Where(result => MatchesUnusedFilters(result, bucketFilter, minConfidence))
            .ToList();

    private static bool HasEffectiveUnusedFilter(string? bucketFilter, string? minConfidence)
        => bucketFilter != null || string.Equals(minConfidence, "medium", StringComparison.Ordinal);

    private static Dictionary<string, List<UnusedSymbolResult>> CreateUnusedBucketResultLists()
        => OrderedUnusedBuckets.ToDictionary(
            bucket => bucket,
            _ => new List<UnusedSymbolResult>(),
            StringComparer.Ordinal);

    private static HashSet<string> GetTargetUnusedBuckets(string? bucketFilter, string? minConfidence)
    {
        var minConfidenceRank = minConfidence == null ? int.MinValue : GetUnusedConfidenceRank(minConfidence);
        return OrderedUnusedBuckets
            .Where(bucket => bucketFilter == null || string.Equals(bucket, bucketFilter, StringComparison.Ordinal))
            .Where(bucket => GetUnusedConfidenceRank(GetUnusedBucketConfidence(bucket)) >= minConfidenceRank)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<int> GetRelevantUnusedProvisionalBuckets(IReadOnlySet<string> targetBuckets)
    {
        if (targetBuckets.Contains(UnusedBucketLikelyPrivate))
            yield return 0;
        if (targetBuckets.Contains(UnusedBucketMaybeNonPublic))
            yield return 1;
        if (targetBuckets.Contains(UnusedBucketPublicOrExported) || targetBuckets.Contains(UnusedBucketReflectionOrConfig))
            yield return 2;
        if (targetBuckets.Contains(UnusedBucketReflectionOrConfig))
            yield return 3;
    }

    private static bool AllTargetUnusedBucketsFilled(
        IReadOnlyDictionary<string, List<UnusedSymbolResult>> resultsByBucket,
        HashSet<string> targetBuckets,
        int targetCount)
    {
        foreach (var bucket in targetBuckets)
        {
            if (resultsByBucket[bucket].Count < targetCount)
                return false;
        }

        return true;
    }

    private static void AddFilteredUnusedResult(
        Dictionary<string, List<UnusedSymbolResult>> resultsByBucket,
        HashSet<string> targetBuckets,
        int targetCount,
        UnusedSymbolResult result,
        string? bucketFilter,
        string? minConfidence)
    {
        if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
            return;
        if (!targetBuckets.Contains(result.UnusedBucket))
            return;

        var bucketResults = resultsByBucket[result.UnusedBucket];
        if (bucketResults.Count < targetCount)
            bucketResults.Add(result);
    }

    private static bool MatchesUnusedFilters(UnusedSymbolResult result, string? bucketFilter, string? minConfidence)
    {
        if (bucketFilter != null && !string.Equals(result.UnusedBucket, bucketFilter, StringComparison.Ordinal))
            return false;

        if (minConfidence != null && GetUnusedConfidenceRank(result.UnusedConfidence) < GetUnusedConfidenceRank(minConfidence))
            return false;

        return true;
    }

    private static string GetUnusedBucketConfidence(string bucket)
        => string.Equals(bucket, UnusedBucketLikelyPrivate, StringComparison.Ordinal) ? "medium" : "low";

    private static int GetUnusedConfidenceRank(string confidence) => confidence switch
    {
        "medium" => 1,
        "low" => 0,
        _ => -1,
    };

    public QueryCountResult CountUnusedSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, string? bucketFilter = null, string? minConfidence = null)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            return new QueryCountResult(0, 0);
        if (bucketFilter != null || minConfidence != null)
            return CountFilteredUnusedSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return CountUnusedSymbolsWithoutSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var graphLangs = ReferenceExtractor.GetSupportedLanguages();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("sr");
        var contextSql = ReferenceContextSql("sr");
        var sql = @"
            SELECT COUNT(*), COUNT(DISTINCT f.path), MAX(CASE WHEN f.lang = 'sql' THEN 1 ELSE 0 END)
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind NOT IN ('import', 'namespace')
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  JOIN files rf ON rf.id = sr.file_id" + referenceLineJoin + @"
                  WHERE sr.symbol_name = s.name
                     OR (f.lang = 'sql' AND rf.lang = 'sql' AND (
                            (sql_resolve_reference_segment_count_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number) = sql_segment_count(s.name)
                             AND sql_reference_matches_target_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number, s.name) = 1)
                         OR (sql_segment_count(sr.symbol_name) = 1
                            AND sql_allow_leaf_fallback_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number) = 1
                            AND sr.symbol_name = sql_leaf_name(s.name) COLLATE NOCASE
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM symbols s_exact
                                    JOIN files f_exact ON f_exact.id = s_exact.file_id
                                    WHERE f_exact.lang = 'sql'
                                      AND sql_segment_count(s_exact.name) = sql_resolve_reference_segment_count_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number)
                                      AND sql_reference_matches_target_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number, s_exact.name) = 1
                                ))
                     ))
              )";
        if (_hasChunksTable && HasTable("chunks"))
        {
            sql += BuildSameFilePrivateUseExclusionSql(
                "s",
                "f",
                $"lower({GetSymbolColumnSql("visibility", "''")})",
                GetSymbolColumnSql("start_line", "s.line"),
                GetSymbolColumnSql("end_line", "s.line"));
        }
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        else
        {
            var langList = graphLangs.ToList();
            for (int i = 0; i < langList.Count; i++)
                cmd.Parameters.AddWithValue($"@gl{i}", langList[i]);
        }
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return new QueryCountResult(0, 0);
        return new QueryCountResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.FieldCount > 2 && !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) != 0);
    }

    private QueryCountResult CountFilteredUnusedSymbols(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return CountFilteredUnusedSymbolsWithoutSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var includesSql = false;
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidates(batchSize, bucket, offset, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var result in batch)
                {
                    if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
                        continue;

                    count++;
                    paths.Add(result.Path);
                    if (IsSqlLanguage(result.Lang))
                        includesSql = true;
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return new QueryCountResult(count, paths.Count, includesSql);
    }

    private QueryCountResult CountFilteredUnusedSymbolsWithoutSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var chunksByFileId = new Dictionary<long, List<UnusedCandidateChunk>>();
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, bucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasSameFilePrivateUse(candidate, chunksByFileId))
                        continue;

                    var result = CreateUnusedSymbolResult(candidate);
                    if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
                        continue;

                    count++;
                    paths.Add(candidate.Path);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return new QueryCountResult(count, paths.Count);
    }

    private QueryCountResult CountUnusedSymbolsWithoutSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var chunksByFileId = new Dictionary<long, List<UnusedCandidateChunk>>();
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, bucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasSameFilePrivateUse(candidate, chunksByFileId))
                        continue;

                    count++;
                    paths.Add(candidate.Path);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return new QueryCountResult(count, paths.Count);
    }

    public bool ScopeMayIncludeSqlSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (lang != null && !IsSqlLanguage(lang))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = """
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'sql'
              AND s.kind NOT IN ('import', 'namespace')
            """;
        if (kind != null)
            sql += " AND s.kind = @kind";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        return cmd.ExecuteScalar() != null;
    }

    private bool HasReflectionAttributeContext(string kind, string path, int startLine)
    {
        if (!_hasChunksTable || startLine <= 1)
            return false;

        var reflectionAttributeNames = GetReflectionAttributeNamesForKind(kind);
        if (reflectionAttributeNames == null)
            return false;

        var excerptStart = Math.Max(1, startLine - UnusedAttributeContextWindow);
        FileExcerptResult? excerpt;
        try
        {
            excerpt = GetExcerpt(path, excerptStart, startLine + UnusedAttributeContextWindow);
        }
        catch (SqliteException ex) when (IsMissingChunksTableError(ex))
        {
            return false;
        }
        if (excerpt == null)
            return false;

        var lines = excerpt.Content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var currentIndex = startLine - excerptStart;
        if (currentIndex < 0 || currentIndex >= lines.Length)
            return false;

        // Sanitize the excerpt across lines so multi-line verbatim / raw /
        // raw-interpolated string literals that contain `[` or `]` in the
        // attribute argument do not leak reflection attribute context onto
        // adjacent symbols. Closes #409.
        // 複数行 verbatim / raw / raw 補間文字列リテラル内の `[` / `]` が
        // 隣接シンボルへ reflection 属性コンテキストを漏らさないよう、
        // 抜粋を行をまたいで sanitize する。#409 を修正。
        var sanitizedLines = SymbolExtractor.SanitizeCSharpLinesForCrossLineScan(lines);
        var triviaMask = BuildTriviaMask(sanitizedLines);
        var attributeBlock = GetAdjacentAttributeBlock(lines, sanitizedLines, triviaMask, currentIndex);
        if (attributeBlock.Count == 0)
            return false;

        var attributeNames = ExtractNormalizedAttributeNames(attributeBlock);
        if (attributeNames.Overlaps(ReflectionIgnoreAttributeNames))
            return false;

        return attributeNames.Overlaps(reflectionAttributeNames);
    }

    private static HashSet<string>? GetReflectionAttributeNamesForKind(string kind)
    {
        if (kind == "property")
            return ReflectionPropertyAttributeNames;

        if (kind is "class" or "struct" or "interface" or "enum")
            return ReflectionTypeAttributeNames;

        return null;
    }

    private static bool IsMissingChunksTableError(SqliteException ex) =>
        ex.SqliteErrorCode == 1
        && ex.Message.Contains("no such table: chunks", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetAdjacentAttributeBlock(string[] lines, string[] sanitizedLines, bool[] triviaMask, int anchorIndex)
    {
        var anchorLine = lines[anchorIndex];
        // Run the inline `[attr] decl;` check against the sanitized anchor so that
        // a line whose first non-whitespace token is a block comment — e.g.
        // `/* note */ [JsonPropertyName("ok")] public string A { get; set; }` —
        // still registers as an inline-attribute-with-declaration. The sanitizer
        // blanks leading `/* ... */` bodies and delimiters to whitespace, leaving
        // `[JsonPropertyName(    )] public string A ...` which satisfies the
        // leading-`[` anchor in LineContainsInlineAttributeAndDeclaration. Using
        // the original line would miss this valid C# shape and drop the property
        // out of `reflection_or_config_suspect`. The #409 intent — refusing to
        // treat multi-line literal continuation tails like `]")] public string A ...`
        // as inline declarations — is preserved: sanitization cannot blank the
        // leading `)` into a `[`, so the leading-`[` anchor still rejects those
        // continuation rows. Closes #409.
        // anchor 行のインライン `[attr] decl;` 判定は sanitize 済み行に対して行う。
        // 行頭ブロックコメントの後ろに属性と宣言が並ぶ、例えば
        // `/* note */ [JsonPropertyName("ok")] public string A { get; set; }` も
        // sanitizer が先頭 `/* ... */` 本体と区切りを空白化するため、
        // `[JsonPropertyName(    )] public string A ...` として扱え、
        // LineContainsInlineAttributeAndDeclaration の先頭 `[` アンカーを満たす。
        // original 行で判定するとこの正しい C# 形を取りこぼし、対象プロパティが
        // `reflection_or_config_suspect` から外れてしまう。#409 の意図
        // （`]")] public string A ...` のような複数行リテラル tail を
        // インライン宣言と見なさない）は、sanitize で先頭 `)` が `[` に変わる
        // ことはないため維持される。#409 を修正。
        if (LineContainsInlineAttributeAndDeclaration(sanitizedLines[anchorIndex]))
            return [sanitizedLines[anchorIndex].Trim()];

        var declarationIndex = anchorIndex;
        if (LooksLikeAttributeBoundaryLine(anchorLine))
        {
            // A line like `]")] public string A ...` is itself both the tail of a
            // multi-line attribute literal AND the inline declaration. In that case
            // the declaration lives on the anchor line itself, so do not walk forward
            // looking for a separate declaration below (which would otherwise skip
            // past the real declaration and pick up an unrelated symbol). Closes #409.
            // `]")] public string A ...` のように複数行属性リテラルの末尾かつ
            // インライン宣言でもある行の場合、宣言は anchor 行自身にあるため、
            // 下方へ別の宣言を探しに行かない（探しに行くと本来の宣言を飛び越して
            // 別シンボルを拾ってしまう）。#409 を修正。
            if (!SanitizedLineHasInlineDeclarationTail(sanitizedLines[anchorIndex]))
            {
                declarationIndex = FindNextDeclarationLine(lines, triviaMask, anchorIndex + 1);
                if (declarationIndex < 0)
                    return [];
            }
        }

        var attributeBottom = FindPreviousNonTriviaLine(lines, triviaMask, declarationIndex - 1);
        if (attributeBottom < 0 || !LooksLikeAttributeBoundaryLine(lines[attributeBottom]))
            return [];

        // If the previous non-trivia line already has its own inline `[attr] decl`,
        // its attribute belongs to that line's declaration, not to the anchor below.
        // Without this guard, a line like `[JsonPropertyName("a[")] public string X { ... }`
        // would leak its reflection attribute onto the next plain property and flip
        // that unrelated symbol into the reflection_or_config_suspect bucket. Closes #375.
        // The same leak surfaces when the `[` and the declaration are split by a
        // multi-line verbatim / raw / raw-interpolated string literal: the line at
        // attributeBottom then starts with a continuation tail like `)]` before the
        // real declaration. LineContainsInlineAttributeAndDeclaration's leading-`[`
        // anchor cannot see that pattern, so we additionally check the sanitized
        // line for trailing declaration content after the last `]`. Both checks
        // run against the sanitized line so that a trailing `// comment` on the
        // attribute row (e.g. `[JsonPropertyName("ok")] // note`) does not count
        // as an inline declaration tail. Closes #409.
        // 直前の非 trivia 行がすでに `[attr] decl` のインライン宣言を持つ場合、
        // その属性はその行の宣言に属し、下の anchor 行には及ばない。
        // このガードが無いと `[JsonPropertyName("a[")] public string X { ... }` の属性が
        // 下の属性なしプロパティに漏れ、無関係なシンボルが reflection_or_config_suspect に
        // 誤分類される。#375 を修正。
        // `[` と宣言が複数行 verbatim / raw / raw 補間文字列リテラルで分断されると、
        // attributeBottom の行は `)]` 等の継続末尾で始まり LineContainsInlineAttributeAndDeclaration の
        // 先頭 `[` アンカーでは捕らえられない。sanitize 済み行の最後の `]` 以降に
        // 宣言本体が残っていないかを併せて確認する。判定はいずれも sanitize 済み行に対して行う。
        // これにより属性行末尾の `// コメント`（例: `[JsonPropertyName("ok")] // note`）が
        // 宣言末尾と誤判定されることも防ぐ。#409 を修正。
        if (attributeBottom != anchorIndex
            && (LineContainsInlineAttributeAndDeclaration(lines[attributeBottom])
                || SanitizedLineHasInlineDeclarationTail(sanitizedLines[attributeBottom])))
            return [];

        // Build the attribute block from the cross-line-sanitized lines so
        // comment bodies never bleed into downstream attribute-name parsing.
        // A multi-line block comment embedded inside an attribute list, e.g.
        //   [
        //       /* explanation
        //          [JsonIgnore] */
        //       JsonPropertyName("ok")
        //   ]
        // has `[JsonIgnore]` inside the comment body. Its original line
        // survives `BuildSingleLineTrivia`, and `ExtractNormalizedAttributeNames`
        // would otherwise parse a phantom `JsonIgnore` attribute that cancels
        // the real `JsonPropertyName`. Sanitized lines blank the comment body
        // (with lexer state carried across physical lines), so the phantom
        // attribute disappears while real identifiers like `JsonPropertyName`
        // are preserved. Closes #409 follow-up.
        // 属性ブロックは横断 sanitize 済み行から構築する。これにより、
        // 属性リスト内に埋め込まれた複数行ブロックコメント（上の例のように
        // `[JsonIgnore]` を本体に含むもの）のコメント本体がダウンストリームの
        // 属性名パースへ漏れることがない。元の行を渡すと `BuildSingleLineTrivia`
        // をすり抜けて `ExtractNormalizedAttributeNames` が幻の `JsonIgnore` を
        // 拾い、本物の `JsonPropertyName` を打ち消してしまっていた。
        // sanitize 済み行は物理行を跨ぐ lexer 状態でコメント本体を空白化するため、
        // 幻の属性は消え、`JsonPropertyName` のような本物の識別子だけが残る。
        // #409 追加修正。
        var block = new List<string>();
        var bracketDepth = 0;
        var sawBracket = false;

        for (int i = attributeBottom; i >= 0; i--)
        {
            var trimmed = sanitizedLines[i].Trim();
            if (triviaMask[i])
            {
                if (sawBracket)
                    block.Add(trimmed);
                continue;
            }

            var hasBracketToken = LooksLikeAttributeBoundaryLine(trimmed);
            if (!sawBracket)
            {
                if (!hasBracketToken)
                    return [];
                sawBracket = true;
            }
            else if (bracketDepth == 0 && !hasBracketToken)
            {
                break;
            }

            block.Add(trimmed);
            bracketDepth += CountBracketDeltaOutsideStrings(trimmed);
            if (bracketDepth < 0)
                bracketDepth = 0;
        }

        block.Reverse();
        return block;
    }

    // Count `] - [` on a C# line while skipping characters that appear inside
    // string or char literals, so standalone attribute rows like `[Obsolete("]")]`
    // do not leave one bracket of residual depth and swallow an unrelated
    // attribute block above them.
    // C# の 1 行について、文字列 / 文字リテラル内の文字を除外した上で `] - [` を数える。
    // `[Obsolete("]")]` のような standalone 属性行が 1 つ分の bracket depth を残して
    // 上の無関係な属性ブロックまで吸い込むのを防ぐ。
    private static int CountBracketDeltaOutsideStrings(string line)
    {
        if (string.IsNullOrEmpty(line))
            return 0;

        var delta = 0;
        var cursor = 0;
        while (cursor < line.Length)
        {
            if (TrySkipCSharpStringOrCharLiteral(line, ref cursor))
                continue;
            var ch = line[cursor++];
            if (ch == '[')
                delta--;
            else if (ch == ']')
                delta++;
        }
        return delta;
    }

    private static bool LineContainsInlineAttributeAndDeclaration(string line)
    {
        // Only a line that actually starts with `[` (after whitespace) can be an
        // inline `[attr] decl;` row. Without this anchor, continuation rows of a
        // multi-line attribute literal (e.g. the `]")]` tail of
        // `[JsonPropertyName(@"a[\n]")]`) pass LooksLikeAttributeBoundaryLine and
        // survive StripLeadingCSharpAttributeLists (which returns the input
        // unchanged when the line doesn't start with `[`), so the guard in
        // GetAdjacentAttributeBlock incorrectly treats them as standalone
        // inline-declaration rows and drops the real attribute block above —
        // flipping reflection-attributed properties out of
        // `reflection_or_config_suspect`. Closes #409.
        // 先頭が `[`（空白を除く）である行だけがインライン `[attr] decl;` 行として成立する。
        // このアンカーが無いと、複数行にまたがる属性リテラルの継続行（例:
        // `[JsonPropertyName(@"a[\n]")]` の末尾 `]")]`）が LooksLikeAttributeBoundaryLine を
        // 通過し、`[` 始まりでない入力をそのまま返す StripLeadingCSharpAttributeLists の
        // 挙動により、GetAdjacentAttributeBlock のガードがそれを単独の
        // インライン宣言行と誤判定して本来の属性ブロックを潰し、reflection 属性付き
        // プロパティが `reflection_or_config_suspect` から外れる。#409 を修正。
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || line[index] != '[')
            return false;

        var remainder = StripLeadingCSharpAttributeLists(line);
        if (string.IsNullOrWhiteSpace(remainder))
            return false;

        remainder = remainder.TrimStart();
        return remainder.Length > 0
            && remainder[0] != '/'
            && remainder[0] != '*'
            && remainder[0] != '#';
    }

    // Detect that the cross-line-sanitized line ends an attribute run with
    // trailing declaration content (single-line `[Foo] decl;` or the tail of a
    // multi-line literal attribute such as `)] decl;`). The sanitizer blanks
    // string / char / comment bodies AND their delimiters, so a genuine
    // declaration `public string X;` shows up as non-whitespace text after the
    // last `]`. A pure attribute line `[Foo]` has only whitespace after its
    // last `]` and returns false here. Closes #409.
    // 横断 sanitize 済みの行が、属性ブロックに続けて宣言本体を抱えているか
    // （単行 `[Foo] decl;` または複数行リテラル属性の末尾 `)] decl;`）を判定する。
    // sanitizer は文字列 / 文字 / コメントの本体と区切りを空白化するため、
    // 実宣言 `public string X;` は最後の `]` 以降に非空白として残る。
    // 属性単独行 `[Foo]` の場合は最後の `]` 以降が空白のみなので false を返す。#409 を修正。
    private static bool SanitizedLineHasInlineDeclarationTail(string sanitizedLine)
    {
        var lastBracket = sanitizedLine.LastIndexOf(']');
        if (lastBracket < 0)
            return false;
        for (var i = lastBracket + 1; i < sanitizedLine.Length; i++)
        {
            if (!char.IsWhiteSpace(sanitizedLine[i]))
                return true;
        }
        return false;
    }

    private static int FindNextDeclarationLine(string[] lines, bool[] triviaMask, int startIndex)
    {
        for (int i = startIndex; i < lines.Length; i++)
        {
            if (!triviaMask[i])
                return i;
        }

        return -1;
    }

    private static int FindPreviousNonTriviaLine(string[] lines, bool[] triviaMask, int startIndex)
    {
        for (int i = startIndex; i >= 0; i--)
        {
            if (!triviaMask[i])
                return i;
        }

        return -1;
    }

    // A line is trivia iff its cross-line-sanitized form is entirely whitespace.
    // `SanitizeCSharpLinesForCrossLineScan` already blanks strings, chars, `//`
    // line comments, and `/* ... */` block comments (with state carried across
    // physical lines), so any non-whitespace left over is real code. The previous
    // heuristic flagged any line that merely *contained* `*/` as trivia, which
    // wrongly skipped attribute rows with a trailing block comment such as
    // `[JsonPropertyName("ok")] /* note */` and made FindPreviousNonTriviaLine
    // overshoot past the real attribute block, dropping reflection context off
    // the following property. Closes #409 follow-up.
    // 行の trivia 判定は、横断 sanitize 済み形がすべて空白かどうかで決める。
    // `SanitizeCSharpLinesForCrossLineScan` は文字列 / 文字 / `//` 行コメント /
    // `/* ... */` ブロックコメント（物理行を跨ぐ状態保持付き）をすべて空白化するため、
    // 残った非空白は必ず本物のコード。以前のヒューリスティックは `*/` を含むだけで
    // trivia 判定していたため、`[JsonPropertyName("ok")] /* note */` のような末尾
    // ブロックコメント付き属性行を飛ばしてしまい、FindPreviousNonTriviaLine が
    // 本来の属性ブロックを越えて遡り、直下プロパティの reflection コンテキストを
    // 落としていた。#409 追加修正。
    private static bool[] BuildTriviaMask(string[] sanitizedLines)
    {
        var triviaMask = new bool[sanitizedLines.Length];
        for (int i = 0; i < sanitizedLines.Length; i++)
            triviaMask[i] = string.IsNullOrWhiteSpace(sanitizedLines[i]);
        return triviaMask;
    }

    private static HashSet<string> ExtractNormalizedAttributeNames(IReadOnlyList<string> attributeBlock)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var content = string.Join("\n", attributeBlock.Where(line => !BuildSingleLineTrivia(line.Trim())));
        var parenDepth = 0;

        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }

            if (parenDepth != 0 || (ch != '[' && ch != ','))
                continue;

            if (!TryReadAttributeIdentifier(content, ref i, out var identifier))
                continue;

            var normalized = NormalizeAttributeIdentifier(identifier);
            if (normalized != null)
                names.Add(normalized);
        }

        return names;
    }

    private static string StripLeadingCSharpAttributeLists(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length || line[index] != '[')
            return line;

        var cursor = index;
        while (cursor < line.Length && line[cursor] == '[')
        {
            var depth = 0;
            var sawBracket = false;
            var breakOnDepthZero = false;
            while (cursor < line.Length)
            {
                // Skip over string and char literals so `[` or `]` inside them do
                // not affect the bracket-depth counter. Without this, a line like
                // `[Foo("[")] public string X { get; set; }` runs to end of line
                // with depth > 0 and the scan returns `string.Empty`, which makes
                // `LineContainsInlineAttributeAndDeclaration` falsely report that
                // the line has no trailing declaration. Closes #375.
                // 文字列・文字リテラル中の `[` `]` が depth 計算を乱さないようスキップする。
                // これを怠ると `[Foo("[")] public string X { get; set; }` のような行で
                // depth が戻らず空文字が返り、`LineContainsInlineAttributeAndDeclaration`
                // が「宣言なし」と誤判定して隣接する別シンボルへ属性が誤帰属する。#375 を修正。
                if (TrySkipCSharpStringOrCharLiteral(line, ref cursor))
                    continue;

                var ch = line[cursor++];
                if (ch == '[')
                {
                    depth++;
                    sawBracket = true;
                }
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0 && sawBracket)
                    {
                        breakOnDepthZero = true;
                        break;
                    }
                }
            }

            if (!breakOnDepthZero)
                return string.Empty;

            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
        }

        return cursor < line.Length ? line[cursor..] : string.Empty;
    }

    /// <summary>
    /// If <paramref name="cursor"/> is at the start of a C# string or char literal
    /// (regular, verbatim, raw, or char), advance <paramref name="cursor"/> past the
    /// closing delimiter and return true. Multi-line strings are clamped to end-of-line
    /// since this helper operates on a single line. Returns false if not at a literal.
    /// C# の文字列・文字リテラル（通常・verbatim・raw・char）先頭にあれば、終端直後まで
    /// <paramref name="cursor"/> を進めて true を返す。単一行前提のため、未終端リテラルは
    /// 行末で打ち切る。リテラル先頭でなければ false を返す。
    /// </summary>
    private static bool TrySkipCSharpStringOrCharLiteral(string line, ref int cursor)
    {
        var start = cursor;
        if (start >= line.Length)
            return false;

        var ch = line[start];

        // Verbatim string: @"..." with "" as escape
        // verbatim 文字列: @"..." で "" が escape
        if (ch == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            var i = start + 2;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        // Interpolated verbatim string: $@"..." or @$"..."
        // 補間 verbatim 文字列: $@"..." または @$"..."
        if ((ch == '$' && start + 2 < line.Length && line[start + 1] == '@' && line[start + 2] == '"')
            || (ch == '@' && start + 2 < line.Length && line[start + 1] == '$' && line[start + 2] == '"'))
        {
            var i = start + 3;
            var braceDepth = 0;
            while (i < line.Length)
            {
                if (braceDepth == 0 && line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                if (line[i] == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth++;
                }
                else if (line[i] == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        if (ch == '"')
        {
            // Raw string: """..."""  (C# 11) — match the opening run length
            // raw 文字列: """..."""（C# 11）— 開始クォート数に合わせて終端を探す
            var runLength = 0;
            while (start + runLength < line.Length && line[start + runLength] == '"')
                runLength++;

            if (runLength >= 3)
            {
                var i = start + runLength;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '"')
                            closeRun++;
                        if (closeRun == runLength)
                        {
                            i += closeRun;
                            break;
                        }
                        i += closeRun;
                        continue;
                    }
                    i++;
                }
                cursor = i;
                return true;
            }

            // Regular string: "..." with \ as escape
            // 通常文字列: "..." で \ が escape
            var k = start + 1;
            while (k < line.Length)
            {
                if (line[k] == '\\' && k + 1 < line.Length)
                {
                    k += 2;
                    continue;
                }
                if (line[k] == '"')
                {
                    k++;
                    break;
                }
                k++;
            }
            cursor = k;
            return true;
        }

        // Interpolated raw string: $"""..."""  and multi-$ form $$"""..."""  (C# 11)
        // — N consecutive `$` means N consecutive `{`/`}` are required to open/close
        // interpolation; fewer are treated as literal.
        // 補間 raw 文字列: $"""..."""、および multi-$ 形式 $$"""..."""（C# 11）—
        // `$` の連続数 N に対し、補間を開閉するには `{` / `}` も N 個連続している必要がある。
        if (ch == '$')
        {
            var dollarCount = 0;
            while (start + dollarCount < line.Length && line[start + dollarCount] == '$')
                dollarCount++;

            var quoteStart = start + dollarCount;
            var rawRunLength = 0;
            while (quoteStart + rawRunLength < line.Length && line[quoteStart + rawRunLength] == '"')
                rawRunLength++;

            if (dollarCount >= 1 && rawRunLength >= 3)
            {
                var i = quoteStart + rawRunLength;
                var braceDepth = 0;
                while (i < line.Length)
                {
                    if (braceDepth == 0 && line[i] == '"')
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '"')
                            closeRun++;
                        if (closeRun == rawRunLength)
                        {
                            i += closeRun;
                            break;
                        }
                        i += closeRun;
                        continue;
                    }
                    if (line[i] == '{')
                    {
                        if (braceDepth == 0)
                        {
                            var openRun = 0;
                            while (i + openRun < line.Length && line[i + openRun] == '{')
                                openRun++;
                            if (openRun >= dollarCount)
                            {
                                braceDepth = 1;
                                i += dollarCount;
                                continue;
                            }
                            // Fewer than $ count — literal in raw interpolated.
                            // $ の連続数より少ない `{` は raw 補間では literal 扱い。
                            i += openRun;
                            continue;
                        }
                        braceDepth++;
                        i++;
                        continue;
                    }
                    if (line[i] == '}')
                    {
                        if (braceDepth == 0)
                        {
                            i++;
                            continue;
                        }
                        if (braceDepth > 1)
                        {
                            braceDepth--;
                            i++;
                            continue;
                        }
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '}')
                            closeRun++;
                        if (closeRun >= dollarCount)
                        {
                            braceDepth = 0;
                            i += dollarCount;
                            continue;
                        }
                        i += closeRun;
                        continue;
                    }
                    i++;
                }
                cursor = i;
                return true;
            }
        }

        // Interpolated string: $"..." — treat braces as skipped so quotes inside
        // `{...}` expressions don't prematurely terminate the string.
        // 補間文字列: $"..." — `{...}` 中のクォートで早期終端しないよう波括弧を追跡する。
        if (ch == '$' && start + 1 < line.Length && line[start + 1] == '"')
        {
            var i = start + 2;
            var braceDepth = 0;
            while (i < line.Length)
            {
                if (braceDepth == 0)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i += 2;
                        continue;
                    }
                    if (line[i] == '"')
                    {
                        i++;
                        break;
                    }
                }
                if (line[i] == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth++;
                }
                else if (line[i] == '}' && braceDepth > 0)
                {
                    if (i + 1 < line.Length && line[i + 1] == '}')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth--;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        if (ch == '\'')
        {
            var k = start + 1;
            while (k < line.Length)
            {
                if (line[k] == '\\' && k + 1 < line.Length)
                {
                    k += 2;
                    continue;
                }
                if (line[k] == '\'')
                {
                    k++;
                    break;
                }
                k++;
            }
            cursor = k;
            return true;
        }

        return false;
    }

    private static bool TryReadAttributeIdentifier(string content, ref int index, out string? identifier)
    {
        identifier = null;
        var i = index + 1;
        while (i < content.Length && char.IsWhiteSpace(content[i]))
            i++;

        var start = i;
        if (!TryConsumeAttributeName(content, ref i))
            return false;
        if (i == start)
            return false;

        while (i < content.Length && char.IsWhiteSpace(content[i]))
            i++;

        // Skip attribute targets like `[property: JsonPropertyName]`.
        var leadingIdentifier = content[start..i].Trim();
        if (i < content.Length && content[i] == ':' && (i + 1 >= content.Length || content[i + 1] != ':') && AttributeTargetNames.Contains(leadingIdentifier))
        {
            i++;
            while (i < content.Length && char.IsWhiteSpace(content[i]))
                i++;
            start = i;
            if (!TryConsumeAttributeName(content, ref i))
                return false;
            if (i == start)
                return false;
        }

        identifier = content[start..i];
        index = i - 1;
        return true;
    }

    private static bool TryConsumeAttributeName(string content, ref int index)
    {
        var consumed = false;
        while (index < content.Length)
        {
            var segmentStart = index;
            while (index < content.Length && (char.IsLetterOrDigit(content[index]) || content[index] == '_'))
                index++;
            if (index == segmentStart)
                break;

            consumed = true;
            if (index + 1 < content.Length && content[index] == ':' && content[index + 1] == ':')
            {
                index += 2;
                continue;
            }

            if (index < content.Length && content[index] == '.')
            {
                index++;
                continue;
            }

            break;
        }

        return consumed;
    }

    private static string? NormalizeAttributeIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var qualifierIndex = identifier.LastIndexOf("::", StringComparison.Ordinal);
        if (qualifierIndex >= 0)
            identifier = identifier[(qualifierIndex + 2)..];

        var lastDot = identifier.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? identifier[(lastDot + 1)..] : identifier;
        if (simpleName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            simpleName = simpleName[..^"Attribute".Length];

        return simpleName.Length == 0 ? null : simpleName.ToLowerInvariant();
    }

    // Pure-trivia classifier used by ExtractNormalizedAttributeNames to skip
    // comment-only rows picked up by the block walker (pure line/block comments
    // and javadoc-style continuation rows). The lone `*/` closing row is
    // already covered by the `StartsWith('*')` check, so we deliberately do
    // NOT flag any line that merely contains `*/` mid-line — that used to
    // discard attribute rows with a trailing block comment
    // (`[JsonPropertyName("ok")] /* note */`) and strip reflection context
    // off the next property. Closes #409 follow-up.
    // ExtractNormalizedAttributeNames がブロック walker に拾われたコメント専用行
    // （純粋な行 / ブロックコメント、javadoc スタイルの継続行）を除外するための
    // 純 trivia 判定。`*/` だけの閉じ行は `StartsWith('*')` で既に拾えるので、
    // 途中に `*/` を含むだけの行はここでは trivia 扱いしない。以前はそれを trivia 扱いして
    // 末尾ブロックコメント付き属性行 `[JsonPropertyName("ok")] /* note */` を落とし、
    // 直下のプロパティから reflection コンテキストを失わせていた。#409 追加修正。
    private static bool BuildSingleLineTrivia(string trimmed)
    {
        return trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    private static bool LooksLikeAttributeBoundaryLine(string line)
    {
        return line.IndexOf('[') >= 0 || line.IndexOf(']') >= 0;
    }

    private static int CountChar(string text, char value)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == value)
                count++;
        }

        return count;
    }

    private static (string Bucket, string Confidence, string Reason) ClassifyUnusedSymbol(bool isPublicOrExported, bool isReflectionOrConfigSuspect, string? visibility)
    {
        if (isReflectionOrConfigSuspect)
        {
            return (
                UnusedBucketReflectionOrConfig,
                "low",
                "public/exported symbol with config or attribute-driven reflection surface and no indexed references");
        }

        if (isPublicOrExported)
        {
            return (
                UnusedBucketPublicOrExported,
                "low",
                "public/exported symbol with no indexed references");
        }

        if (IsPrivateLikeVisibility(visibility))
        {
            return (
                UnusedBucketLikelyPrivate,
                "medium",
                "private/file-local symbol with no indexed references after same-file text validation");
        }

        return (
            UnusedBucketMaybeNonPublic,
            "low",
            "non-public symbol with no indexed references");
    }

    private static bool IsPrivateLikeVisibility(string? visibility)
    {
        return string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "fileprivate", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] OrderedUnusedBuckets =
    [
        UnusedBucketLikelyPrivate,
        UnusedBucketMaybeNonPublic,
        UnusedBucketPublicOrExported,
        UnusedBucketReflectionOrConfig,
    ];

}
