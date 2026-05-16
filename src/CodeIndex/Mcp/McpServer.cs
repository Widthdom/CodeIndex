using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server speaking JSON-RPC 2.0 over a pluggable transport. The
/// default <see cref="StdioMcpTransport"/> preserves the historic stdin/stdout wire path, and
/// <see cref="HttpMcpTransport"/> exposes the same JSON-RPC catalog over POST so AI clients can
/// share a warm server across sessions (issue #1558).
/// プラガブルな <see cref="IMcpTransport"/> 上で JSON-RPC 2.0 を話す MCP サーバー。既定の
/// <see cref="StdioMcpTransport"/> は従来通り stdin/stdout を使い、<see cref="HttpMcpTransport"/>
/// は同じ JSON-RPC カタログを POST で公開して、複数クライアントから暖機済みサーバーを共有できるようにする
/// (issue #1558)。
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
    private readonly IMcpAuthenticator _authenticator;
    private readonly McpToolFilter _toolFilter;
    // Bounds the number of MCP tool calls in flight at once so an unbounded burst of
    // requests cannot exhaust memory or wedge the SQLite reader lock (#1567). The
    // stdio / HTTP loop today only ever has one frame in flight, but the gate
    // documents the contract and is the seam future async dispatch will use.
    // 同時 in-flight ツール呼び出しの上限 (#1567)。stdio / HTTP ループは現状単一スレッド
    // だが、将来の並列ディスパッチに備えて契約を明示し、testable な seam を残す。
    private readonly SemaphoreSlim _concurrencyGate;
    // Server-wide shutdown signal. Cancelled by `notifications/shutdown` (and the
    // `notifications/exit` alias) so the read loop unblocks and exits cleanly even
    // when the transport itself has not closed (#1567).
    // サーバー全体の shutdown シグナル。`notifications/shutdown` (および
    // `notifications/exit`) を受けると cancel され、トランスポート未クローズでも
    // 読み取りループが unblock して正常終了する (#1567)。
    private readonly CancellationTokenSource _shutdownCts = new();
    // Token observed by the currently executing tool call. Set just before
    // `ProcessFrame` runs and reset afterwards so `WithDbReader` can hand a live
    // cancellation token to `DbReader` for SQLite work (#1567).
    // 現在実行中のツール呼び出しが観測するトークン。`ProcessFrame` 実行直前にセットし、
    // 直後にリセットする。`WithDbReader` が `DbReader` にライブな cancellation token
    // を渡せるようにするため (#1567)。
    private CancellationToken _currentRequestToken = CancellationToken.None;
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
    // Caller identity used to key the per-(tool, caller) rate limiter. Captured from the
    // `clientInfo.name` field of the `initialize` request when the client supplies it, so
    // shared / networked MCP deployments can attribute and throttle individual clients
    // instead of treating the whole server as a single bucket (#1560).
    // (tool, caller) ごとのレート制限のキーに使う呼び出し元 ID。`initialize` の
    // `clientInfo.name` から取得し、共有・ネットワーク経由の MCP でクライアント単位の
    // 計量・スロットルが効くようにする（#1560）。
    private string _caller = "unknown";

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
    // Default ceiling on concurrent in-flight tool calls. Matches the issue's suggested
    // default and is generous enough for typical AI clients without letting a burst of
    // tool calls wedge the SQLite reader lock or balloon memory (#1567).
    // 同時 in-flight ツール呼び出し数の既定上限 (#1567)。
    internal const int DefaultMaxConcurrency = 8;

    public McpServer(string dbPath, string version, bool dbPathExplicit = false)
        : this(dbPath, version, dbPathExplicit, null, null, null, DefaultMaxConcurrency)
    {
    }

    public McpServer(string dbPath, string version, bool dbPathExplicit, IMcpAuthenticator authenticator)
        : this(dbPath, version, dbPathExplicit, null, authenticator, null, DefaultMaxConcurrency)
    {
    }

    public McpServer(string dbPath, string version, bool dbPathExplicit, McpToolFilter? toolFilter)
        : this(dbPath, version, dbPathExplicit, null, null, toolFilter, DefaultMaxConcurrency)
    {
    }

    // Legacy internal entry point retained for the existing serializer-injection tests that
    // do not need a custom authenticator or tool filter.
    // serializer 注入だけが必要な既存テスト向けの内部互換 entry。
    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse)
        : this(dbPath, version, dbPathExplicit, serializeResponse, null, null, DefaultMaxConcurrency)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, null, DefaultMaxConcurrency)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, toolFilter, DefaultMaxConcurrency)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter, int maxConcurrency)
    {
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "MCP concurrency cap must be at least 1.");
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
        _authenticator = authenticator ?? LocalStdioAuthenticator.Instance;
        _toolFilter = toolFilter ?? McpToolFilter.FromEnvironment();
        RateLimiter = new RateLimiter(RateLimiterOptions.FromEnvironment());
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// Per-(tool, caller) token bucket throttle for MCP tool calls. Disabled by default so
    /// stdio single-user sessions are unaffected; operators opt in via
    /// `CDIDX_MCP_RATE_LIMIT_RPS` (+ optional `CDIDX_MCP_RATE_LIMIT_BURST`) on the MCP server
    /// process (#1560).
    /// MCP ツール呼び出し向け (tool, caller) 単位のトークンバケットスロットル。既定では無効で
    /// stdio 単一ユーザーには影響しない。`CDIDX_MCP_RATE_LIMIT_RPS`（任意で
    /// `CDIDX_MCP_RATE_LIMIT_BURST`）を MCP サーバープロセスに設定して opt-in する（#1560）。
    /// </summary>
    internal RateLimiter RateLimiter { get; private set; }

    /// <summary>
    /// Replace the rate limiter for tests so they can inject a deterministic clock and
    /// custom options without going through environment variables.
    /// テスト用にレート制限器を差し替える。決定論的なクロックや任意のオプションを環境変数
    /// 経由ではなく直接注入できるようにする。
    /// </summary>
    internal void OverrideRateLimiterForTests(RateLimiter limiter)
    {
        RateLimiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
    }

    /// <summary>
    /// Caller identifier captured from the most recent `initialize` request's
    /// `clientInfo.name` (issue #1560). Exposed for tests so they can verify the limiter is
    /// keyed off the negotiated caller.
    /// 直近の `initialize` の `clientInfo.name` から取得した呼び出し元 ID（#1560）。
    /// テストがレート制限のキーを検証するために公開する。
    /// </summary>
    internal string CurrentCaller => _caller;

    /// <summary>
    /// Cap configured for concurrent in-flight tool calls (#1567). Surfaced for tests so
    /// the bound can be verified without poking at internals.
    /// 現在設定されている in-flight ツール呼び出し上限 (#1567)。テスト向けに公開。
    /// </summary>
    internal int MaxConcurrency { get; }

    /// <summary>
    /// Run the MCP server loop on the default stdio transport. Kept as a thin wrapper around
    /// <see cref="RunAsync(IMcpTransport, CancellationToken)"/> so existing callers stay
    /// source-compatible after the #1558 transport refactor.
    /// 既定の stdio トランスポートで MCP ループを動かす。#1558 のトランスポート抽象化後も
    /// 既存呼び出しがソース互換となるよう <see cref="RunAsync(IMcpTransport, CancellationToken)"/>
    /// のラッパとして残す。
    /// </summary>
    public async Task RunAsync()
    {
        await using var transport = new StdioMcpTransport(StdioBufferSize);
        await RunAsync(transport, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Run the MCP server loop on the supplied transport (issue #1558). The contract is one
    /// read followed by one write — the loop honours notifications (write-null) and ends when
    /// the transport reports end-of-stream.
    /// 指定トランスポート上で MCP ループを動かす (issue #1558)。「読み 1 回 → 書き 1 回」を
    /// 守り、通知は null 書き込みで吸収し、EOS でループを終える。
    /// </summary>
    internal async Task RunAsync(IMcpTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // Link the caller-supplied token (Ctrl+C / HTTP listener stop) with the server-internal
        // shutdown signal so `notifications/shutdown` also wakes any pending `ReadFrameAsync`.
        // The MCP spec leaves shutdown to the transport, but real deployments need a wire-level
        // way to drain in-flight work without killing the process (#1567).
        // Ctrl+C 等の外部 token と内部 shutdown signal をリンクし、`notifications/shutdown` でも
        // pending な `ReadFrameAsync` を unblock できるようにする (#1567)。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var loopToken = linkedCts.Token;

        // Use stderr for logging so stdout stays clean for JSON-RPC
        // stdoutをJSON-RPC用にクリーンに保つため、ログはstderrに出力
        Console.Error.WriteLine($"[cdidx-mcp] Starting MCP server v{_version} (db: {_dbPath}, transport: {transport.Name} @ {transport.Endpoint}, max in-flight: {MaxConcurrency})");

        while (_running)
        {
            // The full read/process/write iteration is wrapped in the same cancellation guard so
            // a Ctrl+C that lands mid-iteration (e.g. while WriteFrameAsync is flushing) still
            // exits the loop cleanly instead of bubbling OperationCanceledException out of the
            // server and past ProgramRunner.RunMcpHttp's graceful-shutdown handler.
            // Ctrl+C が WriteFrameAsync flush 中に来ても OperationCanceledException を呼び元に
            // 漏らさず正常終了するよう、read/process/write 全体を同じ cancellation guard で囲む。
            try
            {
                var frame = await transport.ReadFrameAsync(loopToken).ConfigureAwait(false);
                if (frame == null)
                    break; // transport closed / トランスポートが閉じられた

                // Acquire the concurrency gate before doing any work so a future async dispatch
                // mode (multiple frames in flight) can never run more than `MaxConcurrency` tool
                // calls at once. Today the loop is sequential so the gate is effectively a no-op
                // at runtime, but it documents the contract and gives tests a verifiable bound
                // (#1567).
                // 並列ディスパッチ時に in-flight 数が `MaxConcurrency` を超えないよう、ProcessFrame
                // の手前で gate を取得する (#1567)。
                await _concurrencyGate.WaitAsync(loopToken).ConfigureAwait(false);
                string? response;
                try
                {
                    // Hand the per-request token to `WithDbReader` so SQLite work the tool kicks
                    // off can observe shutdown / client-disconnect cancellation through
                    // `DbReader.Cancellation` (#1567).
                    // ツールが起動する SQLite 作業が shutdown / 切断を観測できるよう per-request
                    // token を `WithDbReader` に渡す (#1567)。
                    _currentRequestToken = loopToken;
                    response = ProcessFrame(frame);
                }
                finally
                {
                    _currentRequestToken = CancellationToken.None;
                    _concurrencyGate.Release();
                }

                await transport.WriteFrameAsync(response, loopToken).ConfigureAwait(false);

                // `notifications/shutdown` flips `_running` inside `HandleMessage`; exit the loop
                // immediately so a subsequent slow `ReadFrameAsync` does not extend the lifetime
                // of a server that has been asked to stop.
                // `notifications/shutdown` が `_running` を倒した直後にループを抜ける (#1567)。
                if (!_running)
                    break;
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
                break;
            }
        }

        Console.Error.WriteLine("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    /// <summary>
    /// Process one MCP JSON-RPC line and write any response to the provided writer. Kept as a
    /// thin wrapper around <see cref="ProcessFrameAsync"/> so existing tests that drive a
    /// <see cref="TextWriter"/> directly stay source-compatible after the #1558 transport refactor.
    /// 1 行分の MCP JSON-RPC を処理して writer に書き込む薄いラッパ。#1558 のトランスポート抽象化後も
    /// 既存テストがソース互換となるよう、<see cref="ProcessFrameAsync"/> をそのまま呼び出す。
    /// </summary>
    internal async Task ProcessLineAsync(string line, TextWriter writer)
    {
        var response = ProcessFrame(line);
        if (response != null)
            await writer.WriteLineAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Process one MCP JSON-RPC frame and return the wire-ready response string (or null when
    /// the request was a notification or otherwise yields no response). This is the
    /// transport-neutral seam used by <see cref="IMcpTransport"/> implementations (issue #1558).
    /// 1 フレーム分の MCP JSON-RPC を処理し、ワイヤー応答文字列を返す（通知などで応答なしの場合は null）。
    /// <see cref="IMcpTransport"/> 実装が共有するトランスポート非依存の合流点 (issue #1558)。
    /// </summary>
    internal string? ProcessFrame(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Reject oversized messages to prevent memory exhaustion
        // メモリ枯渇を防ぐため巨大メッセージを拒否
        if (line.Length > MaxLineLength)
        {
            Console.Error.WriteLine(BuildOversizedMessageLog(line.Length));
            var errorResponse = CreateErrorResponse(null, -32700, "Message too large");
            return errorResponse.ToJsonString(_jsonOptions);
        }

        JsonNode? request = null;
        try
        {
            request = JsonNode.Parse(line);
            if (request == null)
                return null;

            var response = HandleMessage(request);
            return response != null ? _serializeResponse(response) : null;
        }
        catch (JsonException ex)
        {
            // Parse error / パースエラー
            Console.Error.WriteLine(BuildJsonParseErrorLog(ex.Message));
            var errorResponse = CreateErrorResponse(null, -32700, "Parse error");
            return errorResponse.ToJsonString(_jsonOptions);
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
                return errorResponse.ToJsonString(_jsonOptions);
            }
            return null;
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

        // Extract `method` defensively: a non-string `method` (e.g. `"method":42`) must not
        // throw before the auth gate runs, otherwise a token-protected server would surface
        // `-32603 "Internal error"` to an unauthenticated caller instead of `-32001
        // "Unauthorized"`, leaking that the request reached dispatch internals (#1559).
        // `method` は防御的に取り出す。`"method":42` のような非文字列が GetValue<string>()
        // で例外を投げると、認証ゲート前に -32603 が返ってしまい、未認証呼び出し元に dispatch
        // 内部まで届いた事実が漏れる (#1559)。
        var method = TryGetStringMember(obj, "method");
        if (!TryGetRequestId(obj, out var hasId, out var id))
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: id must be string, number, or null");

        // Notifications (no id) don't get a response / 通知（idなし）にはレスポンスなし
        if (method == "notifications/initialized" || method == "notifications/cancelled")
            return null;

        // Graceful shutdown via JSON-RPC notification (#1567). Without this, the only way to
        // stop a long-lived `cdidx mcp` server was to close the transport (stdin EOF / HTTP
        // listener stop), which races with in-flight work and forces clients to send SIGINT.
        // Treating both `notifications/shutdown` (the MCP spec-aligned name) and the legacy
        // LSP-style `notifications/exit` alias as graceful-stop signals lets clients drain the
        // current request and exit cleanly. Cancelling `_shutdownCts` unblocks any pending
        // `ReadFrameAsync` in the loop.
        // JSON-RPC 通知による graceful shutdown (#1567)。`_shutdownCts.Cancel()` でループ側の
        // `ReadFrameAsync` を unblock し、`_running = false` で次のイテレーション開始を抑止する。
        if (string.Equals(method, "notifications/shutdown", StringComparison.Ordinal)
            || string.Equals(method, "notifications/exit", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[cdidx-mcp] Received {method}; draining in-flight work and shutting down.");
            _running = false;
            try
            {
                if (!_shutdownCts.IsCancellationRequested)
                    _shutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Server is already disposing — nothing more to cancel.
                // dispose 中なので追加 cancel は不要。
            }
            return null;
        }

        if (!hasId)
        {
            if (method != null && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(BuildUnknownNotificationLog(method));
            return null;
        }

        // Authenticate every responded request before dispatch so the auth contract is
        // uniform across `initialize`, `tools/list`, `tools/call`, and `ping`. Run auth even
        // when `method` is missing or malformed so a token-protected server cannot be probed
        // for method-shape errors without credentials (#1559). Notifications already
        // short-circuited above because they produce no response and cannot leak an error code.
        // すべての応答対象リクエストを dispatch 前に認証する。`method` が欠落・不正でも
        // 認証は走らせ、トークン保護下のサーバーで未認証呼び出し元に method 形式エラーを
        // 漏らさない (#1559)。通知は応答が無いため上のブランチで先に return している。
        var authResult = _authenticator.Authenticate(request);
        if (!authResult.IsAuthenticated)
        {
            Console.Error.WriteLine(BuildAuthFailureLog(method, authResult.FailureReason));
            return CreateErrorResponse(hasId: true, id: id, code: -32001, message: "Unauthorized");
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

    // Safe accessor that returns null instead of throwing when `name` is missing OR present
    // with a non-string value. JsonNode's `GetValue<string>()` throws InvalidOperationException
    // on non-string scalars, which would bubble out of HandleMessage and turn into -32603
    // before the auth gate runs.
    // `name` が無いケースと文字列以外で存在するケースのどちらでも null を返す安全アクセサ。
    // JsonNode の `GetValue<string>()` は非文字列で例外を投げ、認証ゲート前に -32603 化して
    // しまう。
    private static string? TryGetStringMember(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    // Cap on the logged `method` label. Long enough for every spec method (`notifications/cancelled`
    // is 23 chars) and any plausible client extension, short enough to keep one log line readable.
    // ログ出力する `method` の長さ上限。仕様メソッド全てと拡張も収まる長さで、1 行を読みやすく保つ。
    private const int LoggedMethodMaxLength = 64;

    // Strip caller-controlled control characters from `method` and clamp its length before
    // interpolating into a stderr log line. Prevents log forging: a malicious client could
    // otherwise send `"method":"evil\n[forged]"` and split the diagnostic across two lines
    // (#1559).
    // stderr 行に method を埋め込む前に制御文字を除去し、長さを切る。これをしないと
    // `"method":"evil\n[forged]"` で診断ログを 2 行に分割するログ偽造ができてしまう (#1559)。
    internal static string SanitizeMethodForLog(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return "(none)";
        var sb = new StringBuilder(Math.Min(method.Length, LoggedMethodMaxLength));
        var truncated = false;
        foreach (var ch in method)
        {
            if (sb.Length >= LoggedMethodMaxLength)
            {
                truncated = true;
                break;
            }
            if (ch < 0x20 || ch == 0x7F)
                sb.Append('?');
            else
                sb.Append(ch);
        }
        if (truncated)
            sb.Append('…');
        return sb.ToString();
    }

    // Stderr log for an auth failure. Mirrors the #1530 sanitization pattern: keep the
    // wire response generic and put the detail on stderr for local diagnostics. The method
    // label is run through SanitizeMethodForLog because it is caller-controlled and reaches
    // stderr before any allow-list check (#1559).
    // 認証失敗の stderr ログ。#1530 のサニタイズ方針に倣い、ワイヤ応答は一般化したまま
    // 詳細だけを stderr に残す。method は認証前に通るため SanitizeMethodForLog で
    // 制御文字除去と長さ切詰めを行う (#1559)。
    internal static string BuildAuthFailureLog(string? method, string? reason) =>
        $"[cdidx-mcp] Auth failed for method {SanitizeMethodForLog(method)}: {reason ?? "(unspecified)"}. Set CDIDX_MCP_AUTH_TOKEN on the server and include a matching params.auth.token on each request.";

    /// <summary>
    /// Handle the initialize handshake.
    /// initializeハンドシェイクを処理。
    /// </summary>
    private JsonNode HandleInitialize(JsonNode? id, JsonNode? _params)
    {
        // Caller stickiness: allow upgrading from the default "unknown" bucket to a named
        // identity, but reject re-initialize attempts that swap one named identity for
        // another. Otherwise a single networked session could reset its rate-limit bucket
        // mid-flight by re-initializing under a fresh name (issue #1560 evidence — DoS
        // surface for networked MCP deployments).
        // caller の sticky 制御: 既定の "unknown" バケットからは名前付き ID への昇格を許すが、
        // 名前付き ID 同士のスワップは拒否する。これを許すと 1 セッション内で再 initialize により
        // 新しい名前でレート制限バケットをリセットできてしまい、#1560 が指摘する DoS 経路になる。
        var resolved = ResolveCallerIdentity(_params);
        if (_caller == "unknown")
        {
            _caller = resolved;
        }
        else if (resolved != _caller && resolved != "unknown")
        {
            Console.Error.WriteLine(BuildCallerSwapRejectionLog(_caller, resolved));
        }
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

        string? Read(string key)
        {
            if (clientInfo.TryGetPropertyValue(key, out var node)
                && node is JsonValue value
                && value.TryGetValue<string>(out var s)
                && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
            return null;
        }

        var name = Read("name");
        if (name == null)
            return "unknown";
        var version = Read("version");
        return version == null ? name : $"{name}/{version}";
    }

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
        var data = new JsonObject
        {
            ["error_category"] = "rate_limited",
            ["tool"] = tool,
            ["caller"] = caller,
            ["retry_after_ms"] = retryAfterMs,
        };
        var error = new JsonObject
        {
            ["code"] = -32000,
            ["message"] = $"Rate limit exceeded for tool '{tool}' (retry after {retryAfterMs} ms).",
            ["data"] = data,
        };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = error,
            ["id"] = id is null ? JsonNode.Parse("null") : JsonNode.Parse(id.ToJsonString())
        };
        return response;
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
            var decision = RateLimiter.TryAcquire(toolName, _caller);
            if (!decision.Allowed)
            {
                metricsError = "rate_limited";
                Console.Error.WriteLine(BuildRateLimitedLog(toolName, _caller, decision.RetryAfterMs));
                return CreateRateLimitedErrorResponse(id, toolName, _caller, decision.RetryAfterMs);
            }

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

    // Stderr log emitted when the rate limiter denies a tool call. Mirrors the JSON-RPC
    // `-32000` payload (tool + caller + retry_after_ms) so operators tailing the MCP log
    // can correlate spikes with the structured error returned on the wire (#1560).
    // レート制限で拒否されたツール呼び出しを stderr に記録する。配線上の JSON-RPC `-32000`
    // ペイロードと内容を揃え、運用側がログ追跡から状況把握できるようにする（#1560）。
    internal static string BuildRateLimitedLog(string toolName, string caller, long retryAfterMs) =>
        $"[cdidx-mcp] Rate limit exceeded: tool='{toolName}', caller='{caller}', retry_after_ms={retryAfterMs}. Increase {RateLimiterOptions.RpsEnvVar} / {RateLimiterOptions.BurstEnvVar} on the server, or back off and retry.";

    internal static string BuildCallerSwapRejectionLog(string current, string attempted) =>
        $"[cdidx-mcp] Ignoring re-initialize with new clientInfo identity '{attempted}': retaining original caller '{current}' so rate-limit buckets cannot be reset mid-session.";

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
        // Hand the per-request cancellation token to the reader so SQLite work the tool
        // kicks off can observe shutdown / client-disconnect cancellation (#1567). The
        // token is `CancellationToken.None` outside an in-flight request, preserving the
        // existing behaviour for ad-hoc callers like tests that drive `WithDbReader`
        // through internals.
        // per-request cancellation token を reader に渡し、SQLite 作業が shutdown / 切断を
        // 観測できるようにする (#1567)。
        var requestToken = _currentRequestToken;
        requestToken.ThrowIfCancellationRequested();
        var reader = new DbReader(db.Connection, db.IsReadOnly, requestToken);
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
        try
        {
            if (!_shutdownCts.IsCancellationRequested)
                _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed / 既に dispose 済み
        }
        _shutdownCts.Dispose();
        _concurrencyGate.Dispose();
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
