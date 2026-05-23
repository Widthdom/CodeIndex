using System.Linq;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class LineWidthFormatterTests
{
    private const string ThumbsUp = "\U0001F44D";

    [Fact]
    public void ClampLine_DoesNotSplitHeadSliceSurrogatePair()
    {
        // A run of non-BMP code points where the head-clamp boundary
        // would naturally land on a low surrogate without the fix.
        var line = string.Concat(Enumerable.Repeat(ThumbsUp, 100));

        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 21);

        Assert.True(result.Truncated);
        AssertNoOrphanedSurrogate(result.Text);
        Assert.DoesNotContain('�', result.Text);
    }

    [Fact]
    public void ClampLine_DoesNotSplitTrailingSurrogatePairBeforeMarker()
    {
        var prefix = new string('a', 50);
        var suffix = new string('b', 50);
        // Place the emoji where the head-slice boundary would split it.
        var line = prefix + ThumbsUp + suffix;

        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 62);

        Assert.True(result.Truncated);
        AssertNoOrphanedSurrogate(result.Text);
        Assert.DoesNotContain('�', result.Text);
    }

    [Fact]
    public void ClampLine_KeepsFocusedSurrogatePairIntact()
    {
        var prefix = new string('a', 200);
        var suffix = new string('b', 200);
        var line = prefix + ThumbsUp + suffix;
        var focusColumn = prefix.Length + 1;

        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 64, focusColumn: focusColumn, focusLength: 2);

        Assert.True(result.Truncated);
        AssertNoOrphanedSurrogate(result.Text);
        Assert.DoesNotContain('�', result.Text);
        Assert.Contains(ThumbsUp, result.Text);
    }

    [Fact]
    public void ClampLine_TightBudgetStillProducesValidUtf16()
    {
        var prefix = new string('a', 60);
        var suffix = new string('b', 60);
        var line = prefix + ThumbsUp + suffix;
        var focusColumn = prefix.Length + 1;

        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 4, focusColumn: focusColumn, focusLength: 2);

        Assert.True(result.Truncated);
        AssertNoOrphanedSurrogate(result.Text);
        Assert.DoesNotContain('�', result.Text);
    }

    [Fact]
    public void ClampLine_SurrogatePairAtVeryStartIsPreserved()
    {
        var line = ThumbsUp + new string('a', 100);

        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 20);

        Assert.True(result.Truncated);
        AssertNoOrphanedSurrogate(result.Text);
        Assert.StartsWith(ThumbsUp, result.Text);
    }

    [Fact]
    public void ClampLine_UsesDisplayWidthForCjk()
    {
        var result = LineWidthFormatter.ClampLine("日本語abcdefg", maxLineWidth: 12);

        Assert.True(result.Truncated);
        Assert.StartsWith("日", result.Text);
        Assert.EndsWith("...(+9)...", result.Text);
    }

    [Fact]
    public void ClampLine_UsesDisplayWidthForEmoji()
    {
        var line = "ab" + ThumbsUp + "abcdefghijk";
        var result = LineWidthFormatter.ClampLine(line, maxLineWidth: 12, focusColumn: 3, focusLength: 2);

        Assert.True(result.Truncated);
        Assert.Contains(ThumbsUp, result.Text);
        AssertNoOrphanedSurrogate(result.Text);
    }

    [Fact]
    public void ClampLine_DoesNotTruncateCombiningAccentByCodeUnitLength()
    {
        var result = LineWidthFormatter.ClampLine("e\u0301", maxLineWidth: 1);

        Assert.False(result.Truncated);
        Assert.Equal("e\u0301", result.Text);
    }

    private static void AssertNoOrphanedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                Assert.True(i + 1 < text.Length, "trailing high surrogate without low surrogate");
                Assert.True(char.IsLowSurrogate(text[i + 1]), "high surrogate not followed by low surrogate");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(c), "orphaned low surrogate");
            }
        }
    }
}
