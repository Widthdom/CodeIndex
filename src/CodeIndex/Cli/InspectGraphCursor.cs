using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

internal sealed record InspectGraphCursor(
    string Section,
    int Offset,
    string? CandidateSelector,
    string QueryFingerprint,
    string GenerationFingerprint);

internal static class InspectGraphCursorCodec
{
    private const string Prefix = "inspect-graph:v1:";

    internal static string BuildQueryFingerprint(IEnumerable<string?> components)
        => BuildValueFingerprint(string.Join('\0', components.Select(component => component ?? string.Empty)));

    internal static (string Fingerprint, string? StableAt) BuildGenerationFingerprint(DbReader reader)
    {
        var generation = reader.GetPaginationGeneration();
        return (BuildValueFingerprint(generation.Identity), generation.StableAt);
    }

    internal static string Format(
        string section,
        int offset,
        string? candidateSelector,
        string queryFingerprint,
        string generationFingerprint)
    {
        var payload = new JsonObject
        {
            ["section"] = section,
            ["offset"] = offset,
            ["candidate"] = candidateSelector,
            ["query"] = queryFingerprint,
            ["generation"] = generationFingerprint,
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + encoded;
    }

    internal static bool TryParse(string cursor, out InspectGraphCursor? parsed)
    {
        parsed = null;
        if (!cursor.StartsWith(Prefix, StringComparison.Ordinal) || cursor.Length > 16_384)
            return false;

        var encoded = cursor[Prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - encoded.Length % 4) % 4;
        if (paddingLength > 0)
            encoded += new string('=', paddingLength);

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (payload is null
            || !TryGetString(payload, "section", out var section)
            || section is not ("references" or "callers" or "callees")
            || payload["offset"] is not JsonValue offsetValue
            || !offsetValue.TryGetValue<int>(out var offset)
            || offset < 0
            || !TryGetString(payload, "query", out var queryFingerprint)
            || !TryGetString(payload, "generation", out var generationFingerprint)
            || !IsFingerprint(queryFingerprint)
            || !IsFingerprint(generationFingerprint))
        {
            return false;
        }

        string? candidateSelector = null;
        if (payload["candidate"] is JsonValue candidateValue
            && !candidateValue.TryGetValue<string>(out candidateSelector))
        {
            return false;
        }
        if (candidateSelector is { Length: > 4096 })
            return false;
        parsed = new InspectGraphCursor(
            section,
            offset,
            candidateSelector,
            queryFingerprint,
            generationFingerprint);
        return true;
    }

    private static bool IsFingerprint(string fingerprint)
        => fingerprint.Length == 16 && fingerprint.All(Uri.IsHexDigit);

    private static bool TryGetString(JsonObject payload, string propertyName, out string value)
    {
        value = string.Empty;
        if (payload[propertyName] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var parsedValue)
            || parsedValue == null)
        {
            return false;
        }
        value = parsedValue;
        return true;
    }

    private static string BuildValueFingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
