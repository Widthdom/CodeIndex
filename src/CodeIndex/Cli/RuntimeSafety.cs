using System.Text.RegularExpressions;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class RuntimeSafety
{
    internal static readonly TimeSpan RegexMatchTimeout = BoundedRegex.DefaultMatchTimeout;

    public static void Configure()
    {
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", RegexMatchTimeout);
    }

    public static string FormatRegexTimeout(RegexMatchTimeoutException ex)
        => $"Regex extraction timed out after {ex.MatchTimeout.TotalSeconds:0.###}s while indexing this file. "
           + "The file was skipped so indexing can finish; please report the file or reduce the pathological pattern input.";
}
