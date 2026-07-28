using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Cli;

internal static class DiffCursorCodec
{
    internal const string Prefix = "diff:v1:";
    internal const int MaxCursorLength = 512;

    internal static string CreateSelectionFingerprint(string leftDb, string rightDb, bool includeContent)
    {
        var material = string.Join(
            "\n",
            "diff-record-contract:v1",
            leftDb,
            rightDb,
            includeContent ? "include-content" : "redacted");
        return HexEncoding.ToLowerHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    internal static string Encode(int offset, string selectionFingerprint)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{offset}\n{selectionFingerprint}");
        return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    internal static bool TryDecode(
        string cursor,
        string expectedSelectionFingerprint,
        out int offset,
        out string error)
    {
        offset = 0;
        if (cursor.Length > MaxCursorLength
            || !cursor.StartsWith(Prefix, StringComparison.Ordinal)
            || !TryFromBase64Url(cursor[Prefix.Length..], out var payloadBytes))
        {
            error = $"--cursor must be an opaque {Prefix} cursor returned by a prior detailed diff response";
            return false;
        }

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var separator = payload.IndexOf('\n');
        if (separator <= 0
            || separator == payload.Length - 1
            || !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || offset < 0)
        {
            offset = 0;
            error = $"--cursor must be an opaque {Prefix} cursor returned by a prior detailed diff response";
            return false;
        }

        var actualFingerprint = payload[(separator + 1)..];
        if (!FixedTimeEquals(actualFingerprint, expectedSelectionFingerprint))
        {
            offset = 0;
            error = "--cursor does not match the selected database pair or content policy; restart without --cursor";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0)
            return false;

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding == 1)
            return false;
        if (padding > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
