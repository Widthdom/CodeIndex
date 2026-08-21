using CodeIndex.Cli;
using CodeIndex.Indexer;
using System.Collections;
using System.Text.RegularExpressions;

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
    public void StaticPatternCache_CoversSharedExtractionPatternSet()
    {
        _ = BoundedRegex.IsMatch("token", "token");

        Assert.True(Regex.CacheSize >= BoundedRegex.MinimumStaticPatternCacheSize);
    }

    [Fact]
    public void EnumerateMatches_InstanceRegexTimeout_ReturnsEmpty()
    {
        var regex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        var matches = BoundedRegex.EnumerateMatches(regex, input);

        Assert.Empty(EnumerateConcreteMatches(matches));
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_StopsAfterConsumerBreak()
    {
        var regex = new BoundedRegex(
            @"token|(?:a+)+$",
            default,
            TimeSpan.FromMilliseconds(25));
        var input = "token " + new string('a', 100_000) + "!";
        using var capture = BoundedRegex.CaptureTimeouts("csharp", "bounded_regex_test");
        var matches = BoundedRegex.EnumerateMatches(regex, input).GetEnumerator();

        try
        {
            Assert.True(matches.MoveNext());
            Assert.Equal("token", matches.Current.Value);
            matches.Dispose();
            Assert.False(matches.MoveNext());
        }
        finally
        {
            matches.Dispose();
        }

        Assert.False(capture.HasTimeouts);
    }

    [Fact]
    public void EnumerateMatches_StaticPattern_StreamsInMatchOrder()
    {
        var matches = BoundedRegex
            .EnumerateMatches("alpha beta gamma", @"\w+")
            .Take(2)
            .Select(match => match.Value)
            .ToArray();

        Assert.Equal(["alpha", "beta"], matches);
    }

    [Fact]
    public void EnumerateMatches_StaticPatternCustomTimeout_ReturnsEmpty()
    {
        var input = new string('a', 10_000) + "!";

        var matches = BoundedRegex.EnumerateMatches(
            input,
            "(a+)+$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(1));

        Assert.Empty(matches);
    }

    [Fact]
    public void EnumerateMatches_StaticPatternCustomTimeout_StopsAfterConsumerBreak()
    {
        var input = "token " + new string('a', 10_000) + "!";
        using var capture = BoundedRegex.CaptureTimeouts("csharp", "bounded_regex_test");

        var matches = BoundedRegex
            .EnumerateMatches(
                input,
                @"token|(?:a+)+$",
                RegexOptions.CultureInvariant,
                // Keep this below the production default while leaving full-suite scheduler
                // headroom; the one-millisecond budget used before #5113 could lose the prefix.
                TimeSpan.FromSeconds(1))
            .Take(1)
            .ToArray();

        Assert.Collection(matches, match => Assert.Equal("token", match.Value));
        Assert.False(capture.HasTimeouts);
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_StartsAtRequestedOffset()
    {
        var regex = new BoundedRegex(@"\w+");

        var matches = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "skip alpha beta", startAt: 5));

        Assert.Collection(
            matches,
            match => Assert.Equal("alpha", match.Value),
            match => Assert.Equal("beta", match.Value));
    }

    [Fact]
    public void EnumerateMatches_InstanceRightToLeftRegex_PreservesDefaultStartAndOrder()
    {
        var regex = new BoundedRegex(@"\w+", RegexOptions.RightToLeft);

        var matches = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "alpha beta"));

        Assert.Collection(
            matches,
            match => Assert.Equal("beta", match.Value),
            match => Assert.Equal("alpha", match.Value));
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_InvalidStartAtIsDeferredUntilMoveNext()
    {
        var regex = new BoundedRegex(@"\w+");
        var enumerable = BoundedRegex.EnumerateMatches(regex, "alpha", startAt: 6);
        var enumerator = enumerable.GetEnumerator();
        Exception? exception = null;

        try
        {
            try
            {
                enumerator.MoveNext();
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.False(enumerator.MoveNext());
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_ZeroLengthMatchesAdvance()
    {
        var regex = new BoundedRegex(@"(?=\w)");
        var matches = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "ab"));

        Assert.Collection(
            matches,
            match =>
            {
                Assert.Equal(0, match.Index);
                Assert.Equal(0, match.Length);
            },
            match =>
            {
                Assert.Equal(1, match.Index);
                Assert.Equal(0, match.Length);
            });
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_GAnchorContinuesFromPreviousMatchEnd()
    {
        var regex = new BoundedRegex(@"\G\w");

        var fromStart = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "ab cd"));
        var fromOffset = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "ab cd", startAt: 3));

        Assert.Collection(
            fromStart,
            match => Assert.Equal(("a", 0), (match.Value, match.Index)),
            match => Assert.Equal(("b", 1), (match.Value, match.Index)));
        Assert.Collection(
            fromOffset,
            match => Assert.Equal(("c", 3), (match.Value, match.Index)),
            match => Assert.Equal(("d", 4), (match.Value, match.Index)));
    }

    [Fact]
    public void EnumerateMatches_InstanceRightToLeftRegex_PreservesRequestedStartAndOrder()
    {
        var regex = new BoundedRegex(@"\w+", RegexOptions.RightToLeft);

        var matches = EnumerateConcreteMatches(
            BoundedRegex.EnumerateMatches(regex, "alpha beta gamma", startAt: 10));

        Assert.Collection(
            matches,
            match => Assert.Equal(("beta", 6), (match.Value, match.Index)),
            match => Assert.Equal(("alpha", 0), (match.Value, match.Index)));
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_SuffixTimeoutIsRecordedOnceAndTerminates()
    {
        var regex = new BoundedRegex(
            @"token|(?:a+)+$",
            default,
            TimeSpan.FromMilliseconds(25));
        var input = "token " + new string('a', 100_000) + "!";
        using var capture = BoundedRegex.CaptureTimeouts("csharp", "bounded_regex_test");
        var matches = BoundedRegex.EnumerateMatches(regex, input).GetEnumerator();

        try
        {
            Assert.True(matches.MoveNext());
            Assert.Equal("token", matches.Current.Value);
            Assert.False(matches.MoveNext());
            Assert.Equal(1, capture.TimeoutCount);
            Assert.Equal("matches", Assert.Single(capture.Diagnostics).Operation);

            Assert.False(matches.MoveNext());
            Assert.Equal(1, capture.TimeoutCount);
        }
        finally
        {
            matches.Dispose();
        }
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_InterfaceResetIsNotSupported()
    {
        var regex = new BoundedRegex(@"\w+");
        IEnumerator<Match> matches = BoundedRegex
            .EnumerateMatches(regex, "alpha")
            .GetEnumerator();

        try
        {
            Assert.True(matches.MoveNext());
            Assert.Throws<NotSupportedException>(() => ((IEnumerator)matches).Reset());
        }
        finally
        {
            matches.Dispose();
        }
    }

    [Fact]
    public void EnumerateMatches_InstanceRegex_LinqCompatibilityPreservesOrder()
    {
        var regex = new BoundedRegex(@"\w+");
        IEnumerable<Match> matches = BoundedRegex.EnumerateMatches(regex, "alpha beta");

        var values = matches.Select(match => match.Value).ToArray();

        Assert.Equal(["alpha", "beta"], values);
    }

    [Fact]
    public void CountMatches_InstanceRegex_CountsWithoutMaterializingCollection()
    {
        var regex = new BoundedRegex(@"\w+");

        var count = BoundedRegex.CountMatches(regex, "alpha beta gamma");

        Assert.Equal(3, count);
    }

    [Fact]
    public void CountMatches_InstanceRegexTimeout_ReturnsZero()
    {
        var regex = new BoundedRegex(
            @"token|(?:a+)+$",
            default,
            TimeSpan.FromMilliseconds(1));
        var input = "token " + new string('a', 10_000) + "!";

        var count = BoundedRegex.CountMatches(regex, input);

        Assert.Equal(0, count);
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
    public void CaptureTimeouts_RecordsHashedDiagnosticsForFileIssue()
    {
        const string pattern = "(a+)+$";
        var regex = new BoundedRegex(pattern, default, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 10_000) + "!";

        using var capture = BoundedRegex.CaptureTimeouts("csharp", "reference_extraction");
        var match = regex.Match(input);
        var issue = IndexCommandRunner.BuildRegexTimeoutIssue("src/Pathological.cs", capture);

        Assert.False(match.Success);
        Assert.True(capture.HasTimeouts);
        Assert.NotNull(issue);
        Assert.Equal("regex_timeout", issue.Kind);
        Assert.Equal("src/Pathological.cs", issue.Path);
        Assert.Contains("reference_extraction", issue.Message);
        Assert.Contains(capture.Diagnostics[0].PatternHash, issue.Message);
        Assert.Contains("len=6", issue.Message);
        Assert.DoesNotContain(pattern, issue.Message);
        Assert.DoesNotContain(input, issue.Message);
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

    private static List<Match> EnumerateConcreteMatches(
        BoundedRegex.MatchEnumerable matches)
    {
        List<Match> materialized = [];
        foreach (var match in matches)
            materialized.Add(match);

        return materialized;
    }
}
