using System.Text.Json.Serialization;
using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Formats human-readable search snippets around the first matching line.
/// 人間向け検索スニペットを最初の一致行の前後で整形する。
/// </summary>
public static class SearchSnippetFormatter
{
    public const int DefaultSnippetLines = 8;
    public const int MaxSnippetLines = 20;

    public static SearchSnippetQueryContext PrepareQueryContext(string query)
    {
        var normalizedQuery = query.Trim();
        return new SearchSnippetQueryContext(
            query,
            new SearchSnippetPreparedQuery(normalizedQuery, BuildQueryTokens(query, normalizeCSharpVerbatimNames: false), NormalizeCSharpVerbatimNames: false),
            new SearchSnippetPreparedQuery(CSharpVerbatimNameNormalizer.Normalize(normalizedQuery), BuildQueryTokens(query, normalizeCSharpVerbatimNames: true), NormalizeCSharpVerbatimNames: true));
    }

    public static IReadOnlyList<string> Format(string content, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality)
    {
        return Format(content, PrepareQueryContext(query), maxLines, caseSensitive, maxLineWidth, lang, focusMode);
    }

    public static IReadOnlyList<string> Format(string content, SearchSnippetQueryContext queryContext, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality)
    {
        ArgumentNullException.ThrowIfNull(queryContext);

        var excerpt = BuildExcerpt(content, queryContext, absoluteStartLine: 1, maxLines, caseSensitive, maxLineWidth, lang, focusMode);
        if (excerpt.Lines.Count == 0)
            return [];

        var snippet = new List<string>();
        if (excerpt.TruncatedBefore)
            snippet.Add("...");

        snippet.AddRange(excerpt.Lines);

        if (excerpt.TruncatedAfter)
            snippet.Add("...");

        return snippet;
    }

    public static CompactSearchResult ToCompactResult(SearchResult result, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        return ToCompactResult(result, PrepareQueryContext(query), maxLines, caseSensitive, maxLineWidth, lang, focusMode, exposeLiteralHighlights);
    }

    public static CompactSearchResult ToCompactResult(SearchResult result, SearchSnippetQueryContext queryContext, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        ArgumentNullException.ThrowIfNull(queryContext);

        var excerpt = BuildExcerpt(result.Content, queryContext, result.StartLine, maxLines, caseSensitive, maxLineWidth, lang ?? result.Lang, focusMode, exposeLiteralHighlights);
        return new CompactSearchResult
        {
            Query = queryContext.Query,
            Path = result.Path,
            Lang = result.Lang,
            Visibility = result.Visibility,
            ChunkStartLine = result.StartLine,
            ChunkEndLine = result.EndLine,
            SnippetStartLine = excerpt.StartLine,
            SnippetEndLine = excerpt.EndLine,
            Snippet = string.Join('\n', excerpt.Lines),
            MatchLines = excerpt.MatchLines,
            Highlights = excerpt.Highlights,
            ContextBefore = excerpt.ContextBefore,
            ContextAfter = excerpt.ContextAfter,
            TruncatedLineCount = excerpt.TruncatedLineCount,
            DroppedMatchLineCount = excerpt.DroppedMatchLineCount,
            TruncationContext = excerpt.TruncationContext,
            GuardEvidence = result.GuardEvidence,
            Score = result.Score,
            EnclosingSymbolName = result.EnclosingSymbolName,
            EnclosingSymbolKind = result.EnclosingSymbolKind,
            EnclosingSymbolStartLine = result.EnclosingSymbolStartLine,
            EnclosingSymbolEndLine = result.EnclosingSymbolEndLine,
            EnclosingContainerName = result.EnclosingContainerName,
        };
    }

    public static IEnumerable<CompactSearchResult> ToCompactResults(IEnumerable<SearchResult> results, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        var queryContext = PrepareQueryContext(query);
        return ToCompactResults(results, queryContext, maxLines, caseSensitive, maxLineWidth, lang, focusMode, exposeLiteralHighlights);
    }

    public static IEnumerable<CompactSearchResult> ToCompactResults(IEnumerable<SearchResult> results, SearchSnippetQueryContext queryContext, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        ArgumentNullException.ThrowIfNull(queryContext);

        foreach (var result in results)
            yield return ToCompactResult(result, queryContext, maxLines, caseSensitive, maxLineWidth, lang ?? result.Lang, focusMode, exposeLiteralHighlights);
    }

    public static SearchSnippetExcerpt BuildExcerpt(string content, string query, int absoluteStartLine, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        return BuildExcerpt(content, PrepareQueryContext(query), absoluteStartLine, maxLines, caseSensitive, maxLineWidth, lang, focusMode, exposeLiteralHighlights);
    }

    public static SearchSnippetExcerpt BuildExcerpt(string content, SearchSnippetQueryContext queryContext, int absoluteStartLine, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality, bool exposeLiteralHighlights = false)
    {
        ArgumentNullException.ThrowIfNull(queryContext);

        maxLines = ClampSnippetLines(maxLines);
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);

        var queryForLanguage = queryContext.ForLanguage(lang);
        var normalizedQuery = queryForLanguage.NormalizedQuery;
        var tokens = queryForLanguage.Tokens;
        var normalizeCSharpVerbatimNames = queryForLanguage.NormalizeCSharpVerbatimNames;

        var matchScan = FindMatchingLineIndexes(content, normalizedQuery, tokens, caseSensitive, normalizeCSharpVerbatimNames, maxLines);
        var lineCount = matchScan.LineCount;
        if (lineCount == 0)
        {
            return new SearchSnippetExcerpt
            {
                StartLine = absoluteStartLine,
                EndLine = absoluteStartLine,
            };
        }

        var matchIndexes = matchScan.MatchIndexes;
        var focusStart = matchIndexes.Count > 0 ? matchIndexes[0] : 0;
        var focusEnd = focusStart;
        var includedMatchLineCount = matchIndexes.Count > 0 ? 1 : 0;
        foreach (var matchIndex in matchIndexes.Skip(1))
        {
            if ((matchIndex - focusStart) + 1 > maxLines)
                break;

            focusEnd = matchIndex;
            includedMatchLineCount++;
        }
        var droppedMatchLineCount = Math.Max(0, matchScan.TotalMatchCount - includedMatchLineCount);

        var focusLength = Math.Max(1, (focusEnd - focusStart) + 1);
        var remaining = Math.Max(0, maxLines - focusLength);
        var before = remaining / 2;
        var after = remaining - before;

        var start = Math.Max(0, focusStart - before);
        var end = Math.Min(lineCount - 1, focusEnd + after);
        while ((end - start) + 1 < maxLines)
        {
            if (start > 0)
            {
                start--;
                continue;
            }

            if (end < lineCount - 1)
            {
                end++;
                continue;
            }

            break;
        }

        var matchSet = matchIndexes.ToHashSet();
        var matchLines = new List<int>();
        var highlights = new List<SearchHighlight>();
        var clampedLines = new List<string>((end - start) + 1);
        var truncatedCharCounts = new List<int>();
        var truncatedLineCount = 0;
        var snippetLines = ReadSnippetLines(content, start, end, normalizeCSharpVerbatimNames);

        foreach (var snippetLine in snippetLines)
        {
            var i = snippetLine.Index;
            var originalLine = snippetLine.Text;
            var isMatch = matchSet.Contains(i);
            ClampedTextResult clamped;
            if (normalizeCSharpVerbatimNames && isMatch && snippetLine.NormalizedText != null && snippetLine.RawIndexMap != null)
            {
                clamped = ClampNormalizedSnippetLine(originalLine, snippetLine.NormalizedText, snippetLine.RawIndexMap, maxLineWidth, normalizedQuery, tokens, caseSensitive, focusMode);
            }
            else
            {
                clamped = ClampSnippetLine(originalLine, maxLineWidth, isMatch ? normalizedQuery : null, tokens, caseSensitive, focusMode);
            }
            clampedLines.Add(clamped.Text);
            if (clamped.Truncated)
            {
                truncatedLineCount++;
                truncatedCharCounts.Add(clamped.TruncatedCharCount);
            }

            if (!isMatch)
                continue;

            var absoluteLine = absoluteStartLine + i;
            matchLines.Add(absoluteLine);
            var matchLineForTerms = normalizeCSharpVerbatimNames && snippetLine.NormalizedText != null ? snippetLine.NormalizedText : originalLine;
            var termOccurrences = normalizeCSharpVerbatimNames && snippetLine.NormalizedText != null && snippetLine.RawIndexMap != null
                ? GetMatchedTermOccurrences(snippetLine.NormalizedText, absoluteLine, normalizedQuery, tokens, caseSensitive, originalLine, snippetLine.RawIndexMap)
                : GetMatchedTermOccurrences(originalLine, absoluteLine, normalizedQuery, tokens, caseSensitive);
            var literalTermOccurrences = exposeLiteralHighlights
                ? normalizeCSharpVerbatimNames && snippetLine.NormalizedText != null && snippetLine.RawIndexMap != null
                    ? GetMatchedTermOccurrences(snippetLine.NormalizedText, absoluteLine, normalizedQuery, [], caseSensitive, originalLine, snippetLine.RawIndexMap)
                    : GetMatchedTermOccurrences(originalLine, absoluteLine, normalizedQuery, [], caseSensitive)
                : null;
            highlights.Add(new SearchHighlight
            {
                Line = absoluteLine,
                Text = clamped.Text,
                OriginalLineLength = originalLine.Length,
                Truncated = clamped.Truncated,
                TruncatedCharCounts = clamped.Truncated ? [clamped.TruncatedCharCount] : [],
                Terms = GetMatchedTerms(matchLineForTerms, normalizedQuery, tokens, caseSensitive),
                TermOccurrences = termOccurrences,
                LiteralTerms = exposeLiteralHighlights ? GetMatchedTerms(matchLineForTerms, normalizedQuery, [], caseSensitive) : null,
                LiteralTermOccurrences = literalTermOccurrences,
            });
        }

        return new SearchSnippetExcerpt
        {
            StartLine = absoluteStartLine + start,
            EndLine = absoluteStartLine + end,
            Lines = clampedLines,
            MatchLines = matchLines,
            Highlights = highlights,
            ContextBefore = focusStart - start,
            ContextAfter = end - focusEnd,
            TruncatedBefore = start > 0,
            TruncatedAfter = end < lineCount - 1,
            TruncatedLineCount = truncatedLineCount,
            DroppedMatchLineCount = droppedMatchLineCount,
            TruncationContext = new SearchTruncationContext
            {
                LineCount = truncatedLineCount,
                CharCounts = truncatedCharCounts,
                TotalChars = truncatedCharCounts.Sum(),
            },
        };
    }

    // Clamp one snippet line, keeping the strongest match visible on match lines.
    // 一致行では最も強い一致を残して幅を切り詰める。
    private static ClampedTextResult ClampSnippetLine(string line, int maxLineWidth, string? normalizedQuery, string[] tokens, bool caseSensitive, SearchSnippetFocusMode focusMode)
    {
        if (line.Length <= maxLineWidth)
            return new ClampedTextResult(line, false);

        if (normalizedQuery == null)
            return LineWidthFormatter.ClampLine(line, maxLineWidth);

        var (matchColumn, matchLength) = FindBestMatchColumn(line, normalizedQuery, tokens, caseSensitive, focusMode);
        if (matchColumn <= 0)
            return LineWidthFormatter.ClampLine(line, maxLineWidth);

        return LineWidthFormatter.ClampLine(line, maxLineWidth, matchColumn, matchLength);
    }

    private static ClampedTextResult ClampNormalizedSnippetLine(string originalLine, string normalizedLine, int[] rawIndexMap, int maxLineWidth, string normalizedQuery, string[] tokens, bool caseSensitive, SearchSnippetFocusMode focusMode)
    {
        var (matchColumn, matchLength) = FindBestMatchColumn(normalizedLine, normalizedQuery, tokens, caseSensitive, focusMode);
        if (matchColumn <= 0)
            return LineWidthFormatter.ClampLine(originalLine, maxLineWidth);

        if (!TryReconstructRawSpan(rawIndexMap, normalizedStart: matchColumn - 1, matchLength, out var rawFocusColumn, out var rawFocusLength))
            return LineWidthFormatter.ClampLine(originalLine, maxLineWidth);

        return LineWidthFormatter.ClampLine(originalLine, maxLineWidth, rawFocusColumn, rawFocusLength);
    }

    internal static bool TryReconstructRawSpan(int[] rawIndexMap, int normalizedStart, int matchLength, out int rawFocusColumn, out int rawFocusLength)
    {
        rawFocusColumn = 0;
        rawFocusLength = 0;

        if (normalizedStart < 0 || matchLength <= 0)
            return false;

        var normalizedEnd = normalizedStart + matchLength - 1;
        if (normalizedEnd < normalizedStart || normalizedEnd >= rawIndexMap.Length)
            return false;

        var rawStart = rawIndexMap[normalizedStart];
        var rawEnd = rawIndexMap[normalizedEnd];
        if (rawStart < 0 || rawEnd < rawStart)
            return false;

        rawFocusColumn = rawStart + 1;
        rawFocusLength = rawEnd - rawStart + 1;
        return true;
    }

    private static (int Column, int Length) FindBestMatchColumn(string line, string normalizedQuery, string[] tokens, bool caseSensitive, SearchSnippetFocusMode focusMode)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (focusMode == SearchSnippetFocusMode.Leftmost)
            return FindLeftmostMatchColumn(line, normalizedQuery, tokens, comparison);

        MatchCandidate best = default;
        var fullQueryScore = focusMode == SearchSnippetFocusMode.Proximity ? 3_000 : 4_000;
        var clusterScore = focusMode == SearchSnippetFocusMode.Proximity ? 4_000 : 2_000;
        if (!string.IsNullOrEmpty(normalizedQuery))
        {
            var idx = 0;
            while ((idx = line.IndexOf(normalizedQuery, idx, comparison)) >= 0)
            {
                best = ChooseBetter(best, new MatchCandidate(idx, normalizedQuery.Length, fullQueryScore + ScoreLength(normalizedQuery.Length), null));
                idx += Math.Max(1, normalizedQuery.Length);
            }
        }

        var tokenOccurrences = new List<MatchCandidate>();
        foreach (var token in tokens)
        {
            if (token.Length == 0)
                continue;

            var idx = 0;
            while ((idx = line.IndexOf(token, idx, comparison)) >= 0)
            {
                var occurrence = new MatchCandidate(idx, token.Length, 1_000 + ScoreLength(token.Length), token);
                tokenOccurrences.Add(occurrence);
                best = ChooseBetter(best, occurrence);
                idx += Math.Max(1, token.Length);
            }
        }

        if (tokenOccurrences.Count > 1)
        {
            const int proximityWindowColumns = 80;
            for (int i = 0; i < tokenOccurrences.Count; i++)
            {
                var start = tokenOccurrences[i].Index;
                var endLimit = start + proximityWindowColumns;
                var cluster = tokenOccurrences
                    .Where(candidate => candidate.Index >= start && candidate.Index <= endLimit)
                    .ToArray();
                var tokenCount = cluster
                    .Select(candidate => candidate.Token)
                    .Where(token => !string.IsNullOrEmpty(token))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (tokenCount <= 1)
                    continue;

                var clusterEnd = cluster.Max(candidate => candidate.Index + candidate.Length);
                var totalTokenLength = cluster.Sum(candidate => ScoreLength(candidate.Length));
                best = ChooseBetter(best, new MatchCandidate(start, clusterEnd - start, clusterScore + (tokenCount * 100) + totalTokenLength, null));
            }
        }

        if (!best.IsValid)
            return (0, 0);

        // LineWidthFormatter.ClampLine accepts a 1-based focusColumn.
        // LineWidthFormatter.ClampLine は 1-based の focusColumn を受け取る。
        return (best.Index + 1, Math.Max(1, best.Length));
    }

    private static (int Column, int Length) FindLeftmostMatchColumn(string line, string normalizedQuery, string[] tokens, StringComparison comparison)
    {
        int bestIndex = -1;
        int bestLength = 0;
        if (!string.IsNullOrEmpty(normalizedQuery))
        {
            var idx = line.IndexOf(normalizedQuery, comparison);
            if (idx >= 0)
            {
                bestIndex = idx;
                bestLength = normalizedQuery.Length;
            }
        }

        foreach (var token in tokens)
        {
            if (token.Length == 0)
                continue;
            var idx = line.IndexOf(token, comparison);
            if (idx < 0)
                continue;
            if (bestIndex < 0 || idx < bestIndex)
            {
                bestIndex = idx;
                bestLength = token.Length;
            }
        }

        return bestIndex < 0 ? (0, 0) : (bestIndex + 1, Math.Max(1, bestLength));
    }

    private static MatchCandidate ChooseBetter(MatchCandidate current, MatchCandidate candidate)
    {
        if (!candidate.IsValid)
            return current;
        if (!current.IsValid)
            return candidate;
        if (candidate.Score != current.Score)
            return candidate.Score > current.Score ? candidate : current;
        if (candidate.Length != current.Length)
            return candidate.Length > current.Length ? candidate : current;
        return candidate.Index < current.Index ? candidate : current;
    }

    private static int ScoreLength(int length) => Math.Min(length, 99);

    private readonly record struct MatchCandidate(int Index, int Length, int Score, string? Token)
    {
        public bool IsValid => Index >= 0 && Length > 0;
    }

    public static int ClampSnippetLines(int maxLines) =>
        Math.Clamp(maxLines, 1, MaxSnippetLines);

    private static string[] BuildQueryTokens(string query, bool normalizeCSharpVerbatimNames) =>
        query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(t => t.Length > 0)
            .Where(t => t is not "AND" and not "OR" and not "NOT" and not "NEAR")
            .Select(token => normalizeCSharpVerbatimNames ? CSharpVerbatimNameNormalizer.Normalize(token) : token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static SearchSnippetLineMatchScan FindMatchingLineIndexes(string content, string query, string[] tokens, bool caseSensitive, bool normalizeCSharpVerbatimNames, int maxTrackedWindowLines)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<int>();
        var lineCount = 0;
        var totalMatchCount = 0;
        int? focusStart = null;

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var (i, rawLine) in EnumerateContentLines(content))
            {
                lineCount++;
                var line = normalizeCSharpVerbatimNames ? CSharpVerbatimNameNormalizer.Normalize(rawLine) : rawLine;
                if (line.Contains(query, comparison))
                    AddTrackedMatchIndex(matches, i, maxTrackedWindowLines, ref focusStart, ref totalMatchCount);
            }
        }
        else
        {
            lineCount = CountContentLines(content);
        }

        if (matches.Count > 0 || tokens.Length == 0)
            return new SearchSnippetLineMatchScan(matches, lineCount, totalMatchCount);

        matches.Clear();
        lineCount = 0;
        totalMatchCount = 0;
        focusStart = null;
        foreach (var (i, rawLine) in EnumerateContentLines(content))
        {
            lineCount++;
            var line = normalizeCSharpVerbatimNames ? CSharpVerbatimNameNormalizer.Normalize(rawLine) : rawLine;
            if (tokens.Any(token => line.Contains(token, comparison)))
                AddTrackedMatchIndex(matches, i, maxTrackedWindowLines, ref focusStart, ref totalMatchCount);
        }

        return new SearchSnippetLineMatchScan(matches, lineCount, totalMatchCount);
    }

    private static void AddTrackedMatchIndex(List<int> matches, int lineIndex, int maxTrackedWindowLines, ref int? focusStart, ref int totalMatchCount)
    {
        totalMatchCount++;
        focusStart ??= lineIndex;
        if ((lineIndex - focusStart.Value) + 1 <= maxTrackedWindowLines)
            matches.Add(lineIndex);
    }

    private static List<SearchSnippetLine> ReadSnippetLines(string content, int start, int end, bool normalizeCSharpVerbatimNames)
    {
        var lines = new List<SearchSnippetLine>((end - start) + 1);
        foreach (var (index, rawLine) in EnumerateContentLines(content))
        {
            if (index < start)
                continue;
            if (index > end)
                break;

            if (normalizeCSharpVerbatimNames)
            {
                var normalized = CSharpVerbatimNameNormalizer.Normalize(rawLine, out var rawIndexMap);
                lines.Add(new SearchSnippetLine(index, rawLine, normalized, rawIndexMap));
            }
            else
            {
                lines.Add(new SearchSnippetLine(index, rawLine, null, null));
            }
        }

        return lines;
    }

    private static int CountContentLines(string content)
    {
        var count = 0;
        foreach (var _ in EnumerateContentLines(content))
            count++;
        return count;
    }

    private static IEnumerable<(int Index, string Text)> EnumerateContentLines(string content)
    {
        var lineStart = 0;
        var lineIndex = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            var lineEnd = i;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
                lineEnd--;
            yield return (lineIndex, content[lineStart..lineEnd]);
            lineIndex++;
            lineStart = i + 1;
        }

        yield return (lineIndex, content[lineStart..]);
    }

    private sealed record SearchSnippetLineMatchScan(List<int> MatchIndexes, int LineCount, int TotalMatchCount);

    private sealed record SearchSnippetLine(int Index, string Text, string? NormalizedText, int[]? RawIndexMap);

    private static List<SearchTermOccurrence> GetMatchedTermOccurrences(string line, int absoluteLine, string query, string[] tokens, bool caseSensitive = false, string? rawLine = null, int[]? rawIndexMap = null)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var occurrences = new List<SearchTermOccurrence>();
        if (!string.IsNullOrWhiteSpace(query))
            AddTermOccurrences(occurrences, line, absoluteLine, query, comparison, rawLine, rawIndexMap);

        foreach (var token in tokens)
            AddTermOccurrences(occurrences, line, absoluteLine, token, comparison, rawLine, rawIndexMap);

        return occurrences;
    }

    private static void AddTermOccurrences(List<SearchTermOccurrence> occurrences, string line, int absoluteLine, string term, StringComparison comparison, string? rawLine, int[]? rawIndexMap)
    {
        if (string.IsNullOrEmpty(term))
            return;

        var index = 0;
        while ((index = line.IndexOf(term, index, comparison)) >= 0)
        {
            var occurrenceColumn = index + 1;
            var occurrenceLength = term.Length;
            var occurrenceTerm = line.Substring(index, term.Length);
            if (rawLine != null && rawIndexMap != null && TryReconstructRawSpan(rawIndexMap, index, term.Length, out var rawColumn, out var rawLength))
            {
                occurrenceColumn = rawColumn;
                occurrenceLength = rawLength;
                occurrenceTerm = rawLine.Substring(rawColumn - 1, rawLength);
            }

            if (!occurrences.Any(occurrence =>
                    occurrence.Line == absoluteLine &&
                    occurrence.Column == occurrenceColumn &&
                    occurrence.Length == occurrenceLength &&
                    string.Equals(occurrence.Term, occurrenceTerm, StringComparison.OrdinalIgnoreCase)))
            {
                occurrences.Add(new SearchTermOccurrence
                {
                    Term = occurrenceTerm,
                    Line = absoluteLine,
                    Column = occurrenceColumn,
                    Length = occurrenceLength,
                });
            }
            index += Math.Max(1, term.Length);
        }
    }

    private static List<string> GetMatchedTerms(string line, string query, string[] tokens, bool caseSensitive = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query) && line.Contains(query, comparison))
            terms.Add(query);

        foreach (var token in tokens)
        {
            if (terms.Contains(token, StringComparer.OrdinalIgnoreCase))
                continue;
            if (line.Contains(token, comparison))
                terms.Add(token);
        }

        return terms;
    }

    /// <summary>
    /// Strip FTS5 quoting and wildcards so the token matches literal source text.
    /// FTS5の引用符とワイルドカードを除去し、トークンをソーステキストのリテラルに合わせる。
    /// </summary>
    private static string NormalizeToken(string token)
    {
        // Remove surrounding quotes/parens from FTS5 token syntax, then trailing
        // '*' (FTS5 prefix wildcard) since we match literal token text, not patterns.
        // FTS5トークン構文の囲み引用符/括弧を除去し、末尾の '*'（FTS5接頭辞
        // ワイルドカード）を除去。リテラル照合のためパターンではなく文字列で比較する。
        return token
            .Trim('"', '\'', '(', ')')
            .TrimEnd('*');
    }

}

public sealed class SearchSnippetQueryContext
{
    internal SearchSnippetQueryContext(string query, SearchSnippetPreparedQuery defaultQuery, SearchSnippetPreparedQuery csharpQuery)
    {
        Query = query;
        DefaultQuery = defaultQuery;
        CSharpQuery = csharpQuery;
    }

    public string Query { get; }

    internal SearchSnippetPreparedQuery DefaultQuery { get; }
    internal SearchSnippetPreparedQuery CSharpQuery { get; }

    internal SearchSnippetPreparedQuery ForLanguage(string? lang) =>
        string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase) ? CSharpQuery : DefaultQuery;
}

internal readonly record struct SearchSnippetPreparedQuery(string NormalizedQuery, string[] Tokens, bool NormalizeCSharpVerbatimNames);

public sealed class CompactSearchResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Query { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? Visibility { get; set; }
    public int ChunkStartLine { get; set; }
    public int ChunkEndLine { get; set; }
    public int SnippetStartLine { get; set; }
    public int SnippetEndLine { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public List<int> MatchLines { get; set; } = [];
    public List<SearchHighlight> Highlights { get; set; } = [];
    public int ContextBefore { get; set; }
    public int ContextAfter { get; set; }
    public int TruncatedLineCount { get; set; }
    public int DroppedMatchLineCount { get; set; }
    public SearchTruncationContext TruncationContext { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchGuardEvidence>? GuardEvidence { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchQueryHint? ExactSubstringHint { get; set; }
    public double Score { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingSymbolName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingSymbolKind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EnclosingSymbolStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EnclosingSymbolEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingContainerName { get; set; }
}

public enum SearchSnippetFocusMode
{
    Leftmost,
    Quality,
    Proximity,
}

public sealed class SearchHighlight
{
    public int Line { get; set; }
    public string Text { get; set; } = string.Empty;
    public int OriginalLineLength { get; set; }
    public bool Truncated { get; set; }
    public List<int> TruncatedCharCounts { get; set; } = [];
    public List<string> Terms { get; set; } = [];
    public List<SearchTermOccurrence> TermOccurrences { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LiteralTerms { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchTermOccurrence>? LiteralTermOccurrences { get; set; }
}

public sealed class SearchTermOccurrence
{
    public string Term { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
}

public sealed class SearchTruncationContext
{
    public int LineCount { get; set; }
    public List<int> CharCounts { get; set; } = [];
    public int TotalChars { get; set; }
}

public sealed class SearchSnippetExcerpt
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<string> Lines { get; set; } = [];
    public List<int> MatchLines { get; set; } = [];
    public List<SearchHighlight> Highlights { get; set; } = [];
    public int ContextBefore { get; set; }
    public int ContextAfter { get; set; }
    public bool TruncatedBefore { get; set; }
    public bool TruncatedAfter { get; set; }
    public int TruncatedLineCount { get; set; }
    public int DroppedMatchLineCount { get; set; }
    public SearchTruncationContext TruncationContext { get; set; } = new();
}
