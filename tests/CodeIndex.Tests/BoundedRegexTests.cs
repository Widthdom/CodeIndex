using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public sealed class BoundedRegexTests
{
    [Fact]
    public void DefaultMatchTimeout_KeepsReferenceExtractorFullSuiteHeadroom()
    {
        // Regression coverage for #2947: Release full-suite scheduler contention can make
        // ReferenceExtractor fixtures lose expected regex matches if this budget is too small.
        Assert.InRange(
            BoundedRegex.DefaultMatchTimeout,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultMatchTimeout_MatchesRuntimeSafetyTimeout()
    {
        Assert.Equal(RuntimeSafety.RegexMatchTimeout, BoundedRegex.DefaultMatchTimeout);
    }

    [Fact]
    public void EnumerateMatches_InstanceRegexTimeout_ReturnsEmpty()
    {
        var regex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        var matches = BoundedRegex.EnumerateMatches(regex, input);

        Assert.Empty(matches);
    }

    [Fact]
    public void InstanceMatch_Timeout_ReturnsEmpty()
    {
        var regex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        var match = regex.Match(input);

        Assert.False(match.Success);
    }

    [Fact]
    public void InstanceMatches_Timeout_ReturnsEmpty()
    {
        var regex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        var matches = regex.Matches(input);

        Assert.Empty(matches);
    }

    [Fact]
    public void InstanceIsMatch_Timeout_ReturnsFalse()
    {
        var regex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        var isMatch = regex.IsMatch(input);

        Assert.False(isMatch);
    }
}
