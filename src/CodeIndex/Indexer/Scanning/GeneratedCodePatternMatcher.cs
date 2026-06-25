using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

internal sealed class GeneratedCodePatternMatcher
{
    internal static readonly GeneratedCodePatternMatcher Empty = new([]);

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Rule[] _rules;

    private GeneratedCodePatternMatcher(Rule[] rules)
    {
        _rules = rules;
    }

    internal static GeneratedCodePatternMatcher FromPatterns(IEnumerable<string>? patterns, bool ignoreCase)
    {
        if (patterns == null)
            return Empty;

        var rules = new List<Rule>();
        foreach (var raw in patterns)
        {
            var pattern = NormalizePattern(raw);
            if (pattern.Length == 0)
                continue;

            rules.Add(new Rule(
                pattern,
                MatchBasenameOnly: !pattern.Contains('/', StringComparison.Ordinal),
                BuildMatcher(pattern, ignoreCase)));
        }

        return rules.Count == 0 ? Empty : new GeneratedCodePatternMatcher(rules.ToArray());
    }

    internal bool TryMatch(string relativePath, out string pattern)
    {
        pattern = string.Empty;
        if (_rules.Length == 0)
            return false;

        var normalizedPath = NormalizePath(relativePath);
        string? fileName = null;
        foreach (var rule in _rules)
        {
            var candidate = rule.MatchBasenameOnly
                ? fileName ??= GetFileName(normalizedPath)
                : normalizedPath;
            if (rule.Matcher.IsMatch(candidate))
            {
                pattern = rule.Pattern;
                return true;
            }
        }

        return false;
    }

    private static string NormalizePattern(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var pattern = raw.Trim().Replace('\\', '/');
        while (pattern.StartsWith("./", StringComparison.Ordinal))
            pattern = pattern[2..];
        while (pattern.StartsWith("/", StringComparison.Ordinal))
            pattern = pattern[1..];
        return pattern;
    }

    private static string NormalizePath(string path)
    {
        var start = 0;
        while (path.AsSpan(start).StartsWith("./".AsSpan(), StringComparison.Ordinal))
            start += 2;

        if (path.IndexOf('\\', start) < 0)
            return start == 0 ? path : path[start..];

        return path[start..].Replace('\\', '/');
    }

    private static string GetFileName(string normalizedPath)
    {
        var slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }

    private static Regex BuildMatcher(string pattern, bool ignoreCase)
    {
        var builder = new StringBuilder(pattern.Length * 2);
        builder.Append('^');
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDoubleStar)
                {
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        builder.Append("(?:[^/]+/)*");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
                continue;
            }

            if (ch == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            builder.Append(Regex.Escape(ch.ToString()));
        }
        builder.Append('$');

        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        return new Regex(builder.ToString(), options, MatchTimeout);
    }

    private sealed record Rule(string Pattern, bool MatchBasenameOnly, Regex Matcher);
}
