using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeIndex.Diagnostics;

internal enum RegexRedactionSurface
{
    DiagnosticText,
    DiagnosticSanitizerMessage,
    SuggestionText,
    GlobalToolLogArgument,
    GitHubApiResponseBody,
    AuditArgumentValue,
}

internal static class RegexTimeoutPolicy
{
    internal static readonly TimeSpan RedactionRegexTimeout = TimeSpan.FromSeconds(1);
    internal const string RegexTimeoutCategory = "regex_timeout";
    internal const string ConfiguredPatternRegexTimeoutCategory = "pattern_regex_timeout";
    internal const string RedactionTimeoutCategory = "redaction_timeout";
    internal const string RedactionTimeoutType = "redaction_timeout";
    internal const string DiagnosticSanitizerTimeoutFallback = "[message omitted after sanitization timeout]";
    internal const string SuggestionTextTimeoutFallback = "[REDACTED:redaction_timeout]";
    internal const string GitHubApiResponseBodyTimeoutFallback = "[response body omitted after redaction timeout]";

    internal static string FormatDuration(TimeSpan timeout)
    {
        if (timeout.TotalMilliseconds < 1000)
            return timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms";
        return timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    internal static string FormatIndexingTimeout(RegexMatchTimeoutException ex) =>
        $"Regex extraction timed out after {FormatDuration(ex.MatchTimeout)} while indexing this file. "
        + "The file was skipped so indexing can finish; please report the file or reduce the pathological pattern input.";

    internal static string FormatFindTimeout(RegexMatchTimeoutException ex) =>
        $"regular expression timed out after {FormatDuration(ex.MatchTimeout)} while scanning indexed file contents.";

    internal static string FindTimeoutHint =>
        "Simplify the pattern, narrow the scan with --path/--lang, or omit --regex for literal text.";

    internal static string McpFindTimeoutSuggestion =>
        "Simplify the pattern, narrow the scan with path/lang filters, or disable regex mode for literal text.";

    internal static string FormatConfiguredPatternTimeout(string language, string kind, TimeSpan timeout) =>
        $"[cdidx] Pattern extractor for language '{DiagnosticSanitizer.ForMessage(language)}' "
        + $"kind '{DiagnosticSanitizer.ForMessage(kind)}' timed out after {FormatDuration(timeout)}; skipped this pattern.";

    internal static string RedactionFallback(RegexRedactionSurface surface, string placeholder = DiagnosticRedactor.AngleRedacted) =>
        surface switch
        {
            RegexRedactionSurface.DiagnosticText => placeholder,
            RegexRedactionSurface.DiagnosticSanitizerMessage => DiagnosticSanitizerTimeoutFallback,
            RegexRedactionSurface.SuggestionText => SuggestionTextTimeoutFallback,
            RegexRedactionSurface.GlobalToolLogArgument => placeholder,
            RegexRedactionSurface.GitHubApiResponseBody => GitHubApiResponseBodyTimeoutFallback,
            RegexRedactionSurface.AuditArgumentValue => placeholder,
            _ => placeholder,
        };

    internal static string RedactOrFallback(
        RegexRedactionSurface surface,
        Func<string> redact,
        string placeholder = DiagnosticRedactor.AngleRedacted)
    {
        try
        {
            return redact();
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactionFallback(surface, placeholder);
        }
    }

    internal static bool IsRedactionMatchOrFallback(Func<bool> isMatch)
    {
        try
        {
            return isMatch();
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
