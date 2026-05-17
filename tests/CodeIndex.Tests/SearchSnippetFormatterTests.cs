using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class SearchSnippetFormatterTests
{
    [Fact]
    public void BuildExcerpt_CentersOnMatchAndTracksContext()
    {
        const string content = "line 1\nline 2\ncall Target()\nline 4\nline 5";

        var excerpt = SearchSnippetFormatter.BuildExcerpt(content, "Target", absoluteStartLine: 10, maxLines: 3);

        Assert.Equal(11, excerpt.StartLine);
        Assert.Equal(13, excerpt.EndLine);
        Assert.Equal([12], excerpt.MatchLines);
        Assert.Equal(1, excerpt.ContextBefore);
        Assert.Equal(1, excerpt.ContextAfter);
        Assert.Single(excerpt.Highlights);
        Assert.Contains("Target", excerpt.Highlights[0].Terms);
    }

    [Fact]
    public void BuildExcerpt_CapturesMultipleMatchLinesWhenTheyFit()
    {
        const string content = "Target()\nnoop()\nTarget()\nline 4";

        var excerpt = SearchSnippetFormatter.BuildExcerpt(content, "Target", absoluteStartLine: 1, maxLines: 4);

        Assert.Equal([1, 3], excerpt.MatchLines);
        Assert.Equal(2, excerpt.Highlights.Count);
    }

    [Fact]
    public void ToCompactResult_UsesSnippetInsteadOfFullChunkContent()
    {
        var result = new SearchResult
        {
            Path = "src/app.cs",
            Lang = "csharp",
            StartLine = 20,
            EndLine = 24,
            Content = "line 20\nline 21\nRun(App)\nline 23\nline 24",
            Score = -1.5,
        };

        var compact = SearchSnippetFormatter.ToCompactResult(result, "App", maxLines: 3);

        Assert.Equal("src/app.cs", compact.Path);
        Assert.Equal(20, compact.ChunkStartLine);
        Assert.Equal(24, compact.ChunkEndLine);
        Assert.Equal(21, compact.SnippetStartLine);
        Assert.Equal(23, compact.SnippetEndLine);
        Assert.Contains("Run(App)", compact.Snippet);
        Assert.Equal([22], compact.MatchLines);
        Assert.Single(compact.Highlights);
        Assert.Equal(-1.5, compact.Score);
    }

    [Fact]
    public void Format_AddsTruncationMarkers_WhenExcerptIsTruncated()
    {
        // 10 lines, match on line 5 — with maxLines=3, both ends should be truncated
        // 10行、5行目に一致 — maxLines=3 で両端がトランケートされるはず
        var lines = Enumerable.Range(1, 10).Select(i => i == 5 ? "call Target()" : $"line {i}");
        var content = string.Join('\n', lines);

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 3);

        Assert.Equal("...", formatted[0]);
        Assert.Equal("...", formatted[^1]);
        Assert.True(formatted.Count <= 5); // 3 content lines + up to 2 markers / 3コンテンツ行 + 最大2マーカー
        Assert.Contains(formatted, line => line.Contains("Target"));
    }

    [Fact]
    public void Format_NoMarkers_WhenContentFitsEntirely()
    {
        const string content = "line 1\ncall Target()\nline 3";

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 8);

        Assert.DoesNotContain("...", formatted);
        Assert.Contains(formatted, line => line.Contains("Target"));
    }

    [Fact]
    public void BuildExcerpt_NormalizesCSharpVerbatimQualifiedNamesInNonExactSearch()
    {
        const string content = "namespace Demo;\nusing @Foo.@Bar;\n";

        var excerpt = SearchSnippetFormatter.BuildExcerpt(content, "Foo.Bar", absoluteStartLine: 1, maxLines: 4, caseSensitive: false, lang: "csharp");

        Assert.Equal([2], excerpt.MatchLines);
        Assert.Single(excerpt.Highlights);
        Assert.Contains("Foo.Bar", excerpt.Highlights[0].Terms);
        Assert.Contains("using @Foo.@Bar;", excerpt.Lines);
    }

    [Fact]
    public void TryReconstructRawSpan_ReturnsFalse_WhenNormalizedSpanCannotAnchorEnd()
    {
        var mapped = SearchSnippetFormatter.TryReconstructRawSpan([0, 1, 2], normalizedStart: 1, matchLength: 4, out var rawColumn, out var rawLength);

        Assert.False(mapped);
        Assert.Equal(0, rawColumn);
        Assert.Equal(0, rawLength);
    }

    [Fact]
    public void TryReconstructRawSpan_MapsVerbatimBoundaryToRawColumns()
    {
        var normalized = CSharpVerbatimNameNormalizer.Normalize("using @Foo.@Bar;", out var rawIndexMap);

        var matchStart = normalized.IndexOf("Foo.Bar", StringComparison.Ordinal);
        var mapped = SearchSnippetFormatter.TryReconstructRawSpan(rawIndexMap, matchStart, "Foo.Bar".Length, out var rawColumn, out var rawLength);

        Assert.True(mapped);
        Assert.Equal(8, rawColumn);
        Assert.Equal(8, rawLength);
    }

    [Fact]
    public void Format_TruncatedAfterOnly_WhenMatchIsNearStart()
    {
        // Match on line 1 with maxLines=2 out of 6 — truncated after only
        // 1行目に一致、maxLines=2/全6行 — 後ろだけトランケート
        var lines = Enumerable.Range(1, 6).Select(i => i == 1 ? "call Target()" : $"line {i}");
        var content = string.Join('\n', lines);

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 2);

        Assert.NotEqual("...", formatted[0]);
        Assert.Equal("...", formatted[^1]);
    }

    [Fact]
    public void Format_TruncatedBeforeOnly_WhenMatchIsNearEnd()
    {
        // Match on last line with maxLines=2 out of 6 — truncated before only
        // 最終行に一致、maxLines=2/全6行 — 前だけトランケート
        var lines = Enumerable.Range(1, 6).Select(i => i == 6 ? "call Target()" : $"line {i}");
        var content = string.Join('\n', lines);

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 2);

        Assert.Equal("...", formatted[0]);
        Assert.NotEqual("...", formatted[^1]);
    }

    [Fact]
    public void Format_ClampsLongMatchLine_AroundFirstMatchToken()
    {
        // A single massive minified line with one match token — the clamped output
        // must keep the match visible and must not return the entire raw line.
        // 1 行巨大なミニファイ済み行に 1 つだけ match token を置いた場合、
        // クランプ後でも match が見える状態で、生データ全量は返さない。
        var huge = new string('a', 10_000) + "Target" + new string('b', 10_000);

        var formatted = SearchSnippetFormatter.Format(huge, "Target", maxLines: 1, maxLineWidth: 200);

        var joined = string.Join('\n', formatted);
        Assert.Contains("Target", joined);
        Assert.Contains("...(+", joined);
        Assert.True(joined.Length <= 400, $"expected clamped output under 400 chars, got {joined.Length}");
    }

    [Fact]
    public void Format_ClampsLongMatchLine_AroundFullQueryBeforeLeftmostToken()
    {
        var content = "alpha " + new string('x', 1_000) + " alpha beta gamma " + new string('y', 1_000);

        var formatted = SearchSnippetFormatter.Format(content, "alpha beta gamma", maxLines: 1, maxLineWidth: 96);

        var joined = string.Join('\n', formatted);
        Assert.Contains("alpha beta gamma", joined);
        Assert.DoesNotContain(new string('x', 120), joined);
        Assert.Contains("...(+", joined);
    }

    [Fact]
    public void Format_LeftmostFocusMode_KeepsLegacyEarliestToken()
    {
        var content = "alpha " + new string('x', 1_000) + " alpha beta gamma " + new string('y', 1_000);

        var formatted = SearchSnippetFormatter.Format(content, "alpha beta gamma", maxLines: 1, maxLineWidth: 96, focusMode: SearchSnippetFocusMode.Leftmost);

        var joined = string.Join('\n', formatted);
        Assert.Contains("alpha", joined);
        Assert.DoesNotContain("alpha beta gamma", joined);
        Assert.Contains("...(+", joined);
    }

    [Fact]
    public void Format_ClampsLongMatchLine_AroundTokenClusterBeforeLeftmostToken()
    {
        var content = "alpha " + new string('x', 1_000) + " beta gamma " + new string('y', 1_000);

        var formatted = SearchSnippetFormatter.Format(content, "alpha beta gamma", maxLines: 1, maxLineWidth: 96);

        var joined = string.Join('\n', formatted);
        Assert.Contains("beta gamma", joined);
        Assert.DoesNotContain(new string('x', 120), joined);
        Assert.Contains("...(+", joined);
    }

    [Fact]
    public void Format_ClampsLongLines_EvenWhenMaxLineWidthExplicit()
    {
        // A non-match long line should still be clamped, bounded by maxLineWidth.
        // match しない長い行も maxLineWidth でクランプされる。
        var content = "Target appears here\n" + new string('x', 5_000);

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 2, maxLineWidth: 128);

        var joined = string.Join('\n', formatted);
        Assert.Contains("Target appears here", joined);
        Assert.Contains("...(+", joined);
        Assert.DoesNotContain(new string('x', 1_000), joined);
    }

    [Fact]
    public void ToCompactResult_ReportsTruncationMetadata_WhenLineIsClamped()
    {
        var hugeLine = new string('a', 1_000) + "Target" + new string('b', 1_000);
        var result = new SearchResult
        {
            Path = "dist/app.min.js",
            Lang = "javascript",
            StartLine = 1,
            EndLine = 1,
            Content = hugeLine,
            Score = -1.0,
        };

        var compact = SearchSnippetFormatter.ToCompactResult(result, "Target", maxLines: 1, maxLineWidth: 256);

        Assert.Contains("Target", compact.Snippet);
        Assert.True(compact.Snippet.Length <= 512, $"clamped snippet should stay bounded, got {compact.Snippet.Length}");
        Assert.Equal(1, compact.TruncatedLineCount);
        Assert.Single(compact.Highlights);
        Assert.True(compact.Highlights[0].Truncated);
        Assert.Equal(hugeLine.Length, compact.Highlights[0].OriginalLineLength);
    }

    [Fact]
    public void Format_DoesNotClamp_WhenLineFitsWithinMaxLineWidth()
    {
        const string content = "call Target()";

        var formatted = SearchSnippetFormatter.Format(content, "Target", maxLines: 1, maxLineWidth: 64);

        var joined = string.Join('\n', formatted);
        Assert.Equal("call Target()", joined);
        Assert.DoesNotContain("...(+", joined);
    }

    [Fact]
    public void Format_DoesNotClamp_WhenMaxLineWidthIsZero()
    {
        var huge = new string('a', 1_000) + "Target" + new string('b', 1_000);

        var formatted = SearchSnippetFormatter.Format(huge, "Target", maxLines: 1, maxLineWidth: 0);

        var joined = string.Join('\n', formatted);
        Assert.Equal(huge, joined);
        Assert.DoesNotContain("...(+", joined);
    }
}
