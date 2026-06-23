using System.Text.RegularExpressions;

namespace CodeIndex.Diagnostics;

internal static class DiagnosticSanitizer
{
    private const int MaxDiagnosticFieldLength = 240;
    private const int MaxSanitizerInputLength = MaxDiagnosticFieldLength * 8;
    internal const string RegexTimeoutFallbackMessage = RegexTimeoutPolicy.DiagnosticSanitizerTimeoutFallback;

    public static string ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = NormalizeSeparators(TryGetFullPath(path.Trim()));
        var cdidxIndex = normalized.IndexOf("/.cdidx/", StringComparison.OrdinalIgnoreCase);
        if (cdidxIndex >= 0)
            return Truncate(normalized[(cdidxIndex + 1)..]);

        var configIndex = normalized.IndexOf("/.config/cdidx/", StringComparison.OrdinalIgnoreCase);
        if (configIndex >= 0)
            return Truncate("<user-config>/" + normalized[(configIndex + "/.config/cdidx/".Length)..]);

        var fileName = Path.GetFileName(normalized);
        return Truncate(string.IsNullOrWhiteSpace(fileName) ? "<path>" : fileName);
    }

    public static string? ForOptionalLabel(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : ForMessage(value);

    public static string ForMessage(string? message)
        => ForMessage(message, RedactAbsolutePaths);

    internal static string ForMessage(string? message, Func<string, string> redactPaths)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var singleLine = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        if (singleLine.Length > MaxSanitizerInputLength)
            singleLine = singleLine[..MaxSanitizerInputLength] + " ...";

        try
        {
            var withoutPaths = redactPaths(singleLine);
            return Truncate(CollapseWhitespace(withoutPaths).Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            return RegexTimeoutPolicy.RedactionFallback(RegexRedactionSurface.DiagnosticSanitizerMessage);
        }
    }

    private static string TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    private static string NormalizeSeparators(string value)
        => value.Replace('\\', '/');

    private static string CollapseWhitespace(string value)
    {
        var collapsed = new System.Text.StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                    collapsed.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            collapsed.Append(character);
            previousWasWhitespace = false;
        }

        return collapsed.ToString();
    }

    private static string RedactAbsolutePaths(string value)
    {
        var redacted = new System.Text.StringBuilder(value.Length);
        for (int index = 0; index < value.Length;)
        {
            if (!TryGetAbsolutePathEnd(value, index, out var end))
            {
                redacted.Append(value[index]);
                index++;
                continue;
            }

            redacted.Append("<path>");
            index = end;
        }

        return redacted.ToString();
    }

    private static bool TryGetAbsolutePathEnd(string value, int start, out int end)
    {
        end = start;
        int bodyStart;
        if (IsPathSeparator(value[start]))
        {
            bodyStart = start + 1;
        }
        else if (start + 2 < value.Length
            && IsAsciiLetter(value[start])
            && value[start + 1] == ':'
            && IsPathSeparator(value[start + 2]))
        {
            bodyStart = start + 3;
        }
        else
        {
            return false;
        }

        if (bodyStart >= value.Length || IsPathTerminator(value[bodyStart]))
            return false;

        end = bodyStart + 1;
        while (end < value.Length && !IsPathTerminator(value[end]))
            end++;

        return true;
    }

    private static bool IsAsciiLetter(char value)
        => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsPathSeparator(char value)
        => value is '/' or '\\';

    private static bool IsPathTerminator(char value)
        => char.IsWhiteSpace(value) || value is '\'' or '"' or ';' or ':' or ',' or ')';

    private static string Truncate(string value)
        => value.Length <= MaxDiagnosticFieldLength
            ? value
            : value[..MaxDiagnosticFieldLength] + "...";
}
