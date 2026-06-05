using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Full-text search operations (partial class split from DbReader.cs).
/// 全文検索操作（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    internal const int FtsUnicode61MaxTokenLength = 1000;
    internal const string AllTokensFilteredByLengthReason = "all_tokens_filtered_by_length";
    internal const int MaxRawFtsQueryLength = 2000;
    internal const int MaxRawFtsBooleanOperators = 64;
    internal const int MaxRawFtsNearOperators = 16;
    internal const int MaxRawFtsParenthesisDepth = 16;
    internal const int MaxRawFtsNearDistance = 100;
    internal const int DefaultSearchGuardWindow = 8;
    internal const int MaxSearchGuardWindow = 200;
    internal const int MaxSearchGuardFilters = 8;
    internal const int MaxGuardedSearchCandidates = 1000;
    private const int MinGuardedSearchCandidates = 200;
    private const int GuardedSearchOverFetchFactor = 50;
    private const int MaxSearchGuardLineWindowCacheEntries = 256;

    /// <summary>
    /// Sanitize user input for FTS5 MATCH by quoting each token as a phrase.
    /// FTS5 MATCH用にユーザー入力をサニタイズ（各トークンをフレーズとして引用）。
    /// Prevents FTS5 syntax errors from special characters (*, ", AND, OR, NOT, NEAR, etc.).
    /// 特殊文字（*, ", AND, OR, NOT, NEAR等）によるFTS5構文エラーを防止する。
    /// When <paramref name="prefix"/> is true, every token is treated as an FTS5 prefix phrase
    /// so callers can opt in to wildcard matching for scripts (notably CJK) that unicode61
    /// tokenizes as a single run.
    /// <paramref name="prefix"/> が true の場合、すべてのトークンを FTS5 prefix phrase として扱い、
    /// unicode61 が単一トークンにまとめる CJK のようなスクリプトでも opt-in で wildcard マッチさせる。
    /// </summary>
    internal static string SanitizeFtsQuery(string query, bool prefix)
    {
        // Escape double quotes inside the query, then wrap each whitespace-separated
        // token in double quotes so FTS5 treats them as literal phrases. A trailing
        // `*` on the user-supplied token is preserved as a prefix-search shorthand so
        // `auth*` can match `authenticate` without requiring raw FTS5 syntax.
        // クエリ内のダブルクォートをエスケープし、各トークンをダブルクォートで囲む。
        // ユーザー入力末尾の `*` は prefix 検索の shorthand として保持し、`auth*` で
        // `authenticate` を raw FTS5 構文なしに検索できるようにする。
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return "\"\"";
        return string.Join(" ", tokens.Select(token => FormatFtsToken(token, prefix)));
    }

    public static FtsQueryDiagnostics AnalyzeFtsQuery(string query, bool rawQuery = false, bool prefix = false, string? lang = null)
    {
        if (rawQuery || string.IsNullOrWhiteSpace(query))
            return FtsQueryDiagnostics.None;

        var normalizedQuery = NormalizeLiteralSearchQuery(query, NormalizeQueryLanguage(lang));
        var tokens = normalizedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Length > 1 && token.EndsWith('*') ? token[..^1] : token)
            .Where(token => token.Length > 0)
            .ToArray();
        if (tokens.Length == 0)
            return FtsQueryDiagnostics.None;

        var tooLong = tokens.Where(token => token.EnumerateRunes().Count() > FtsUnicode61MaxTokenLength).Distinct(StringComparer.Ordinal).ToArray();
        if (tooLong.Length == tokens.Length)
            return new FtsQueryDiagnostics(AllTokensFilteredByLengthReason, tooLong);

        return FtsQueryDiagnostics.None;
    }

    /// <summary>
    /// Build a single FTS5 phrase token. A trailing user-supplied `*` is preserved as a prefix
    /// shorthand; otherwise the token is quoted as a literal phrase. When <paramref name="prefix"/>
    /// is true, the token is additionally upgraded to an FTS5 prefix phrase regardless of the
    /// trailing `*`, so callers (e.g. the `--prefix` CLI flag) can opt in to wildcard matching
    /// without editing every token. The default behavior is strict: searching `計算` matches the
    /// indexed token `計算` only and does NOT widen to `計算する` — users who want the broader
    /// behavior pass `--prefix` (or append `*`) explicitly. This is a deliberate trade-off
    /// (issue #1519): previously every CJK token was unconditionally promoted to a prefix phrase
    /// because FTS5's default unicode61 tokenizer keeps a run of adjacent CJK codepoints as a
    /// single token, but that silently widened exact CJK identifier lookups and broke relevance.
    /// FTS5フレーズトークンを構築する。ユーザー入力末尾の `*` は prefix shorthand として保持し、
    /// それ以外は literal phrase として quote する。<paramref name="prefix"/> が true の場合は
    /// 末尾 `*` の有無に関わらず prefix phrase に昇格させ、`--prefix` のような CLI フラグから
    /// 全トークンを一括で wildcard 化できるようにする。デフォルト挙動は strict — `計算` を引いても
    /// indexed token `計算` のみにマッチし、`計算する` までは広げない。広範囲マッチが必要なら
    /// `--prefix` か末尾 `*` を明示的に付ける。issue #1519 の意図的なトレードオフで、以前は CJK
    /// トークンを無条件に prefix phrase に昇格させていたが（FTS5 既定の unicode61 トークナイザが
    /// 連続する CJK コードポイントを単一トークンとして扱うため）、CJK 識別子の厳密検索が静かに広がり
    /// relevance を壊していた。
    /// </summary>
    private static string FormatFtsToken(string token, bool prefix)
    {
        var hasExplicitPrefix = token.Length > 1 && token.EndsWith('*');
        var literalToken = hasExplicitPrefix ? token[..^1] : token;
        var quoted = "\"" + literalToken.Replace("\"", "\"\"") + "\"";
        // '"phrase"*' (no space) is FTS5 prefix-phrase syntax. '"phrase" *' (with space)
        // means "phrase followed by any token", which is not what we want.
        // '"phrase"*'（スペースなし）がFTS5のprefix phrase構文。スペース有りは別意味なので付けない。
        return (hasExplicitPrefix || prefix) ? quoted + "*" : quoted;
    }

    /// <summary>
    /// Full-text search across indexed chunks using FTS5.
    /// FTS5を使ったチャンク全文検索。
    /// </summary>
    public List<SearchResult> Search(string query, int limit = 20, string? lang = null, bool rawQuery = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool deduplicate = true, DateTime? since = null, bool exact = false, bool prefix = false, bool visibilityRank = true, SearchCursor? cursor = null, IReadOnlyList<SearchGuardFilter>? guardFilters = null, int guardWindow = DefaultSearchGuardWindow)
    {
        // Guard against empty/whitespace queries that would match everything
        // 空白のみのクエリが全件マッチするのを防止
        if (string.IsNullOrWhiteSpace(query))
            return [];

        lang = NormalizeQueryLanguage(lang);
        var normalizedQuery = rawQuery ? query : NormalizeLiteralSearchQuery(query, lang);
        var coverageTokens = exact ? new List<string>() : GetSearchCoverageTokens(normalizedQuery, rawQuery);
        var hasGuardFilters = guardFilters is { Count: > 0 };
        var searchMatchLineContext = SearchMatchLineContext.Create(query, lang, exact);
        var exactSubstringBoost = !exact && !rawQuery && IsPunctuationHeavyLiteralQuery(query);
        var guardedCandidateLimit = hasGuardFilters ? GetGuardedSearchCandidateLimit(limit, cursor) : 0;
        using var cmd = _conn.CreateCommand();
        string sql;

        if (exact)
        {
            // Exact substring match using instr() — case-sensitive, no FTS5 tokenization
            // instr() による完全部分一致検索 — 大文字小文字区別、FTS5トークナイズなし
            sql = $@"
                SELECT f.path, f.lang, c.start_line, c.end_line, c.content,
                       0.0 AS rank,
                       {GetSearchVisibilitySql()} AS visibility,
                       c.id AS chunk_id
                FROM chunks c
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}
                WHERE instr(
                    {GetExactSearchTextSql("c.content", "f.lang")},
                    {GetExactSearchTextSql("@exactQuery", "f.lang")}
                ) > 0";
        }
        else
        {
            var sanitizedQuery = rawQuery ? ValidateRawFtsQuery(query) : SanitizeFtsQuery(normalizedQuery, prefix);
            if (rawQuery)
                ValidateRawFtsNearDistance(sanitizedQuery);
            sql = $@"
                SELECT f.path, f.lang, c.start_line, c.end_line, c.content,
                       rank,
                       {GetSearchVisibilitySql()} AS visibility,
                       c.id AS chunk_id
                FROM fts_chunks
                JOIN chunks c ON fts_chunks.rowid = c.id
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}";
            sql += " WHERE fts_chunks MATCH @query";
            cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        }
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {GetSearchOrderSql(coverageTokens.Count, exactSubstringBoost)}";
        if (hasGuardFilters)
            sql += " LIMIT @candidateFetchLimit";
        else
            sql += " LIMIT @limit";
        if (cursor is { } && !hasGuardFilters)
            sql += " OFFSET @cursorOffset";

        cmd.CommandText = sql;
        if (exact)
            cmd.Parameters.AddWithValue("@exactQuery", query);
        cmd.Parameters.AddWithValue("@rankingQuery", normalizedQuery.Trim());
        cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(normalizedQuery.Trim())}%");
        cmd.Parameters.AddWithValue("@visibilityRank", visibilityRank ? 1 : 0);
        AddSearchCoverageParameters(cmd, coverageTokens);
        if (!hasGuardFilters)
            cmd.Parameters.AddWithValue("@limit", limit);
        else
            cmd.Parameters.AddWithValue("@candidateFetchLimit", guardedCandidateLimit + 1);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        if (cursor is { } searchCursorParameter && !hasGuardFilters)
        {
            cmd.Parameters.AddWithValue("@cursorOffset", searchCursorParameter.Offset);
        }
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var raw = new List<SearchResult>();
        var nextOffset = hasGuardFilters ? 0 : cursor?.Offset ?? 0;
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                nextOffset++;
                raw.Add(new SearchResult
                {
                    Path = reader.GetString(0),
                    Lang = GetNullableString(reader, 1),
                    StartLine = reader.GetInt32(2),
                    EndLine = reader.GetInt32(3),
                    Content = reader.GetString(4),
                    Score = reader.GetDouble(5),
                    Visibility = GetNullableString(reader, 6),
                    ChunkId = reader.GetInt64(7),
                    NextOffset = nextOffset,
                });
            }
        }
        catch (SqliteException ex) when (rawQuery && IsFtsQuerySyntaxError(ex))
        {
            throw new FtsQuerySyntaxException(ex.Message, ex);
        }

        var guardCandidateLimitReached = hasGuardFilters && raw.Count > guardedCandidateLimit;
        if (guardCandidateLimitReached)
            raw.RemoveRange(guardedCandidateLimit, raw.Count - guardedCandidateLimit);

        if (hasGuardFilters)
            raw = FilterBySearchGuards(raw, SearchPrimaryMatchContext.Create(query, normalizedQuery, rawQuery, exact, lang), guardFilters!, guardWindow);

        var results = deduplicate ? DeduplicateOverlappingResults(raw) : raw;
        if (guardCandidateLimitReached && results.Count < GetGuardedSearchRequestedPageEnd(limit, cursor))
            throw new SearchGuardCandidateLimitException(guardedCandidateLimit, limit, cursor?.Offset ?? 0);

        AttachSearchEnclosingSymbols(results, searchMatchLineContext);
        return hasGuardFilters ? PageGuardedSearchResults(results, limit, cursor) : results;
    }

    private static int GetGuardedSearchCandidateLimit(int limit, SearchCursor? cursor)
    {
        var requestedLimit = Math.Max(0L, limit);
        var requestedOffset = Math.Max(0L, cursor?.Offset ?? 0);
        var requestedPageEnd = requestedOffset + requestedLimit;
        if (requestedPageEnd <= 0)
            return 0;

        var overFetched = requestedPageEnd * GuardedSearchOverFetchFactor;
        var candidateLimit = Math.Max(MinGuardedSearchCandidates, overFetched);
        return (int)Math.Min(MaxGuardedSearchCandidates, candidateLimit);
    }

    private static long GetGuardedSearchRequestedPageEnd(int limit, SearchCursor? cursor)
    {
        var requestedLimit = Math.Max(0L, limit);
        var requestedOffset = Math.Max(0L, cursor?.Offset ?? 0);
        return requestedOffset + requestedLimit;
    }

    private void AttachSearchEnclosingSymbols(IReadOnlyList<SearchResult> results, SearchMatchLineContext matchLineContext)
    {
        foreach (var result in results)
        {
            var matchLine = GetFirstSearchMatchLine(result, matchLineContext);
            if (!matchLine.HasValue)
                continue;

            var symbol = GetSearchEnclosingSymbol(result.Path, matchLine.Value);
            if (symbol == null)
                continue;

            result.EnclosingSymbolName = symbol.Name;
            result.EnclosingSymbolKind = symbol.Kind;
            result.EnclosingSymbolStartLine = symbol.StartLine;
            result.EnclosingSymbolEndLine = symbol.EndLine;
            result.EnclosingContainerName = symbol.ContainerName;
        }
    }

    private static int? GetFirstSearchMatchLine(SearchResult result, SearchMatchLineContext context)
    {
        var prepared = context.ForResult(result);
        int? firstTokenMatchLine = null;

        foreach (var (lineIndex, text) in EnumerateContentLines(result.Content))
        {
            var line = prepared.NormalizeLine(text);
            if (!string.IsNullOrWhiteSpace(prepared.NormalizedQuery) &&
                line.Contains(prepared.NormalizedQuery, prepared.Comparison))
            {
                return result.StartLine + lineIndex;
            }

            if (firstTokenMatchLine.HasValue || prepared.Tokens.Length == 0)
                continue;

            if (prepared.Tokens.Any(token => line.Contains(token, prepared.Comparison)))
                firstTokenMatchLine = result.StartLine + lineIndex;
        }

        return firstTokenMatchLine;
    }

    private sealed class SearchMatchLineContext
    {
        private readonly string _query;
        private readonly string? _queryLang;
        private readonly bool _caseSensitive;
        private readonly Dictionary<string, SearchMatchLineTerms> _termsByLang = new(StringComparer.OrdinalIgnoreCase);

        private SearchMatchLineContext(string query, string? queryLang, bool caseSensitive)
        {
            _query = query;
            _queryLang = queryLang;
            _caseSensitive = caseSensitive;
        }

        public static SearchMatchLineContext Create(string query, string? queryLang, bool caseSensitive)
            => new(query, queryLang, caseSensitive);

        public SearchMatchLineTerms ForResult(SearchResult result)
        {
            var lang = _queryLang ?? result.Lang;
            var key = lang ?? string.Empty;
            if (_termsByLang.TryGetValue(key, out var prepared))
                return prepared;

            prepared = SearchMatchLineTerms.Create(_query, lang, _caseSensitive);
            _termsByLang[key] = prepared;
            return prepared;
        }
    }

    private sealed record SearchMatchLineTerms(
        string NormalizedQuery,
        string[] Tokens,
        StringComparison Comparison,
        string? Lang)
    {
        public static SearchMatchLineTerms Create(string query, string? lang, bool caseSensitive)
        {
            var normalizedQuery = ExactSourceSearchNormalizer.Normalize(query.Trim(), lang);
            var tokens = query
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSearchSnippetToken)
                .Where(token => token.Length > 0)
                .Where(token => token is not "AND" and not "OR" and not "NOT" and not "NEAR")
                .Select(token => ExactSourceSearchNormalizer.Normalize(token, lang))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return new SearchMatchLineTerms(normalizedQuery, tokens, comparison, lang);
        }

        public string NormalizeLine(string line) => ExactSourceSearchNormalizer.Normalize(line, Lang);
    }

    private static string NormalizeSearchSnippetToken(string token)
        => token
            .Trim('"', '\'', '(', ')')
            .TrimEnd('*');

    private SearchEnclosingSymbol? GetSearchEnclosingSymbol(string path, int matchLine)
    {
        using var cmd = _conn.CreateCommand();
        var startLineSql = GetSymbolColumnSql("start_line", "s.line", "s");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line", "s");
        var containerNameSql = GetSymbolColumnSql("container_name", symbolAlias: "s");
        cmd.CommandText = $@"
            SELECT s.name,
                   s.kind,
                   {startLineSql} AS start_line,
                   {endLineSql} AS end_line,
                   {containerNameSql} AS container_name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND {startLineSql} <= @matchLine
              AND {endLineSql} >= @matchLine
            ORDER BY CASE s.kind
                         WHEN 'function' THEN 0
                         WHEN 'test.method' THEN 0
                         WHEN 'property' THEN 1
                         WHEN 'class' THEN 2
                         WHEN 'interface' THEN 2
                         WHEN 'struct' THEN 2
                         WHEN 'enum' THEN 2
                         ELSE 3
                     END,
                     ({endLineSql} - {startLineSql}) ASC,
                     {startLineSql} DESC,
                     s.line DESC,
                     s.id ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@matchLine", matchLine);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        return new SearchEnclosingSymbol(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            GetNullableString(reader, 4));
    }

    private sealed record SearchEnclosingSymbol(string Name, string Kind, int StartLine, int EndLine, string? ContainerName);

    public QueryCountResult CountSearchResults(string query, string? lang = null, bool rawQuery = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool deduplicate = true, DateTime? since = null, bool exact = false, bool prefix = false, bool visibilityRank = true, IReadOnlyList<SearchGuardFilter>? guardFilters = null, int guardWindow = DefaultSearchGuardWindow)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new QueryCountResult(0, 0);

        if (guardFilters is { Count: > 0 })
        {
            var guardedResults = Search(query, int.MaxValue, lang, rawQuery, pathPatterns, excludePathPatterns, excludeTests, deduplicate, since, exact, prefix, visibilityRank, guardFilters: guardFilters, guardWindow: guardWindow);
            return new QueryCountResult(guardedResults.Count, guardedResults.Select(result => result.Path).Distinct(StringComparer.Ordinal).Count());
        }

        lang = NormalizeQueryLanguage(lang);
        var normalizedQuery = rawQuery ? query : NormalizeLiteralSearchQuery(query, lang);
        var coverageTokens = exact ? new List<string>() : GetSearchCoverageTokens(normalizedQuery, rawQuery);
        using var cmd = _conn.CreateCommand();
        string sql;

        if (exact)
        {
            sql = $@"
                SELECT f.path, c.start_line, c.end_line,
                       0.0 AS rank
                FROM chunks c
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}
                WHERE instr(
                    {GetExactSearchTextSql("c.content", "f.lang")},
                    {GetExactSearchTextSql("@exactQuery", "f.lang")}
                ) > 0";
        }
        else
        {
            var sanitizedQuery = rawQuery ? ValidateRawFtsQuery(query) : SanitizeFtsQuery(normalizedQuery, prefix);
            if (rawQuery)
                ValidateRawFtsNearDistance(sanitizedQuery);
            sql = $@"
                SELECT f.path, c.start_line, c.end_line,
                       rank
                FROM fts_chunks
                JOIN chunks c ON fts_chunks.rowid = c.id
                JOIN files f ON c.file_id = f.id{SearchSymbolMatchJoinsSql}";
            sql += " WHERE fts_chunks MATCH @query";
            cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        }
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {GetSearchOrderSql(coverageTokens.Count, exactSubstringBoost: false)}";

        cmd.CommandText = sql;
        if (exact)
            cmd.Parameters.AddWithValue("@exactQuery", query);
        cmd.Parameters.AddWithValue("@rankingQuery", normalizedQuery.Trim());
        cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(normalizedQuery.Trim())}%");
        cmd.Parameters.AddWithValue("@visibilityRank", visibilityRank ? 1 : 0);
        AddSearchCoverageParameters(cmd, coverageTokens);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var keptIntervals = new Dictionary<string, IntervalSet>(StringComparer.Ordinal);
        var count = 0;
        var fileCount = 0;
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var path = reader.GetString(0);
                var startLine = reader.GetInt32(1);
                var endLine = reader.GetInt32(2);
                if (!deduplicate)
                {
                    count++;
                    if (!keptIntervals.ContainsKey(path))
                    {
                        keptIntervals[path] = new IntervalSet();
                        fileCount++;
                    }
                    continue;
                }

                if (!keptIntervals.TryGetValue(path, out var intervals))
                {
                    intervals = new IntervalSet();
                    keptIntervals[path] = intervals;
                }

                var hadCoverage = intervals.Count > 0;
                if (!intervals.AddIfAddsCoverage(startLine, endLine))
                    continue;

                count++;
                if (!hadCoverage)
                    fileCount++;
            }
        }
        catch (SqliteException ex) when (rawQuery && IsFtsQuerySyntaxError(ex))
        {
            throw new FtsQuerySyntaxException(ex.Message, ex);
        }

        return new QueryCountResult(count, fileCount);
    }

    private List<SearchResult> FilterBySearchGuards(
        List<SearchResult> results,
        SearchPrimaryMatchContext primaryMatchContext,
        IReadOnlyList<SearchGuardFilter> guardFilters,
        int guardWindow)
    {
        guardWindow = Math.Clamp(guardWindow, 0, MaxSearchGuardWindow);
        var filtered = new List<SearchResult>(results.Count);
        var lineWindowCache = new Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>>();
        foreach (var result in results)
        {
            foreach (var (focusLine, focusText) in FindPrimarySearchMatchLines(result, primaryMatchContext))
            {
                var guardEvidence = new List<SearchGuardEvidence>();
                var keep = true;
                foreach (var filter in guardFilters)
                {
                    var match = FindGuardEvidence(result.Path, focusLine, filter, guardWindow, primaryMatchContext.GetEffectiveLang(result), lineWindowCache);
                    var matched = match != null;
                    if (filter.Role == SearchGuardRole.Require && !matched)
                    {
                        keep = false;
                        break;
                    }
                    if (filter.Role == SearchGuardRole.Reject && matched)
                    {
                        keep = false;
                        break;
                    }
                    if (match != null)
                        guardEvidence.Add(match);
                }

                if (!keep)
                    continue;

                filtered.Add(new SearchResult
                {
                    Path = result.Path,
                    Lang = result.Lang,
                    StartLine = focusLine,
                    EndLine = focusLine,
                    Content = focusText,
                    Score = result.Score,
                    Visibility = result.Visibility,
                    GuardEvidence = guardEvidence.Count == 0 ? null : guardEvidence,
                    ChunkId = result.ChunkId,
                    NextOffset = result.NextOffset,
                });
            }
        }

        return filtered;
    }

    private static List<(int LineNumber, string Text)> FindPrimarySearchMatchLines(SearchResult result, SearchPrimaryMatchContext context)
    {
        if (context.Terms.Length == 0)
        {
            foreach (var (lineIndex, text) in EnumerateContentLines(result.Content))
                return [(result.StartLine + lineIndex, text)];

            return [(result.StartLine, string.Empty)];
        }

        var normalizeCSharp = context.ShouldNormalizeCSharp(result);
        var matches = new List<(int LineNumber, string Text)>();
        foreach (var (lineIndex, text) in EnumerateContentLines(result.Content))
        {
            var line = normalizeCSharp ? CSharpVerbatimNameNormalizer.Normalize(text) : text;
            var lineMatches = context.RequireAllTermsOnLine
                ? context.Terms.All(term => line.Contains(term, context.Comparison))
                : context.Terms.Any(term => line.Contains(term, context.Comparison));
            if (lineMatches)
                matches.Add((result.StartLine + lineIndex, text));
        }

        return matches;
    }

    private sealed record SearchPrimaryMatchContext(
        string[] Terms,
        bool RawQuery,
        bool Exact,
        string? QueryLang)
    {
        public StringComparison Comparison => Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        public bool RequireAllTermsOnLine => !RawQuery && !Exact && Terms.Length > 1;

        public static SearchPrimaryMatchContext Create(string query, string normalizedQuery, bool rawQuery, bool exact, string? queryLang)
            => new(BuildPrimarySearchMatchTerms(query, normalizedQuery, rawQuery, exact), rawQuery, exact, queryLang);

        public string? GetEffectiveLang(SearchResult result) => QueryLang ?? result.Lang;

        public bool ShouldNormalizeCSharp(SearchResult result)
            => string.Equals(GetEffectiveLang(result), "csharp", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildPrimarySearchMatchTerms(string query, string normalizedQuery, bool rawQuery, bool exact)
    {
        IEnumerable<string> rawTerms = !exact && !rawQuery
            ? normalizedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : [rawQuery ? query.Trim() : normalizedQuery.Trim()];
        var terms = rawTerms.Select(NormalizeGuardSearchTerm).ToList();
        if (!exact && rawQuery)
            terms.AddRange(GetSearchCoverageTokens(normalizedQuery, rawQuery));

        return terms
            .Select(NormalizeGuardSearchTerm)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SearchGuardEvidence? FindGuardEvidence(
        string path,
        int focusLine,
        SearchGuardFilter filter,
        int guardWindow,
        string? lang,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        var windowStart = filter.Direction == SearchGuardDirection.Before
            ? Math.Max(1, focusLine - guardWindow)
            : focusLine + 1;
        var windowEnd = filter.Direction == SearchGuardDirection.Before
            ? Math.Max(0, focusLine - 1)
            : focusLine + guardWindow;

        if (windowEnd < windowStart)
            return null;

        var lineWindow = ReadLineWindow(path, windowStart, windowEnd, lineWindowCache);
        if (lineWindow.Count == 0)
            return null;

        var guardQuery = NormalizeGuardQuery(filter.Query, lang);
        if (guardQuery.Length == 0)
            return null;

        foreach (var (lineNumber, text) in lineWindow)
        {
            var candidate = string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase)
                ? CSharpVerbatimNameNormalizer.Normalize(text)
                : text;
            if (!candidate.Contains(guardQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            return new SearchGuardEvidence
            {
                Role = FormatSearchGuardRole(filter.Role),
                Direction = FormatSearchGuardDirection(filter.Direction),
                Query = filter.Query,
                Line = lineNumber,
                Text = text,
            };
        }

        return null;
    }

    private SortedDictionary<int, string> ReadLineWindow(
        string path,
        int startLine,
        int endLine,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache)
    {
        var key = new SearchGuardLineWindowKey(path, startLine, endLine);
        if (lineWindowCache.TryGetValue(key, out var cached))
            return cached;

        var lineWindow = ReadLineWindow(path, startLine, endLine);
        if (lineWindowCache.Count < MaxSearchGuardLineWindowCacheEntries)
            lineWindowCache[key] = lineWindow;
        return lineWindow;
    }

    private SortedDictionary<int, string> ReadLineWindow(string path, int startLine, int endLine)
    {
        var linesByNumber = new SortedDictionary<int, string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.start_line, c.content
            FROM chunks c
            JOIN files f ON c.file_id = f.id
            WHERE f.path = @path
              AND c.end_line >= @startLine
              AND c.start_line <= @endLine
            ORDER BY c.start_line ASC, c.id ASC";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@startLine", startLine);
        cmd.Parameters.AddWithValue("@endLine", endLine);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var chunkStartLine = reader.GetInt32(0);
            var relativeStart = Math.Max(0, startLine - chunkStartLine);
            var relativeEnd = Math.Max(relativeStart, endLine - chunkStartLine);
            foreach (var (lineOffset, text) in EnumerateContentLines(reader.GetString(1), relativeStart, relativeEnd))
            {
                var lineNumber = chunkStartLine + lineOffset;
                if (lineNumber < startLine || lineNumber > endLine)
                    continue;
                linesByNumber.TryAdd(lineNumber, text);
            }
        }

        return linesByNumber;
    }

    private readonly record struct SearchGuardLineWindowKey(string Path, int StartLine, int EndLine);

    private static List<SearchResult> PageGuardedSearchResults(List<SearchResult> results, int limit, SearchCursor? cursor)
    {
        var offset = Math.Max(0, cursor?.Offset ?? 0);
        var page = results.Skip(offset).Take(Math.Max(0, limit)).ToList();
        for (var i = 0; i < page.Count; i++)
            page[i].NextOffset = offset + i + 1;
        return page;
    }

    private static string NormalizeGuardQuery(string query, string? lang)
    {
        var normalized = NormalizeGuardSearchTerm(query.Normalize(NormalizationForm.FormC));
        return string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase)
            ? CSharpVerbatimNameNormalizer.Normalize(normalized)
            : normalized;
    }

    private static string NormalizeGuardSearchTerm(string value)
        => value.Trim().Trim('"', '\'', '(', ')').TrimEnd('*');

    private static string[] SplitContentLines(string content)
        => content.Replace("\r\n", "\n").Split('\n');

    private static IEnumerable<(int Index, string Text)> EnumerateContentLines(string content) =>
        EnumerateContentLines(content, startIndex: 0, endIndex: int.MaxValue);

    private static IEnumerable<(int Index, string Text)> EnumerateContentLines(string content, int startIndex, int endIndex)
    {
        if (endIndex < startIndex)
            yield break;

        var lineStart = 0;
        var lineIndex = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            var lineEnd = i;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
                lineEnd--;
            if (lineIndex > endIndex)
                yield break;
            if (lineIndex >= startIndex)
                yield return (lineIndex, content[lineStart..lineEnd]);
            lineIndex++;
            lineStart = i + 1;
        }

        if (lineIndex >= startIndex && lineIndex <= endIndex)
            yield return (lineIndex, content[lineStart..]);
    }

    private static string FormatSearchGuardRole(SearchGuardRole role)
        => role == SearchGuardRole.Require ? "require" : "reject";

    private static string FormatSearchGuardDirection(SearchGuardDirection direction)
        => direction == SearchGuardDirection.Before ? "before" : "after";

    private static string NormalizeLiteralSearchQuery(string query, string? lang)
    {
        var normalized = query.Normalize(NormalizationForm.FormC);
        return string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase)
            ? CSharpVerbatimNameNormalizer.Normalize(normalized)
            : normalized;
    }

    internal static string ValidateRawFtsQuery(string query)
    {
        if (query.Length > MaxRawFtsQueryLength)
            throw new FtsQuerySyntaxException($"raw FTS5 query is too long ({query.Length} characters); maximum is {MaxRawFtsQueryLength}. Split the query or drop `--fts` for literal-safe search.");

        ValidateRawFtsColumns(query);

        var booleanOperators = 0;
        var nearOperators = 0;
        var depth = 0;
        var maxDepth = 0;
        var inQuote = false;

        for (var i = 0; i < query.Length; i++)
        {
            var ch = query[i];
            if (ch == '"')
            {
                if (inQuote && i + 1 < query.Length && query[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
                continue;

            if (ch == '(')
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
                continue;
            }
            if (ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (!IsFtsIdentifierStart(ch))
                continue;

            var start = i;
            i++;
            while (i < query.Length && IsFtsIdentifierPart(query[i]))
                i++;
            var length = i - start;
            i--;

            if (TokenEquals(query, start, length, "AND") || TokenEquals(query, start, length, "OR") || TokenEquals(query, start, length, "NOT"))
                booleanOperators++;
            else if (TokenEquals(query, start, length, "NEAR"))
                nearOperators++;
        }

        if (booleanOperators > MaxRawFtsBooleanOperators)
            throw new FtsQuerySyntaxException($"raw FTS5 query is too complex ({booleanOperators} boolean operators); maximum is {MaxRawFtsBooleanOperators}. Split the query or drop `--fts` for literal-safe search.");
        if (nearOperators > MaxRawFtsNearOperators)
            throw new FtsQuerySyntaxException($"raw FTS5 query is too complex ({nearOperators} NEAR operators); maximum is {MaxRawFtsNearOperators}. Split the query or drop `--fts` for literal-safe search.");
        if (maxDepth > MaxRawFtsParenthesisDepth)
            throw new FtsQuerySyntaxException($"raw FTS5 query is too deeply nested (parenthesis depth {maxDepth}); maximum is {MaxRawFtsParenthesisDepth}. Split the query or drop `--fts` for literal-safe search.");

        return query;
    }

    private static bool TokenEquals(string query, int start, int length, string value)
        => length == value.Length && string.Compare(query, start, value, 0, value.Length, StringComparison.Ordinal) == 0;

    private static bool IsFtsIdentifierStart(char ch)
        => char.IsLetter(ch) || ch == '_';

    private static bool IsFtsIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private static bool IsFtsQuerySyntaxError(SqliteException ex)
    {
        var message = ex.Message;
        return message.Contains("fts5: syntax error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unterminated string", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such column", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRawFtsColumns(string query)
    {
        var inQuote = false;

        for (var i = 0; i < query.Length; i++)
        {
            var ch = query[i];
            if (ch == '"')
            {
                if (inQuote && i + 1 < query.Length && query[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inQuote = !inQuote;
                continue;
            }

            if (inQuote || ch != ':')
                continue;

            if (i > 0 && query[i - 1] == '}')
            {
                ValidateRawFtsColumnList(query, i);
                continue;
            }

            var end = i;
            var start = end - 1;
            while (start >= 0 && IsFtsColumnIdentifierChar(query[start]))
                start--;
            start++;

            if (start == end)
                continue;

            ValidateRawFtsColumn(query[start..end]);
        }

    }

    private static void ValidateRawFtsColumnList(string query, int colonIndex)
    {
        var start = colonIndex - 2;
        while (start >= 0 && query[start] != '{')
            start--;

        if (start < 0)
            return;

        var columns = query[(start + 1)..(colonIndex - 1)]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var column in columns)
            ValidateRawFtsColumn(column);
    }

    private static void ValidateRawFtsColumn(string column)
    {
        const string validColumn = "content";
        if (string.Equals(column, validColumn, StringComparison.OrdinalIgnoreCase))
            return;

        throw new FtsQuerySyntaxException(
            $"unknown FTS5 column qualifier '{column}:'. The fts_chunks index only exposes the '{validColumn}' column; use '{validColumn}:' or drop the qualifier.",
            new ArgumentException("Unknown FTS5 column qualifier.", nameof(column)));
    }

    private static bool IsFtsColumnIdentifierChar(char ch)
        => ch == '_' || char.IsAsciiLetterOrDigit(ch);

    private static void ValidateRawFtsNearDistance(string query)
    {
        var inQuote = false;
        for (var i = 0; i < query.Length; i++)
        {
            if (query[i] == '"')
            {
                if (inQuote && i + 1 < query.Length && query[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
                continue;

            if (!IsNearOperatorAt(query, i))
                continue;

            var openParen = SkipWhitespace(query, i + "NEAR".Length);
            if (openParen >= query.Length || query[openParen] != '(')
                continue;

            var closeParen = FindNearCloseParen(query, openParen + 1);
            if (closeParen < 0)
                continue;

            if (TryReadNearDistance(query.AsSpan(openParen + 1, closeParen - openParen - 1), out var distance)
                && (distance < 0 || distance > MaxRawFtsNearDistance))
            {
                throw new FtsQuerySyntaxException(
                    $"FTS5 NEAR distance must be between 0 and {MaxRawFtsNearDistance}, got {distance}.");
            }

            i = closeParen;
        }
    }

    private static bool IsNearOperatorAt(string query, int index)
    {
        if (index + "NEAR".Length > query.Length)
            return false;
        if (!query.AsSpan(index, "NEAR".Length).Equals("NEAR", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsFtsBoundary(query, index - 1) && IsFtsBoundary(query, index + "NEAR".Length);
    }

    private static bool IsFtsBoundary(string query, int index)
        => index < 0 || index >= query.Length || !char.IsLetterOrDigit(query[index]) && query[index] != '_';

    private static int SkipWhitespace(string query, int index)
    {
        while (index < query.Length && char.IsWhiteSpace(query[index]))
            index++;
        return index;
    }

    private static int FindNearCloseParen(string query, int index)
    {
        var inQuote = false;
        while (index < query.Length)
        {
            var ch = query[index];
            if (ch == '"')
            {
                if (inQuote && index + 1 < query.Length && query[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                inQuote = !inQuote;
            }
            else if (!inQuote && ch == ')')
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool TryReadNearDistance(ReadOnlySpan<char> nearBody, out long distance)
    {
        distance = 0;
        var lastComma = nearBody.LastIndexOf(',');
        var candidate = (lastComma >= 0 ? nearBody[(lastComma + 1)..] : nearBody).Trim();
        if (candidate.Length == 0)
            return false;

        if (long.TryParse(candidate.ToString(), out distance))
            return true;

        if (!IsSignedIntegerLiteral(candidate))
            return false;

        distance = MaxRawFtsNearDistance + 1L;
        return true;
    }

    private static bool IsSignedIntegerLiteral(ReadOnlySpan<char> value)
    {
        var start = value[0] is '+' or '-' ? 1 : 0;
        if (start == value.Length)
            return false;

        for (var i = start; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
                return false;
        }

        return true;
    }

    private static string GetExactSearchTextSql(string valueSql, string langSql)
        => $"CASE WHEN {langSql} IN ('csharp', 'java', 'kotlin') THEN sql_normalize_exact_source_name({valueSql}, {langSql}) " +
           $"WHEN {langSql} = 'sql' THEN sql_normalize_name({valueSql}) " +
           $"ELSE {valueSql} END";

    /// <summary>
    /// Remove search results that are fully covered by higher-ranked results in the same file.
    /// Chunks use 10-line overlap, so adjacent chunks can produce duplicate matches, but a
    /// later chunk may still contain legitimate hits outside the overlap.
    /// 同じファイル内で上位の結果に完全包含される結果を除去する。
    /// チャンクは10行重複するが、後続チャンクは重複範囲外の正当なヒットを含みうる。
    /// </summary>
    private static List<SearchResult> DeduplicateOverlappingResults(List<SearchResult> results)
    {
        if (results.Count <= 1)
            return results;

        var keptIntervals = new Dictionary<string, IntervalSet>(StringComparer.Ordinal);
        var deduped = new List<SearchResult>();
        foreach (var r in results)
        {
            if (!keptIntervals.TryGetValue(r.Path, out var intervals))
            {
                intervals = new IntervalSet();
                keptIntervals[r.Path] = intervals;
            }

            if (!intervals.AddIfAddsCoverage(r.StartLine, r.EndLine))
                continue;

            deduped.Add(r);
        }
        return deduped;
    }

    private sealed class IntervalSet
    {
        private readonly List<(int Start, int End)> _intervals = [];

        public int Count => _intervals.Count;

        public bool AddIfAddsCoverage(int start, int end)
        {
            if (end < start)
                (start, end) = (end, start);

            var insertIndex = FindInsertIndex(start);
            var mergeStart = start;
            var mergeEnd = end;
            var firstMergeIndex = insertIndex;

            if (insertIndex > 0 && OverlapsOrTouches(_intervals[insertIndex - 1], start, end))
            {
                var previous = _intervals[insertIndex - 1];
                if (previous.Start <= start && previous.End >= end)
                    return false;

                firstMergeIndex = insertIndex - 1;
                mergeStart = Math.Min(mergeStart, previous.Start);
                mergeEnd = Math.Max(mergeEnd, previous.End);
            }

            var removeCount = 0;
            var scanIndex = firstMergeIndex;
            while (scanIndex < _intervals.Count && OverlapsOrTouches(_intervals[scanIndex], mergeStart, mergeEnd))
            {
                var current = _intervals[scanIndex];
                if (current.Start <= start && current.End >= end)
                    return false;

                mergeStart = Math.Min(mergeStart, current.Start);
                mergeEnd = Math.Max(mergeEnd, current.End);
                removeCount++;
                scanIndex++;
            }

            if (removeCount > 0)
                _intervals.RemoveRange(firstMergeIndex, removeCount);

            _intervals.Insert(firstMergeIndex, (mergeStart, mergeEnd));
            return true;
        }

        private int FindInsertIndex(int start)
        {
            var low = 0;
            var high = _intervals.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (_intervals[mid].Start < start)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        private static bool OverlapsOrTouches((int Start, int End) interval, int start, int end)
        {
            return interval.Start <= end + 1 && interval.End + 1 >= start;
        }
    }

    private static string GetSearchOrderSql(int coverageTokenCount, bool exactSubstringBoost)
    {
        var coverageOrder = GetSearchCoverageOrderSql(coverageTokenCount);
        var exactSubstringOrder = exactSubstringBoost
            ? $"CASE WHEN instr({GetExactSearchTextSql("c.content", "f.lang")}, {GetExactSearchTextSql("@rankingQuery", "f.lang")}) > 0 THEN 0 ELSE 1 END, "
            : string.Empty;
        return $"{PathBucketOrder}, {exactSubstringOrder}{ExactSymbolMatchOrder}, {PrefixSymbolMatchOrder}, {SearchVisibilityOrder}, {PathTextMatchOrder}, {ChunkTextMatchOrder}, {ChunkStructuredFieldOrder}, {ChunkSymbolKindOrder}, {ChunkSymbolDepthOrder}, {coverageOrder}rank, f.modified DESC, f.path, c.id ASC";
    }

    private static bool IsPunctuationHeavyLiteralQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0 || !trimmed.Any(char.IsLetterOrDigit))
            return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var punctuationCount = trimmed.Count(IsCodePunctuation);
        return punctuationCount >= 2 || tokens.Any(IsStandaloneCodeOperatorToken);
    }

    private static bool IsStandaloneCodeOperatorToken(string token)
        => token.Length > 0
            && token.All(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ch != '_')
            && token.Any(IsCodePunctuation);

    private static bool IsCodePunctuation(char ch)
    {
        if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '_')
            return false;

        return ch is '.'
            or ':' or ';' or ','
            or '=' or '$' or '@' or '#'
            or '%' or '^' or '&' or '|'
            or '!' or '?' or '+' or '-'
            or '*' or '/' or '\\'
            or '<' or '>'
            or '(' or ')' or '[' or ']'
            or '{' or '}'
            or '"' or '\'' or '`' or '~';
    }

    private static string GetSearchCoverageOrderSql(int coverageTokenCount)
    {
        if (coverageTokenCount <= 1)
            return string.Empty;

        var matchedTerms = string.Join(" + ", Enumerable.Range(0, coverageTokenCount)
            .Select(i => $"CASE WHEN instr(lower(c.content), @coverageToken{i}) > 0 THEN 1 ELSE 0 END"));
        return $"CASE WHEN (({matchedTerms}) * 2) >= {coverageTokenCount} THEN 0 ELSE 1 END, ";
    }

    private static List<string> GetSearchCoverageTokens(string query, bool rawQuery)
    {
        var tokens = rawQuery ? ExtractRawFtsCoverageTokens(query) : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
            return [];

        return tokens
            .Select(GetSearchCoverageToken)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string GetSearchCoverageToken(string token)
    {
        var literalToken = token.Length > 1 && token.EndsWith('*') ? token[..^1] : token.Trim('"');
        return literalToken.ToLowerInvariant();
    }

    private static string[] ExtractRawFtsCoverageTokens(string query)
    {
        var tokens = new List<string>();
        for (var i = 0; i < query.Length; i++)
        {
            var ch = query[i];
            if (ch == '"')
            {
                var start = ++i;
                while (i < query.Length)
                {
                    if (query[i] == '"' && i + 1 < query.Length && query[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    if (query[i] == '"')
                        break;
                    i++;
                }

                if (i > start)
                    tokens.Add(query[start..i].Replace("\"\"", "\"", StringComparison.Ordinal));
                continue;
            }

            if (!IsFtsIdentifierStart(ch))
                continue;

            var startIdentifier = i;
            i++;
            while (i < query.Length && IsFtsIdentifierPart(query[i]))
                i++;

            var token = query[startIdentifier..i];
            if (!IsRawFtsOperatorToken(token) && !IsRawFtsColumnQualifierToken(query, startIdentifier, i))
                tokens.Add(token);
            i--;
        }

        return tokens.ToArray();
    }

    private static bool IsRawFtsColumnQualifierToken(string query, int tokenStart, int tokenEnd)
    {
        var afterToken = SkipWhitespace(query, tokenEnd);
        if (afterToken < query.Length && query[afterToken] == ':')
            return true;

        var columnListStart = query.LastIndexOf('{', tokenStart);
        if (columnListStart < 0)
            return false;

        var columnListEnd = query.IndexOf('}', tokenEnd);
        if (columnListEnd < 0)
            return false;

        var afterColumnList = SkipWhitespace(query, columnListEnd + 1);
        return afterColumnList < query.Length && query[afterColumnList] == ':';
    }

    private static bool IsRawFtsOperatorToken(string token)
        => string.Equals(token, "AND", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "OR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "NOT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "NEAR", StringComparison.OrdinalIgnoreCase);

    private static void AddSearchCoverageParameters(SqliteCommand cmd, IReadOnlyList<string> coverageTokens)
    {
        for (var i = 0; i < coverageTokens.Count; i++)
            cmd.Parameters.AddWithValue($"@coverageToken{i}", coverageTokens[i]);
    }

    private static string SearchVisibilityOrder => @"
        CASE
            WHEN @visibilityRank = 0 THEN 0
            ELSE COALESCE(exact_symbol_match.visibility_order, prefix_symbol_match.visibility_order, 4)
        END";

    private static string GetSearchVisibilitySql() => @"
        CASE
            WHEN exact_symbol_match.visibility IS NOT NULL THEN exact_symbol_match.visibility
            ELSE prefix_symbol_match.visibility
        END";

}
