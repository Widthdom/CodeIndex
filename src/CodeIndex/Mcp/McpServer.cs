using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server over stdin/stdout using JSON-RPC 2.0.
/// stdin/stdout上のJSON-RPC 2.0によるMCPサーバー。
/// Protocol version: 2024-11-05
/// </summary>
public partial class McpServer
{
    private readonly string _dbPath;
    private readonly bool _dbPathExplicit;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<JsonNode, string> _serializeResponse;
    private bool _running = true;

    private const string ProtocolVersion = "2025-03-26";
    private const int MaxLimit = 200;
    private const int MaxQueryLength = 1000;
    private const int MaxLineLength = 1_000_000; // 1 MB per JSON-RPC message / 1メッセージあたり最大1MB
    // Stdio buffer for the JSON-RPC loop. Sized to fit typical large MCP payloads (e.g. batch_query)
    // in a single read so the StreamReader does not grow from its 1 KB default toward MaxLineLength.
    // JSON-RPCループのstdioバッファ。大きめのMCPペイロードを1回の読み取りで吸収し、
    // StreamReaderのデフォルト1KBから繰り返し拡張されるのを避けるサイズ。
    private const int StdioBufferSize = 64 * 1024;

    public McpServer(string dbPath, string version, bool dbPathExplicit = false)
        : this(dbPath, version, dbPathExplicit, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse)
    {
        _dbPath = dbPath;
        _dbPathExplicit = dbPathExplicit;
        _version = version;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        _serializeResponse = serializeResponse ?? (node => node.ToJsonString(_jsonOptions));
    }

    /// <summary>
    /// Run the MCP server loop, reading JSON-RPC messages from stdin and writing responses to stdout.
    /// MCPサーバーループを実行。stdinからJSON-RPCメッセージを読み、stdoutにレスポンスを書く。
    /// </summary>
    public async Task RunAsync()
    {
        // Use stderr for logging so stdout stays clean for JSON-RPC
        // stdoutをJSON-RPC用にクリーンに保つため、ログはstderrに出力
        Console.Error.WriteLine($"[cdidx-mcp] Starting MCP server v{_version} (db: {_dbPath})");

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: StdioBufferSize);
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false), bufferSize: StdioBufferSize) { AutoFlush = true };

        while (_running)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
                break; // stdin closed / stdinが閉じられた

            await ProcessLineAsync(line, writer);
        }

        Console.Error.WriteLine("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    /// <summary>
    /// Process one MCP JSON-RPC line and write any response to the provided writer.
    /// 1行分のMCP JSON-RPCを処理し、必要ならwriterにレスポンスを書き込む。
    /// </summary>
    internal async Task ProcessLineAsync(string line, TextWriter writer)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Reject oversized messages to prevent memory exhaustion
        // メモリ枯渇を防ぐため巨大メッセージを拒否
        if (line.Length > MaxLineLength)
        {
            Console.Error.WriteLine(BuildOversizedMessageLog(line.Length));
            var errorResponse = CreateErrorResponse(null, -32700, "Message too large");
            await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions));
            return;
        }

        JsonNode? request = null;
        try
        {
            request = JsonNode.Parse(line);
            if (request == null)
                return;

            var response = HandleMessage(request);
            if (response != null)
            {
                await writer.WriteLineAsync(_serializeResponse(response));
            }
        }
        catch (JsonException ex)
        {
            // Parse error / パースエラー
            Console.Error.WriteLine(BuildJsonParseErrorLog(ex.Message));
            var errorResponse = CreateErrorResponse(null, -32700, "Parse error");
            await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(BuildUnhandledLoopErrorLog(ex.Message));
            if (request is JsonObject requestObj && requestObj.TryGetPropertyValue("id", out var requestId))
            {
                var errorResponse = CreateErrorResponse(true, requestId, -32603, ex.Message);
                await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions));
            }
        }
    }

    /// <summary>
    /// Route a JSON-RPC message to the appropriate handler.
    /// JSON-RPCメッセージを適切なハンドラにルーティング。
    /// </summary>
    internal JsonNode? HandleMessage(JsonNode request)
    {
        if (request is not JsonObject obj)
            return CreateErrorResponse(hasId: false, id: null, code: -32600, message: "Invalid request: expected JSON object");

        var method = obj["method"]?.GetValue<string>();
        if (!TryGetRequestId(obj, out var hasId, out var id))
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: id must be string, number, or null");

        // Notifications (no id) don't get a response / 通知（idなし）にはレスポンスなし
        if (method == "notifications/initialized" || method == "notifications/cancelled")
            return null;

        if (!hasId)
        {
            if (method != null && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(BuildUnknownNotificationLog(method));
            return null;
        }

        if (method == null)
        {
            return CreateErrorResponse(hasId: true, id: id, code: -32600, message: "Invalid request: missing method");
        }

        return method switch
        {
            "initialize" => HandleInitialize(id, request["params"]),
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolsCall(id, request["params"]),
            "ping" => CreateSuccessResponse(hasId, id, new JsonObject()),
            _ => CreateErrorResponse(hasId: true, id: id, code: -32601, message: $"Method not found: {method}"),
        };
    }

    /// <summary>
    /// Handle the initialize handshake.
    /// initializeハンドシェイクを処理。
    /// </summary>
    private JsonNode HandleInitialize(JsonNode? id, JsonNode? _params)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "cdidx",
                ["version"] = _version
            },
            // Server instructions — tool-selection guidance for AI clients
            // サーバー指示 — AIクライアント向けツール選択ガイダンス
            ["instructions"] = BuildInstructions()
        };
        return CreateSuccessResponse(true, id, result);
    }

    // Tool definitions are in McpToolDefinitions.cs / ツール定義は McpToolDefinitions.cs に分離


    /// <summary>
    /// Execute a tool call.
    /// ツール呼び出しを実行。
    /// </summary>
    private JsonNode HandleToolsCall(JsonNode? id, JsonNode? callParams)
    {
        var toolName = callParams?["name"]?.GetValue<string>();
        var args = callParams?["arguments"];

        if (toolName == null)
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing tool name");

        Database.DbDebug.ResetContext();
        try
        {
            return toolName switch
            {
                "search" => ExecuteSearch(id, args),
                "definition" => ExecuteDefinition(id, args),
                "references" => ExecuteReferences(id, args),
                "callers" => ExecuteCallers(id, args),
                "callees" => ExecuteCallees(id, args),
                "symbols" => ExecuteSymbols(id, args),
                "files" => ExecuteFiles(id, args),
                "find_in_file" => ExecuteFindInFile(id, args),
                "excerpt" => ExecuteExcerpt(id, args),
                "map" => ExecuteMap(id, args),
                "analyze_symbol" => ExecuteAnalyzeSymbol(id, args),
                "status" => ExecuteStatus(id),
                "outline" => ExecuteOutline(id, args),
                "batch_query" => ExecuteBatchQuery(id, args),
                "deps" => ExecuteDeps(id, args),
                "impact_analysis" => ExecuteImpactAnalysis(id, args),
                "languages" => ExecuteLanguages(id),
                "validate" => ExecuteValidate(id, args),
                "unused_symbols" => ExecuteUnusedSymbols(id, args),
                "symbol_hotspots" => ExecuteSymbolHotspots(id, args),
                "ping" => ExecutePing(id),
                "index" => ExecuteIndex(id, args),
                "backfill_fold" => ExecuteBackfillFold(id),
                "suggest_improvement" => ExecuteSuggestImprovement(id, args),
                _ => CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"Unknown tool: {toolName}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(BuildToolErrorLog(toolName, ex.Message));
            Database.DbDebug.DumpToStderr(ex);
            return CreateToolErrorResponse(true, id, $"Error executing {toolName}: {ex.Message}");
        }
        finally
        {
            Database.DbDebug.ResetContext();
        }
    }

    internal static string BuildOversizedMessageLog(int lineLength) =>
        $"[cdidx-mcp] Message too large ({lineLength} bytes), rejecting. Split the request into smaller JSON-RPC messages or shorter arguments, then retry.";

    internal static string BuildJsonParseErrorLog(string detail) =>
        $"[cdidx-mcp] JSON parse error: {detail}. Send one UTF-8 JSON-RPC object per line and retry.";

    internal static string BuildUnhandledLoopErrorLog(string detail) =>
        $"[cdidx-mcp] Error: {detail}. This request was skipped; fix the request or inspect the server environment, then retry.";

    internal static string BuildToolErrorLog(string toolName, string detail) =>
        $"[cdidx-mcp] Tool error ({toolName}): {detail}. Fix the tool arguments, refresh the index if needed, then retry.";

    internal static string BuildUnknownNotificationLog(string method) =>
        $"[cdidx-mcp] Ignoring unknown notification: {method}";

    // Tool implementations are in McpToolHandlers.cs / ツール実装は McpToolHandlers.cs に分離

    // --- DB helper / DBヘルパー ---

    private JsonNode WithDbReader(JsonNode? id, Func<DbReader, JsonNode> action)
    {
        // Accept SQLite file: URIs the same way the CLI does (QueryCommandRunner.WithDb),
        // so AI agents on read-only mounts can pass `--db file:///abs/path?immutable=1` and
        // reach the read-only escape hatch in DbContext. File.Exists is skipped for URI-
        // shaped values because they may carry query params meaningless to the filesystem.
        // CLI と同じく file: URI を受け付け、サンドボックス用の escape hatch に到達できるようにする。
        var isUri = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(_dbPath))
            return CreateToolErrorResponse(true, id, $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first.");

        using var db = new DbContext(_dbPath);
        db.TryMigrateForRead();
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return action(reader);
    }

    // --- JSON-RPC helpers / JSON-RPCヘルパー ---

    private static bool TryGetRequestId(JsonObject request, out bool hasId, out JsonNode? id)
    {
        hasId = request.TryGetPropertyValue("id", out id);
        if (!hasId)
            return true;

        if (id is null)
            return true;

        if (id is JsonValue)
        {
            var serialized = id.ToJsonString();
            if (serialized.Length == 0)
                return false;

            var first = serialized[0];
            return first == '"' || first == '-' || char.IsDigit(first) || first == 'n';
        }

        return false;
    }

    private static JsonObject CreateSuccessResponse(JsonNode? id, JsonNode result)
        => CreateSuccessResponse(id is not null, id, result);

    private static JsonObject CreateSuccessResponse(bool hasId, JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        if (hasId)
            response["id"] = id is null ? JsonNode.Parse("null") : JsonNode.Parse(id.ToJsonString());
        return response;
    }

    private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message)
        => CreateErrorResponse(id is not null, id, code, message);

    private static JsonObject CreateErrorResponse(bool hasId, JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        if (hasId)
            response["id"] = id is null ? JsonNode.Parse("null") : JsonNode.Parse(id.ToJsonString());
        return response;
    }

    /// <summary>
    /// Create a tool result response (MCP format).
    /// ツール結果レスポンスを作成（MCP形式）。
    /// </summary>
    private static JsonObject CreateToolResult(JsonNode? id, string text, JsonNode? structuredContent = null)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            }
        };
        if (structuredContent != null)
            result["structuredContent"] = structuredContent;
        return CreateSuccessResponse(true, id, result);
    }

    /// <summary>
    /// Create a tool error response (MCP format with isError flag).
    /// ツールエラーレスポンスを作成（isErrorフラグ付きMCP形式）。
    /// </summary>
    private static JsonObject CreateToolErrorResponse(JsonNode? id, string message)
        => CreateToolErrorResponse(id is not null, id, message);

    private static JsonObject CreateToolErrorResponse(bool hasId, JsonNode? id, string message)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message
                }
            },
            ["isError"] = true
        };
        return CreateSuccessResponse(hasId, id, result);
    }

    private static JsonObject CreateToolDefinition(string name, string description, JsonObject inputSchema,
        JsonObject? annotations = null)
    {
        var def = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
        if (annotations != null)
            def["annotations"] = annotations;
        return def;
    }

    /// <summary>
    /// Build MCP tool annotations for a read-only query tool.
    /// 読み取り専用クエリツール用のMCPツールアノテーションを構築。
    /// </summary>
    private static JsonObject ReadOnlyAnnotations() => new()
    {
        ["readOnlyHint"] = true,
        ["destructiveHint"] = false,
        ["idempotentHint"] = true,
        ["openWorldHint"] = false
    };

    /// <summary>
    /// Build MCP tool annotations for the index (write) tool.
    /// index（書き込み）ツール用のMCPツールアノテーションを構築。
    /// Destructive because --rebuild drops the DB; not idempotent because
    /// re-indexing replaces chunks/symbols/references per file.
    /// --rebuildでDBを削除するため破壊的。再インデックスはファイルごとに
    /// チャンク・シンボル・参照を置き換えるため冪等ではない。
    /// </summary>
    private static JsonObject IndexAnnotations() => new()
    {
        ["readOnlyHint"] = false,
        ["destructiveHint"] = true,
        ["idempotentHint"] = false,
        ["openWorldHint"] = false
    };

    /// <summary>
    /// Build MCP tool annotations for the suggest_improvement tool.
    /// suggest_improvementツール用のMCPツールアノテーションを構築。
    /// Not read-only (writes suggestion to disk), not destructive,
    /// idempotent (duplicate submissions are safely deduplicated).
    /// 読み取り専用ではない（提案をディスクに書き込む）、破壊的ではない、
    /// 冪等（重複送信は安全に排除される）。
    /// </summary>
    private static JsonObject SuggestionAnnotations() => new()
    {
        ["readOnlyHint"] = false,
        ["destructiveHint"] = false,
        ["idempotentHint"] = true,
        ["openWorldHint"] = false
    };
}
