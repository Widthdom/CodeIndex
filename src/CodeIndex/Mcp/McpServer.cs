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
/// Supported protocol versions: see <see cref="SupportedProtocolVersions"/> (negotiated per
/// `initialize` request, #1554).
/// 対応プロトコルバージョン: <see cref="SupportedProtocolVersions"/> 参照（`initialize` ごとに交渉, #1554）。
/// </summary>
public partial class McpServer : IDisposable
{
    private readonly string _dbPath;
    private readonly bool _dbPathExplicit;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<JsonNode, string> _serializeResponse;
    private readonly McpToolFilter _toolFilter;
    private bool _running = true;
    // Per-session DbContext reused across MCP tool calls. Holding the connection open
    // avoids reopening SQLite, reapplying pragmas, and re-registering every SQL function
    // on each invocation (issue #1494).
    // セッション内で MCP ツール呼び出しごとに再利用する DbContext。接続再開・PRAGMA 再適用・
    // SQL 関数再登録のコストを毎回払わないために保持する（#1494）。
    private DbContext? _sharedDb;
    // TryMigrateForRead is a read-path concern (legacy / read-only sandbox DBs). It is
    // idempotent but does run PRAGMA table_info + CREATE INDEX IF NOT EXISTS round trips,
    // so we run it once per session. Write tools (`index`, `backfill_fold`) cover the same
    // surface via InitializeSchema, which also flips this flag through MarkSharedDbMigrated.
    // TryMigrateForRead は read path 向けの遅延移行で、レガシー DB / read-only サンドボックス
    // でのみ意味を持つ。冪等だが PRAGMA table_info などの往復が発生するため、セッションで一度だけ
    // 実行する。書き込みツールは InitializeSchema で同等以上の DDL を流すため、そこでフラグを立てる。
    private bool _sharedDbReadMigrated;
    private bool _disposed;

    // Preferred MCP protocol version returned when the client does not pin one. This is the
    // newest entry in `SupportedProtocolVersions` and must stay in lockstep with that array.
    // 既定の MCP プロトコルバージョン。クライアントが指定しなかった場合に返す値で、
    // `SupportedProtocolVersions` の先頭（最新）と一致させる。
    private const string ProtocolVersion = "2025-03-26";
    // MCP protocol versions this server can speak, newest first. Issue #1554: the
    // `initialize` response used to advertise a single hardcoded version and ignored the
    // client's requested `protocolVersion`, so any spec bump silently desynced clients and
    // servers. Negotiation walks this set so older clients on `2024-11-05` keep working and
    // unknown future versions surface as a structured `-32602` instead of a misleading echo.
    // このサーバーが話せる MCP プロトコルバージョン（新しい順）。Issue #1554: 旧実装は
    // ハードコードした 1 つのバージョンだけを返し、クライアントが要求した `protocolVersion`
    // を無視していたため、仕様改訂のたびに無言で互換が崩れていた。`2024-11-05` の旧クライアント
    // を引き続きサポートしつつ、未知バージョンは構造化された `-32602` で明示的に拒否する。
    internal static readonly string[] SupportedProtocolVersions = { "2025-03-26", "2024-11-05" };
    private const int MaxLimit = 200;
    private const int MaxQueryLength = 1000;
    // Upper bound on the `impact_analysis` `maxDepth` argument. Deep monorepos can have
    // legitimate caller chains exceeding 10 hops (e.g. DI container → factory → service →
    // handler → business logic), so the previous cap of 10 silently downgraded such requests.
    // The result-set `limit` (`MaxLimit`) and BFS visited-set still bound traversal cost.
    // `impact_analysis` の `maxDepth` 引数の上限。深いモノレポでは 10 hops 超の正当な caller
    // チェーン (DI container → factory → service → handler → business logic) があり、旧上限
    // 10 では黙ってダウングレードしていた。結果件数 `limit` (`MaxLimit`) と BFS の visited-set
    // が探索コストを抑える役割を担う。
    private const int MaxImpactDepth = 50;
    // Per-call cap on the `before` / `after` context-line parameters accepted by `excerpt`.
    // Without an upper bound, `int.MaxValue` previously drove `startLine - before` into underflow
    // and `endLine + after` into overflow before `Math.Max/Min` clamped, so the slice path saw
    // nonsensical ranges. Mirrors the CLI `--before` / `--after` cap (#1528).
    // `excerpt` が受け取る `before` / `after` の上限。上限が無いと `int.MaxValue` で
    // `startLine - before` が underflow、`endLine + after` が overflow し、`Math.Max/Min` で clamp
    // する前に slice 経路が破綻していたため、CLI の `--before` / `--after` 上限と揃える（#1528）。
    private const int MaxContextLines = 1000;
    private const int MaxLineLength = 1_000_000; // 1 MB per JSON-RPC message / 1メッセージあたり最大1MB
    // Stdio buffer for the JSON-RPC loop. Sized to fit typical large MCP payloads (e.g. batch_query)
    // in a single read so the StreamReader does not grow from its 1 KB default toward MaxLineLength.
    // JSON-RPCループのstdioバッファ。大きめのMCPペイロードを1回の読み取りで吸収し、
    // StreamReaderのデフォルト1KBから繰り返し拡張されるのを避けるサイズ。
    private const int StdioBufferSize = 64 * 1024;

    public McpServer(string dbPath, string version, bool dbPathExplicit = false)
        : this(dbPath, version, dbPathExplicit, null, null)
    {
    }

    public McpServer(string dbPath, string version, bool dbPathExplicit, McpToolFilter? toolFilter)
        : this(dbPath, version, dbPathExplicit, null, toolFilter)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse)
        : this(dbPath, version, dbPathExplicit, serializeResponse, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, McpToolFilter? toolFilter)
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
        _toolFilter = toolFilter ?? McpToolFilter.FromEnvironment();
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
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                break; // stdin closed / stdinが閉じられた

            await ProcessLineAsync(line, writer).ConfigureAwait(false);
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
            await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions)).ConfigureAwait(false);
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
                await writer.WriteLineAsync(_serializeResponse(response)).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            // Parse error / パースエラー
            Console.Error.WriteLine(BuildJsonParseErrorLog(ex.Message));
            var errorResponse = CreateErrorResponse(null, -32700, "Parse error");
            await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Stderr keeps the full message for local diagnostics, but the
            // wire response only carries the exception type so SQLite-style
            // "near 'foo': syntax error" detail or other content-bearing
            // strings cannot leak to the JSON-RPC client (#1530).
            // stderr には診断用に詳細を残すが、ネットワークに出るレスポンスには
            // 例外型のみを返し、SQLite の "near 'foo': syntax error" などを通じた
            // 内容漏れを防ぐ（#1530）。
            Console.Error.WriteLine(BuildUnhandledLoopErrorLog(ex.Message));
            if (request is JsonObject requestObj && requestObj.TryGetPropertyValue("id", out var requestId))
            {
                var errorResponse = CreateErrorResponse(true, requestId, -32603, BuildSanitizedLoopErrorMessage(ex));
                await writer.WriteLineAsync(errorResponse.ToJsonString(_jsonOptions)).ConfigureAwait(false);
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
        var negotiated = NegotiateProtocolVersion(_params, out var requestedVersion);
        if (negotiated == null)
        {
            // No overlap between the client's requested version and this server's supported
            // set. Issue #1554: respond with structured `-32602` (invalid params) carrying the
            // requested + supported versions in `error.data` so clients can branch on it
            // instead of guessing why the handshake silently failed.
            // クライアント要求バージョンとサーバー対応集合に重なりがない場合。Issue #1554:
            // クライアントが分岐判定できるよう、`error.data` に要求バージョンと対応バージョン
            // を入れた -32602 (invalid params) を返す。
            Console.Error.WriteLine(BuildUnsupportedProtocolLog(requestedVersion));
            return CreateUnsupportedProtocolError(id, requestedVersion);
        }

        var result = new JsonObject
        {
            ["protocolVersion"] = negotiated,
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

    /// <summary>
    /// Resolve the protocol version to advertise back to the client. Returns the version
    /// string on success and `null` when the client pinned an unsupported version.
    /// Issue #1554: the previous handshake hardcoded a single version, so a future MCP
    /// spec bump would silently break clients. The negotiation now mirrors the MCP spec:
    /// echo the client's requested version when it is in our supported set, fall back to
    /// the preferred version when no version was supplied, and surface a structured error
    /// when there is no overlap (no silent downgrade so clients cannot mistakenly proceed
    /// against an unsupported wire format).
    /// クライアントに返すプロトコルバージョンを決める。成功時はバージョン文字列、
    /// クライアントが対応外バージョンを指定した場合は `null` を返す。Issue #1554:
    /// 旧実装はハードコードした 1 つのバージョンだけを返していたため、将来の仕様改訂で
    /// 無言で互換が壊れる。本ロジックは MCP 仕様準拠で、要求バージョンが対応集合にあれば
    /// それをそのまま返し、未指定なら既定バージョンを返し、重なりが無い場合は構造化エラー
    /// を返す（黙ってダウングレードしないことでクライアントが誤った wire format で進むのを防ぐ）。
    /// </summary>
    internal static string? NegotiateProtocolVersion(JsonNode? initializeParams, out string? requestedVersion)
    {
        requestedVersion = null;
        if (initializeParams is JsonObject obj
            && obj.TryGetPropertyValue("protocolVersion", out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var versionString)
            && !string.IsNullOrWhiteSpace(versionString))
        {
            requestedVersion = versionString;
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

    private static JsonObject CreateUnsupportedProtocolError(JsonNode? id, string? requestedVersion)
    {
        var supportedArray = new JsonArray();
        foreach (var supported in SupportedProtocolVersions)
            supportedArray.Add(JsonValue.Create(supported));

        var data = new JsonObject
        {
            ["supportedVersions"] = supportedArray
        };
        if (requestedVersion != null)
            data["requestedVersion"] = requestedVersion;

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
            ["id"] = id is null ? JsonNode.Parse("null") : JsonNode.Parse(id.ToJsonString())
        };
        return response;
    }

    internal static string BuildUnsupportedProtocolMessage(string? requestedVersion)
    {
        var supported = string.Join(", ", SupportedProtocolVersions);
        var requested = string.IsNullOrEmpty(requestedVersion) ? "(unspecified)" : requestedVersion;
        return $"Unsupported MCP protocolVersion '{requested}'. Server supports: {supported}.";
    }

    internal static string BuildUnsupportedProtocolLog(string? requestedVersion)
    {
        var supported = string.Join(", ", SupportedProtocolVersions);
        var requested = string.IsNullOrEmpty(requestedVersion) ? "(unspecified)" : requestedVersion;
        return $"[cdidx-mcp] Rejecting initialize: client requested protocolVersion '{requested}', server supports {supported}. Upgrade the server or pin a supported version on the client.";
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

        // Per-deployment enablement gate (#1561). Disabled known tools return `-32601 method
        // not found` so clients can branch on a structured JSON-RPC code; truly unknown names
        // still fall through to the existing `-32602 Unknown tool` path so typos remain
        // distinguishable from operator-disabled tools.
        // デプロイ単位の有効化ゲート (#1561)。既知ツールが無効化されている場合は `-32601`
        // を返し、クライアントが構造化 code で判定できるようにする。サーバーに無い名前は
        // 既存の `-32602 Unknown tool` 経路に流し、オペレータによる無効化と typo を区別する。
        if (McpToolFilter.IsKnownTool(toolName) && !_toolFilter.IsEnabled(toolName))
            return CreateErrorResponse(hasId: true, id: id, code: -32601, message: $"Tool not enabled: {toolName}");

        Database.DbDebug.ResetContext();
        var metricsStartedAt = DateTimeOffset.UtcNow;
        var metricsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? metricsError = null;
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
            // Stderr captures the full ex.Message for local debugging, but the
            // JSON-RPC tool result is sanitized down to the tool name +
            // exception type. ex.Message can otherwise echo bound parameter
            // values (e.g. SQLite errors quote the offending literal) or path
            // / content fragments, which would leak to the client through the
            // MCP transcript (#1530).
            // stderr には ex.Message をそのまま残してローカルデバッグを支えるが、
            // JSON-RPC のツール結果は tool 名 + 例外型のみに絞る。SQLite 例外などは
            // バインド値や該当リテラルを含むため、生のメッセージをクライアントに渡すと
            // パスや索引内容が漏れる（#1530）。
            Console.Error.WriteLine(BuildToolErrorLog(toolName, ex.Message));
            Database.DbDebug.DumpToStderr(ex);
            metricsError = ex.GetType().Name;
            return CreateToolErrorResponse(true, id, BuildSanitizedToolErrorMessage(toolName, ex));
        }
        finally
        {
            Database.DbDebug.ResetContext();
            if (MetricsSink.IsActive)
            {
                metricsStopwatch.Stop();
                MetricsSink.Record(new MetricsEvent(
                    Timestamp: metricsStartedAt,
                    Tool: toolName,
                    Source: "mcp",
                    ElapsedMs: metricsStopwatch.Elapsed.TotalMilliseconds,
                    ExitCode: metricsError == null ? 0 : 1,
                    Language: TryReadStringArg(args, "language") ?? TryReadStringArg(args, "lang"),
                    Error: metricsError));
            }
        }
    }

    private static string? TryReadStringArg(JsonNode? args, string key)
    {
        if (args is null)
            return null;

        try
        {
            var node = args[key];
            if (node is null)
                return null;
            if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
                return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
        }
        catch
        {
            // Best-effort: any oddity in argument shape just suppresses the language hint.
            // ベストエフォート: 引数形状が不正でも language ヒントを抑止するだけ。
        }
        return null;
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

    // Wire-safe error body for the tool catch-all. Mentions the tool and the
    // exception type so the client can branch (retry vs. surface to user)
    // while keeping bound values or matched content out of the response (#1530).
    // ツール catch-all のワイヤー向け本文。クライアントが分岐できるよう tool 名と
    // 例外型は残し、バインド値や一致内容は含めない（#1530）。
    internal static string BuildSanitizedToolErrorMessage(string toolName, Exception ex) =>
        $"Error executing {toolName} ({ex.GetType().Name}). See cdidx server stderr for details.";

    // Wire-safe error body for the JSON-RPC loop catch-all. Same rationale as
    // the tool catch-all (#1530).
    // JSON-RPC ループ catch-all のワイヤー向け本文。理由はツール catch-all と同じ（#1530）。
    internal static string BuildSanitizedLoopErrorMessage(Exception ex) =>
        $"Internal error ({ex.GetType().Name}). See cdidx server stderr for details.";

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
        {
            // Drop any stale cached context so the next tool call can re-open after the user
            // creates the DB (e.g. via an external `cdidx index`). Without this, a missed
            // file lookup would leave a closed/disposed handle blocking later open attempts.
            // ユーザーが後から DB を作った場合に再オープンできるよう、キャッシュをここで破棄。
            CloseSharedDb();
            return CreateToolErrorResponse(true, id, $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first.");
        }

        var db = GetOrOpenSharedDb();
        if (!_sharedDbReadMigrated)
        {
            db.TryMigrateForRead();
            _sharedDbReadMigrated = true;
        }
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return action(reader);
    }

    /// <summary>
    /// Open the per-session DbContext on first use and reuse it on every subsequent call.
    /// Centralising the open lets us pay the connection setup, pragma application, and SQL
    /// function registration once per MCP session instead of once per tool invocation
    /// (#1494). The MCP loop is single-threaded, so no locking is required.
    /// MCP セッション初回呼び出し時に DbContext を開き、以後は再利用する。接続セットアップや
    /// PRAGMA・SQL 関数登録のコストを毎ツール呼び出しごとに払わないようにする（#1494）。
    /// MCP ループは単一スレッドのためロック不要。
    /// </summary>
    internal DbContext GetOrOpenSharedDb()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sharedDb != null)
            return _sharedDb;

        _sharedDb = new DbContext(_dbPath);
        return _sharedDb;
    }

    /// <summary>
    /// Mark the shared DbContext as already covered by `TryMigrateForRead`. Write tools that
    /// run `InitializeSchema` reuse the same connection, so the read path can skip the
    /// migration round trip on later calls.
    /// 書き込みツールが InitializeSchema を流した後の共有 DbContext に対し、read path の
    /// TryMigrateForRead を省略するためのマーカ。
    /// </summary>
    internal void MarkSharedDbMigrated() => _sharedDbReadMigrated = true;

    private void CloseSharedDb()
    {
        _sharedDb?.Dispose();
        _sharedDb = null;
        _sharedDbReadMigrated = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseSharedDb();
        GC.SuppressFinalize(this);
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
