using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

/// <summary>
/// Converts client-controlled JSON-RPC ids into a bounded, process-local correlation token.
/// The raw id remains available to the protocol layer, but telemetry receives only this token
/// plus non-content metadata (#4551).
/// クライアント制御の JSON-RPC id を、プロセス内だけで相関可能な固定長トークンへ変換する。
/// protocol layer は raw id を保持するが、telemetry にはこのトークンと非内容 metadata だけを渡す (#4551)。
/// </summary>
internal static class McpRequestIdTelemetry
{
    internal const string TokenPrefix = "rid:v1:";
    internal const int TokenDigestBytes = 16;
    internal const int ProcessSaltBytes = 32;
    internal const int MaxDistinctTokensPerProcess = 4096;
    internal const int MaxTokenCardinalityPerProcess = MaxDistinctTokensPerProcess + 1;
    internal static readonly int TokenLength = TokenPrefix.Length + (TokenDigestBytes * 2);

    private static readonly McpRequestIdTelemetryProvider ProcessProvider = new(
        RandomNumberGenerator.GetBytes(ProcessSaltBytes),
        MaxDistinctTokensPerProcess);

    internal static McpRequestIdTelemetryData Create(JsonNode? id)
        => ProcessProvider.Create(id);

    internal static McpRequestIdTelemetryData Create(JsonElement id)
        => ProcessProvider.Create(id);
}

/// <summary>
/// Process-local request-id token provider. It preserves exact correlation for a bounded number
/// of distinct ids, then maps every previously unseen id to one salted fixed-length overflow
/// token so hostile clients cannot create unbounded telemetry cardinality (#4551).
/// process-local な request-id token provider。上限件数までは id ごとの相関を維持し、
/// それ以降の未観測 id は salted な固定長 overflow token 1 個へ集約することで、悪意ある
/// client が telemetry cardinality を無制限に増やせないようにする (#4551)。
/// </summary>
internal sealed class McpRequestIdTelemetryProvider
{
    private const string HashDomain = "cdidx:mcp:jsonrpc-id:v1";
    private const string OverflowType = "overflow";
    private const string OverflowValue = "distinct-id-budget-exhausted";

    private readonly object _gate = new();
    private readonly byte[] _processSalt;
    private readonly int _maxDistinctTokens;
    private readonly HashSet<string> _distinctTokens = new(StringComparer.Ordinal);
    private readonly string _overflowToken;

    internal McpRequestIdTelemetryProvider(byte[] processSalt, int maxDistinctTokens)
    {
        ArgumentNullException.ThrowIfNull(processSalt);
        if (processSalt.Length != McpRequestIdTelemetry.ProcessSaltBytes)
        {
            throw new ArgumentException(
                $"Request-id telemetry salt must be exactly {McpRequestIdTelemetry.ProcessSaltBytes} bytes.",
                nameof(processSalt));
        }
        if (maxDistinctTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistinctTokens), maxDistinctTokens, "Distinct-token limit must be positive.");

        _processSalt = (byte[])processSalt.Clone();
        _maxDistinctTokens = maxDistinctTokens;
        _overflowToken = ComputeToken(OverflowType, OverflowValue);
    }

    internal McpRequestIdTelemetryData Create(JsonNode? id)
    {
        if (id is null)
            return CreateCore("null", "null", length: 0);

        if (id is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => CreateString(value.TryGetValue<string>(out var text) ? text : string.Empty),
                JsonValueKind.Number => CreateNumber(value.ToJsonString()),
                JsonValueKind.Null => CreateCore("null", "null", length: 0),
                _ => CreateCore("invalid", "invalid", length: 0),
            };
        }

        return CreateCore("invalid", "invalid", length: 0);
    }

    internal McpRequestIdTelemetryData Create(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.String => CreateString(id.GetString() ?? string.Empty),
            JsonValueKind.Number => CreateNumber(id.GetRawText()),
            JsonValueKind.Null => CreateCore("null", "null", length: 0),
            _ => CreateCore("invalid", "invalid", length: 0),
        };

    private McpRequestIdTelemetryData CreateString(string value)
        => CreateBounded("string", value);

    private McpRequestIdTelemetryData CreateNumber(string value)
        => CreateBounded("number", value);

    private McpRequestIdTelemetryData CreateBounded(string type, string value)
    {
        if (value.Length > McpServer.MaxRequestIdCharacterCount
            || Encoding.UTF8.GetByteCount(value) > McpServer.MaxRequestIdByteLength)
        {
            // Production request parsing rejects oversized ids before telemetry creation.
            // Keep this provider safe for future callers too: use a hash domain that cannot
            // collide with a valid literal "oversized", and collapse the reported character
            // length to one bounded over-limit sentinel (#4551).
            // production の request parse は oversized id を telemetry 作成前に拒否する。
            // 将来の caller に対しても安全にするため、正規の literal "oversized" と衝突しない
            // hash domain を使い、文字数も有限な上限超過 sentinel へ集約する (#4551)。
            return CreateCore(
                type,
                "oversized",
                Math.Min(value.Length, McpServer.MaxRequestIdCharacterCount + 1),
                tokenType: $"oversized:{type}");
        }

        return CreateCore(type, value, value.Length);
    }

    private McpRequestIdTelemetryData CreateCore(string type, string value, int length, string? tokenType = null)
    {
        var candidate = ComputeToken(tokenType ?? type, value);
        lock (_gate)
        {
            if (_distinctTokens.Contains(candidate))
                return new McpRequestIdTelemetryData(candidate, type, length);

            if (_distinctTokens.Count < _maxDistinctTokens)
            {
                _distinctTokens.Add(candidate);
                return new McpRequestIdTelemetryData(candidate, type, length);
            }
        }

        return new McpRequestIdTelemetryData(_overflowToken, type, length);
    }

    private string ComputeToken(string type, string value)
    {
        var domainBytes = Encoding.UTF8.GetBytes(HashDomain);
        var typeBytes = Encoding.UTF8.GetBytes(type);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[domainBytes.Length + 1 + typeBytes.Length + 1 + valueBytes.Length];
        domainBytes.CopyTo(input, 0);
        input[domainBytes.Length] = 0;
        var typeOffset = domainBytes.Length + 1;
        typeBytes.CopyTo(input, typeOffset);
        var valueOffset = typeOffset + typeBytes.Length + 1;
        input[valueOffset - 1] = 0;
        valueBytes.CopyTo(input, valueOffset);

        var digest = HMACSHA256.HashData(_processSalt, input);
        return McpRequestIdTelemetry.TokenPrefix
            + Convert.ToHexString(digest.AsSpan(0, McpRequestIdTelemetry.TokenDigestBytes)).ToLowerInvariant();
    }
}

internal readonly record struct McpRequestIdTelemetryData(string Token, string Type, int Length);
