using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class LineRangeTextTests
{
    [Fact]
    public void Join_CombinesInclusiveLineRange()
    {
        var lines = new[] { "zero", "one", "two", "three" };

        var result = LineRangeText.Join(lines, 1, 2);

        Assert.Equal("one\ntwo", result);
    }

    [Fact]
    public void Join_ClampsRangeToAvailableLines()
    {
        var lines = new[] { "zero", "one" };

        var result = LineRangeText.Join(lines, -4, 20);

        Assert.Equal("zero\none", result);
    }

    [Fact]
    public void Join_ReturnsEmptyForEmptyOrInvertedRange()
    {
        Assert.Equal(string.Empty, LineRangeText.Join(Array.Empty<string>(), 0, 0));
        Assert.Equal(string.Empty, LineRangeText.Join(new[] { "zero" }, 1, 0));
    }

    [Fact]
    public void Join_PreservesSingleLineWithoutAllocatingSeparator()
    {
        var line = "only";
        var lines = new[] { "before", line, "after" };

        var result = LineRangeText.Join(lines, 1, 1);

        Assert.Same(line, result);
    }
}
