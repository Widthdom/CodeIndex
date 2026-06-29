using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Diagnostics;

internal static class SensitiveNameClassifier
{
    private static readonly string[] NormalizedSensitiveFragments =
    [
        "pwd",
        "auth",
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "accesskey",
        "privatekey",
        "authorization",
        "credential",
        "sessioncookie",
    ];

    internal static string RegexFragmentPattern { get; } = BuildRegexFragmentPattern();

    internal static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
            return false;

        foreach (var fragment in NormalizedSensitiveFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string NormalizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static string BuildRegexFragmentPattern()
    {
        var pattern = new StringBuilder();
        foreach (var fragment in NormalizedSensitiveFragments)
        {
            if (pattern.Length > 0)
                pattern.Append('|');
            AppendSeparatorTolerantFragment(pattern, fragment);
        }

        return pattern.ToString();
    }

    private static void AppendSeparatorTolerantFragment(StringBuilder pattern, string fragment)
    {
        for (var i = 0; i < fragment.Length; i++)
        {
            if (i > 0)
                pattern.Append("[._-]*");
            pattern.Append(Regex.Escape(fragment[i].ToString()));
        }
    }
}
