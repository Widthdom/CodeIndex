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

    public static IReadOnlyList<string> Format(string content, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality)
    {
        var excerpt = BuildExcerpt(content, query, absoluteStartLine: 1, maxLines, caseSensitive, maxLineWidth, lang, focusMode);
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

    public static CompactSearchResult ToCompactResult(SearchResult result, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality)
    {
        var excerpt = BuildExcerpt(result.Content, query, result.StartLine, maxLines, caseSensitive, maxLineWidth, lang ?? result.Lang, focusMode);
        return new CompactSearchResult
        {
            Query = query,
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
            TruncationContext = excerpt.TruncationContext,
            Score = result.Score,
        };
    }

    public static SearchSnippetExcerpt BuildExcerpt(string content, string query, int absoluteStartLine, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null, SearchSnippetFocusMode focusMode = SearchSnippetFocusMode.Quality)
    {
        maxLines = ClampSnippetLines(maxLines);
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            return new SearchSnippetExcerpt
            {
                StartLine = absoluteStartLine,
                EndLine = absoluteStartLine,
            };
        }

        var normalizedQuery = query.Trim();
        var normalizeCSharpVerbatimNames = string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase);
        if (normalizeCSharpVerbatimNames)
            normalizedQuery = CSharpVerbatimNameNormalizer.Normalize(normalizedQuery);

        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(t => t.Length > 0)
            .Where(t => t is not "AND" and not "OR" and not "NOT" and not "NEAR")
            .Select(token => normalizeCSharpVerbatimNames ? CSharpVerbatimNameNormalizer.Normalize(token) : token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[]? normalizedLines = null;
        int[][]? rawIndexMaps = null;
        if (normalizeCSharpVerbatimNames)
        {
            normalizedLines = new string[lines.Length];
            rawIndexMaps = new int[lines.Length][];
            for (int i = 0; i < lines.Length; i++)
                normalizedLines[i] = CSharpVerbatimNameNormalizer.Normalize(lines[i], out rawIndexMaps[i]);
        }

        var matchLinesSource = normalizedLines ?? lines;
        var matchIndexes = FindMatchingLineIndexes(matchLinesSource, normalizedQuery, tokens, caseSensitive);
        var focusStart = matchIndexes.Count > 0 ? matchIndexes[0] : 0;
        var focusEnd = focusStart;
        foreach (var matchIndex in matchIndexes.Skip(1))
        {
            if ((matchIndex - focusStart) + 1 > maxLines)
                break;

            focusEnd = matchIndex;
        }

        var focusLength = Math.Max(1, (focusEnd - focusStart) + 1);
        var remaining = Math.Max(0, maxLines - focusLength);
        var before = remaining / 2;
        var after = remaining - before;

        var start = Math.Max(0, focusStart - before);
        var end = Math.Min(lines.Length - 1, focusEnd + after);
        while ((end - start) + 1 < maxLines)
        {
            if (start > 0)
            {
                start--;
                continue;
            }

            if (end < lines.Length - 1)
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

        for (int i = start; i <= end; i++)
        {
            var originalLine = lines[i];
            ClampedTextResult clamped;
            if (normalizeCSharpVerbatimNames && matchSet.Contains(i) && normalizedLines != null && rawIndexMaps != null)
            {
                clamped = ClampNormalizedSnippetLine(originalLine, normalizedLines[i], rawIndexMaps[i], maxLineWidth, normalizedQuery, tokens, caseSensitive, focusMode);
            }
            else
            {
                clamped = ClampSnippetLine(originalLine, maxLineWidth, matchSet.Contains(i) ? normalizedQuery : null, tokens, caseSensitive, focusMode);
            }
            clampedLines.Add(clamped.Text);
            if (clamped.Truncated)
            {
                truncatedLineCount++;
                truncatedCharCounts.Add(clamped.TruncatedCharCount);
            }

            if (!matchSet.Contains(i))
                continue;

            var absoluteLine = absoluteStartLine + i;
            matchLines.Add(absoluteLine);
            highlights.Add(new SearchHighlight
            {
                Line = absoluteLine,
                Text = clamped.Text,
                OriginalLineLength = originalLine.Length,
                Truncated = clamped.Truncated,
                TruncatedCharCounts = clamped.Truncated ? [clamped.TruncatedCharCount] : [],
                Terms = GetMatchedTerms(normalizeCSharpVerbatimNames && normalizedLines != null ? normalizedLines[i] : originalLine, normalizedQuery, tokens, caseSensitive),
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
            TruncatedAfter = end < lines.Length - 1,
            TruncatedLineCount = truncatedLineCount,
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

    private static List<int> FindMatchingLineIndexes(string[] lines, string query, string[] tokens, bool caseSensitive = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<int>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, comparison))
                    matches.Add(i);
            }
        }

        if (matches.Count > 0 || tokens.Length == 0)
            return matches;

        for (int i = 0; i < lines.Length; i++)
        {
            if (tokens.Any(token => lines[i].Contains(token, comparison)))
                matches.Add(i);
        }

        return matches;
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
    public SearchTruncationContext TruncationContext { get; set; } = new();
    public double Score { get; set; }
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
    public SearchTruncationContext TruncationContext { get; set; } = new();
}
