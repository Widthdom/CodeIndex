using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public sealed class RegexTimeoutPolicyTests
{
    [Fact]
    public void FormatFindTimeout_SharedByCliAndMcp_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(25));

        Assert.Equal("25ms", QueryCommandRunner.FormatRegexMatchTimeout(timeout.MatchTimeout));
        Assert.Equal(
            "regular expression timed out after 25ms while scanning indexed file contents.",
            RegexTimeoutPolicy.FormatFindTimeout(timeout));
        Assert.Equal(RegexTimeoutPolicy.RegexTimeoutCategory, McpErrorEnvelope.CategoryRegexTimeout);
        Assert.Equal("regex_timeout", RegexTimeoutPolicy.RegexTimeoutCategory);
    }

    [Fact]
    public void FormatIndexingTimeout_UsesSharedDurationFormatter_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(1500));

        var message = RuntimeSafety.FormatRegexTimeout(timeout);

        Assert.Contains("timed out after 1.5s", message, StringComparison.Ordinal);
        Assert.Contains("indexing this file", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactOrFallback_ForcedRegexTimeout_ReturnsSurfacePolicy_Issue3993()
    {
        AssertRedactionFallback(RegexRedactionSurface.DiagnosticText, "[REDACTED]");
        AssertRedactionFallback(RegexRedactionSurface.DiagnosticSanitizerMessage, RegexTimeoutPolicy.DiagnosticSanitizerTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.SuggestionText, RegexTimeoutPolicy.SuggestionTextTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.GlobalToolLogArgument, "[REDACTED]");
        AssertRedactionFallback(RegexRedactionSurface.GitHubApiResponseBody, RegexTimeoutPolicy.GitHubApiResponseBodyTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.AuditArgumentValue, "[REDACTED]");
    }

    private static void AssertRedactionFallback(RegexRedactionSurface surface, string expected)
    {
        var timeout = new RegexMatchTimeoutException(
            "secret",
            "secret",
            TimeSpan.FromMilliseconds(5));

        var redacted = RegexTimeoutPolicy.RedactOrFallback(
            surface,
            () => throw timeout,
            "[REDACTED]");

        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void IsRedactionMatchOrFallback_ForcedRegexTimeout_FailsClosed_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "secret",
            "secret",
            TimeSpan.FromMilliseconds(5));

        var isMatch = RegexTimeoutPolicy.IsRedactionMatchOrFallback(() => throw timeout);

        Assert.True(isMatch);
    }
}
