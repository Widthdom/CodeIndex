using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Cli;

internal static class DiffCursorCodec
{
    internal const string Prefix = "diff:v1:";
    internal const int MaxCursorLength = 512;

    internal static string CreateSelectionFingerprint(
        string leftDb,
        string rightDb,
        bool includeContent,
        bool dataOnly,
        bool includeTelemetry)
    {
        using var hash = CreateSelectionHash(leftDb, rightDb, includeContent, dataOnly, includeTelemetry);
        return CompleteSelectionFingerprint(hash);
    }

    internal static IncrementalHash CreateSelectionHash(
        string leftDb,
        string rightDb,
        bool includeContent,
        bool dataOnly,
        bool includeTelemetry)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSelectionPart(hash, "diff-record-selection:v1");
        AppendSelectionPart(hash, leftDb);
        AppendSelectionPart(hash, rightDb);
        AppendSelectionPart(hash, includeContent ? "include-content" : "redacted");
        AppendSelectionPart(
            hash,
            includeTelemetry
                ? "semantic-with-telemetry"
                : dataOnly ? "data-only" : "semantic");
        return hash;
    }

    internal static void AppendSelectionRecord(
        IncrementalHash hash,
        string area,
        string side,
        string identitySha256)
    {
        AppendSelectionPart(hash, area);
        AppendSelectionPart(hash, side);
        AppendSelectionPart(hash, identitySha256);
    }

    internal static string CompleteSelectionFingerprint(IncrementalHash hash)
        => HexEncoding.ToLowerHexString(hash.GetHashAndReset());

    internal static string Encode(int offset, string selectionFingerprint)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{offset}\n{selectionFingerprint}");
        return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    internal static bool TryDecode(
        string cursor,
        out int offset,
        out string selectionFingerprint,
        out string error)
    {
        offset = 0;
        selectionFingerprint = string.Empty;
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

        selectionFingerprint = payload[(separator + 1)..];
        if (selectionFingerprint.Length != SHA256.HashSizeInBytes * 2
            || selectionFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            offset = 0;
            selectionFingerprint = string.Empty;
            error = $"--cursor must be an opaque {Prefix} cursor returned by a prior detailed diff response";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void AppendSelectionPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
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
