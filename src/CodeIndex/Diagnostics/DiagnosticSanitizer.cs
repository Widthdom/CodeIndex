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

    public static string ForSupportSafePath(string? value)
        => ForPathValue(value, redactPaths: true);

    public static string ForPathWithSecretsRedacted(string? value)
        => ForPathValue(value, redactPaths: false);

    private static string ForPathValue(string? value, bool redactPaths)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var secretsRedacted = DiagnosticRedactor.RedactSensitiveText(trimmed);
            return redactPaths ? ForPath(secretsRedacted) : secretsRedacted;
        }

        var queryIndex = trimmed.IndexOf('?');
        var pathEnd = queryIndex >= 0 ? queryIndex : trimmed.Length;
        var path = trimmed["file:".Length..pathEnd];
        var secretsRedactedPath = DiagnosticRedactor.RedactSensitiveText(path);
        var redactedPath = redactPaths ? ForPath(secretsRedactedPath) : secretsRedactedPath;
        if (queryIndex < 0)
            return "file:" + redactedPath;

        var query = trimmed[(queryIndex + 1)..];
        var redactedSegments = query
            .Split('&', StringSplitOptions.None)
            .Select(segment => RedactFileUriQuerySegment(segment, redactPaths));
        return "file:" + redactedPath + "?" + string.Join('&', redactedSegments);
    }

    public static string? ForOptionalLabel(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : ForMessage(value);

    public static string ForMessage(string? message)
        => ForMessage(message, MaxDiagnosticFieldLength);

    public static string ForMessage(string? message, int maxLength)
        => ForMessage(message, RedactAbsolutePaths, maxLength);

    internal static string ForMessage(string? message, Func<string, string> redactPaths)
        => ForMessage(message, redactPaths, MaxDiagnosticFieldLength);

    internal static string ForMessage(string? message, Func<string, string> redactPaths, int maxLength)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Maximum diagnostic length must be positive.");
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
            var withoutSensitiveValues = DiagnosticRedactor.RedactSensitiveText(withoutPaths);
            return Truncate(CollapseWhitespace(withoutSensitiveValues).Trim(), maxLength);
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

    private static string RedactFileUriQuerySegment(string segment, bool redactPaths)
    {
        var equalsIndex = segment.IndexOf('=');
        if (equalsIndex < 0)
        {
            var decodedSegment = TryUnescapeDataString(segment);
            var redactedSegment = RedactFileUriQueryComponent(decodedSegment, redactPaths);
            return !redactPaths && string.Equals(redactedSegment, decodedSegment, StringComparison.Ordinal)
                ? segment
                : EscapeFileUriQueryComponent(redactedSegment);
        }

        var rawKey = segment[..equalsIndex];
        var rawValue = segment[(equalsIndex + 1)..];
        var decodedKey = TryUnescapeDataString(rawKey);
        var decodedValue = TryUnescapeDataString(rawValue);
        var redactedKey = RedactFileUriQueryComponent(decodedKey, redactPaths);
        var redactedValue = DiagnosticRedactor.IsSensitiveName(decodedKey)
            ? "<redacted>"
            : RedactFileUriQueryComponent(decodedValue, redactPaths);
        var changed = !string.Equals(redactedKey, decodedKey, StringComparison.Ordinal)
            || !string.Equals(redactedValue, decodedValue, StringComparison.Ordinal);
        return !redactPaths && !changed
            ? segment
            : EscapeFileUriQueryComponent(redactedKey) + "=" + EscapeFileUriQueryComponent(redactedValue);
    }

    private static string RedactFileUriQueryComponent(string value, bool redactPaths)
    {
        var secretsRedacted = DiagnosticRedactor.RedactSensitiveText(value);
        if (!redactPaths)
            return secretsRedacted;
        if (secretsRedacted.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return ForSupportSafePath(secretsRedacted);
        if (IsAbsolutePath(secretsRedacted))
            return ForPath(secretsRedacted);
        return DiagnosticRedactor.RedactSensitiveText(secretsRedacted, "<path>", redactPaths: true);
    }

    private static string TryUnescapeDataString(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string EscapeFileUriQueryComponent(string value)
        => Uri.EscapeDataString(value)
            .Replace("%3Credacted%3E", "<redacted>", StringComparison.OrdinalIgnoreCase)
            .Replace("%3Cpath%3E", "<path>", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsolutePath(string value)
        => Path.IsPathRooted(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || (value.Length >= 3
                && IsAsciiLetter(value[0])
                && value[1] == ':'
                && IsPathSeparator(value[2]));

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

        var quotedTerminator = start > 0 && IsQuote(value[start - 1])
            ? value[start - 1]
            : '\0';

        if (bodyStart >= value.Length || IsPathTerminator(value[bodyStart], quotedTerminator))
            return false;

        end = bodyStart + 1;
        while (end < value.Length && !IsPathTerminator(value[end], quotedTerminator))
            end++;

        return true;
    }

    private static bool IsAsciiLetter(char value)
        => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsPathSeparator(char value)
        => value is '/' or '\\';

    private static bool IsQuote(char value)
        => value is '\'' or '"';

    private static bool IsPathTerminator(char value, char quotedTerminator)
        => quotedTerminator == '\0'
            ? char.IsWhiteSpace(value) || value is '\'' or '"' or ';' or ':' or ',' or ')'
            : value == quotedTerminator;

    private static string Truncate(string value)
        => Truncate(value, MaxDiagnosticFieldLength);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
}
