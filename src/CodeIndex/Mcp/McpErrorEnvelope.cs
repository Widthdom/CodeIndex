using System;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Mcp;

/// <summary>
/// Structured-error envelope for MCP JSON-RPC and tool-result responses (issue #1581).
/// Every error payload carries `data.category`, `data.suggestion`, and `data.retry_safe`
/// so MCP clients can branch on a stable machine-readable category instead of parsing
/// `message` strings. Server-defined JSON-RPC codes (`-32000..-32099`) are layered on
/// top of the standard JSON-RPC range and documented in `DEVELOPER_GUIDE.md`.
/// MCP の JSON-RPC エラーとツール結果エラーに共通する構造化データエンベロープ（#1581）。
/// `data.category` / `data.suggestion` / `data.retry_safe` を必ず含めることで、クライアントが
/// `message` 文字列を解析せず安定したカテゴリで分岐できるようにする。サーバー固有コードは
/// `-32000..-32099` の予約レンジから割り当て、コード表は `DEVELOPER_GUIDE.md` に記載する。
/// </summary>
internal static class McpErrorEnvelope
{
    // Server-defined error codes. The JSON-RPC 2.0 spec reserves `-32000..-32099` for
    // implementations. The standard range (`-32700`, `-32600..-32603`) still applies to
    // the JSON-RPC envelope itself (parse error, invalid request, method not found,
    // invalid params, internal error); the codes below cover cdidx-specific categories.
    // JSON-RPC 2.0 仕様は `-32000..-32099` を実装定義に予約しており、ここでは cdidx 固有の
    // カテゴリに割り当てる。標準コード（`-32700`, `-32600..-32603`）は引き続き JSON-RPC 自体
    // のエラー（パースエラー、不正リクエスト、未知メソッド、不正パラメータ、内部エラー）に
    // 使う。
    public const int CodeRateLimited = -32000;
    public const int CodeUnauthorized = -32001;
    public const int CodeIndexMissing = -32010;
    public const int CodeIndexStale = -32011;
    public const int CodeIndexCorrupted = -32012;
    public const int CodeRequestCancelled = -32015;

    // Category names (stable wire identifiers). Keep in lockstep with `DEVELOPER_GUIDE.md`.
    // カテゴリ名（安定ワイヤ識別子）。`DEVELOPER_GUIDE.md` と同期させる。
    public const string CategoryParseError = "parse_error";
    public const string CategoryMessageTooLarge = "message_too_large";
    public const string CategoryInvalidRequest = "invalid_request";
    public const string CategoryMethodNotFound = "method_not_found";
    public const string CategoryMissingParameter = "missing_parameter";
    public const string CategoryInvalidArgument = "invalid_argument";
    public const string CategoryToolUnknown = "tool_unknown";
    public const string CategoryToolDisabled = "tool_disabled";
    public const string CategoryPermissionDenied = "permission_denied";
    public const string CategoryRateLimited = "rate_limited";
    public const string CategoryRequestCancelled = "request_cancelled";
    public const string CategoryIndexMissing = "index_missing";
    public const string CategoryIndexStale = "index_stale";
    public const string CategoryIndexCorrupted = "index_corrupted";
    public const string CategoryInternalError = "internal_error";

    /// <summary>
    /// Build the canonical `data` object shared by JSON-RPC error responses and MCP
    /// tool-result errors. Callers may pass `extraData` to merge in category-specific
    /// fields (e.g. `tool`, `retry_after_ms` for rate-limited responses); these are
    /// added after the canonical keys so the canonical contract always wins on collision.
    /// JSON-RPC エラーと MCP ツール結果エラーで共通する `data` オブジェクトを構築する。
    /// `extraData` でカテゴリ固有のフィールド（例: rate-limited の `tool` / `retry_after_ms`）
    /// を合流できる。canonical キー（`category` / `suggestion` / `retry_safe`）は後から
    /// 上書きされないよう先に書き込む。
    /// </summary>
    public static JsonObject BuildData(string category, string suggestion, bool retrySafe, JsonObject? extraData = null)
    {
        var data = new JsonObject
        {
            ["category"] = category,
            ["suggestion"] = suggestion,
            ["retry_safe"] = retrySafe,
        };
        if (extraData != null)
        {
            foreach (var kvp in extraData)
            {
                // Skip canonical keys so callers cannot accidentally shadow the contract.
                // canonical キーは extra でも上書きさせない。
                if (kvp.Key is "category" or "suggestion" or "retry_safe")
                    continue;
                data[kvp.Key] = McpJsonNode.Clone(kvp.Value);
            }
        }
        return data;
    }

    /// <summary>
    /// Classification returned by <see cref="ClassifyException"/>. `JsonRpcCode` is the wire
    /// code to use on the JSON-RPC error path (`ProcessFrame` catch-all); the tool-result error
    /// path ignores it and only consumes category / suggestion / retry_safe.
    /// `ClassifyException` の戻り値。`JsonRpcCode` は JSON-RPC エラーパス（`ProcessFrame`
    /// catch-all）用のワイヤコード。ツール結果エラーパスはコードを使わずカテゴリ等のみ消費する。
    /// </summary>
    public readonly record struct Classification(string Category, string Suggestion, bool RetrySafe, int JsonRpcCode);

    /// <summary>
    /// Map an unhandled exception to a structured error category so clients can branch on
    /// `index_stale` (retry after refresh) vs `index_corrupted` (give up) vs `internal_error`
    /// (#1581). Detection looks at exception type plus selected SQLite message fragments —
    /// the wire response still does not leak the raw message (#1530).
    /// 未処理例外を構造化カテゴリにマッピングし、クライアントが `index_stale`（再構築後に再試行）/
    /// `index_corrupted`（諦め）/ `internal_error` を区別できるようにする（#1581）。検出は例外型と
    /// SQLite メッセージの一部のみを参照する。生メッセージはワイヤに乗らない（#1530）。
    /// </summary>
    public static Classification ClassifyException(Exception ex)
    {
        if (ex is null)
            return new Classification(CategoryInternalError, InternalErrorSuggestion, false, -32603);

        if (ex is OperationCanceledException)
            return new Classification(
                CategoryRequestCancelled,
                "Request was cancelled before completion. Reissue the call if the work is still needed.",
                RetrySafe: true,
                JsonRpcCode: CodeRequestCancelled);

        if (ex is SqliteException sqlite)
        {
            var msg = sqlite.Message ?? string.Empty;
            if (msg.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("no such column", StringComparison.OrdinalIgnoreCase))
                return new Classification(
                    CategoryIndexStale,
                    "Index schema is older than this cdidx build. Run `cdidx index <projectPath> --rebuild`, then retry.",
                    RetrySafe: true,
                    JsonRpcCode: CodeIndexStale);
            if (msg.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase))
                return new Classification(
                    CategoryIndexCorrupted,
                    "Index database is corrupted. Delete the index DB and run `cdidx index <projectPath>` to rebuild.",
                    RetrySafe: false,
                    JsonRpcCode: CodeIndexCorrupted);
        }

        return new Classification(CategoryInternalError, InternalErrorSuggestion, false, -32603);
    }

    public const string InternalErrorSuggestion =
        "Unhandled error inside cdidx. Check the cdidx server stderr for the exception type; file an issue if it is reproducible.";
}
