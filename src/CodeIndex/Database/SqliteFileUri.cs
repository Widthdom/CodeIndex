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
        return RequestsImmutableSnapshot(uriText)
            || HasQuerySegment(uriText, "mode=ro");
    }

    // Freshness diagnostics must be conservative: SQLite still honors immutable=1 when
    // unrelated URI parameters (for example cache=shared) are present. This is deliberately
    // broader than RequestsUnambiguousImmutableSnapshot, whose stricter contract protects
    // generation-zero cursor trust rather than stale-snapshot reporting.
    // freshness 診断では追加 URI parameter があっても SQLite の immutable=1 を検出する。
    internal static bool RequestsImmutableSnapshot(string uriText)
        => HasQuerySegment(uriText, "immutable=1");

    internal static bool RequestsUnambiguousImmutableSnapshot(string uriText)
    {
        if (!StartsWithFileScheme(uriText)
            || !TryValidateBounds(uriText, out var queryIndex, out _)
            || queryIndex < 0)
        {
            return false;
        }

        var sawImmutable = false;
        var sawReadOnlyMode = false;
        var query = uriText.AsSpan(queryIndex + 1);
        while (!query.IsEmpty)
        {
            var ampersandIndex = query.IndexOf('&');
            var segment = ampersandIndex >= 0 ? query[..ampersandIndex] : query;
            if (segment.SequenceEqual("immutable=1".AsSpan()))
            {
                if (sawImmutable)
                    return false;
                sawImmutable = true;
            }
            else if (segment.SequenceEqual("mode=ro".AsSpan()))
            {
                if (sawReadOnlyMode)
                    return false;
                sawReadOnlyMode = true;
            }
            else
            {
                // Generation-zero cursors rely on SQLite actually honoring immutable=1.
                // Reject unknown, encoded, case-variant, whitespace-padded, or conflicting
                // query parameters instead of interpreting them more broadly than SQLite.
                // 世代 0 cursor は SQLite が immutable=1 を実際に解釈することが前提。
                // unknown / encoded / case variant / 空白付き / 競合 parameter は拒否する。
                return false;
            }

            if (ampersandIndex < 0)
                break;
            query = query[(ampersandIndex + 1)..];
        }

        return sawImmutable;
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

    private static bool HasQuerySegment(string uriText, string expectedSegment)
    {
        if (!StartsWithFileScheme(uriText)
            || !TryValidateBounds(uriText, out var queryIndex, out _)
            || queryIndex < 0)
        {
            return false;
        }

        var query = uriText.AsSpan(queryIndex + 1);
        while (!query.IsEmpty)
        {
            var ampersandIndex = query.IndexOf('&');
            var segment = ampersandIndex >= 0 ? query[..ampersandIndex] : query;
            if (segment.Trim().Equals(expectedSegment.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (ampersandIndex < 0)
                break;
            query = query[(ampersandIndex + 1)..];
        }

        return false;
    }
}
