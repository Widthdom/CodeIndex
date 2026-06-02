using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class BoundedRegexTests
{
    [Fact]
    public void DefaultMatchTimeout_MatchesRuntimeSafetyTimeout()
    {
        Assert.Equal(RuntimeSafety.RegexMatchTimeout, BoundedRegex.DefaultMatchTimeout);
    }
}
