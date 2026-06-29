using System.Text;

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
}
