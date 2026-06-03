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
}
