using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

internal static class RegexRegistry
{
    internal static readonly TimeSpan FileIgnorePatternMatchTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan GeneratedCodePatternMatchTimeout = TimeSpan.FromMilliseconds(50);

    internal static Regex CreateFindRegex(string query, bool exact, TimeSpan matchTimeout)
    {
        var options = RegexOptions.CultureInvariant;
        if (!exact)
            options |= RegexOptions.IgnoreCase;
        return CreateRegex(query, options, matchTimeout);
    }

    internal static Regex CreateFileIgnorePatternRegex(string pattern) =>
        CreateRegex(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
            FileIgnorePatternMatchTimeout);

    internal static Regex CreateGeneratedCodePatternRegex(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        return CreateRegex(pattern, options, GeneratedCodePatternMatchTimeout);
    }

    private static Regex CreateRegex(string pattern, RegexOptions options, TimeSpan matchTimeout) =>
        new Regex(pattern, options, matchTimeout);
}
