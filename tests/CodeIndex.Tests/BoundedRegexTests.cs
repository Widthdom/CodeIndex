using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public sealed class BoundedRegexTests
{
    [Fact]
    public void DefaultMatchTimeout_KeepsBoundedMatchesFromTimingOutUnderNormalSchedulerContention()
    {
        Assert.InRange(
            BoundedRegex.DefaultMatchTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultMatchTimeout_MatchesRuntimeSafetyTimeout()
    {
        Assert.Equal(RuntimeSafety.RegexMatchTimeout, BoundedRegex.DefaultMatchTimeout);
    }
}
