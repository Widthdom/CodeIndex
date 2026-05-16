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

    public static IReadOnlyList<string> Format(string content, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null)
    {
        var excerpt = BuildExcerpt(content, query, absoluteStartLine: 1, maxLines, caseSensitive, maxLineWidth, lang);
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

    public static CompactSearchResult ToCompactResult(SearchResult result, string query, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null)
    {
        var excerpt = BuildExcerpt(result.Content, query, result.StartLine, maxLines, caseSensitive, maxLineWidth, lang ?? result.Lang);
        return new CompactSearchResult
        {
            Query = query,
            Path = result.Path,
            Lang = result.Lang,
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
            Score = result.Score,
        };
    }

    public static SearchSnippetExcerpt BuildExcerpt(string content, string query, int absoluteStartLine, int maxLines = DefaultSnippetLines, bool caseSensitive = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, string? lang = null)
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
        var truncatedLineCount = 0;

        for (int i = start; i <= end; i++)
        {
            var originalLine = lines[i];
            ClampedTextResult clamped;
            if (normalizeCSharpVerbatimNames && matchSet.Contains(i) && normalizedLines != null && rawIndexMaps != null)
            {
                clamped = ClampNormalizedSnippetLine(originalLine, normalizedLines[i], rawIndexMaps[i], maxLineWidth, normalizedQuery, tokens, caseSensitive);
            }
            else
            {
                clamped = ClampSnippetLine(originalLine, maxLineWidth, matchSet.Contains(i) ? normalizedQuery : null, tokens, caseSensitive);
            }
            clampedLines.Add(clamped.Text);
            if (clamped.Truncated)
                truncatedLineCount++;

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
        };
    }

    // Clamp one snippet line, keeping the first match token visible on match lines.
    // 一致行では最初の一致トークンを残して幅を切り詰める。
    private static ClampedTextResult ClampSnippetLine(string line, int maxLineWidth, string? normalizedQuery, string[] tokens, bool caseSensitive)
    {
        if (line.Length <= maxLineWidth)
            return new ClampedTextResult(line, false);

        if (normalizedQuery == null)
            return LineWidthFormatter.ClampLine(line, maxLineWidth);

        var (matchColumn, matchLength) = FindFirstMatchColumn(line, normalizedQuery, tokens, caseSensitive);
        if (matchColumn <= 0)
            return LineWidthFormatter.ClampLine(line, maxLineWidth);

        return LineWidthFormatter.ClampLine(line, maxLineWidth, matchColumn, matchLength);
    }

    private static ClampedTextResult ClampNormalizedSnippetLine(string originalLine, string normalizedLine, int[] rawIndexMap, int maxLineWidth, string normalizedQuery, string[] tokens, bool caseSensitive)
    {
        var (matchColumn, matchLength) = FindFirstMatchColumn(normalizedLine, normalizedQuery, tokens, caseSensitive);
        if (matchColumn <= 0)
            return LineWidthFormatter.ClampLine(originalLine, maxLineWidth);

        var normalizedStart = matchColumn - 1;
        var normalizedEnd = Math.Min(rawIndexMap.Length - 1, normalizedStart + Math.Max(1, matchLength) - 1);
        var rawFocusColumn = rawIndexMap[normalizedStart] + 1;
        var rawFocusLength = rawIndexMap[normalizedEnd] - rawIndexMap[normalizedStart] + 1;
        return LineWidthFormatter.ClampLine(originalLine, maxLineWidth, rawFocusColumn, rawFocusLength);
    }

    private static (int Column, int Length) FindFirstMatchColumn(string line, string normalizedQuery, string[] tokens, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

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

        if (bestIndex < 0)
            return (0, 0);

        // LineWidthFormatter.ClampLine accepts a 1-based focusColumn.
        // LineWidthFormatter.ClampLine は 1-based の focusColumn を受け取る。
        return (bestIndex + 1, Math.Max(1, bestLength));
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
    public double Score { get; set; }
}

public sealed class SearchHighlight
{
    public int Line { get; set; }
    public string Text { get; set; } = string.Empty;
    public int OriginalLineLength { get; set; }
    public bool Truncated { get; set; }
    public List<string> Terms { get; set; } = [];
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
}
