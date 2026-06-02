namespace CodeIndex.Models;

internal static class SuggestionEvidencePaths
{
    public const int MaxCount = 20;
    public const int MaxLength = 260;

    public static List<string> Normalize(string[]? values)
    {
        if (values == null || values.Length == 0)
            return [];

        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!TryNormalize(value, out var normalized, out _))
                continue;
            if (normalized.Length > 0 && !result.Contains(normalized, StringComparer.Ordinal))
                result.Add(normalized);
        }

        return result;
    }

    public static bool TryNormalize(string value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var path = value.Trim();
        if (path.Length == 0)
            return true;
        if (path.Length > MaxLength)
        {
            error = $"evidencePaths contains a path longer than {MaxLength} characters.";
            return false;
        }
        if (path.Any(char.IsControl))
        {
            error = "evidencePaths entries must not contain control characters.";
            return false;
        }

        path = path.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];

        if (path.Length == 0)
            return true;
        if (IsRootedOrHomePath(path) || path.Contains("://", StringComparison.Ordinal))
        {
            error = "evidencePaths entries must be repository-relative paths.";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return true;
        if (segments.Any(segment => segment is "." or ".."))
        {
            error = "evidencePaths entries must not contain . or .. path segments.";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static bool IsRootedOrHomePath(string path)
    {
        return path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("~", StringComparison.Ordinal)
            || (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':');
    }

    private static bool IsAsciiLetter(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
