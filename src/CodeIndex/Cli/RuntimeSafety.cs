using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class RuntimeSafety
{
    internal static readonly TimeSpan RegexMatchTimeout = BoundedRegex.DefaultMatchTimeout;

    public static void Configure()
    {
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", RegexMatchTimeout);
    }

    public static string FormatRegexTimeout(RegexMatchTimeoutException ex) =>
        RegexTimeoutPolicy.FormatIndexingTimeout(ex);
}
