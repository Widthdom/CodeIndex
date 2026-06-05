using System.Globalization;

namespace CodeIndex.Database;

internal static class SqliteFileUri
{
    internal const int MaxUriLength = 16 * 1024;
    internal const int MaxQueryLength = 2048;
    internal const int MaxDiagnosticValueLength = 256;

    private const string FileSchemePrefix = "file:";

    internal static bool StartsWithFileScheme(string uriText)
        => uriText.StartsWith(FileSchemePrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetPathBeforeQuery(string uriText, out string pathText, out FormatException? parseError)
    {
        pathText = uriText;
        if (!StartsWithFileScheme(uriText))
        {
            parseError = null;
            return true;
        }

        if (!TryValidateBounds(uriText, out var queryIndex, out parseError))
            return false;

        pathText = queryIndex >= 0 ? uriText[..queryIndex] : uriText;
        return true;
    }

    internal static bool TryValidateBounds(string uriText, out FormatException? parseError)
    {
        if (!StartsWithFileScheme(uriText))
        {
            parseError = null;
            return true;
        }

        return TryValidateBounds(uriText, out _, out parseError);
    }

    internal static bool RequestsReadOnly(string uriText)
    {
        if (!StartsWithFileScheme(uriText))
            return false;

        if (!TryValidateBounds(uriText, out var queryIndex, out _))
            return false;

        if (queryIndex < 0)
            return false;

        var query = uriText.AsSpan(queryIndex + 1);
        while (!query.IsEmpty)
        {
            var ampersandIndex = query.IndexOf('&');
            var segment = ampersandIndex >= 0 ? query[..ampersandIndex] : query;
            segment = segment.Trim();
            if (segment.Equals("immutable=1".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("mode=ro".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ampersandIndex < 0)
                break;

            query = query[(ampersandIndex + 1)..];
        }

        return false;
    }

    internal static string TruncateDiagnosticValue(string value)
    {
        if (value.Length <= MaxDiagnosticValueLength)
            return value;

        return value[..MaxDiagnosticValueLength] +
            "...(truncated, " +
            value.Length.ToString(CultureInfo.InvariantCulture) +
            " chars)";
    }

    internal static string FormatParseError(Exception? parseError)
        => parseError?.Message ?? "Invalid SQLite file URI.";

    private static bool TryValidateBounds(string uriText, out int queryIndex, out FormatException? parseError)
    {
        queryIndex = -1;
        if (uriText.Length > MaxUriLength)
        {
            parseError = new FormatException(
                $"SQLite file URI length exceeds {MaxUriLength.ToString(CultureInfo.InvariantCulture)} characters.");
            return false;
        }

        queryIndex = uriText.IndexOf('?');
        if (queryIndex >= 0)
        {
            var queryLength = uriText.Length - queryIndex - 1;
            if (queryLength > MaxQueryLength)
            {
                parseError = new FormatException(
                    $"SQLite file URI query length exceeds {MaxQueryLength.ToString(CultureInfo.InvariantCulture)} characters.");
                return false;
            }
        }

        parseError = null;
        return true;
    }
}
