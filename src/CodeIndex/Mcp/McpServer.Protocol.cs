using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Mcp;

public partial class McpServer : IDisposable
{


    /// <summary>
    /// Resolve the caller identity used by the per-(tool, caller) rate limiter from an
    /// `initialize` request's `clientInfo`. Falls back to `"unknown"` when the client did
    /// not supply a name so anonymous callers still get a coherent bucket of their own
    /// (instead of accidentally sharing one with named clients) (#1560).
    /// (tool, caller) ごとのレート制限で使う呼び出し元 ID を `initialize` の `clientInfo` から
    /// 解決する。`name` が無い場合は `"unknown"` を返し、匿名クライアントが他の名前付きクライアントと
    /// バケットを共有しないようにする（#1560）。
    /// </summary>
    internal static string ResolveCallerIdentity(JsonNode? initializeParams)
    {
        if (initializeParams is not JsonObject obj)
            return "unknown";
        if (obj["clientInfo"] is not JsonObject clientInfo)
            return "unknown";

        var name = TryReadBoundedClientInfoMember(clientInfo, "name")?.Text;
        if (name == null)
            return "unknown";
        var version = TryReadBoundedClientInfoMember(clientInfo, "version")?.Text;
        return version == null ? name : $"{name}/{version}";
    }

    /// <summary>
    /// Return the requested protocol version when supported, the preferred version when the
    /// field is absent or malformed, and <see langword="null"/> when there is no overlap.
    /// 対応する要求バージョン、未指定・不正型なら既定バージョン、対応外なら
    /// <see langword="null"/> を返す。
    /// </summary>
    internal static string? NegotiateProtocolVersion(JsonNode? initializeParams, out BoundedMcpText? requestedVersion)
    {
        requestedVersion = null;
        if (initializeParams is JsonObject obj
            && obj.TryGetPropertyValue("protocolVersion", out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var versionString)
            && !string.IsNullOrWhiteSpace(versionString))
        {
            requestedVersion = BoundProtocolVersionForDisplay(versionString);
            foreach (var supported in SupportedProtocolVersions)
            {
                if (string.Equals(supported, versionString, StringComparison.Ordinal))
                    return supported;
            }
            return null;
        }

        // Field absent / null / malformed: fall back to the preferred version so clients
        // that omit the field (or send a non-string sentinel) keep working as before.
        // 未指定 / null / 不正型: 既定バージョンに fallback して既存クライアントの互換を保つ。
        return ProtocolVersion;
    }

    private static JsonObject CreateUnsupportedProtocolError(JsonNode? id, BoundedMcpText? requestedVersion)
    {
        var supportedArray = new JsonArray();
        foreach (var supported in SupportedProtocolVersions)
            supportedArray.Add(JsonValue.Create(supported));

        // Keep the #1554 version-negotiation fields, then layer the #1581 canonical envelope
        // on top via BuildData so this path also carries `category` / `suggestion` /
        // `retry_safe` like every other JSON-RPC error.
        // #1554 のバージョン交渉用フィールドを保ちつつ、#1581 の canonical envelope を
        // BuildData で重ねて、他の JSON-RPC エラーと同様に category / suggestion / retry_safe
        // を含めるようにする。
        var extra = new JsonObject
        {
            ["supportedVersions"] = supportedArray
        };
        if (requestedVersion != null)
        {
            extra["requestedVersion"] = requestedVersion.Value.Text;
            requestedVersion.Value.AddMetadata(extra, "requestedVersion");
        }

        var data = McpErrorEnvelope.BuildData(
            McpErrorEnvelope.CategoryInvalidArgument,
            "Reissue `initialize` with one of `data.supportedVersions` in `params.protocolVersion`, or omit the field to fall back to the server's newest supported version.",
            retrySafe: false,
            AddCorrelationData(extra));

        var error = new JsonObject
        {
            ["code"] = -32602,
            ["message"] = BuildUnsupportedProtocolMessage(requestedVersion),
            ["data"] = data
        };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = error,
            ["id"] = McpJsonNode.Clone(id)
        };
        return response;
    }

    internal static string BuildUnsupportedProtocolMessage(string? requestedVersion)
        => BuildUnsupportedProtocolMessage(BoundProtocolVersionForDisplay(requestedVersion));

    private static string BuildUnsupportedProtocolMessage(BoundedMcpText? requestedVersion)
    {
        var supported = string.Join(", ", SupportedProtocolVersions);
        var requested = requestedVersion?.Text ?? "(unspecified)";
        return $"Unsupported MCP protocolVersion '{requested}'. Server supports: {supported}.";
    }

    internal static string BuildUnsupportedProtocolLog(string? requestedVersion)
        => BuildUnsupportedProtocolLog(BoundProtocolVersionForDisplay(requestedVersion));

    private static string BuildUnsupportedProtocolLog(BoundedMcpText? requestedVersion)
    {
        var supported = string.Join(", ", SupportedProtocolVersions);
        var requested = requestedVersion?.Text ?? "(unspecified)";
        return $"[cdidx-mcp] Rejecting initialize: client requested protocolVersion '{requested}', server supports {supported}. Upgrade the server or pin a supported version on the client.";
    }

    private static BoundedMcpText? BoundProtocolVersionForDisplay(string? requestedVersion)
        => string.IsNullOrEmpty(requestedVersion)
            ? null
            : McpBoundedText.ForDisplay(requestedVersion, McpBoundedText.MaxProtocolVersionChars);

    private static BoundedMcpText BoundClientInfoForDisplay(string value)
        => McpBoundedText.ForDisplay(value, McpBoundedText.MaxClientInfoChars);

    private static BoundedMcpText BoundClientIdentityForDisplay(string value)
        => McpBoundedText.ForDisplay(value, McpBoundedText.MaxClientIdentityChars);

    private static string? ResolveKnownRateLimitBucketName(string? toolName)
    {
        // Only canonical known-tool names receive a secondary per-tool bucket. Missing,
        // malformed, oversized, case-variant, and unknown names are covered solely by the
        // fixed caller-wide pre-validation bucket, so they cannot create name-derived keys
        // (#4547).
        // canonical な既知ツール名だけに secondary per-tool bucket を割り当てる。missing /
        // malformed / oversized / 大文字小文字 variant / unknown は caller-wide の固定
        // pre-validation bucket だけで扱い、名前由来キーを作成させない（#4547）。
        if (toolName is not null)
        {
            foreach (var knownToolName in McpToolFilter.KnownToolNames)
            {
                if (string.Equals(knownToolName, toolName, StringComparison.Ordinal))
                    return knownToolName;
            }
        }

        return null;
    }

    /// <summary>
    /// Build a structured `-32000` JSON-RPC error for a rate-limited tool call. Surfacing
    /// the limit category in `error.data.error_category` (alongside `tool`, `caller`, and
    /// `retry_after_ms`) lets MCP clients branch on the failure type without parsing the
    /// human-readable `message` (#1560).
    /// レート制限で拒否されたツール呼び出し用の構造化 `-32000` JSON-RPC エラーを構築する。
    /// `error.data.error_category` を併記することでクライアントが `message` 文字列を解析せず
    /// 失敗カテゴリで分岐できるようにする（#1560）。
    /// </summary>
    internal static JsonObject CreateRateLimitedErrorResponse(JsonNode? id, string tool, string caller, long retryAfterMs)
    {
        var toolDisplay = BoundToolNameForDisplay(tool);
        var callerDisplay = BoundClientIdentityForDisplay(caller);
        // #1560 contract preserved: `error_category`, `tool`, `caller`, `retry_after_ms`.
        // #1581 adds the canonical envelope (`category`, `suggestion`, `retry_safe`) alongside.
        // #1560 の契約（`error_category`, `tool`, `caller`, `retry_after_ms`）を維持しつつ、
        // #1581 で導入した canonical envelope（`category`, `suggestion`, `retry_safe`）を併記する。
        var extraData = new JsonObject
        {
            ["error_category"] = "rate_limited",
            ["tool"] = toolDisplay.Text,
            ["caller"] = callerDisplay.Text,
            ["retry_after_ms"] = retryAfterMs,
        };
        toolDisplay.AddMetadata(extraData, "tool");
        callerDisplay.AddMetadata(extraData, "caller");
        var data = McpErrorEnvelope.BuildData(
            category: McpErrorEnvelope.CategoryRateLimited,
            suggestion: $"Back off for at least {retryAfterMs} ms before retrying this tool, or raise {RateLimiterOptions.RpsEnvVar} / {RateLimiterOptions.BurstEnvVar} on the server.",
            retrySafe: true,
            extraData: AddCorrelationData(extraData));
        var error = new JsonObject
        {
            ["code"] = -32000,
            ["message"] = $"Rate limit exceeded for tool '{toolDisplay.Text}' (retry after {retryAfterMs} ms).",
            ["data"] = data,
        };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = error,
            ["id"] = McpJsonNode.Clone(id)
        };
        return response;
    }

}
