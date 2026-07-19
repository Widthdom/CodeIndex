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
    private static int s_nextClientRequestId;
    private static readonly object s_serverLifecycleGate = new();
    private static int s_activeServerCount;
    private readonly string _dbPath;
    private readonly bool _dbPathExplicit;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<JsonNode, string> _serializeResponse;
    private readonly bool _usesDefaultResponseSerializer;
    private readonly IMcpAuthenticator _authenticator;
    private readonly McpToolFilter _toolFilter;
    private readonly TimeProvider _timeProvider;
    // Bounds the number of MCP operations actually executing at once. A separate frame-admission
    // bound prevents work waiting for this gate from growing without limit (#1567, #4536).
    // 実際に実行中の MCP operation 数を制限する。別の frame admission 上限により、この gate を
    // 待つ work が無制限に増えないようにする (#1567, #4536)。
    private readonly SemaphoreSlim _concurrencyGate;
    private int _maxAcceptedConcurrentFrames;
    private int _acceptedConcurrentFrameCount;
    // Server-wide shutdown signal. Cancelled by `notifications/shutdown` (and the
    // `notifications/exit` alias) so the read loop unblocks and exits cleanly even
    // when the transport itself has not closed (#1567).
    // サーバー全体の shutdown シグナル。`notifications/shutdown` (および
    // `notifications/exit`) を受けると cancel され、トランスポート未クローズでも
    // 読み取りループが unblock して正常終了する (#1567)。
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _shutdownCancellationGate = new();
    private Task? _shutdownCancellationTask;
    private int _shutdownCtsDisposed;
    // Active JSON-RPC requests keyed by their serialized `id`, so MCP `$/cancelRequest`
    // notifications can cancel the exact in-flight tool instead of only shutting down the
    // whole server (#1418).
    // JSON-RPC request id ごとの実行中 CTS。MCP `$/cancelRequest` 通知でサーバー全体ではなく
    // 対象ツール呼び出しだけを cancel するため (#1418)。
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);
    // Batch workers can leave valid items queued behind the execution gate. Keep their
    // cancellation sources in a separate durable registry until dispatch moves them into
    // `_activeRequests`, so cancellation never depends on the short scheduler-race tombstone
    // below (#4545).
    // batch worker 待ちの valid item は execution gate の後ろに滞留し得る。dispatch が
    // `_activeRequests` へ移すまで専用 registry で cancellation source を保持し、短命な
    // scheduler-race tombstone に依存せず cancel できるようにする (#4545)。
    private readonly ConcurrentDictionary<string, QueuedBatchRequestRegistration> _queuedBatchRequests = new(StringComparer.Ordinal);
    // A stdio cancellation frame can win the scheduler race against the request task that
    // read the preceding request frame. Keep a tiny, short-lived tombstone so registration
    // consumes that cancellation instead of silently dropping it (#1418).
    // stdio の cancellation frame が、直前の request frame を読んだ task の登録より先に
    // 処理されることがある。短命の tombstone を置き、登録時に cancel を消費する (#1418)。
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingRequestCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pendingClientRequests = new(StringComparer.Ordinal);
    private readonly object _requestTimeoutDiagnosticsGate = new();
    private readonly object _healthStateGate = new();
    // Shared writer operations must not use the session DbContext concurrently. This gate is
    // intentionally not disposed: bounded teardown may leave a late task that still releases it.
    // shared writer は session DbContext を同時使用しない。bounded teardown 後の late task が
    // release し得るため、この gate は意図的に dispose しない。
    private readonly SemaphoreSlim _sharedDbWriteGate = new(1, 1);
    // Token observed by the currently executing tool call. Set just before
    // `ProcessFrame` runs and reset afterwards so `WithDbReader` can hand a live
    // cancellation token to `DbReader` for SQLite work (#1567).
    // 現在実行中のツール呼び出しが観測するトークン。`ProcessFrame` 実行直前にセットし、
    // 直後にリセットする。`WithDbReader` が `DbReader` にライブな cancellation token
    // を渡せるようにするため (#1567)。
    private readonly AsyncLocal<CancellationToken> _currentRequestToken = new();
    private readonly AsyncLocal<IndexAuditContext?> _currentIndexAuditContext = new();
    private readonly AsyncLocal<bool> _isolateDbForCurrentRequest = new();
    private readonly AsyncLocal<DbReader?> _activeSqliteDiagnosticsReader = new();
    // JSON-RPC batches divide the complete array-envelope budget among response-bearing
    // items. resources/list and resources/read observe their current share here so large
    // pages cannot each claim the full response cap and overflow the aggregate frame.
    // JSON-RPC batch は配列 envelope 全体の budget を応答対象 item で分配する。
    // resources/list と resources/read は現在の割当をここから参照し、複数ページが
    // response 上限を重複利用しない。
    private readonly AsyncLocal<int?> _currentBatchResponseItemMaxBytes = new();
    private readonly AsyncLocal<Func<string, CancellationToken, Task>?> _currentOutOfBandFrameWriter = new();
    private readonly AsyncLocal<bool> _canAwaitClientResponses = new();
    private readonly AsyncLocal<DeferredFrameLogBuffer?> _deferredFrameLogs = new();
    // Isolated actions can outlive their JSON-RPC response after deadline or transport
    // cancellation. Collect them per frame so HTTP request-body reservations remain bounded
    // until the underlying work actually exits (#4546).
    // deadline / transport cancellation 後も response より長く残る isolated action を frame ごとに
    // 収集し、実処理終了まで HTTP request-body reservation を維持する (#4546)。
    private readonly AsyncLocal<ConcurrentQueue<Task>?> _currentDetachedIsolatedActions = new();
    // A successful initialize inside a JSON-RPC batch must order later items in that same
    // frame without publishing the new session globally before the exact response serializes.
    // JSON-RPC batch 内で成功した initialize は、対応 response の serialize 前に global 公開せず、
    // 同一 frame の後続 item にだけ新しい session snapshot を見せる。
    private readonly AsyncLocal<FrameInitializeState?> _frameInitializeState = new();
    private static readonly AsyncLocal<RequestCorrelationContext?> CurrentCorrelationContext = new();
    private readonly object _initializeStateGate = new();
    private volatile bool _running = true;
    private volatile bool _enforceInitializationLifecycle;
    // Zero outside a transport loop. HTTP publishes its configured body cap here so handlers
    // can shape a valid response before the transport would otherwise reject it.
    // transport loop 外では 0。HTTP の body 上限を handler に渡し、transport 側の拒否前に
    // 有効な大きさへ response を整形する。
    private int _activeTransportMaxResponseBytes;
    private long _timedOutIsolatedActionDrainingCount;
    private long _timedOutIsolatedActionDrainedCount;
    private RequestTimeoutDrainDiagnostic? _lastRequestTimeoutDrainDiagnostic;
    // Per-session DbContext reused across MCP tool calls. Holding the connection open
    // avoids reopening SQLite, reapplying pragmas, and re-registering every SQL function
    // on each invocation (issue #1494).
    // セッション内で MCP ツール呼び出しごとに再利用する DbContext。接続再開・PRAGMA 再適用・
    // SQL 関数再登録のコストを毎回払わないために保持する（#1494）。
    private DbContext? _sharedDb;
    private bool _disposed;
    // Per-call MCP audit log (#1562). Null when no `--audit-log` path was supplied. Captured
    // from the constructor so the AuditLogSink lifecycle (file handle / rotation) is owned by
    // ProgramRunner, not by every tool dispatch site.
    // ツール呼び出し監査ログ (#1562)。`--audit-log` 未指定時は null。AuditLogSink のライフサイクル
    // (ファイルハンドル / rotation) は ProgramRunner 側で所有する。
    private readonly AuditLogSink? _auditLog;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _inFlightDrainGracePeriod = DefaultEofDrainTimeout;
    private readonly TimeSpan _inFlightPostCancelGracePeriod = DefaultEofPostCancelDrainTimeout;
    private readonly TimeSpan? _keepAliveInterval;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastRequestAt;
    private readonly SemaphoreSlim _textWriterGate = new(1, 1);
    // `initialize.clientInfo` echoed into every audit record so the trail can answer
    // "which client issued this call?" without a second log source. Updated on every
    // `initialize` so a single-session reconnection picks up the new caller identity.
    // `initialize.clientInfo` を audit に転写し、別ログを引かなくても呼び出し元を辿れるよう
    // にする。`initialize` 毎に上書きすることで再接続時に caller identity が追随する。
    // The same snapshot carries the sticky caller used by the per-(tool, caller) limiter.
    // 同じ snapshot に (tool, caller) 単位の limiter が使う sticky caller も保持する。
    // Publish negotiated initialize metadata through one immutable reference. Writers are
    // serialized by `_initializeStateGate`; readers capture this reference once so a draining
    // request cannot combine fields from before and after a successful re-initialize (#4540).
    // initialize で交渉した metadata は単一の immutable reference として公開する。writer は
    // `_initializeStateGate` で直列化し、reader は reference を一度だけ取得することで、drain 中の
    // request が成功した re-initialize の前後の field を混在させない (#4540)。
    private InitializeSessionState _initializeState = InitializeSessionState.Empty;
    private string _mcpLogLevel = "info";
    // Opaque per-server-instance session id copied into suggestion attribution records (#1873).
    // #1873 の提案 attribution 用に保存する、サーバーインスタンス単位の不透明セッションID。
    private readonly string _sessionId = Guid.NewGuid().ToString("D");

    // Preferred MCP protocol version returned when the client does not pin one. This is the
    // newest entry in `SupportedProtocolVersions` and must stay in lockstep with that array.
    // 既定の MCP プロトコルバージョン。クライアントが指定しなかった場合に返す値で、
    // `SupportedProtocolVersions` の先頭（最新）と一致させる。
    private const string ProtocolVersion = "2025-06-18";
    // MCP protocol versions this server can speak, newest first. Issue #1554: the
    // `initialize` response used to advertise a single hardcoded version and ignored the
    // client's requested `protocolVersion`, so any spec bump silently desynced clients and
    // servers. Negotiation walks this set so Codex clients on `2025-06-18` can initialize,
    // older clients keep working, and unknown future versions surface as a structured `-32602`
    // instead of a misleading echo.
    // このサーバーが話せる MCP プロトコルバージョン（新しい順）。Issue #1554: 旧実装は
    // ハードコードした 1 つのバージョンだけを返し、クライアントが要求した `protocolVersion`
    // を無視していたため、仕様改訂のたびに無言で互換が崩れていた。`2025-06-18` を使う Codex と
    // 旧クライアントの両方をサポートしつつ、未知バージョンは構造化された `-32602` で明示的に拒否する。
    internal static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int MaxLimit = 200;
    // Upper bound on the `impact_analysis` `maxHops` argument. Deep monorepos can have
    // legitimate caller chains exceeding 10 hops (e.g. DI container → factory → service →
    // handler → business logic), so the previous cap of 10 silently downgraded such requests.
    // The result-set `limit` (`MaxLimit`) and BFS visited-set still bound traversal cost.
    // `impact_analysis` の `maxHops` 引数の上限。深いモノレポでは 10 hops 超の正当な caller
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
    internal const int MaxLineCharacterCount = 1_000_000;
    internal const int MaxLineByteLength = 1_048_576;
    internal const int DefaultMaxResponseBytes = 10 * 1024 * 1024;
    internal const int MaxConfiguredResponseBytes = 64 * 1024 * 1024;
    internal const int MaxClientResponseJsonBytes = 1 * 1024 * 1024;
    internal const int MaxMcpPaginationOffset = 10_000;
    internal const int MaxResourceListCursorChars = 23;
    internal const int MinResourceListMaxBytes = 4 * 1024;
    internal const int DefaultResourceListMaxBytes = HttpMcpTransport.DefaultMaxResponseBodyBytes;
    internal const int MaxResourceListMaxBytes = HttpMcpTransport.DefaultMaxResponseBodyBytes;
    internal const int ResourceListPageSize = 200;
    private const int ResourceListCursorPayloadBytes = 17;
    private const byte ResourceListCursorVersion = 1;
    // `resources/read` keeps its text budget below the default HTTP response ceiling even
    // when JSON escaping expands every source byte. Cursor pages also cap logical lines so
    // files containing many empty lines cannot turn a small byte budget into unbounded DB work.
    // `resources/read` の本文上限は、全 source byte が JSON escape で膨張しても既定 HTTP
    // response 上限内に収まる値とする。空行が多いファイルで小さい byte budget が無制限の
    // DB 処理に化けないよう、cursor page の論理行数にも上限を設ける。
    internal const int MinResourceReadMaxBytes = 4;
    internal const int DefaultResourceReadMaxBytes = 64 * 1024;
    internal const int MaxResourceReadMaxBytes = 128 * 1024;
    internal const int MaxResourceReadLinesPerPage = 1_000;
    internal const int MaxResourceReadCursorCharacters = 128;
    internal const int DefaultToolsListPageSize = 24;
    internal const int MaxToolsListPageSize = 24;
    internal const int MaxMcpMapDepth = 32;
    internal const double MinKeepAliveIntervalSeconds = 1.0;
    internal const double MaxKeepAliveIntervalSeconds = 300.0;
    private const string MaxResponseBytesEnvVar = "CDIDX_MCP_RESPONSE_MAX_BYTES";
    private const string KeepAliveIntervalEnvironmentVariable = "CDIDX_MCP_KEEP_ALIVE_INTERVAL_S";
    internal const string DebugEnvironmentVariable = "CDIDX_DEBUG";
    private const string SamplingEnabledEnvironmentVariable = "CDIDX_MCP_SAMPLING";
    internal const int MaxJsonDepth = 32;
    internal const int MaxBatchRequestCount = 100;
    internal const int MaxRequestIdCharacterCount = 128;
    internal const int MaxRequestIdByteLength = 256;
    internal const int MaxClientRootCount = 16;
    internal const int MaxClientRootUriChars = 512;
    internal const int MaxClientCapabilitiesJsonBytes = 8 * 1024;
    internal const int MaxClientCapabilitiesDepth = 8;
    // Stdio buffer for the JSON-RPC loop. Sized to fit typical large MCP payloads (e.g. batch_query)
    // in a single read so the StreamReader does not grow from its 1 KB default toward MaxLineCharacterCount.
    // JSON-RPCループのstdioバッファ。大きめのMCPペイロードを1回の読み取りで吸収し、
    // StreamReaderのデフォルト1KBから繰り返し拡張されるのを避けるサイズ。
    private const int StdioBufferSize = 64 * 1024;
    // Default ceiling on concurrent in-flight tool calls. Matches the issue's suggested
    // default and is generous enough for typical AI clients without letting a burst of
    // tool calls wedge the SQLite reader lock or balloon memory (#1567).
    // 同時 in-flight ツール呼び出し数の既定上限 (#1567)。
    internal const int DefaultMaxConcurrency = 8;
    internal const int DefaultMaxConcurrentFrameBacklog = 64;
    private const int MaxPendingRequestCancellationCount = 64;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultEofDrainTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultEofPostCancelDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingRequestCancellationTtl = TimeSpan.FromSeconds(5);

    public McpServer(string dbPath, string version, bool dbPathExplicit = false)
        : this(dbPath, version, dbPathExplicit, null, null, null, null, DefaultMaxConcurrency, null)
    {
    }

    public McpServer(string dbPath, string version, bool dbPathExplicit, IMcpAuthenticator authenticator)
        : this(dbPath, version, dbPathExplicit, null, authenticator, null, null, DefaultMaxConcurrency, null)
    {
    }

    public McpServer(string dbPath, string version, bool dbPathExplicit, McpToolFilter? toolFilter)
        : this(dbPath, version, dbPathExplicit, null, null, toolFilter, null, DefaultMaxConcurrency, null)
    {
    }

    // Legacy internal entry point retained for the existing serializer-injection tests that
    // do not need a custom authenticator or tool filter.
    // serializer 注入だけが必要な既存テスト向けの内部互換 entry。
    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse)
        : this(dbPath, version, dbPathExplicit, serializeResponse, null, null, null, DefaultMaxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, AuditLogSink? auditLog)
        : this(dbPath, version, dbPathExplicit, null, null, null, auditLog, DefaultMaxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, null, null, DefaultMaxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, toolFilter, null, DefaultMaxConcurrency, null)
    {
    }

    // Concurrency-cap injection overload preserved from #1567. Maps to the master constructor
    // with a null AuditLogSink so the maxConcurrency tests do not need to thread an audit log.
    // #1567 由来の maxConcurrency 注入用 overload。auditLog は null 固定で master に流す。
    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter, int maxConcurrency)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, toolFilter, null, maxConcurrency, null)
    {
    }

    // Combined entry point used by ProgramRunner so a single MCP session can carry both an
    // optional authenticator (#1559) and an optional audit log (#1562). Other combinations
    // already have dedicated convenience overloads above.
    // ProgramRunner が authenticator (#1559) と audit log (#1562) を同時に注入できる
    // 経路。それ以外の組み合わせは上の個別 overload で済む。
    internal McpServer(string dbPath, string version, bool dbPathExplicit, IMcpAuthenticator? authenticator, AuditLogSink? auditLog)
        : this(dbPath, version, dbPathExplicit, null, authenticator, null, auditLog, DefaultMaxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter, AuditLogSink? auditLog)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, toolFilter, auditLog, DefaultMaxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter, AuditLogSink? auditLog, int maxConcurrency)
        : this(dbPath, version, dbPathExplicit, serializeResponse, authenticator, toolFilter, auditLog, maxConcurrency, null)
    {
    }

    internal McpServer(string dbPath, string version, bool dbPathExplicit, Func<JsonNode, string>? serializeResponse, IMcpAuthenticator? authenticator, McpToolFilter? toolFilter, AuditLogSink? auditLog, int maxConcurrency, TimeProvider? timeProvider)
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
        _usesDefaultResponseSerializer = serializeResponse is null;
        _serializeResponse = serializeResponse ?? (node => node.ToJsonString(_jsonOptions));
        _authenticator = authenticator ?? LocalStdioAuthenticator.Instance;
        _toolFilter = toolFilter ?? McpToolFilter.FromEnvironment();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetUtcNow();
        _lastRequestAt = _startedAt;
        _auditLog = auditLog;
        RateLimiter = new RateLimiter(RateLimiterOptions.FromEnvironment());
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        MaxConcurrency = maxConcurrency;
        _maxAcceptedConcurrentFrames = maxConcurrency > int.MaxValue - DefaultMaxConcurrentFrameBacklog
            ? int.MaxValue
            : maxConcurrency + DefaultMaxConcurrentFrameBacklog;
        _requestTimeout = DefaultRequestTimeout;
        _keepAliveInterval = ReadKeepAliveIntervalFromEnvironment();
        lock (s_serverLifecycleGate)
            s_activeServerCount++;
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
    private InitializeSessionState PublishedInitializeState => Volatile.Read(ref _initializeState);

    private InitializeSessionState CurrentInitializeState
        => _frameInitializeState.Value?.Current ?? PublishedInitializeState;

    internal string CurrentCaller => CurrentInitializeState.Caller;

    /// <summary>
    /// Opaque session id used for suggestion attribution records (#1873).
    /// 提案 attribution レコードに使う不透明セッションID (#1873)。
    /// </summary>
    internal string CurrentSessionId => _sessionId;

    internal Action<JsonNode?>? RequestRegisteredForTests { get; set; }
    internal Action? CancellationRegistriesMissedForTests { get; set; }
    internal Func<CancellationToken, Task>? RequestDelayForTests { get; set; }
    internal Func<JsonNode?, CancellationToken, Task>? RequestDelayForTestsWithId { get; set; }
    internal Action? ResourceReadMetadataLoadedForTests { get; set; }
    internal Action? McpSessionSnapshotCapturedForTests { get; set; }
    internal bool ShutdownRequestedForTests => _shutdownCts.IsCancellationRequested;
    internal CancellationToken ShutdownTokenForTests => _shutdownCts.Token;

    /// <summary>
    /// Cap configured for concurrent in-flight tool calls (#1567). Surfaced for tests so
    /// the bound can be verified without poking at internals.
    /// 現在設定されている in-flight ツール呼び出し上限 (#1567)。テスト向けに公開。
    /// </summary>
    internal int MaxConcurrency { get; }
    internal int AvailableConcurrencySlotsForTests => _concurrencyGate.CurrentCount;
    internal int AcceptedConcurrentFrameCountForTests => Volatile.Read(ref _acceptedConcurrentFrameCount);
    internal int QueuedBatchRequestCountForTests => _queuedBatchRequests.Count;

    internal int MaxAcceptedConcurrentFrames
    {
        get => _maxAcceptedConcurrentFrames;
        init => _maxAcceptedConcurrentFrames = value < MaxConcurrency
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "MCP accepted-frame capacity cannot be lower than max concurrency.")
            : value;
    }

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    internal TimeSpan RequestTimeout
    {
        get => _requestTimeout;
        init => _requestTimeout = value <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "MCP request timeout must be greater than zero.")
            : value;
    }

    internal TimeSpan InFlightDrainGracePeriod
    {
        get => _inFlightDrainGracePeriod;
        init => _inFlightDrainGracePeriod = value < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "MCP in-flight drain grace period cannot be negative.")
            : value;
    }

    internal TimeSpan InFlightPostCancelGracePeriod
    {
        get => _inFlightPostCancelGracePeriod;
        init => _inFlightPostCancelGracePeriod = value < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "MCP in-flight post-cancel grace period cannot be negative.")
            : value;
    }

    /// <summary>
    /// Run the MCP server loop on the default stdio transport. Kept as a thin wrapper around
    /// <see cref="RunAsync(IMcpTransport, CancellationToken)"/> so existing callers stay
    /// source-compatible after the #1558 transport refactor. SIGINT (Ctrl+C) and SIGTERM are
    /// translated into loop cancellation so orchestrators (systemd, launchd, supervisord) can
    /// achieve a clean shutdown instead of hanging until stdin closes (#1573).
    /// 既定の stdio トランスポートで MCP ループを動かす。#1558 のトランスポート抽象化後も
    /// 既存呼び出しがソース互換となるよう <see cref="RunAsync(IMcpTransport, CancellationToken)"/>
    /// のラッパとして残す。SIGINT (Ctrl+C) と SIGTERM をループキャンセルに変換し、stdin が閉じる
    /// まで固まる旧挙動を解消する（systemd / launchd / supervisord から graceful shutdown 可能に, #1573）。
    /// </summary>
    public async Task RunAsync()
    {
        await using var transport = new StdioMcpTransport(StdioBufferSize);
        using var cts = new CancellationTokenSource();
        using (RegisterShutdownHandlers(cts))
        {
            await RunAsync(transport, cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Register cross-platform SIGINT (Ctrl+C) and SIGTERM handlers that cancel <paramref name="cts"/>
    /// so orchestrator-driven shutdowns drain the loop cleanly instead of leaving the MCP process
    /// hung on stdin or force-killed mid-iteration (#1573). The returned IDisposable removes the
    /// handlers; dispose it before disposing the CTS to avoid races between a late signal and CTS
    /// teardown.
    /// SIGINT (Ctrl+C) と SIGTERM を `cts` のキャンセルに変換するクロスプラットフォームハンドラを登録する
    /// （#1573）。返り値の IDisposable でハンドラを解除する。late signal と CTS 破棄の競合を避けるため、
    /// CTS の Dispose より先にこれを Dispose する。
    /// </summary>
    internal static IDisposable RegisterShutdownHandlers(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            if (cts.IsCancellationRequested)
                return;
            // Honour the signal without letting the .NET runtime terminate the process before
            // the loop has a chance to drain and dispose the shared DbContext.
            // .NET runtime の即時終了を抑え、ループが DbContext を片付ける猶予を確保する。
            e.Cancel = true;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* signal raced disposal — nothing to cancel. */ }
        };
        Console.CancelKeyPress += cancelHandler;

        PosixSignalRegistration? sigtermRegistration = null;
        try
        {
            sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                if (cts.IsCancellationRequested)
                    return;
                ctx.Cancel = true;
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* see CancelKeyPress branch. */ }
            });
        }
        catch (PlatformNotSupportedException)
        {
            // PosixSignal.SIGTERM is supported on net8.0 across Windows/Linux/macOS, but a future
            // niche runtime might not implement it. Console.CancelKeyPress still covers Ctrl+C
            // everywhere, so degrade silently rather than refusing to start.
            // .NET 8 では SIGTERM がクロスプラットフォーム対応だが、将来の特殊ランタイムで未対応の
            // 可能性に備え、Console.CancelKeyPress による Ctrl+C カバレッジを残してサイレントに縮退する。
        }

        return new ShutdownHandlerRegistration(cancelHandler, sigtermRegistration);
    }

    private sealed class ShutdownHandlerRegistration : IDisposable
    {
        private ConsoleCancelEventHandler? _cancelHandler;
        private PosixSignalRegistration? _sigterm;

        public ShutdownHandlerRegistration(ConsoleCancelEventHandler cancelHandler, PosixSignalRegistration? sigterm)
        {
            _cancelHandler = cancelHandler;
            _sigterm = sigterm;
        }

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _cancelHandler, null);
            if (handler != null)
                Console.CancelKeyPress -= handler;
            var sigterm = Interlocked.Exchange(ref _sigterm, null);
            sigterm?.Dispose();
        }
    }

    /// <summary>
    /// Run the MCP server loop on the supplied transport (issue #1558). Base transports use one
    /// read followed by one write; concurrent-capable transports bind a response writer to each
    /// frame. Notifications write null and end-of-stream terminates the loop.
    /// 指定トランスポート上で MCP ループを動かす (issue #1558)。基本 transport は「読み 1 回 →
    /// 書き 1 回」、並行対応 transport は frame ごとに response writer を紐付ける。通知は null を
    /// 書き、EOS でループを終える。
    /// </summary>
    internal async Task RunAsync(IMcpTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _enforceInitializationLifecycle = true;
        Volatile.Write(
            ref _activeTransportMaxResponseBytes,
            transport is IMcpResponseSizeLimitProvider responseLimitProvider
                ? responseLimitProvider.MaxResponseFrameBytes
                : 0);

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
        ConsoleUi.TryWriteErrorLine($"[cdidx-mcp] Starting MCP server v{_version} (db: {FormatDbPathForLog(_dbPath)}, transport: {transport.Name} @ {transport.Endpoint}, max in-flight: {MaxConcurrency})");

        if (transport is HttpMcpTransport httpTransport)
        {
            httpTransport.OutOfBandFrameHandler = (frame, _) => ProcessFrameAsync(frame);
            httpTransport.HealthJsonProvider = () => BuildHealthJson(httpTransport);
            httpTransport.KeepAliveInterval = _keepAliveInterval;
            httpTransport.KeepAliveFrameProvider = BuildKeepAliveNotificationJson;
        }

        try
        {
            if (string.Equals(transport.Name, "stdio", StringComparison.OrdinalIgnoreCase)
                || transport is IConcurrentMcpTransport)
            {
                await RunConcurrentFrameLoopAsync(transport, loopToken, cancellationToken).ConfigureAwait(false);
                return;
            }

            Task? terminalTransportWriteTask = null;
            try
            {
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

                        string? response;
                        try
                        {
                            // Hand the per-request token to `WithDbReader` so SQLite work the tool kicks
                            // off can observe shutdown / client-disconnect cancellation through
                            // `DbReader.Cancellation` (#1567).
                            // ツールが起動する SQLite 作業が shutdown / 切断を観測できるよう per-request
                            // token を `WithDbReader` に渡す (#1567)。
                            _currentRequestToken.Value = loopToken;
                            _currentOutOfBandFrameWriter.Value = transport is IOutOfBandMcpTransport outOfBandTransport
                                ? (frameToWrite, writeToken) => outOfBandTransport.WriteOutOfBandFrameAsync(frameToWrite, writeToken)
                                : null;
                            _canAwaitClientResponses.Value = transport is IOutOfBandMcpTransport
                                && (transport is not HttpMcpTransport httpResponseTransport || httpResponseTransport.HasEventStreams);
                            BeginDeferredFrameLogs();
                            response = await ProcessFrameAsync(frame).ConfigureAwait(false);
                        }
                        finally
                        {
                            _currentRequestToken.Value = CancellationToken.None;
                            _currentOutOfBandFrameWriter.Value = null;
                            _canAwaitClientResponses.Value = false;
                        }

                        // Internal shutdown cancels `loopToken` to stop reads and request actions, but
                        // the initiating notification still owns one transport completion (HTTP 204).
                        // Use only the caller token for that completion; bounded teardown below still
                        // limits a writer that does not finish (#4543).
                        // internal shutdown では read/action 用 loopToken を cancel するが、起点の
                        // notification に対応する transport completion (HTTP 204) は完了させる。
                        // write は caller token のみを使い、停止しない writer は下の bounded teardown
                        // で制限する (#4543)。
                        var responseWriteTask = WriteFrameSafelyAsync(transport, response, cancellationToken);
                        if (!_running)
                        {
                            // Do not await an uncooperative base-transport shutdown completion inline:
                            // the common finally must own its bounded deadline (#4543).
                            // 応答しない base transport の shutdown completion を inline await せず、
                            // common finally の bounded deadline に委ねる (#4543)。
                            terminalTransportWriteTask = responseWriteTask;
                            break;
                        }

                        await responseWriteTask.ConfigureAwait(false);
                        FlushDeferredFrameLogs();

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
                    catch (DecoderFallbackException ex)
                    {
                        BeginDeferredFrameLogs();
                        terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                            writeGate: null,
                            transport,
                            BuildInvalidUtf8ParseErrorResponse(ex),
                            cancellationToken);
                        break;
                    }
                    catch (BoundedLineLengthException ex)
                    {
                        BeginDeferredFrameLogs();
                        terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                            writeGate: null,
                            transport,
                            BuildOversizedLineErrorResponse(ex),
                            cancellationToken);
                        break;
                    }
                }
            }
            finally
            {
                // Base transports have no detached request list, but shutdown cancellation
                // callbacks and malformed-input writes still participate in the same bounded
                // teardown contract as concurrent transports (#4543).
                // base transport に detached request list は無いが、shutdown callback と
                // malformed-input write は concurrent transport と同じ bounded teardown
                // 契約へ必ず流す (#4543)。
                await DrainInFlightTasksAsync(
                    [],
                    InFlightDrainGracePeriod,
                    InFlightPostCancelGracePeriod,
                    cancellationToken,
                    terminalTransportWriteTask).ConfigureAwait(false);
                FlushDeferredFrameLogs();
            }
        }
        finally
        {
            Volatile.Write(ref _activeTransportMaxResponseBytes, 0);
            if (transport is HttpMcpTransport httpTransportToClear)
            {
                httpTransportToClear.OutOfBandFrameHandler = null;
                httpTransportToClear.HealthJsonProvider = null;
                httpTransportToClear.KeepAliveInterval = null;
                httpTransportToClear.KeepAliveFrameProvider = null;
            }
        }

        CommandErrorWriter.WriteStderr("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    private async Task RunConcurrentFrameLoopAsync(
        IMcpTransport transport,
        CancellationToken loopToken,
        CancellationToken externalCancellationToken)
    {
        var writeGate = new SemaphoreSlim(1, 1);
        var admissionGate = new SemaphoreSlim(MaxAcceptedConcurrentFrames, MaxAcceptedConcurrentFrames);
        var tasks = new List<Task>();
        Task protocolBarrier = Task.CompletedTask;
        Task? terminalTransportWriteTask = null;
        var hasRequestScopedWriters = transport is IConcurrentMcpTransport;

        async Task WriteTransportFrameResponseAsync(
            Func<string?, CancellationToken, Task> writeResponseAsync,
            string? response)
        {
            // Concurrent transports provide one writer per request, so serializing those writers
            // behind the base-transport gate lets an unrelated stuck response retain later HTTP
            // request resources. Base transports (notably stdio) still require the shared gate.
            // concurrent transport は request ごとの writer を持つため、base transport 用 gate
            // に直列化すると無関係な stuck response が後続 HTTP resource を保持してしまう。
            // stdio 等の base transport だけ shared gate を維持する (#4546)。
            if (hasRequestScopedWriters)
            {
                await WriteFrameSafelyAsync(
                    writeResponseAsync,
                    response,
                    externalCancellationToken).ConfigureAwait(false);
                FlushDeferredFrameLogs();
                return;
            }

            await writeGate.WaitAsync(externalCancellationToken).ConfigureAwait(false);
            try
            {
                await WriteFrameSafelyAsync(
                    writeResponseAsync,
                    response,
                    externalCancellationToken).ConfigureAwait(false);
                FlushDeferredFrameLogs();
            }
            finally
            {
                writeGate.Release();
            }
        }

        try
        {
            while (_running)
            {
                PruneCompletedRequestTasks(tasks);
                McpTransportFrame? transportFrame;
                try
                {
                    if (transport is IConcurrentMcpTransport concurrentTransport)
                    {
                        transportFrame = await concurrentTransport.ReadConcurrentFrameAsync(loopToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var readFrame = await transport.ReadFrameAsync(loopToken).ConfigureAwait(false);
                        transportFrame = readFrame is null
                            ? null
                            : new McpTransportFrame(readFrame, transport.WriteFrameAsync);
                    }
                }
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DecoderFallbackException ex)
                {
                    BeginDeferredFrameLogs();
                    terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                        writeGate,
                        transport,
                        BuildInvalidUtf8ParseErrorResponse(ex),
                        externalCancellationToken);
                    break;
                }
                catch (BoundedLineLengthException ex)
                {
                    BeginDeferredFrameLogs();
                    terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                        writeGate,
                        transport,
                        BuildOversizedLineErrorResponse(ex),
                        externalCancellationToken);
                    break;
                }
                if (transportFrame is null)
                    break;
                var frame = transportFrame.Frame;
                var writeResponseAsync = transportFrame.WriteResponseAsync;
                var transportRequestToken = transportFrame.RequestCancellationToken;

                if (IsCancellationFrame(frame))
                {
                    try
                    {
                        BeginDeferredFrameLogs();
                        var response = await ProcessFrameAsync(frame).ConfigureAwait(false);
                        await WriteTransportFrameResponseAsync(writeResponseAsync, response).ConfigureAwait(false);
                    }
                    finally
                    {
                        transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
                    }
                    continue;
                }

                if (IsServerResponseFrame(frame))
                {
                    try
                    {
                        BeginDeferredFrameLogs();
                        var response = await ProcessFrameAsync(frame).ConfigureAwait(false);
                        await WriteTransportFrameResponseAsync(writeResponseAsync, response).ConfigureAwait(false);
                    }
                    finally
                    {
                        transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
                    }
                    continue;
                }

                // Admission is deliberately non-blocking: waiting here would prevent a later
                // cancellation/client-response frame from being read while execution is saturated.
                // Excess ordinary work receives a retry-safe JSON-RPC overload response instead of
                // retaining another frame/task/HTTP context without bound (#4536).
                // admission は non-blocking にする。ここで待つと execution 飽和中に後続の
                // cancellation/client-response frame を読めなくなるため。上限超過 work は task や
                // HTTP context を保持し続けず、retry-safe overload response を返す (#4536)。
                if (!admissionGate.Wait(0))
                {
                    try
                    {
                        // Keep every response-bearing id registered until its retry-safe overload
                        // response has reached the transport. A cancellation before or during that
                        // write then belongs to this rejected occurrence instead of poisoning a later
                        // same-id retry (#4536, #4545).
                        // retry-safe overload 応答が transport へ届くまで response-bearing id を登録する。
                        // reject 前または write 中の cancel をこの occurrence に束縛し、同じ id の後続
                        // retry へ持ち越さない (#4536, #4545)。
                        using var capacityRejectedRegistrations = new CapacityRejectedFrameRegistrations(this);
                        BeginDeferredFrameLogs();
                        var response = await ProcessFrameAsync(
                            frame,
                            beforeDispatchAsync: null,
                            rejectForCapacity: true,
                            capacityRejectedRegistrations: capacityRejectedRegistrations).ConfigureAwait(false);
                        await WriteTransportFrameResponseAsync(writeResponseAsync, response).ConfigureAwait(false);
                    }
                    finally
                    {
                        transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
                    }
                    continue;
                }
                Interlocked.Increment(ref _acceptedConcurrentFrameCount);

                var requestTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var isProtocolBarrier = IsProtocolOrderingBarrierFrame(frame);
                var precedingBarrier = protocolBarrier;
                var tasksAcceptedBeforeBarrier = isProtocolBarrier ? tasks.ToArray() : [];
                Func<CancellationToken, Task> awaitPredecessorsAsync = isProtocolBarrier
                    ? token => AwaitProtocolPredecessorsAsync(tasksAcceptedBeforeBarrier, token)
                    : token => AwaitProtocolPredecessorsAsync([precedingBarrier], token);
                var predecessorTask = new Lazy<Task>(
                    () => awaitPredecessorsAsync(loopToken),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                Task BeforeDispatchAsync(CancellationToken token)
                    => predecessorTask.Value.WaitAsync(token);
                // Accepted frames are bounded independently from executing operations. The request
                // registers its id/cancellation state before awaiting protocol predecessors and the
                // execution gate, so a cancellation cannot expire while queued (#4536).
                // accepted frame と executing operation は別々に上限化する。request は protocol
                // predecessor / execution gate を待つ前に id と cancellation state を登録するため、
                // queue 中に cancellation が失効しない (#4536)。
                Task requestTask;
                try
                {
                    requestTask = Task.Run(async () =>
                    {
                        var detachedIsolatedActions = new ConcurrentQueue<Task>();
                        var previousDetachedIsolatedActions = _currentDetachedIsolatedActions.Value;
                        try
                        {
                            requestTaskStarted.TrySetResult();
                            using var frameCts = transportRequestToken.CanBeCanceled
                                ? CancellationTokenSource.CreateLinkedTokenSource(loopToken, transportRequestToken)
                                : null;
                            var frameToken = frameCts?.Token ?? loopToken;
                            string? response = null;
                            try
                            {
                                _currentDetachedIsolatedActions.Value = detachedIsolatedActions;
                                _currentRequestToken.Value = frameToken;
                                _currentOutOfBandFrameWriter.Value = transport is IOutOfBandMcpTransport outOfBandTransport
                                    ? (frameToWrite, writeToken) => outOfBandTransport.WriteOutOfBandFrameAsync(frameToWrite, writeToken)
                                    : string.Equals(transport.Name, "stdio", StringComparison.OrdinalIgnoreCase)
                                        ? async (frameToWrite, writeToken) =>
                                    {
                                        await writeGate.WaitAsync(writeToken).ConfigureAwait(false);
                                        try
                                        {
                                            await transport.WriteFrameAsync(frameToWrite, writeToken).ConfigureAwait(false);
                                        }
                                        finally
                                        {
                                            writeGate.Release();
                                        }
                                    }
                                : null;
                                _canAwaitClientResponses.Value = _currentOutOfBandFrameWriter.Value is not null
                                    && (transport is not HttpMcpTransport httpResponseTransport || httpResponseTransport.HasEventStreams);
                                BeginDeferredFrameLogs();
                                response = await ProcessFrameAsync(
                                    frame,
                                    BeforeDispatchAsync,
                                    rejectForCapacity: false).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (frameToken.IsCancellationRequested)
                            {
                                // Keep the transport's strict one-frame/one-writer contract. HTTP
                                // observes its own terminal reason and aborts/finalizes the response
                                // when the per-request lifetime expires (#4546).
                                // transport の frame/writer 対応を維持する。request lifetime 期限切れ時は
                                // HTTP 側が terminal reason を観測して response を abort/finalize する。
                                response = null;
                            }
                            finally
                            {
                                _currentDetachedIsolatedActions.Value = previousDetachedIsolatedActions;
                                _currentRequestToken.Value = CancellationToken.None;
                                _canAwaitClientResponses.Value = false;
                                _currentOutOfBandFrameWriter.Value = null;
                            }

                            // Malformed/unauthorized frames can return before normal dispatch. Start their
                            // predecessor wait here so such a frame cannot collapse a protocol barrier.
                            // malformed / unauthorized frame が dispatch 前に return しても protocol
                            // barrier を消してしまわないよう、未開始ならここで predecessor を待つ。
                            if (!predecessorTask.IsValueCreated)
                            {
                                try
                                {
                                    await predecessorTask.Value.WaitAsync(frameToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (frameToken.IsCancellationRequested)
                                {
                                    // A canceled frame no longer needs protocol ordering, but its
                                    // request-scoped writer still owns mandatory response cleanup.
                                    // cancel 済み frame は protocol ordering を待たず、対応 writer
                                    // による必須 cleanup だけを完了させる (#4546)。
                                    response = null;
                                }
                            }

                            await WriteTransportFrameResponseAsync(writeResponseAsync, response).ConfigureAwait(false);
                        }
                        finally
                        {
                            var retainedWork = detachedIsolatedActions.IsEmpty
                                ? Task.CompletedTask
                                : ObserveDetachedIsolatedActionsAsync(detachedIsolatedActions.ToArray());
                            transportFrame.CompleteResourceRetentionWhen(retainedWork);
                            Interlocked.Decrement(ref _acceptedConcurrentFrameCount);
                            admissionGate.Release();
                        }
                    }, CancellationToken.None);
                }
                catch
                {
                    transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
                    Interlocked.Decrement(ref _acceptedConcurrentFrameCount);
                    admissionGate.Release();
                    throw;
                }
                tasks.Add(requestTask);
                if (isProtocolBarrier)
                    protocolBarrier = requestTask;
                await requestTaskStarted.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
        {
            // Every loop exit, including cancellation during inline control/overload writes,
            // reaches the bounded drain in finally (#4543).
        }
        finally
        {
            try
            {
                await DrainInFlightTasksAsync(
                    tasks,
                    InFlightDrainGracePeriod,
                    InFlightPostCancelGracePeriod,
                    externalCancellationToken,
                    terminalTransportWriteTask).ConfigureAwait(false);
            }
            finally
            {
                // The bounded EOF drain can intentionally leave late request tasks running. Those
                // tasks can still own the write gate or reach the stdio writer until their finally
                // blocks run. Publish that aggregate even if draining itself exits unexpectedly,
                // then clean up the gates only after every accepted task is done (#3999, #4543).
                // bounded EOF drain は late request task を残すことがある。finally が走るまで gate や
                // stdio writer を使い得るため、drain 自体が異常終了しても aggregate を公開し、全
                // accepted task 完了後に gate を dispose する (#3999, #4543)。
                var transportWork = BuildDrainOperationsTask(tasks, terminalTransportWriteTask);
                if (transport is StdioMcpTransport stdioTransport)
                    stdioTransport.DeferDisposalUntil(transportWork);
                _ = DisposeConcurrentLoopGatesAfterAsync(transportWork, writeGate, admissionGate);
            }
        }
        CommandErrorWriter.WriteStderr("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    private static async Task DisposeConcurrentLoopGatesAfterAsync(
        Task transportWork,
        SemaphoreSlim writeGate,
        SemaphoreSlim admissionGate)
    {
        try
        {
            await transportWork.ConfigureAwait(false);
        }
        catch
        {
            // Request faults are reported by the bounded drain; gate cleanup must still run.
        }
        finally
        {
            writeGate.Dispose();
            admissionGate.Dispose();
        }
    }

    private static async Task ObserveDetachedIsolatedActionsAsync(Task[] actions)
    {
        try
        {
            await Task.WhenAll(actions).ConfigureAwait(false);
        }
        catch
        {
            // Dispatch cleanup observes each action and owns its diagnostics. This aggregate is
            // only a transport resource-lifetime signal and must always settle successfully.
            // 各 action の例外と診断は dispatch cleanup が所有する。この aggregate は transport
            // resource lifetime の signal に限るため、常に正常完了させる。
            foreach (var action in actions)
            {
                if (action.IsFaulted)
                    _ = action.Exception;
            }
        }
    }

    internal static int PruneCompletedRequestTasks(List<Task> tasks)
    {
        var removed = 0;
        for (var i = tasks.Count - 1; i >= 0; i--)
        {
            var task = tasks[i];
            if (!task.IsCompleted)
                continue;

            ObserveCompletedRequestTask(task);
            tasks.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private static void ObserveCompletedRequestTask(Task task)
    {
        if (!task.IsFaulted)
            return;

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] In-flight request ended during transport teardown ({ex.GetType().Name}).");
        }
    }

    private static async Task AwaitProtocolPredecessorsAsync(
        IReadOnlyCollection<Task> predecessors,
        CancellationToken cancellationToken)
    {
        if (predecessors.Count == 0)
            return;

        try
        {
            await Task.WhenAll(predecessors).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A predecessor owns its own wire response and is observed by task pruning. An
            // unrelated fault must not permanently wedge the ordered session lane (#4536).
            // predecessor の fault は個別 response と task pruning で観測する。無関係な fault
            // により ordered session lane を永続停止させない (#4536)。
        }
    }

    private async Task WriteTerminalProtocolErrorAsync(
        SemaphoreSlim? writeGate,
        IMcpTransport transport,
        string response,
        CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        try
        {
            if (writeGate is not null)
            {
                await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }

            await WriteFrameSafelyAsync(transport, response, cancellationToken).ConfigureAwait(false);
            FlushDeferredFrameLogs();
        }
        finally
        {
            if (gateAcquired)
                writeGate!.Release();
        }
    }

    internal async Task DrainInFlightTasksAsync(
        List<Task> tasks,
        TimeSpan gracePeriod,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken = default,
        Task? terminalTransportWriteTask = null)
    {
        PruneCompletedRequestTasks(tasks);
        var shutdownCancellationTask = GetShutdownCancellationTask();
        var drainOperations = BuildDrainOperationsTask(tasks, terminalTransportWriteTask);

        // A shutdown notification may already have started cancellation before EOF reached this
        // method. In that case the post-cancel deadline begins immediately and includes callback
        // completion; running another pre-cancel grace window would extend teardown incorrectly.
        // shutdown notification が EOF より先に cancellation を開始済みなら、callback 完了も
        // post-cancel deadline に含め、pre-cancel grace を重ねない (#4543)。
        if (shutdownCancellationTask is not null)
        {
            await AwaitPostCancellationDrainAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                shutdownCancellationTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }

        if (drainOperations.IsCompleted)
        {
            await ObserveCompletedDrainAndShutdownAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }

        var graceDelay = Task.Delay(gracePeriod, externalCancellationToken);
        var completed = await Task.WhenAny(drainOperations, graceDelay).ConfigureAwait(false);
        if (completed == drainOperations)
        {
            await ObserveCompletedDrainAndShutdownAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }
        if (graceDelay.IsCanceled)
        {
            ObserveLateInFlightTasks(drainOperations);
            return;
        }

        PruneCompletedRequestTasks(tasks);
        if (tasks.Count > 0)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport teardown has {tasks.Count} in-flight request(s); cancelling after {gracePeriod.TotalMilliseconds:0}ms grace period.");
        }
        if (terminalTransportWriteTask is { IsCompleted: false })
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport response/completion write is still pending after {gracePeriod.TotalMilliseconds:0}ms grace period; cancelling transport teardown.");
        }

        shutdownCancellationTask = RequestShutdownCancellation();
        await AwaitPostCancellationDrainAsync(
            tasks,
            drainOperations,
            terminalTransportWriteTask,
            shutdownCancellationTask,
            postCancelGracePeriod,
            externalCancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveCompletedDrainAndShutdownAsync(
        List<Task> tasks,
        Task drainOperations,
        Task? terminalTransportWriteTask,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken)
    {
        await ObserveInFlightTasksAsync(drainOperations).ConfigureAwait(false);

        // A queued shutdown frame can start cancellation while the request drain is completing.
        // Re-read the task after all accepted work has finished; the original snapshot may have
        // been null even though a slow cancellation callback is now running (#4543).
        // queued shutdown frame は request drain 完了直前に cancellation を開始できるため、accepted
        // work 完了後に task を再取得する。初回 snapshot が null でも slow callback が実行中の
        // race を bounded post-cancel deadline へ含める (#4543)。
        var shutdownCancellationTask = GetShutdownCancellationTask();
        if (shutdownCancellationTask is null)
            return;

        await AwaitPostCancellationDrainAsync(
            tasks,
            drainOperations,
            terminalTransportWriteTask,
            shutdownCancellationTask,
            postCancelGracePeriod,
            externalCancellationToken).ConfigureAwait(false);
    }

    private static Task BuildDrainOperationsTask(IReadOnlyCollection<Task> tasks, Task? terminalTransportWriteTask)
    {
        if (terminalTransportWriteTask is null)
            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);

        var operations = new Task[tasks.Count + 1];
        var operationIndex = 0;
        foreach (var task in tasks)
            operations[operationIndex++] = task;
        operations[^1] = terminalTransportWriteTask;
        return Task.WhenAll(operations);
    }

    private async Task AwaitPostCancellationDrainAsync(
        List<Task> tasks,
        Task drainOperations,
        Task? terminalTransportWriteTask,
        Task shutdownCancellationTask,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken)
    {
        // Internal shutdown cancels the linked loop token. Use the original caller token so it
        // cannot collapse this deadline, while Ctrl+C/SIGTERM/transport cancellation can still
        // interrupt it (#3400, #4543).
        // internal shutdown では post-cancel deadline を潰さず、外部 cancellation では中断可能にする。
        var postCancelWork = Task.WhenAll(drainOperations, shutdownCancellationTask);
        var postCancelDelay = Task.Delay(postCancelGracePeriod, externalCancellationToken);
        var completed = await Task.WhenAny(postCancelWork, postCancelDelay).ConfigureAwait(false);
        if (completed == postCancelWork)
        {
            await ObserveInFlightTasksAsync(drainOperations).ConfigureAwait(false);
            _ = shutdownCancellationTask.Exception;
            return;
        }
        if (postCancelDelay.IsCanceled)
        {
            ObserveLateInFlightTasks(postCancelWork);
            return;
        }

        PruneCompletedRequestTasks(tasks);
        if (tasks.Count > 0)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport teardown final deadline expired with {tasks.Count} in-flight request(s) remaining after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }
        if (terminalTransportWriteTask is { IsCompleted: false })
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport response/completion write is still pending after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }
        if (!shutdownCancellationTask.IsCompleted)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Shutdown cancellation callbacks are still running after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }

        // Use an uncancelled observer so late faults are still observed after the bounded,
        // client-visible drain window (#3774, #4543).
        // bounded drain window 後の late fault も未キャンセル observer で観測する。
        ObserveLateInFlightTasks(postCancelWork);
    }

    private Task? GetShutdownCancellationTask()
    {
        lock (_shutdownCancellationGate)
            return _shutdownCancellationTask;
    }

    private Task RequestShutdownCancellation()
    {
        TaskCompletionSource completion;
        lock (_shutdownCancellationGate)
        {
            if (_shutdownCancellationTask is not null)
                return _shutdownCancellationTask;

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCancellationTask = completion.Task;
        }

        _ = CompleteShutdownCancellationAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownCancellationAsync(TaskCompletionSource completion)
    {
        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race; cancellation can no longer be requested.
        }
        catch (Exception ex)
        {
            try
            {
                CommandErrorWriter.WriteStderr(
                    $"[cdidx-mcp] Shutdown cancellation callback failed during transport teardown ({ex.GetType().Name}).");
            }
            catch
            {
                // Diagnostics must never abort bounded teardown.
            }
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private static void ObserveLateInFlightTasks(Task tasks)
        => _ = tasks.ContinueWith(task =>
        {
            _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task ObserveInFlightTasksAsync(Task tasks)
    {
        try
        {
            await tasks.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] In-flight request ended during transport teardown ({ex.GetType().Name}).");
        }
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
        BeginDeferredFrameLogs();
        var response = await ProcessFrameAsync(line).ConfigureAwait(false);
        if (response != null)
        {
            try
            {
                await _textWriterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await WriteJsonLineAsync(writer, response).ConfigureAwait(false);
                    FlushDeferredFrameLogs();
                }
                finally
                {
                    _textWriterGate.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                WriteMcpLogLine(BuildResponseWriteErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
                FlushDeferredFrameLogs();
            }
        }
    }

    private static async Task WriteJsonLineAsync(TextWriter writer, string response)
    {
        await writer.WriteAsync(response).ConfigureAwait(false);
        await writer.WriteAsync('\n').ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteFrameSafelyAsync(IMcpTransport transport, string? response, CancellationToken cancellationToken)
        => await WriteFrameSafelyAsync(transport.WriteFrameAsync, response, cancellationToken).ConfigureAwait(false);

    private static async Task WriteFrameSafelyAsync(
        Func<string?, CancellationToken, Task> writeFrameAsync,
        string? response,
        CancellationToken cancellationToken)
    {
        try
        {
            await writeFrameAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            WriteMcpLogLine(BuildResponseWriteErrorLog("write operation was canceled"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            WriteMcpLogLine(BuildResponseWriteErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
        }
    }

    private static bool IsServerResponseFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _))
            return false;

        return node is JsonObject obj
            && obj.ContainsKey("id")
            && obj["method"] is null
            && (obj.ContainsKey("result") || obj.ContainsKey("error"));
    }

    private string BuildInvalidUtf8ParseErrorResponse(DecoderFallbackException ex)
    {
        DeferFrameLog(BuildInvalidUtf8ErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
        var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Parse error: invalid UTF-8 input",
            category: McpErrorEnvelope.CategoryParseError,
            suggestion: "Send one JSON-RPC 2.0 object per line encoded as valid UTF-8. Reject or re-encode malformed bytes before retrying.",
            retrySafe: false);
        return errorResponse.ToJsonString(_jsonOptions);
    }

    internal static string BuildInvalidUtf8ErrorLog(string detail)
        => $"[cdidx-mcp] JSON parse error: invalid UTF-8 input ({detail}). Send one UTF-8 JSON-RPC object per line; reject or re-encode malformed bytes before retrying.";

    private string BuildOversizedLineErrorResponse(BoundedLineLengthException ex)
        => BuildOversizedLineErrorResponse(ex.CharactersRead, ex.Utf8BytesRead);

    private string BuildOversizedLineErrorResponse(int charactersRead, int utf8BytesRead)
    {
        DeferFrameLog(BuildOversizedMessageLog(charactersRead, utf8BytesRead));
        var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Message too large",
            category: McpErrorEnvelope.CategoryMessageTooLarge,
            suggestion: $"JSON-RPC frame exceeds the {MaxLineCharacterCount} character or {MaxLineByteLength} byte cap. Split the request into smaller calls or use `batch_query` with smaller slots.",
            retrySafe: false);
        return errorResponse.ToJsonString(_jsonOptions);
    }

    /// <summary>
    /// Process one MCP JSON-RPC frame and return the wire-ready response string (or null when
    /// the request was a notification or otherwise yields no response). This synchronous wrapper
    /// is retained for compatibility tests and legacy in-process callers only; transports and
    /// request loops should call <see cref="ProcessFrameAsync"/> so cancellation and shutdown can
    /// flow without sync-over-async blocking (#3770).
    /// 1 フレーム分の MCP JSON-RPC を処理し、ワイヤー応答文字列を返す（通知などで応答なしの場合は null）。
    /// この同期ラッパは互換テストと legacy in-process 呼び出し専用に残す。transport と request loop は
    /// sync-over-async blocking を避けるため <see cref="ProcessFrameAsync"/> を await する (#3770)。
    /// </summary>
    internal string? ProcessFrame(string line)
        // Synchronous callers are compatibility entry points for tests and non-async hosts;
        // transport loops use ProcessFrameAsync directly so request handling stays async.
        => ProcessFrameAsync(line).GetAwaiter().GetResult();

    internal Task<string?> ProcessFrameAsync(string line)
        => ProcessFrameAsync(line, beforeDispatchAsync: null, rejectForCapacity: false);

    private async Task<string?> ProcessFrameAsync(
        string line,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        CapacityRejectedFrameRegistrations? capacityRejectedRegistrations = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Reject oversized messages to prevent memory exhaustion
        // メモリ枯渇を防ぐため巨大メッセージを拒否
        var byteLength = Encoding.UTF8.GetByteCount(line);
        if (line.Length > MaxLineCharacterCount || byteLength > MaxLineByteLength)
            return BuildOversizedLineErrorResponse(line.Length, byteLength);

        JsonNode? request = null;
        var responseHasId = true;
        JsonNode? responseId = null;
        IDisposable? frameCorrelationScope = null;
        var deferredInitializeCommits = new DeferredInitializeCommits();
        try
        {
            request = JsonFrameParser.ParseNode(line, MaxJsonDepth);
            if (request == null)
                return CreateExpectedJsonObjectErrorResponse().ToJsonString(_jsonOptions);

            if (TryCompletePendingClientRequest(request))
                return null;

            capacityRejectedRegistrations?.Register(request);
            ExtractResponseId(request, out responseHasId, out responseId);
            // A batch frame has no single JSON-RPC id. Invalid ids and malformed scalar frames
            // also use id:null only for the JSON-RPC error response; that wire fallback must not
            // be mistaken for an explicit null request id in telemetry. Batch items establish
            // their own valid-id contexts in HandleMessageAsync.
            // batch frame 自体には単一の JSON-RPC id がない。invalid id や scalar frame の
            // id:null は error response 専用で、telemetry 上の明示 null id と混同しない。
            // batch item は HandleMessageAsync で valid id ごとの context を作る。
            var frameHasRequestId = request is JsonObject requestObject
                && TryGetRequestId(requestObject, out var requestObjectHasId, out _)
                && requestObjectHasId;
            var frameHasCorrelation = responseHasId && request is not JsonArray;
            if (frameHasCorrelation && CurrentCorrelationContext.Value is null)
                frameCorrelationScope = BeginRequestCorrelation(responseId, frameHasRequestId);
            using var activity = StartMcpActivity(request, frameHasRequestId, responseId);
            var response = await HandleMessageAsync(
                request,
                isolateRequestDb: true,
                beforeDispatchAsync,
                rejectForCapacity,
                queuedBatchRegistration: null,
                deferredInitializeCommits).ConfigureAwait(false);
            activity?.SetTag("rpc.result", response is null ? "notification" : "response");
            if (response is null)
                return null;

            var serialized = SerializeResponseOrFallback(
                response,
                responseHasId,
                responseId,
                out var serializedOriginalResponse);
            if (serializedOriginalResponse)
            {
                foreach (var state in deferredInitializeCommits.GetIncludedStates(response))
                    CommitInitializeState(state);
            }

            return serialized;
        }
        catch (JsonException ex)
        {
            // Parse error / パースエラー
            DeferFrameLog(BuildJsonParseErrorLog(JsonFrameParser.FormatExceptionDetail(ex)));
            var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Parse error",
                category: McpErrorEnvelope.CategoryParseError,
                suggestion: $"For MCP stdio, send one UTF-8 JSON-RPC 2.0 object per LF-delimited line with nesting depth <= {MaxJsonDepth}. Do not send LSP Content-Length framing.",
                retrySafe: false);
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
            DeferFrameLog(BuildUnhandledLoopErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            var classification = McpErrorEnvelope.ClassifyException(ex);
            var errorResponse = CreateErrorResponse(responseHasId, responseId, classification.JsonRpcCode,
                BuildSanitizedLoopErrorMessage(ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe);
            return SerializeResponseOrFallback(
                errorResponse,
                responseHasId,
                responseId,
                out _);
        }
        finally
        {
            frameCorrelationScope?.Dispose();
        }
    }

    private static Activity? StartMcpActivity(JsonNode request, bool responseHasId, JsonNode? responseId)
    {
        var method = request is JsonObject obj ? TryGetStringMember(obj, "method") : null;
        var traceParent = TryGetMcpTraceParent(request);
        ActivityContext parentContext = default;
        if (traceParent != null)
            ActivityContext.TryParse(traceParent, traceState: null, out parentContext);

        var activity = parentContext != default
            ? CodeIndexTelemetry.ActivitySource.StartActivity("mcp.request", ActivityKind.Server, parentContext)
            : CodeIndexTelemetry.ActivitySource.StartActivity("mcp.request", ActivityKind.Server);
        activity?.SetTag("rpc.system", "jsonrpc");
        activity?.SetTag("rpc.service", "mcp");
        if (!string.IsNullOrWhiteSpace(method))
            activity?.SetTag("rpc.method", method);
        if (responseHasId)
        {
            var requestId = McpRequestIdTelemetry.Create(responseId);
            activity?.SetTag("rpc.request_id", requestId.Token);
            activity?.SetTag("rpc.request_id_type", requestId.Type);
            activity?.SetTag("rpc.request_id_length", requestId.Length);
        }
        return activity;
    }

    private bool TryCompletePendingClientRequest(JsonNode request)
    {
        if (request is not JsonObject obj
            || !obj.TryGetPropertyValue("id", out var id)
            || obj["method"] is not null)
            return false;

        if (!TrySerializeRequestId(id, out var serializedId, out _))
            return false;

        var key = serializedId ?? "null";
        if (!_pendingClientRequests.TryRemove(key, out var pending))
            return false;

        if (obj.TryGetPropertyValue("error", out var error) && error is not null)
        {
            if (!TrySerializeClientResponseError(error, out var serializedError, out var errorBytes))
            {
                DeferFrameLog(BuildClientResponseTooLargeLog("error", errorBytes));
                pending.TrySetException(new InvalidOperationException(BuildClientResponseTooLargeMessage(errorBytes)));
            }
            else
            {
                pending.TrySetException(new InvalidOperationException(serializedError));
            }
        }
        else if (!TryCloneClientResponsePayload(obj["result"], out var resultClone, out var resultBytes))
        {
            DeferFrameLog(BuildClientResponseTooLargeLog("result", resultBytes));
            pending.TrySetException(new InvalidOperationException(BuildClientResponseTooLargeMessage(resultBytes)));
        }
        else
        {
            pending.TrySetResult(resultClone);
        }
        return true;
    }

    internal Task<JsonNode?> RegisterPendingClientRequestForTests(string id)
    {
        var key = JsonSerializer.Serialize(id);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingClientRequests.TryAdd(key, pending))
            throw new InvalidOperationException($"Pending MCP client request already registered: {id}");
        return pending.Task;
    }

    private async Task<JsonNode?> SendClientRequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken)
    {
        if (ClientRequestHandlerForTests is { } handler)
        {
            if (!TryCloneClientResponsePayload(handler(method, @params), out var handlerClone, out var handlerBytes))
            {
                DeferFrameLog(BuildClientResponseTooLargeLog("result", handlerBytes));
                return null;
            }
            return handlerClone;
        }

        var writer = _currentOutOfBandFrameWriter.Value;
        if (writer is null || !_canAwaitClientResponses.Value)
            return null;

        var id = "cdidx-" + Interlocked.Increment(ref s_nextClientRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = JsonSerializer.Serialize(id);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingClientRequests.TryAdd(key, pending))
            return null;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
            request["params"] = @params;

        using var timeoutScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.McpClientRequest,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        using var cancellationRegistration = timeoutScope.Token.Register(static state =>
        {
            var tuple = ((McpServer server, string key, TaskCompletionSource<JsonNode?> pending))state!;
            if (tuple.server._pendingClientRequests.TryRemove(tuple.key, out var _))
                tuple.pending.TrySetCanceled();
        }, (this, key, pending));

        try
        {
            await writer(request.ToJsonString(_jsonOptions), timeoutScope.Token).ConfigureAwait(false);
            return await pending.Task.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingClientRequests.TryRemove(key, out var _);
        }
    }

    internal bool TryCloneClientResponsePayloadForTests(JsonNode? payload, out JsonNode? clone, out int bytesWritten)
        => TryCloneClientResponsePayload(payload, out clone, out bytesWritten);

    internal bool TrySerializeClientResponseErrorForTests(JsonNode error, out string? serialized, out int bytesWritten)
        => TrySerializeClientResponseError(error, out serialized, out bytesWritten);

    private bool TryCloneClientResponsePayload(JsonNode? payload, out JsonNode? clone, out int bytesWritten)
    {
        clone = null;
        bytesWritten = 0;
        if (payload is null)
            return true;

        if (!TryMeasureJsonUtf8BytesWithinLimit(payload, _jsonOptions, MaxClientResponseJsonBytes, out bytesWritten))
            return false;

        clone = McpJsonNode.Clone(payload);
        return true;
    }

    private bool TrySerializeClientResponseError(JsonNode error, out string? serialized, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(error, _jsonOptions, MaxClientResponseJsonBytes, captureSerialized: true, out serialized, out bytesWritten);

    private static string? TryGetMcpTraceParent(JsonNode request)
    {
        if (request is not JsonObject obj ||
            obj["params"] is not JsonObject parameters ||
            parameters["_meta"] is not JsonObject meta)
            return null;

        if (meta["traceparent"] is not JsonValue valueNode ||
            !valueNode.TryGetValue<string>(out var value))
            return null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string SerializeResponseOrFallback(
        JsonNode response,
        bool hasId,
        JsonNode? id,
        out bool serializedOriginalResponse)
    {
        serializedOriginalResponse = false;
        try
        {
            var responseLimit = GetMaxResponseBytes();
            if (_usesDefaultResponseSerializer)
            {
                if (!TrySerializeJsonNodeWithinByteLimit(response, _jsonOptions, responseLimit, captureSerialized: true, out var boundedSerialized, out var boundedResponseBytes))
                    return CreateResponseTooLargeError(hasId, id, boundedResponseBytes, responseLimit, actualBytesExact: false).ToJsonString(_jsonOptions);

                serializedOriginalResponse = true;
                return boundedSerialized!;
            }

            var serialized = _serializeResponse(response);
            var responseBytes = Encoding.UTF8.GetByteCount(serialized);
            if (responseBytes <= responseLimit)
            {
                serializedOriginalResponse = true;
                return serialized;
            }

            return CreateResponseTooLargeError(hasId, id, responseBytes, responseLimit).ToJsonString(_jsonOptions);
        }
        catch (Exception ex)
        {
            DeferFrameLog(BuildResponseSerializationErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            return BuildMinimalInternalErrorResponse(hasId, id, ex);
        }
    }

    private void DeferFrameLog(string message)
        => DeferFrameLog(() => WriteMcpLogLine(message));

    private void DeferFrameLog(Action writeLog)
    {
        var context = CurrentCorrelationContext.Value;
        var logs = _deferredFrameLogs.Value;
        if (logs is null)
        {
            WriteWithCorrelationContext(context, writeLog);
            return;
        }

        logs.Add(() => WriteWithCorrelationContext(context, writeLog));
    }

    private static void WriteWithCorrelationContext(RequestCorrelationContext? context, Action writeLog)
    {
        var previous = CurrentCorrelationContext.Value;
        try
        {
            CurrentCorrelationContext.Value = context;
            writeLog();
        }
        finally
        {
            CurrentCorrelationContext.Value = previous;
        }
    }

    private void BeginDeferredFrameLogs()
        => _deferredFrameLogs.Value = new DeferredFrameLogBuffer();

    private void FlushDeferredFrameLogs()
    {
        var logs = _deferredFrameLogs.Value;
        if (logs is null)
            return;

        _deferredFrameLogs.Value = null;
        logs.ForwardTo(static log => log());
    }

    private sealed class DeferredFrameLogBuffer
    {
        private readonly object _gate = new();
        private List<Action>? _logs = [];
        private Action<Action>? _lateLogForwarder;

        public void Add(Action log)
        {
            Action<Action>? lateLogForwarder;
            lock (_gate)
            {
                if (_logs is not null)
                {
                    _logs.Add(log);
                    return;
                }

                lateLogForwarder = _lateLogForwarder;
            }

            (lateLogForwarder ?? (static lateLog => lateLog()))(log);
        }

        public void ForwardTo(Action<Action> lateLogForwarder)
        {
            lock (_gate)
            {
                if (_logs is null)
                    return;

                foreach (var log in _logs)
                    lateLogForwarder(log);
                _logs = null;
                _lateLogForwarder = lateLogForwarder;
            }
        }
    }

    private sealed class CapacityRejectedFrameRegistrations : IDisposable
    {
        private readonly McpServer _owner;
        private readonly HashSet<string> _requestKeys = new(StringComparer.Ordinal);
        private readonly List<QueuedBatchRequestRegistration> _registrations = [];
        private bool _disposed;

        internal CapacityRejectedFrameRegistrations(McpServer owner)
        {
            _owner = owner;
        }

        internal void Register(JsonNode request)
        {
            if (request is JsonArray batch)
            {
                foreach (var item in batch)
                    RegisterItem(item);
                return;
            }

            RegisterItem(request);
        }

        private void RegisterItem(JsonNode? item)
        {
            if (!BatchItemRequiresResponse(item, out var responseId)
                || SerializeRequestId(responseId) is not { } requestKey
                || !_requestKeys.Add(requestKey))
            {
                return;
            }

            if (_owner.TryRegisterQueuedBatchRequest(requestKey) is { } registration)
                _registrations.Add(registration);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var registration in _registrations)
                registration.DisposeIfUnclaimed();
        }
    }

    private sealed class QueuedBatchRequestRegistration
    {
        private readonly McpServer _owner;
        private readonly string _requestKey;
        private readonly CancellationTokenSource _cancellation;
        // 0 = queued, 1 = claimed by normal dispatch, 2 = cleaned before dispatch.
        private int _state;

        internal QueuedBatchRequestRegistration(
            McpServer owner,
            string requestKey,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _requestKey = requestKey;
            _cancellation = cancellation;
        }

        internal CancellationToken Token => _cancellation.Token;

        internal bool TryCancel()
        {
            try
            {
                _cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Dispatch won the move into `_activeRequests`; the caller will retry there.
                return false;
            }
        }

        internal bool TryClaim()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return false;
            RemoveAndDispose();
            return true;
        }

        internal void DisposeIfUnclaimed()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
                return;
            RemoveAndDispose();
        }

        private void RemoveAndDispose()
        {
            if (_owner._queuedBatchRequests.TryGetValue(_requestKey, out var current)
                && ReferenceEquals(current, this))
            {
                _owner._queuedBatchRequests.TryRemove(_requestKey, out _);
            }
            _cancellation.Dispose();
        }
    }

    private static void WriteMcpLogLine(string message)
    {
        var line = AddCorrelationPrefix(message);
        try
        {
            CommandErrorWriter.WriteStderr(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Best-effort diagnostics: a closed redirected stderr must not break the MCP request.
        }
        GlobalToolLog.Info(line);
    }

    private static string AddCorrelationPrefix(string message)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return message;

        var requestId = context.TelemetryRequestId;
        var prefix = requestId is { } presentRequestId
            ? $"[rid={presentRequestId.Token} rid_type={presentRequestId.Type} rid_length={presentRequestId.Length.ToString(CultureInfo.InvariantCulture)} cid={context.CorrelationId}] "
            : $"[cid={context.CorrelationId}] ";
        return message.StartsWith("[cdidx-mcp] ", StringComparison.Ordinal)
            ? "[cdidx-mcp] " + prefix + message["[cdidx-mcp] ".Length..]
            : prefix + message;
    }

    private static void ExtractResponseId(JsonNode request, out bool hasId, out JsonNode? id)
    {
        if (request is JsonObject obj)
        {
            if (TryGetRequestId(obj, out hasId, out var requestId))
                id = McpJsonNode.Clone(requestId);
            else
                id = null;
            return;
        }

        // For malformed non-object JSON values, JSON-RPC error responses should still carry
        // id:null instead of disappearing when handling or serialization fails.
        hasId = true;
        id = null;
    }

    private static string BuildMinimalInternalErrorResponse(bool hasId, JsonNode? id, Exception ex)
    {
        var message = $"Internal error while serializing MCP response ({ex.GetType().Name}). See cdidx server stderr for details.";
        var builder = new StringBuilder("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":");
        builder.Append(JsonSerializer.Serialize(message));
        AppendMinimalCorrelationData(builder);
        builder.Append('}');
        if (hasId)
        {
            builder.Append(",\"id\":");
            builder.Append(id is null ? "null" : id.ToJsonString());
        }
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendMinimalCorrelationData(StringBuilder builder)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return;

        builder.Append(",\"data\":{\"correlation_id\":");
        builder.Append(JsonSerializer.Serialize(context.CorrelationId));
        if (context.WireRequestId != null)
        {
            builder.Append(",\"request_id\":");
            builder.Append(JsonSerializer.Serialize(context.WireRequestId));
        }
        builder.Append('}');
    }

    /// <summary>
    /// Route a JSON-RPC message to the appropriate handler. This synchronous wrapper is retained
    /// for compatibility tests and legacy in-process callers only; transports should prefer
    /// <see cref="HandleMessageAsync(JsonNode)"/> to avoid sync-over-async dispatch (#3770).
    /// JSON-RPCメッセージを適切なハンドラにルーティング。この同期ラッパは互換テストと legacy
    /// in-process 呼び出し専用に残し、transport は sync-over-async dispatch を避けるため
    /// <see cref="HandleMessageAsync(JsonNode)"/> を優先する (#3770)。
    /// </summary>
    internal JsonNode? HandleMessage(JsonNode request)
        // Keep this sync wrapper for existing in-process callers; async transports call
        // HandleMessageAsync so server loops do not need a sync-over-async bridge.
        => HandleMessageAsync(
            request,
            isolateRequestDb: false,
            beforeDispatchAsync: null,
            rejectForCapacity: false,
            queuedBatchRegistration: null,
            deferredInitializeCommits: null).GetAwaiter().GetResult();

    internal Task<JsonNode?> HandleMessageAsync(JsonNode request)
        => HandleMessageAsync(
            request,
            isolateRequestDb: false,
            beforeDispatchAsync: null,
            rejectForCapacity: false,
            queuedBatchRegistration: null,
            deferredInitializeCommits: null);

    private async Task<JsonNode?> HandleMessageAsync(
        JsonNode request,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (request is JsonArray batch)
        {
            if (deferredInitializeCommits is null)
            {
                return await HandleBatchMessageAsync(
                    batch,
                    isolateRequestDb,
                    beforeDispatchAsync,
                    rejectForCapacity,
                    deferredInitializeCommits).ConfigureAwait(false);
            }

            var previousFrameInitializeState = _frameInitializeState.Value;
            var initialFrameInitializeState = CurrentInitializeState;
            var frameInitializeState = new FrameInitializeState(
                initialFrameInitializeState,
                isProvisionalGeneration: false);
            _frameInitializeState.Value = frameInitializeState;
            var batchBeforeDispatchAsync = beforeDispatchAsync;
            if (beforeDispatchAsync is not null)
            {
                batchBeforeDispatchAsync = async cancellationToken =>
                {
                    await beforeDispatchAsync(cancellationToken).ConfigureAwait(false);
                    // The concurrent loop accepts and pre-registers a batch before its protocol
                    // predecessor finishes. Advance only this batch's original generation after
                    // that predecessor commits; timed-out older frames retain their own holders,
                    // and an in-batch initialize replaces this holder instead of being overwritten.
                    // concurrent loop は protocol predecessor 完了前に batch を受理・事前登録する。
                    // predecessor の commit 後、この batch の元 generation だけを進める。timeout
                    // 後の旧 frame は別 holder を保持し、batch 内 initialize は holder 自体を置換する。
                    frameInitializeState.TryAdvanceToPublishedGeneration(
                        initialFrameInitializeState,
                        PublishedInitializeState);
                };
            }
            try
            {
                return await HandleBatchMessageAsync(
                    batch,
                    isolateRequestDb,
                    batchBeforeDispatchAsync,
                    rejectForCapacity,
                    deferredInitializeCommits).ConfigureAwait(false);
            }
            finally
            {
                _frameInitializeState.Value = previousFrameInitializeState;
            }
        }

        if (request is not JsonObject obj)
            return CreateExpectedJsonObjectErrorResponse();

        lock (_healthStateGate)
            _lastRequestAt = _timeProvider.GetUtcNow();

        // Extract `method` defensively: a non-string `method` (e.g. `"method":42`) must not
        // throw before the auth gate runs, otherwise a token-protected server would surface
        // `-32603 "Internal error"` to an unauthenticated caller instead of `-32001
        // "Unauthorized"`, leaking that the request reached dispatch internals (#1559).
        // `method` は防御的に取り出す。`"method":42` のような非文字列が GetValue<string>()
        // で例外を投げると、認証ゲート前に -32603 が返ってしまい、未認証呼び出し元に dispatch
        // 内部まで届いた事実が漏れる (#1559)。
        var method = TryGetStringMember(obj, "method");
        if (!TryGetRequestId(obj, out var hasId, out var id, out var idError))
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: BuildInvalidRequestIdMessage(idError),
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: BuildInvalidRequestIdSuggestion(idError),
                retrySafe: false,
                extraData: BuildInvalidRequestIdData(idError));

        if (TryGetStringMember(obj, "jsonrpc") != "2.0")
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: jsonrpc must be exactly \"2.0\"",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "Set the top-level `jsonrpc` member to the string `2.0`.",
                retrySafe: false);

        using var correlationScope = hasId && CurrentCorrelationContext.Value is null ? BeginRequestCorrelation(id) : null;

        // A JSON-RPC notification cannot carry an error response, but that does not make it
        // safe to bypass authentication when handling it mutates server state. Authenticate
        // every state-changing notification before cancellation, roots, or lifecycle state is
        // touched; on denial, emit only the bounded local diagnostic and preserve the required
        // no-response wire contract (#4537).
        // JSON-RPC notification はエラー応答を持てないが、server state を変更する通知まで認証を
        // 省略してよいことにはならない。cancellation / roots / lifecycle state に触れる前に認証し、
        // 拒否時は bounded なローカル診断だけを残して no-response 契約を維持する (#4537)。
        if (IsStateChangingNotification(method))
        {
            var notificationAuth = _authenticator.Authenticate(request);
            if (!notificationAuth.IsAuthenticated)
            {
                WriteMcpLogLine(BuildAuthFailureLog(method, notificationAuth.FailureReason));
                return null;
            }
        }

        if (method == "$/cancelRequest" || method == "notifications/cancelled")
        {
            TryCancelRequest(request["params"]);
            return null;
        }

        if (rejectForCapacity && IsStateChangingNotification(method))
        {
            // Eager cancellation is handled above. Other state notifications are dropped on
            // admission overflow regardless of a malformed id, matching the normal no-id
            // overload contract without mutating roots or lifecycle state (#4536, #4545).
            // eager cancellation は上で処理済み。それ以外の state notification は malformed
            // id の有無に関係なく admission overflow 時に drop し、roots/lifecycle を変更しない。
            return null;
        }

        var protocolPredecessorAwaited = false;
        if (IsStateChangingNotification(method) && beforeDispatchAsync is not null)
        {
            // Cancellation controls intentionally bypass protocol barriers, but roots/lifecycle
            // notifications must not mutate state before an earlier initialize commits. Apply the
            // method semantic even when a malformed client attaches an id to the notification.
            // cancellation control は protocol barrier を bypass する一方、roots/lifecycle
            // notification は先行 initialize の commit 前に state を変更してはならない。
            // malformed client が id を付けた場合も method semantics に基づいて待機する。
            await beforeDispatchAsync(_currentRequestToken.Value).ConfigureAwait(false);
            protocolPredecessorAwaited = true;
        }

        if (!hasId)
        {
            if (rejectForCapacity)
                return null;
            if (!protocolPredecessorAwaited && beforeDispatchAsync is not null)
                await beforeDispatchAsync(_currentRequestToken.Value).ConfigureAwait(false);
        }

        // Notifications (no id) don't get a response / 通知（idなし）にはレスポンスなし
        if (method == "notifications/initialized")
            return null;

        if (method == "notifications/roots/list_changed")
        {
            MarkClientRootsStale();
            _frameInitializeState.Value?.MarkRootsChangeAccepted();
            return null;
        }

        // Graceful shutdown via JSON-RPC notification (#1567). Without this, the only way to
        // stop a long-lived `cdidx mcp` server was to close the transport (stdin EOF / HTTP
        // listener stop), which races with in-flight work and forces clients to send SIGINT.
        // Treating both `notifications/shutdown` (the MCP spec-aligned name) and the legacy
        // LSP-style `notifications/exit` alias as graceful-stop signals lets clients drain the
        // current request and exit cleanly. Asynchronous cancellation unblocks any pending
        // `ReadFrameAsync` without letting a slow user callback hold the dispatch thread (#4543).
        // JSON-RPC 通知による graceful shutdown (#1567)。非同期 cancellation で slow callback に
        // dispatch thread を塞がせず `ReadFrameAsync` を unblock する (#4543)。
        if (string.Equals(method, "notifications/shutdown", StringComparison.Ordinal)
            || string.Equals(method, "notifications/exit", StringComparison.Ordinal))
        {
            WriteMcpLogLine($"[cdidx-mcp] Received {method}; draining in-flight work and shutting down.");
            _running = false;
            _ = RequestShutdownCancellation();
            return null;
        }

        if (!hasId)
        {
            if (method != null && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
                WriteMcpLogLine(BuildUnknownNotificationLog(method));
            return null;
        }

        // Authenticate every responded request before dispatch so the auth contract is
        // uniform across `initialize`, `tools/list`, `tools/call`, and `ping`. Run auth even
        // when `method` is missing or malformed so a token-protected server cannot be probed
        // for method-shape errors without credentials (#1559). State-changing notifications
        // pass through their own auth gate above; side-effect-free notifications short-circuit
        // without authentication because they produce no response.
        // すべての応答対象リクエストを dispatch 前に認証する。`method` が欠落・不正でも
        // 認証は走らせ、トークン保護下のサーバーで未認証呼び出し元に method 形式エラーを
        // 漏らさない (#1559)。state-changing notification は上の専用ゲートで認証し、
        // 副作用のない notification だけを応答なしで short-circuit する。
        var authResult = _authenticator.Authenticate(request);
        if (!authResult.IsAuthenticated)
        {
            DeferFrameLog(BuildAuthFailureLog(method, authResult.FailureReason));
            return CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeUnauthorized, message: "Unauthorized",
                category: McpErrorEnvelope.CategoryPermissionDenied,
                suggestion: "Set CDIDX_MCP_AUTH_TOKEN on the server and include a matching params.auth.token (or an `Authorization: Bearer <token>` header for HTTP) on each request.",
                retrySafe: false);
        }

        if (rejectForCapacity)
            return CreateServerBusyResponse(id);

        if (method == null)
        {
            return CreateErrorResponse(hasId: true, id: id, code: -32600, message: "Invalid request: missing method",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "JSON-RPC 2.0 requires a string `method` field.",
                retrySafe: false);
        }

        return await DispatchWithRequestCancellationAsync(id, isolateRequestDb, beforeDispatchAsync, queuedBatchRegistration, () =>
        {
            if (_enforceInitializationLifecycle && !CurrentInitializeState.Initialized && method != "initialize")
            {
                return Task.FromResult<JsonNode>(CreateErrorResponse(hasId: true, id: id, code: -32002, message: "Server not initialized",
                    category: McpErrorEnvelope.CategoryInvalidRequest,
                    suggestion: "Send a successful `initialize` request before calling other MCP methods.",
                    retrySafe: true));
            }

            return method switch
            {
                "initialize" => Task.FromResult<JsonNode>(HandleInitialize(
                    id,
                    request["params"],
                    deferredInitializeCommits)),
                "tools/list" => Task.FromResult<JsonNode>(HandleToolsList(id, request["params"])),
                "tools/call" => HandleToolsCallAsync(hasId, id, request["params"]),
                "resources/list" => Task.FromResult<JsonNode>(HandleResourcesList(id, request["params"])),
                "resources/read" => Task.FromResult<JsonNode>(HandleResourcesRead(id, request["params"])),
                "prompts/list" => Task.FromResult<JsonNode>(HandlePromptsList(id)),
                "prompts/get" => Task.FromResult<JsonNode>(HandlePromptsGet(id, request["params"])),
                "logging/setLevel" => HandleLoggingSetLevelAsync(id, request["params"]),
                "ping" => Task.FromResult<JsonNode>(CreateSuccessResponse(hasId, id, BuildHealthResult())),
                _ => Task.FromResult<JsonNode>(CreateErrorResponse(hasId: true, id: id, code: -32601, message: $"Method not found: {method}",
                    category: McpErrorEnvelope.CategoryMethodNotFound,
                    suggestion: "Supported methods: initialize, tools/list, tools/call, resources/list, resources/read, prompts/list, prompts/get, logging/setLevel, ping, notifications/initialized, notifications/cancelled, notifications/shutdown.",
                    retrySafe: false)),
            };
        }).ConfigureAwait(false);
    }

    private static JsonObject CreateExpectedJsonObjectErrorResponse()
        => CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: expected JSON object",
            category: McpErrorEnvelope.CategoryInvalidRequest,
            suggestion: "Send a JSON-RPC 2.0 object (e.g. {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}).",
            retrySafe: false);

    private static bool IsStateChangingNotification(string? method)
        => method is "$/cancelRequest"
            or "notifications/cancelled"
            or "notifications/roots/list_changed"
            or "notifications/shutdown"
            or "notifications/exit";

    private static JsonObject CreateServerBusyResponse(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            McpErrorEnvelope.CodeServerBusy,
            "Server busy: MCP request backlog is full",
            category: McpErrorEnvelope.CategoryServerBusy,
            suggestion: "Retry after one or more in-flight MCP requests complete.",
            retrySafe: true,
            extraData: new JsonObject { ["retry_after_ms"] = 1000 });

    private string BuildHealthJson(HttpMcpTransport? httpTransport = null)
        => BuildHealthResult(httpTransport).ToJsonString(_jsonOptions);

    private string BuildKeepAliveNotificationJson()
    {
        var now = _timeProvider.GetUtcNow();
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/keep_alive",
            ["params"] = new JsonObject
            {
                ["server_time"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["uptime_s"] = Math.Max(0, (long)Math.Floor((now - _startedAt).TotalSeconds)),
            }
        };
        return notification.ToJsonString(_jsonOptions);
    }

    private static TimeSpan? ReadKeepAliveIntervalFromEnvironment()
    {
        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(KeepAliveIntervalEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds < MinKeepAliveIntervalSeconds
            || seconds > MaxKeepAliveIntervalSeconds)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(KeepAliveIntervalEnvironmentVariable, raw);
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Ignoring invalid {KeepAliveIntervalEnvironmentVariable}='{displayValue}'. Expected a finite value between {MinKeepAliveIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} and {MaxKeepAliveIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds. Keep-alive notifications stay disabled.");
            return null;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private JsonObject BuildHealthResult(HttpMcpTransport? httpTransport = null)
    {
        var now = _timeProvider.GetUtcNow();
        var dbOpen = ProbeDbHealth(out var dbError);
        var httpResponseCleanupDegraded = httpTransport?.ResponseCleanupDegraded ?? false;
        var httpRequestLogDegraded = httpTransport?.RequestLogDegraded ?? false;
        var auditLogDiagnostics = _auditLog?.SnapshotDiagnostics();
        var auditLogDegraded = IsAuditLogDegraded(auditLogDiagnostics);
        DateTimeOffset lastRequestAt;
        lock (_healthStateGate)
            lastRequestAt = _lastRequestAt;
        var result = new JsonObject
        {
            ["status"] = dbOpen && !httpResponseCleanupDegraded && !httpRequestLogDegraded && !auditLogDegraded ? "ok" : "degraded",
            ["uptime_s"] = Math.Max(0, (long)Math.Floor((now - _startedAt).TotalSeconds)),
            ["last_request_at"] = lastRequestAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["db_open"] = dbOpen,
            ["last_db_check_at"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["transport_ready"] = _running,
        };
        if (httpTransport is not null)
        {
            result["http_max_request_body_bytes"] = httpTransport.MaxRequestBodyBytes;
            result["http_request_body_idle_timeout_ms"] = (long)httpTransport.RequestBodyIdleTimeout.TotalMilliseconds;
            result["http_request_lifetime_timeout_ms"] = (long)httpTransport.RequestLifetimeTimeout.TotalMilliseconds;
            result["http_request_body_budget_limit_bytes"] = httpTransport.MaxInFlightRequestBodyBytes;
            result["http_request_body_bytes_in_flight"] = httpTransport.InFlightRequestBodyBytes;
            result["http_request_body_process_bytes_in_flight"] = httpTransport.ProcessInFlightRequestBodyBytes;
            result["http_request_body_peak_bytes"] = httpTransport.PeakInFlightRequestBodyBytes;
            result["http_request_body_budget_scope"] = "process";
            result["http_request_body_budget_rejection_count"] = httpTransport.RequestBodyBudgetLimitRejectionCount;
            result["http_request_body_idle_timeout_count"] = httpTransport.RequestBodyIdleTimeoutCount;
            result["http_request_lifetime_timeout_count"] = httpTransport.RequestLifetimeTimeoutCount;
            result["http_client_disconnect_count"] = httpTransport.ClientDisconnectCount;
            result["http_queued_request_cancellation_count"] = httpTransport.QueuedRequestCancellationCount;
            result["http_event_stream_count"] = httpTransport.EventStreamCount;
            result["http_event_stream_limit"] = httpTransport.MaxEventStreams;
            result["http_max_concurrent_handlers"] = httpTransport.MaxConcurrentHandlers;
            result["http_post_handler_capacity"] = httpTransport.PostHandlerCapacity;
            result["http_event_stream_handler_capacity"] = httpTransport.EventStreamHandlerCapacity;
            result["http_separate_event_stream_handlers"] = httpTransport.UsesSeparateEventStreamHandlers;
            result["http_queued_request_count"] = httpTransport.QueuedRequestCount;
            result["http_request_queue_limit"] = httpTransport.MaxQueuedRequests;
            result["http_request_log_queue_depth"] = httpTransport.RequestLogQueueDepth;
            result["http_request_log_queue_capacity"] = httpTransport.RequestLogQueueCapacity;
            result["http_request_log_dropped_count"] = httpTransport.RequestLogDroppedCount;
            result["http_request_log_queue_full_drop_count"] = httpTransport.RequestLogQueueFullDropCount;
            result["http_request_log_callback_failure_count"] = httpTransport.RequestLogCallbackFailureCount;
            result["http_request_log_degraded"] = httpRequestLogDegraded;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastRequestLogDropReason))
                result["http_request_log_last_drop_reason"] = httpTransport.LastRequestLogDropReason;
            result["http_concurrent_handler_rejection_count"] = httpTransport.ConcurrentHandlerLimitRejectionCount;
            result["http_request_queue_rejection_count"] = httpTransport.RequestQueueLimitRejectionCount;
            result["http_event_stream_rejection_count"] = httpTransport.EventStreamLimitRejectionCount;
            result["http_event_stream_drop_count"] = httpTransport.EventStreamDropCount;
            result["http_event_stream_write_failure_drop_count"] = httpTransport.EventStreamWriteFailureDropCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastEventStreamDropReason))
                result["http_event_stream_last_drop_reason"] = httpTransport.LastEventStreamDropReason;
            result["http_auth_denial_count"] = httpTransport.AuthDenialCount;
            result["http_auth_denial_missing_count"] = httpTransport.AuthDenialMissingCount;
            result["http_auth_denial_ambiguous_count"] = httpTransport.AuthDenialAmbiguousCount;
            result["http_auth_denial_wrong_scheme_count"] = httpTransport.AuthDenialWrongSchemeCount;
            result["http_auth_denial_malformed_token_count"] = httpTransport.AuthDenialMalformedTokenCount;
            result["http_auth_denial_oversized_token_count"] = httpTransport.AuthDenialOversizedTokenCount;
            result["http_auth_denial_wrong_token_count"] = httpTransport.AuthDenialWrongTokenCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastAuthDenialReason))
                result["http_auth_denial_last_reason"] = httpTransport.LastAuthDenialReason;
            result["http_auth_required"] = httpTransport.RequiresBearerToken;
            result["http_auth_disabled"] = httpTransport.AuthDisabled;
            if (!string.IsNullOrWhiteSpace(httpTransport.AuthDisabledWarning))
                result["http_auth_disabled_warning"] = httpTransport.AuthDisabledWarning;
            result["http_response_cleanup_degraded"] = httpResponseCleanupDegraded;
            result["http_response_abort_cleanup_failure_count"] = httpTransport.ResponseAbortCleanupFailureCount;
            result["http_response_close_cleanup_failure_count"] = httpTransport.ResponseCloseCleanupFailureCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastResponseAbortCleanupFailure))
                result["http_response_abort_cleanup_last_error"] = httpTransport.LastResponseAbortCleanupFailure;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastResponseCloseCleanupFailure))
                result["http_response_close_cleanup_last_error"] = httpTransport.LastResponseCloseCleanupFailure;
        }
        if (auditLogDiagnostics is not null)
            result["audit_log"] = BuildAuditLogStatus(auditLogDiagnostics);
        result["metrics"] = BuildMetricsStatus(MetricsSink.SnapshotDiagnostics());
        if (!string.IsNullOrWhiteSpace(dbError))
            result["db_error"] = dbError;
        return result;
    }

    private bool ProbeDbHealth(out string? error)
    {
        var ok = false;
        string? probeError = null;
        try
        {
            using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                _dbPath,
                pooling: false,
                out _,
                out _);
            connection.Open();
            using var command = SqliteConnectionPolicy.CreateCommand(connection);
            command.CommandText = "SELECT 1;";
            _ = command.ExecuteScalar();
            ok = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            probeError = ex.GetType().Name;
        }

        error = probeError;
        return ok;
    }

    private async Task<JsonNode?> HandleBatchMessageAsync(
        JsonArray batch,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (batch.Count == 0)
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: empty batch",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "JSON-RPC 2.0 batch requests must contain at least one request object.",
                retrySafe: false);

        if (batch.Count > MaxBatchRequestCount)
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: batch too large",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: $"JSON-RPC batch requests are limited to {MaxBatchRequestCount} items.",
                retrySafe: false);

        // Client replies complete server-initiated requests and never produce a response item.
        // Consume matched replies before reserving response bytes; unmatched response-shaped
        // objects remain ordinary invalid requests and retain their budget slot.
        // client reply は server 起点 request を完了し response item を生成しないため、response
        // budget 予約前に matched reply を consume する。unmatched object は invalid request として残す。
        var completed = new bool[batch.Count];
        for (var index = 0; index < batch.Count; index++)
        {
            if (batch[index] is JsonObject itemObject
                && TryCompletePendingClientRequest(itemObject))
            {
                completed[index] = true;
            }
        }

        BatchResponseBudgetSlot?[]? budgetSlots = null;
        int?[]? batchResponseItemLimits = null;
        JsonObject? batchBudgetPreflightError = null;
        var batchResponseLimit = 0;
        var activeTransportMaxResponseBytes = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (_usesDefaultResponseSerializer)
        {
            // The complete JSON array owns one response budget. Reserve brackets, commas, and a
            // bounded error for every response-bearing item, then divide the remaining bytes
            // deterministically before concurrent dispatch. JSON 配列全体で 1 つの response
            // budget を共有する。bracket、comma、各 response item の bounded error を予約し、
            // 残りを concurrent dispatch 前に決定的に分配する。
            batchResponseLimit = GetMaxResponseBytes();
            if (activeTransportMaxResponseBytes > 0)
                batchResponseLimit = Math.Min(activeTransportMaxResponseBytes, batchResponseLimit);
            budgetSlots = new BatchResponseBudgetSlot?[batch.Count];
            batchResponseItemLimits = new int?[batch.Count];
            long reservedErrorBytes = 0;
            var responseCount = 0;
            for (var index = 0; index < batch.Count; index++)
            {
                if (completed[index])
                    continue;
                if (!TryCreateBatchResponseBudgetSlot(batch[index], out var slot))
                    continue;

                budgetSlots[index] = slot;
                reservedErrorBytes += slot.ErrorResponseBytes;
                responseCount++;
            }

            if (responseCount > 0)
            {
                var payloadBytes = batchResponseLimit - 2L - (responseCount - 1L);
                if (payloadBytes < reservedErrorBytes)
                {
                    // Defer the terminal budget error until request IDs are durably registered
                    // and cancellation controls have run. No ordinary or state-changing work is
                    // dispatched on this path (#4544, #4545).
                    // terminal budget error は request ID の durable 登録と cancellation control
                    // 実行後まで保留し、通常処理や他の state mutation は開始しない。
                    batchBudgetPreflightError = CreateBatchEnvelopeBudgetError(
                        batchResponseLimit,
                        retrySafe: true);
                }
                else
                {
                    var distributableBytes = payloadBytes - reservedErrorBytes;
                    var fairShareBytes = distributableBytes / responseCount;
                    var remainderBytes = distributableBytes % responseCount;
                    for (var index = 0; index < batch.Count; index++)
                    {
                        if (budgetSlots[index] is not { } slot)
                            continue;

                        var itemExtraBytes = fairShareBytes;
                        if (remainderBytes > 0)
                        {
                            itemExtraBytes++;
                            remainderBytes--;
                        }
                        batchResponseItemLimits[index] = checked((int)(slot.ErrorResponseBytes + itemExtraBytes));
                    }

                    // Equal caps can strand the same resource-serialization fragment in every slot.
                    // Move one minimum page quantum from the first resources/list slot to the last so
                    // one concurrent page can consume that deterministic slack without exceeding the
                    // aggregate cap. 等分時に各 slot へ同じ serialization 断片が残るのを避けるため、
                    // 最初の resources/list から最後へ最小 page 予算 1 単位を移す。
                    var firstResourceIndex = -1;
                    var lastResourceIndex = -1;
                    for (var index = 0; index < batch.Count; index++)
                    {
                        if (budgetSlots[index]?.CanShapeResourcesListResponse != true)
                            continue;
                        if (firstResourceIndex < 0)
                            firstResourceIndex = index;
                        lastResourceIndex = index;
                    }
                    if (firstResourceIndex >= 0 && lastResourceIndex != firstResourceIndex)
                    {
                        var donorSlot = budgetSlots[firstResourceIndex]!.Value;
                        var donorLimit = batchResponseItemLimits[firstResourceIndex]!.Value;
                        var transferableBytes = Math.Min(
                            MinResourceListMaxBytes,
                            donorLimit - donorSlot.ErrorResponseBytes);
                        batchResponseItemLimits[firstResourceIndex] = donorLimit - transferableBytes;
                        batchResponseItemLimits[lastResourceIndex] = checked(
                            batchResponseItemLimits[lastResourceIndex]!.Value + transferableBytes);
                    }
                }
            }
        }

        // A batch is one wire frame but each item is an independently bounded JSON-RPC
        // operation (#4545). Invalid items are materialized immediately, cancellation controls
        // run eagerly, and state-changing items split the remaining work into ordered segments.
        // Response nodes are retained by input index so completion timing cannot reorder the wire
        // response. バッチは 1 wire frame だが、各 item を独立した bounded operation として扱う。
        // 不正 item は即時確定し、cancel control は先行処理し、状態変更 item で順序 segment を区切る。
        var responsesByIndex = new JsonNode?[batch.Count];
        var logsByIndex = new DeferredFrameLogBuffer?[batch.Count];
        var orderingFences = new bool[batch.Count];
        var cancellationItems = new bool[batch.Count];
        var queuedRegistrations = new QueuedBatchRequestRegistration?[batch.Count];
        var seenRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var isolateBatchItems = isolateRequestDb || batch.Count > 1;

        for (var index = 0; index < batch.Count; index++)
        {
            if (completed[index])
                continue;

            var item = batch[index];
            if (item is null || item is not JsonObject and not JsonArray)
            {
                using (BeginBatchItemCorrelation(id: null, index))
                    responsesByIndex[index] = CreateInvalidBatchItemResponse(nestedBatch: false);
                completed[index] = true;
                continue;
            }
            if (item is JsonArray)
            {
                using (BeginBatchItemCorrelation(id: null, index))
                    responsesByIndex[index] = CreateInvalidBatchItemResponse(nestedBatch: true);
                completed[index] = true;
                continue;
            }
            var itemObject = (JsonObject)item;
            if (IsCancellationItem(itemObject))
            {
                // Execute controls only after this pass has durably registered every unique
                // request ID. This preserves eager cancellation even when the control precedes
                // its target and the short tombstone cache is full (#4545).
                // 全 unique request ID を durable 登録してから control を実行する。cancel が target
                // より先でも、短命 tombstone cache が満杯でも eager cancellation を保つ。
                cancellationItems[index] = true;
                continue;
            }

            orderingFences[index] = IsProtocolOrderingBarrierItem(itemObject);
            if (TryGetRequestId(itemObject, out var hasId, out var id)
                && hasId
                && SerializeRequestId(id) is { } requestKey)
            {
                if (!seenRequestIds.Add(requestKey))
                {
                    // Preserve the pre-concurrency behavior for duplicate ids in one batch: the
                    // later occurrence starts only after the earlier occurrence has completed.
                    // 同一 batch 内の重複 id は、後続を fence にして従来の逐次 semantics を保つ。
                    orderingFences[index] = true;
                }
                else if (!rejectForCapacity)
                {
                    queuedRegistrations[index] = TryRegisterQueuedBatchRequest(requestKey);
                }
            }
        }

        for (var index = 0; index < batch.Count; index++)
        {
            if (!cancellationItems[index])
                continue;

            var cancellationResult = await ExecuteBatchItemAsync(
                batch[index]!,
                index,
                isolateRequestDb: true,
                beforeDispatchAsync: null,
                rejectForCapacity: false,
                queuedBatchRegistration: null,
                responseItemMaxBytes: batchResponseItemLimits?[index],
                deferredInitializeCommits).ConfigureAwait(false);
            responsesByIndex[index] = cancellationResult.Response;
            logsByIndex[index] = cancellationResult.Logs;
            completed[index] = true;
        }

        if (batchBudgetPreflightError is not null)
        {
            foreach (var registration in queuedRegistrations)
                registration?.DisposeIfUnclaimed();
            MergeBatchItemLogs(logsByIndex);
            return batchBudgetPreflightError;
        }

        if (rejectForCapacity)
        {
            for (var index = 0; index < batch.Count; index++)
            {
                if (completed[index])
                    continue;
                var result = await ExecuteBatchItemAsync(
                    batch[index]!,
                    index,
                    isolateBatchItems,
                    beforeDispatchAsync: null,
                    rejectForCapacity: true,
                    queuedBatchRegistration: null,
                    responseItemMaxBytes: batchResponseItemLimits?[index],
                    deferredInitializeCommits).ConfigureAwait(false);
                responsesByIndex[index] = result.Response;
                logsByIndex[index] = result.Logs;
                completed[index] = true;
            }

            MergeBatchItemLogs(logsByIndex);
            return BuildBatchResponse(
                responsesByIndex,
                budgetSlots,
                batchResponseItemLimits,
                batchResponseLimit);
        }

        var independentSegment = new List<int>();
        for (var index = 0; index < batch.Count; index++)
        {
            if (completed[index])
                continue;

            if (!orderingFences[index])
            {
                independentSegment.Add(index);
                continue;
            }

            await ExecuteBatchSegmentAsync(
                batch,
                independentSegment,
                isolateBatchItems,
                responsesByIndex,
                logsByIndex,
                queuedRegistrations,
                batchResponseItemLimits,
                deferredInitializeCommits,
                beforeDispatchAsync).ConfigureAwait(false);
            independentSegment.Clear();
            await ExecuteBatchItemAsync(
                batch[index]!,
                index,
                isolateBatchItems,
                responsesByIndex,
                logsByIndex,
                beforeDispatchAsync,
                queuedRegistrations[index],
                batchResponseItemLimits?[index],
                deferredInitializeCommits).ConfigureAwait(false);

            var fenceResponse = responsesByIndex[index];
            if (fenceResponse is not null
                && deferredInitializeCommits?.TryGetRegisteredState(fenceResponse, out var initializeState) == true)
            {
                _frameInitializeState.Value = new FrameInitializeState(
                    BuildCommittedInitializeState(CurrentInitializeState, initializeState, logCallerSwap: false),
                    isProvisionalGeneration: true);
            }
            else if (_frameInitializeState.Value is { } currentFrameState
                && currentFrameState.TryConsumeAcceptedRootsChange())
            {
                var nextState = currentFrameState.IsProvisionalGeneration
                    ? currentFrameState.Current with { ClientRootsStale = true }
                    : PublishedInitializeState;
                _frameInitializeState.Value = new FrameInitializeState(
                    nextState,
                    currentFrameState.IsProvisionalGeneration);
            }
        }

        await ExecuteBatchSegmentAsync(
            batch,
            independentSegment,
            isolateBatchItems,
            responsesByIndex,
            logsByIndex,
            queuedRegistrations,
            batchResponseItemLimits,
            deferredInitializeCommits,
            beforeDispatchAsync).ConfigureAwait(false);
        MergeBatchItemLogs(logsByIndex);

        return BuildBatchResponse(
            responsesByIndex,
            budgetSlots,
            batchResponseItemLimits,
            batchResponseLimit);
    }

    private QueuedBatchRequestRegistration? TryRegisterQueuedBatchRequest(string requestKey)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _currentRequestToken.Value,
            _shutdownCts.Token);
        var registration = new QueuedBatchRequestRegistration(this, requestKey, cancellation);
        if (!_queuedBatchRequests.TryAdd(requestKey, registration))
        {
            registration.DisposeIfUnclaimed();
            return null;
        }

        if (TryConsumePendingRequestCancellation(requestKey))
            registration.TryCancel();
        return registration;
    }

    private JsonNode? BuildBatchResponse(
        IReadOnlyList<JsonNode?> responsesByIndex,
        IReadOnlyList<BatchResponseBudgetSlot?>? budgetSlots,
        IReadOnlyList<int?>? responseItemLimits,
        int batchResponseLimit)
    {
        var responses = new JsonArray();
        for (var index = 0; index < responsesByIndex.Count; index++)
        {
            var response = responsesByIndex[index];
            if (response is not null
                && budgetSlots?[index] is { } slot
                && responseItemLimits?[index] is { } itemResponseLimit
                && !TryMeasureJsonUtf8BytesWithinLimit(
                    response,
                    _jsonOptions,
                    itemResponseLimit,
                    out _)
                && (slot.CanShapeResourcesReadResponse
                    || (slot.CanShapeResourcesListResponse
                        && IsResourcesListSuccessResponse(response))))
            {
                response = slot.ErrorResponse;
            }

            if (response is not null)
                responses.Add(response);
        }

        if (responses.Count == 0)
            return null;
        if (batchResponseLimit > 0
            && !TryMeasureJsonUtf8BytesWithinLimit(responses, _jsonOptions, batchResponseLimit, out _))
        {
            // Generic and state-changing responses are never rewritten item-by-item. If their
            // aggregate exceeds the cap, report an unknown completion state so clients do not
            // retry effects unsafely. generic / state-changing response は item ごとに書き換えず、
            // aggregate 超過時は completion unknown を返して危険な retry を防ぐ。
            return CreateBatchEnvelopeBudgetError(batchResponseLimit, retrySafe: false);
        }
        return responses;
    }

    private static JsonObject CreateInvalidBatchItemResponse(bool nestedBatch)
        => CreateErrorResponse(
            hasId: true,
            id: null,
            code: -32600,
            message: nestedBatch ? "Invalid request: nested batches are not supported" : "Invalid request: expected JSON object",
            category: McpErrorEnvelope.CategoryInvalidRequest,
            suggestion: nestedBatch
                ? "JSON-RPC batch items must be request objects, not nested arrays."
                : "Each JSON-RPC batch item must be a request object.",
            retrySafe: false);

    private static bool IsCancellationItem(JsonObject item)
        => TryGetStringMember(item, "method") is "$/cancelRequest" or "notifications/cancelled";

    private async Task ExecuteBatchSegmentAsync(
        JsonArray batch,
        IReadOnlyList<int> indexes,
        bool isolateRequestDb,
        JsonNode?[] responsesByIndex,
        DeferredFrameLogBuffer?[] logsByIndex,
        QueuedBatchRequestRegistration?[] queuedRegistrations,
        int?[]? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits,
        Func<CancellationToken, Task>? beforeDispatchAsync)
    {
        if (indexes.Count == 0)
            return;

        var nextIndex = -1;
        var workers = new Task[Math.Min(indexes.Count, MaxConcurrency)];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Run(async () =>
            {
                while (true)
                {
                    var segmentIndex = Interlocked.Increment(ref nextIndex);
                    if (segmentIndex >= indexes.Count)
                        return;

                    var batchIndex = indexes[segmentIndex];
                    await ExecuteBatchItemAsync(
                        batch[batchIndex]!,
                        batchIndex,
                        isolateRequestDb,
                        responsesByIndex,
                        logsByIndex,
                        beforeDispatchAsync,
                        queuedRegistrations[batchIndex],
                        responseItemMaxBytes?[batchIndex],
                        deferredInitializeCommits).ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ExecuteBatchItemAsync(
        JsonNode item,
        int index,
        bool isolateRequestDb,
        JsonNode?[] responsesByIndex,
        DeferredFrameLogBuffer?[] logsByIndex,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        int? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var result = await ExecuteBatchItemAsync(
            item,
            index,
            isolateRequestDb,
            beforeDispatchAsync,
            rejectForCapacity: false,
            queuedBatchRegistration,
            responseItemMaxBytes,
            deferredInitializeCommits).ConfigureAwait(false);
        responsesByIndex[index] = result.Response;
        logsByIndex[index] = result.Logs;
    }

    private async Task<(JsonNode? Response, DeferredFrameLogBuffer Logs)> ExecuteBatchItemAsync(
        JsonNode item,
        int index,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        int? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var parentLogs = _deferredFrameLogs.Value;
        var previousBatchResponseItemMaxBytes = _currentBatchResponseItemMaxBytes.Value;
        var itemLogs = new DeferredFrameLogBuffer();
        _deferredFrameLogs.Value = itemLogs;
        _currentBatchResponseItemMaxBytes.Value = responseItemMaxBytes;
        Database.DbDebug.ResetContext();
        ExtractResponseId(item, out var hasId, out var id);
        var hasTelemetryRequestId = item is JsonObject itemObject
            && TryGetRequestId(itemObject, out var itemHasId, out _)
            && itemHasId;
        using var correlationScope = BeginBatchItemCorrelation(id, index, hasTelemetryRequestId);
        try
        {
            var response = await HandleMessageAsync(
                item,
                isolateRequestDb,
                beforeDispatchAsync,
                rejectForCapacity,
                queuedBatchRegistration,
                deferredInitializeCommits).ConfigureAwait(false);
            return (response, itemLogs);
        }
        catch (Exception ex)
        {
            DeferFrameLog(BuildUnhandledLoopErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            if (!hasId)
                return (null, itemLogs);

            var classification = McpErrorEnvelope.ClassifyException(ex);
            return (CreateErrorResponse(
                hasId: true,
                id,
                classification.JsonRpcCode,
                BuildSanitizedLoopErrorMessage(ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe), itemLogs);
        }
        finally
        {
            Database.DbDebug.ResetContext();
            _currentBatchResponseItemMaxBytes.Value = previousBatchResponseItemMaxBytes;
            _deferredFrameLogs.Value = parentLogs;
            queuedBatchRegistration?.DisposeIfUnclaimed();
        }
    }

    private void MergeBatchItemLogs(IReadOnlyList<DeferredFrameLogBuffer?> logsByIndex)
    {
        var parentLogs = _deferredFrameLogs.Value;
        Action<Action> forward = parentLogs is null
            ? static log => log()
            : parentLogs.Add;
        foreach (var itemLogs in logsByIndex)
            itemLogs?.ForwardTo(forward);
    }

    private bool TryCreateBatchResponseBudgetSlot(JsonNode? item, out BatchResponseBudgetSlot slot)
    {
        slot = default;
        if (!BatchItemRequiresResponse(item, out var responseId))
            return false;

        var canShapeResourcesListResponse = CanShapeResourcesListResponse(item);
        var canShapeResourcesReadResponse = CanShapeResourcesReadResponse(item);
        var errorResponse = canShapeResourcesReadResponse
            ? CreateResourceReadBatchItemBudgetError(responseId)
            : CreateBatchItemBudgetError(responseId);
        _ = TryMeasureJsonUtf8BytesWithinLimit(errorResponse, _jsonOptions, int.MaxValue, out var errorResponseBytes);
        slot = new BatchResponseBudgetSlot(
            errorResponse,
            errorResponseBytes,
            canShapeResourcesListResponse,
            canShapeResourcesReadResponse);
        return true;
    }

    private static bool CanShapeResourcesListResponse(JsonNode? item)
        => item is JsonObject request
            && TryGetRequestId(request, out var hasId, out _)
            && hasId
            && TryGetStringMember(request, "jsonrpc") == "2.0"
            && TryGetStringMember(request, "method") == "resources/list";

    private static bool CanShapeResourcesReadResponse(JsonNode? item)
        => item is JsonObject request
            && TryGetRequestId(request, out var hasId, out _)
            && hasId
            && TryGetStringMember(request, "jsonrpc") == "2.0"
            && TryGetStringMember(request, "method") == "resources/read";

    private static bool IsResourcesListSuccessResponse(JsonNode response)
        => response is JsonObject responseObject
            && responseObject["result"] is JsonObject result
            && result["resources"] is JsonArray;

    private static bool BatchItemRequiresResponse(JsonNode? item, out JsonNode? responseId)
    {
        responseId = null;
        if (item is not JsonObject request)
            return true;

        if (!TryGetRequestId(request, out var hasId, out var id)
            || TryGetStringMember(request, "jsonrpc") != "2.0")
        {
            return true;
        }

        var method = TryGetStringMember(request, "method");
        if (method is "$/cancelRequest"
            or "notifications/cancelled"
            or "notifications/initialized"
            or "notifications/roots/list_changed"
            or "notifications/shutdown"
            or "notifications/exit")
        {
            return false;
        }
        if (!hasId)
            return false;

        responseId = McpJsonNode.Clone(id);
        return true;
    }

    private static JsonObject CreateBatchItemBudgetError(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            code: -32603,
            message: "resources/list could not fit within its share of the active batch response byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Request a smaller resources/list page, split the batch, or raise the applicable MCP or transport response byte limit.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["reason"] = "batch_response_budget_exceeded",
            });

    private static JsonObject CreateResourceReadBatchItemBudgetError(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            code: -32603,
            message: "Batch response budget too small.",
            category: McpErrorEnvelope.CategoryInternalError,
            suggestion: "Use a smaller JSON-RPC batch and retry.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "batch_response_budget_too_small",
            });

    private static JsonObject CreateBatchEnvelopeBudgetError(int batchResponseLimit, bool retrySafe)
        => CreateErrorResponse(
            hasId: true,
            id: null,
            code: -32603,
            message: retrySafe
                ? "The JSON-RPC batch cannot fit within the active response byte limit."
                : "The completed JSON-RPC batch exceeded the active response byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: retrySafe
                ? "Split the batch into fewer requests or raise the applicable MCP or transport response byte limit."
                : "Do not automatically retry state-changing items; their completion state is unknown. Split future batches into fewer requests.",
            retrySafe,
            extraData: new JsonObject
            {
                ["reason"] = retrySafe
                    ? "batch_response_budget_too_small"
                    : "batch_response_budget_exceeded",
                ["limit_bytes"] = batchResponseLimit,
                ["completion_state"] = retrySafe ? "not_started" : "unknown",
            });

    private readonly record struct BatchResponseBudgetSlot(
        JsonObject ErrorResponse,
        int ErrorResponseBytes,
        bool CanShapeResourcesListResponse,
        bool CanShapeResourcesReadResponse);

    private async Task<JsonNode> DispatchWithRequestCancellationAsync(
        JsonNode? id,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        Func<Task<JsonNode>> action)
    {
        var requestKey = SerializeRequestId(id);
        var telemetryRequestId = McpRequestIdTelemetry.Create(id);
        var requestCts = queuedBatchRegistration is null
            ? CancellationTokenSource.CreateLinkedTokenSource(_currentRequestToken.Value, _shutdownCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                _currentRequestToken.Value,
                _shutdownCts.Token,
                queuedBatchRegistration.Token);
        var registeredRequest = false;
        if (requestKey is not null)
        {
            if (!_activeRequests.TryAdd(requestKey, requestCts))
            {
                requestCts.Dispose();
                return CreateErrorResponse(hasId: true, id: id, code: -32600, message: "Duplicate in-flight request id",
                    category: McpErrorEnvelope.CategoryInvalidRequest,
                    suggestion: "JSON-RPC request ids must be unique while a previous request with the same id is still running.",
                    retrySafe: true);
            }
            registeredRequest = true;
            if (queuedBatchRegistration is not null && !queuedBatchRegistration.TryClaim())
                CancelRequestCts(requestCts);
            if (TryConsumePendingRequestCancellation(requestKey))
                CancelRequestCts(requestCts);
            RequestRegisteredForTests?.Invoke(id);
        }

        var previousToken = _currentRequestToken.Value;
        Stopwatch? stopwatch = null;
        var cleanupNow = true;
        var executionSlotAcquired = false;
        var releaseExecutionSlotNow = true;
        try
        {
            _currentRequestToken.Value = requestCts.Token;
            requestCts.Token.ThrowIfCancellationRequested();
            if (beforeDispatchAsync is not null)
                await beforeDispatchAsync(requestCts.Token).ConfigureAwait(false);
            await _concurrencyGate.WaitAsync(requestCts.Token).ConfigureAwait(false);
            executionSlotAcquired = true;
            requestCts.Token.ThrowIfCancellationRequested();
            stopwatch = Stopwatch.StartNew();

            if (!isolateRequestDb)
            {
                requestCts.CancelAfter(_requestTimeout);
                var previousIsolation = _isolateDbForCurrentRequest.Value;
                _isolateDbForCurrentRequest.Value = false;
                try
                {
                    await DelayRequestForTestsAsync(id, requestCts.Token).ConfigureAwait(false);
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    _isolateDbForCurrentRequest.Value = previousIsolation;
                }
            }

            var actionTask = Task.Run(async () =>
            {
                var previousIsolation = _isolateDbForCurrentRequest.Value;
                _isolateDbForCurrentRequest.Value = isolateRequestDb;
                try
                {
                    await DelayRequestForTestsAsync(id, requestCts.Token).ConfigureAwait(false);
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    _isolateDbForCurrentRequest.Value = previousIsolation;
                }
            }, requestCts.Token);
            using var timeoutDelayCts = new CancellationTokenSource();
            var remainingTimeout = _requestTimeout - stopwatch.Elapsed;
            var timeoutTask = remainingTimeout <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(remainingTimeout, timeoutDelayCts.Token);
            var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = requestCts.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancellationSignal);
            var cancellationTask = cancellationSignal.Task;
            var completed = await Task.WhenAny(actionTask, timeoutTask, cancellationTask).ConfigureAwait(false);
            try { timeoutDelayCts.Cancel(); }
            catch (ObjectDisposedException) { /* the timeout signal has already completed. */ }
            if (completed == cancellationTask && _shutdownCts.IsCancellationRequested)
            {
                // EOF/server shutdown owns the bounded outer request-task drain. Keep this
                // dispatch attached to non-cooperative work so teardown does not manufacture a
                // late cancellation response or race a terminal protocol-error write (#4543).
                // EOF/server shutdown は外側の bounded request-task drain が所有する。非協調 work を
                // detach せず、遅延 cancel response や terminal protocol-error write との race を防ぐ。
                return await actionTask.ConfigureAwait(false);
            }
            if (completed != actionTask)
            {
                var timedOut = completed == timeoutTask;
                if (timedOut)
                    CancelRequestCts(requestCts);
                var elapsed = stopwatch.Elapsed;
                if (timedOut)
                    RecordTimedOutIsolatedActionDraining(telemetryRequestId, elapsed);
                cleanupNow = false;
                releaseExecutionSlotNow = false;
                _currentDetachedIsolatedActions.Value?.Enqueue(actionTask);
                // This cleanup must run even after request timeout/shutdown cancellation;
                // otherwise `_activeRequests`, the linked CTS, and the execution lease would leak
                // when an isolated action eventually observes cancellation and exits. The lease
                // intentionally remains held until the underlying action actually ends so timeout
                // responses cannot let live handlers exceed MaxConcurrency (#3722, #4536, #4545).
                // request timeout / shutdown cancellation 後でも cleanup は必ず実行する。
                // underlying action が実際に終了するまで execution lease も保持し、timeout response
                // の後に live handler が MaxConcurrency を超えないようにする (#3722, #4536, #4545)。
                _ = actionTask.ContinueWith(task =>
                {
                    try
                    {
                        _ = task.Exception;
                        if (registeredRequest)
                            _activeRequests.TryRemove(requestKey!, out _);
                        if (timedOut)
                            RecordTimedOutIsolatedActionDrained(telemetryRequestId, task);
                    }
                    finally
                    {
                        requestCts.Dispose();
                        _concurrencyGate.Release();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return timedOut
                    ? CreateRequestTimeoutResponse(id, elapsed, isolatedActionDraining: true)
                    : CreateCancelledResponse(id);
            }

            return await actionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            if (stopwatch is not null
                && !previousToken.IsCancellationRequested
                && !_shutdownCts.IsCancellationRequested
                && stopwatch.Elapsed >= _requestTimeout)
                return CreateRequestTimeoutResponse(id, stopwatch.Elapsed);
            return CreateCancelledResponse(id);
        }
        finally
        {
            _currentRequestToken.Value = previousToken;
            if (executionSlotAcquired && releaseExecutionSlotNow)
                _concurrencyGate.Release();
            if (cleanupNow)
            {
                if (registeredRequest)
                    _activeRequests.TryRemove(requestKey!, out _);
                requestCts.Dispose();
            }
        }
    }

    private Task DelayRequestForTestsAsync(JsonNode? id, CancellationToken cancellationToken)
    {
        if (RequestDelayForTestsWithId is { } delayWithId)
            return delayWithId(McpJsonNode.Clone(id), cancellationToken);
        return RequestDelayForTests is { } delay
            ? delay(cancellationToken)
            : Task.CompletedTask;
    }

    private static JsonObject CreateRequestTimeoutResponse(JsonNode? id, TimeSpan elapsed, bool isolatedActionDraining = false)
        => CreateErrorResponse(hasId: true, id: id, code: -32603, message: "Request timed out",
            category: McpErrorEnvelope.CategoryInternalError,
            suggestion: "Retry with a narrower query, refresh the index if it is degraded, or increase the MCP request timeout before retrying.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["reason"] = "timeout",
                ["timeout_category"] = OperationTimeoutCategories.McpRequest,
                ["elapsed_ms"] = (long)Math.Ceiling(elapsed.TotalMilliseconds),
                ["isolated_action_draining"] = isolatedActionDraining,
            });

    private void RecordTimedOutIsolatedActionDraining(McpRequestIdTelemetryData requestId, TimeSpan elapsed)
    {
        var elapsedMs = (long)Math.Ceiling(elapsed.TotalMilliseconds);
        Interlocked.Increment(ref _timedOutIsolatedActionDrainingCount);
        lock (_requestTimeoutDiagnosticsGate)
        {
            _lastRequestTimeoutDrainDiagnostic = new RequestTimeoutDrainDiagnostic(
                requestId,
                elapsedMs,
                "draining");
        }
        CommandErrorWriter.WriteStderr(BuildTimedOutIsolatedActionDrainingLog(requestId, elapsedMs));
    }

    private void RecordTimedOutIsolatedActionDrained(McpRequestIdTelemetryData requestId, Task task)
    {
        Interlocked.Decrement(ref _timedOutIsolatedActionDrainingCount);
        Interlocked.Increment(ref _timedOutIsolatedActionDrainedCount);
        var state = task.IsCanceled ? "canceled" : task.IsFaulted ? "faulted" : "completed";
        lock (_requestTimeoutDiagnosticsGate)
        {
            _lastRequestTimeoutDrainDiagnostic = new RequestTimeoutDrainDiagnostic(
                requestId,
                null,
                state);
        }
    }

    internal JsonObject BuildRequestTimeoutDiagnosticsStatus()
    {
        RequestTimeoutDrainDiagnostic? last;
        lock (_requestTimeoutDiagnosticsGate)
        {
            last = _lastRequestTimeoutDrainDiagnostic;
        }

        var payload = new JsonObject
        {
            ["isolated_action_draining_count"] = Interlocked.Read(ref _timedOutIsolatedActionDrainingCount),
            ["isolated_action_drained_count"] = Interlocked.Read(ref _timedOutIsolatedActionDrainedCount),
            ["timeout_ms"] = (long)Math.Ceiling(_requestTimeout.TotalMilliseconds),
        };
        if (last is not null)
        {
            payload["last"] = new JsonObject
            {
                ["request_id"] = last.RequestId.Token,
                ["request_id_type"] = last.RequestId.Type,
                ["request_id_length"] = last.RequestId.Length,
                ["elapsed_ms"] = last.ElapsedMs.HasValue ? JsonValue.Create(last.ElapsedMs.Value) : null,
                ["state"] = last.State,
            };
        }
        return payload;
    }

    internal static string BuildTimedOutIsolatedActionDrainingLog(McpRequestIdTelemetryData requestId, long elapsedMs)
        => $"[cdidx-mcp] Request timed out while isolated action is still draining: request_id={requestId.Token} request_id_type={requestId.Type} request_id_length={requestId.Length.ToString(CultureInfo.InvariantCulture)} elapsed_ms={elapsedMs}. The response has been sent; cleanup will continue in the background.";

    private static IDisposable BeginRequestCorrelation(JsonNode? id, bool includeRequestId = true)
    {
        var previous = CurrentCorrelationContext.Value;
        CurrentCorrelationContext.Value = new RequestCorrelationContext(
            SerializeRequestId(id),
            includeRequestId ? McpRequestIdTelemetry.Create(id) : null,
            Guid.NewGuid().ToString("D"));
        return new CorrelationScope(previous);
    }

    private static IDisposable BeginBatchItemCorrelation(JsonNode? id, int itemIndex, bool includeRequestId = false)
    {
        var previous = CurrentCorrelationContext.Value;
        var correlationId = previous is null
            ? Guid.NewGuid().ToString("D")
            : $"{previous.CorrelationId}.{itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        CurrentCorrelationContext.Value = new RequestCorrelationContext(
            SerializeRequestId(id),
            includeRequestId ? McpRequestIdTelemetry.Create(id) : null,
            correlationId);
        return new CorrelationScope(previous);
    }

    private static IDisposable BeginChildCorrelation(int childIndex)
    {
        var previous = CurrentCorrelationContext.Value;
        var requestId = previous?.WireRequestId;
        var telemetryRequestId = previous?.TelemetryRequestId;
        var correlationId = previous == null
            ? Guid.NewGuid().ToString("D")
            : $"{previous.CorrelationId}.{childIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        CurrentCorrelationContext.Value = new RequestCorrelationContext(requestId, telemetryRequestId, correlationId);
        return new CorrelationScope(previous);
    }

    private sealed record RequestCorrelationContext(
        string? WireRequestId,
        McpRequestIdTelemetryData? TelemetryRequestId,
        string CorrelationId);
    private sealed record RequestTimeoutDrainDiagnostic(
        McpRequestIdTelemetryData RequestId,
        long? ElapsedMs,
        string State);

    private sealed class CorrelationScope : IDisposable
    {
        private readonly RequestCorrelationContext? _previous;
        private bool _disposed;

        public CorrelationScope(RequestCorrelationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentCorrelationContext.Value = _previous;
            _disposed = true;
        }
    }

    private void TryCancelRequest(JsonNode? cancelParams)
    {
        var requestId = cancelParams?["id"] ?? cancelParams?["requestId"];
        var requestKey = SerializeRequestId(requestId);
        if (requestKey == null)
            return;
        if (_activeRequests.TryGetValue(requestKey, out var cts))
        {
            CancelRequestCts(cts);
            return;
        }
        if (_queuedBatchRequests.TryGetValue(requestKey, out var queuedRequest)
            && queuedRequest.TryCancel())
        {
            return;
        }

        CancellationRegistriesMissedForTests?.Invoke();
        RememberPendingRequestCancellation(requestKey);
        if (_activeRequests.TryGetValue(requestKey, out cts))
        {
            _ = TryConsumePendingRequestCancellation(requestKey);
            CancelRequestCts(cts);
            return;
        }
        if (_queuedBatchRequests.TryGetValue(requestKey, out queuedRequest))
        {
            // The target can enter the durable registry after the first lookup but before the
            // bounded tombstone insertion. Recheck it independently of tombstone capacity so a
            // full cache cannot discard cancellation for an already-queued batch item (#4545).
            // target は初回 lookup 後、bounded tombstone 挿入前に durable registry へ入り得る。
            // tombstone capacity と独立して再確認し、満杯でも登録済み batch item の cancel を
            // 失わないようにする (#4545)。
            _ = TryConsumePendingRequestCancellation(requestKey);
            if (queuedRequest.TryCancel())
                return;
            if (_activeRequests.TryGetValue(requestKey, out cts))
            {
                CancelRequestCts(cts);
                return;
            }
        }
    }

    private void RememberPendingRequestCancellation(string requestKey)
    {
        var now = _timeProvider.GetUtcNow();
        PrunePendingRequestCancellations(now);
        if (_pendingRequestCancellations.Count < MaxPendingRequestCancellationCount)
            _pendingRequestCancellations[requestKey] = now;
    }

    private bool TryConsumePendingRequestCancellation(string requestKey)
    {
        var now = _timeProvider.GetUtcNow();
        PrunePendingRequestCancellations(now);
        if (!_pendingRequestCancellations.TryGetValue(requestKey, out var cancelledAt))
            return false;
        if (now - cancelledAt > PendingRequestCancellationTtl)
        {
            _pendingRequestCancellations.TryRemove(requestKey, out _);
            return false;
        }

        return _pendingRequestCancellations.TryRemove(requestKey, out _);
    }

    private void PrunePendingRequestCancellations(DateTimeOffset now)
    {
        foreach (var entry in _pendingRequestCancellations)
        {
            if (now - entry.Value > PendingRequestCancellationTtl)
                _pendingRequestCancellations.TryRemove(entry.Key, out _);
        }
    }

    private static void CancelRequestCts(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* completed while cancellation was being delivered. */ }
    }

    private static bool IsCancellationFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _)
            || node is not JsonObject obj)
            return false;

        var method = TryGetStringMember(obj, "method");
        return string.Equals(method, "$/cancelRequest", StringComparison.Ordinal)
            || string.Equals(method, "notifications/cancelled", StringComparison.Ordinal);
    }

    private static bool IsProtocolOrderingBarrierFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _))
            return false;

        if (node is JsonArray batch)
            return batch.Any(IsProtocolOrderingBarrierItem);
        return IsProtocolOrderingBarrierItem(node);
    }

    private static bool IsProtocolOrderingBarrierItem(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return false;

        return TryGetStringMember(obj, "method") switch
        {
            "initialize" or
            "logging/setLevel" or
            "notifications/initialized" or
            "notifications/roots/list_changed" or
            "notifications/shutdown" or
            "notifications/exit" => true,
            _ => false,
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
    private JsonNode HandleInitialize(
        JsonNode? id,
        JsonNode? _params,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var negotiated = NegotiateProtocolVersion(_params, out var requestedVersion);
        if (negotiated == null)
        {
            // No overlap between the client's requested version and this server's supported
            // set. Issue #1554: respond with structured `-32602` (invalid params) carrying the
            // requested + supported versions in `error.data` so clients can branch on it
            // instead of guessing why the handshake silently failed. Reject before committing
            // any client/session snapshot so a failed re-initialize cannot corrupt the active
            // session (#4536, #4540).
            // クライアント要求バージョンとサーバー対応集合に重なりがない場合。Issue #1554:
            // クライアントが分岐判定できるよう、`error.data` に要求バージョンと対応バージョン
            // を入れた -32602 (invalid params) を返す。client/session snapshot の commit 前に
            // 拒否し、失敗した re-initialize で有効 session を壊さない (#4536, #4540)。
            DeferFrameLog(BuildUnsupportedProtocolLog(requestedVersion));
            return CreateUnsupportedProtocolError(id, requestedVersion);
        }

        // Parse caller-controlled identity, capability, and root metadata into a detached
        // draft. None of it becomes observable session state until protocol negotiation and
        // complete success-response serialization have both succeeded (#4540).
        // caller が制御する identity / capability / root metadata は切り離した draft へ解析する。
        // protocol 交渉と success response の serialization が完了するまで公開しない (#4540)。
        var initializeState = BuildInitializeState(_params);
        var result = new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                },
                ["resources"] = new JsonObject
                {
                    ["subscribe"] = false,
                    ["listChanged"] = false
                },
                ["prompts"] = new JsonObject
                {
                    ["listChanged"] = false
                },
                ["logging"] = new JsonObject(),
                ["roots"] = new JsonObject
                {
                    ["listChanged"] = true
                },
                ["sampling"] = new JsonObject()
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
        var response = CreateSuccessResponse(true, id, result);
        if (deferredInitializeCommits is null)
            CommitInitializeState(initializeState);
        else
            deferredInitializeCommits.Register(response, initializeState);
        return response;
    }

    /// <summary>
    /// Build a detached snapshot of caller-controlled initialize metadata. The caller must
    /// commit this snapshot only after protocol negotiation and success-response serialization succeed.
    /// caller が制御する initialize metadata の切り離した snapshot を構築する。呼び出し元は
    /// protocol 交渉と success response の serialization 成功後に限って commit すること。
    /// </summary>
    private PendingInitializeState BuildInitializeState(JsonNode? initializeParams)
    {
        BoundedMcpText? clientNameDisplay = null;
        BoundedMcpText? clientVersionDisplay = null;
        JsonNode? clientCapabilities = null;
        int? clientCapabilitiesSerializedBytes = null;
        string? clientCapabilitiesTruncationReason = null;
        var clientSupportsRoots = false;
        var clientSupportsSampling = false;
        var clientRoots = new List<string>();
        var clientRootDiagnostics = new List<string>();
        var clientRootsTruncated = false;
        var markClientRootsStale = false;

        if (initializeParams is JsonObject obj)
        {
            markClientRootsStale = true;
            if (obj["clientInfo"] is JsonObject info)
            {
                clientNameDisplay = TryReadBoundedClientInfoMember(info, "name");
                clientVersionDisplay = TryReadBoundedClientInfoMember(info, "version");
            }

            if (!obj.TryGetPropertyValue("capabilities", out var capabilities))
                obj.TryGetPropertyValue("clientCapabilities", out capabilities);
            if (capabilities is not null)
            {
                if (capabilities is JsonObject capabilitiesObject)
                {
                    clientSupportsRoots = capabilitiesObject.TryGetPropertyValue("roots", out var rootsCapability)
                        && rootsCapability is not null;
                    clientSupportsSampling = capabilitiesObject.TryGetPropertyValue("sampling", out var samplingCapability)
                        && samplingCapability is not null;
                }

                if (!TryMeasureJsonUtf8BytesWithinLimit(capabilities, _jsonOptions, MaxClientCapabilitiesJsonBytes, out var serializedBytes))
                {
                    clientCapabilitiesSerializedBytes = serializedBytes;
                    clientCapabilities = new JsonObject();
                    clientCapabilitiesTruncationReason = "byte_limit";
                }
                else
                {
                    clientCapabilitiesSerializedBytes = serializedBytes;
                    if (!IsJsonNodeDepthWithinLimit(capabilities, MaxClientCapabilitiesDepth))
                    {
                        clientCapabilities = new JsonObject();
                        clientCapabilitiesTruncationReason = "depth_limit";
                    }
                    else
                    {
                        clientCapabilities = McpJsonNode.Clone(capabilities);
                    }
                }
            }

            void AddRoot(string uri)
            {
                clientRoots.Add(uri);
                if (clientRootDiagnostics.Count >= MaxClientRootCount)
                {
                    clientRootsTruncated = true;
                    return;
                }

                var display = McpBoundedText.ForDisplay(uri, MaxClientRootUriChars);
                clientRootDiagnostics.Add(display.Text);
                clientRootsTruncated |= display.Truncated;
            }

            if (TryReadStringValue(obj["rootUri"]) is { Length: > 0 } rootUri)
                AddRoot(rootUri);

            if (obj["roots"] is JsonArray roots)
            {
                foreach (var root in roots)
                {
                    var uri = TryReadStringValue(root?["uri"]) ?? TryReadStringValue(root);
                    if (!string.IsNullOrWhiteSpace(uri))
                        AddRoot(uri);
                }
            }
        }

        return new PendingInitializeState(
            ResolveCallerIdentity(initializeParams),
            markClientRootsStale,
            clientNameDisplay,
            clientVersionDisplay,
            clientCapabilities,
            clientCapabilitiesSerializedBytes,
            clientCapabilitiesTruncationReason,
            clientSupportsRoots,
            clientSupportsSampling,
            clientRoots.ToArray(),
            clientRootDiagnostics.ToArray(),
            clientRootsTruncated);
    }

    private void CommitInitializeState(PendingInitializeState state)
    {
        lock (_initializeStateGate)
        {
            var committed = BuildCommittedInitializeState(
                PublishedInitializeState,
                state,
                logCallerSwap: true);

            // One release publication makes lifecycle and all negotiated metadata visible
            // together; no reader can observe initialized=true with a partial state (#4540).
            // lifecycle と交渉済み metadata を 1 回の release publication で同時に公開し、
            // initialized=true と部分的な state の組み合わせを reader に見せない (#4540)。
            Volatile.Write(ref _initializeState, committed);
        }
    }

    private InitializeSessionState BuildCommittedInitializeState(
        InitializeSessionState previous,
        PendingInitializeState state,
        bool logCallerSwap)
    {
        var caller = previous.Caller;

        // Caller stickiness: allow upgrading from the default "unknown" bucket to a named
        // identity, but reject successful re-initialize attempts that swap named identities.
        // caller の sticky 制御: "unknown" から名前付き ID への昇格だけを許可し、成功した
        // re-initialize による名前付き ID 同士のスワップは拒否する。
        if (caller == "unknown")
        {
            caller = state.ResolvedCaller;
        }
        else if (state.ResolvedCaller != caller && state.ResolvedCaller != "unknown" && logCallerSwap)
        {
            DeferFrameLog(BuildCallerSwapRejectionLog(caller, state.ResolvedCaller));
        }

        return new InitializeSessionState(
            true,
            caller,
            state.ClientNameDisplay,
            state.ClientVersionDisplay,
            state.ClientCapabilities,
            state.ClientCapabilitiesSerializedBytes,
            state.ClientCapabilitiesTruncationReason,
            state.ClientSupportsRoots,
            state.ClientSupportsSampling,
            state.ClientRoots.ToArray(),
            state.ClientRootDiagnostics.ToArray(),
            state.ClientRootsTruncated,
            state.MarkClientRootsStale || previous.ClientRootsStale);
    }

    private sealed record InitializeSessionState(
        bool Initialized,
        string Caller,
        BoundedMcpText? ClientNameDisplay,
        BoundedMcpText? ClientVersionDisplay,
        JsonNode? ClientCapabilities,
        int? ClientCapabilitiesSerializedBytes,
        string? ClientCapabilitiesTruncationReason,
        bool ClientSupportsRoots,
        bool ClientSupportsSampling,
        string[] ClientRoots,
        string[] ClientRootDiagnostics,
        bool ClientRootsTruncated,
        bool ClientRootsStale)
    {
        internal static InitializeSessionState Empty { get; } = new(
            false,
            "unknown",
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            true);

        internal string? ClientName => ClientNameDisplay?.Text;
        internal string? ClientVersion => ClientVersionDisplay?.Text;
        internal int ClientRootCount => ClientRoots.Length;
    }

    private sealed record PendingInitializeState(
        string ResolvedCaller,
        bool MarkClientRootsStale,
        BoundedMcpText? ClientNameDisplay,
        BoundedMcpText? ClientVersionDisplay,
        JsonNode? ClientCapabilities,
        int? ClientCapabilitiesSerializedBytes,
        string? ClientCapabilitiesTruncationReason,
        bool ClientSupportsRoots,
        bool ClientSupportsSampling,
        string[] ClientRoots,
        string[] ClientRootDiagnostics,
        bool ClientRootsTruncated);

    private sealed class FrameInitializeState
    {
        private readonly object _gate = new();
        private InitializeSessionState _current;
        private int _acceptedRootsChange;

        internal FrameInitializeState(
            InitializeSessionState current,
            bool isProvisionalGeneration)
        {
            _current = current;
            IsProvisionalGeneration = isProvisionalGeneration;
        }

        internal bool IsProvisionalGeneration { get; }
        internal InitializeSessionState Current => Volatile.Read(ref _current);

        internal void MarkRootsChangeAccepted()
            => Volatile.Write(ref _acceptedRootsChange, 1);

        internal bool TryConsumeAcceptedRootsChange()
            => Interlocked.Exchange(ref _acceptedRootsChange, 0) != 0;

        internal bool TryAdvanceToPublishedGeneration(
            InitializeSessionState expectedState,
            InitializeSessionState publishedState)
        {
            if (IsProvisionalGeneration)
                return false;

            lock (_gate)
            {
                if (!ReferenceEquals(Current, expectedState))
                    return false;

                Volatile.Write(ref _current, publishedState);
                return true;
            }
        }

        internal bool TryRefreshClientRoots(
            InitializeSessionState expectedState,
            ClientRootSnapshot refreshedRoots)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(Current, expectedState))
                    return false;

                Volatile.Write(
                    ref _current,
                    expectedState with
                    {
                        ClientRoots = refreshedRoots.Roots.ToArray(),
                        ClientRootDiagnostics = refreshedRoots.Diagnostics.ToArray(),
                        ClientRootsTruncated = refreshedRoots.Truncated,
                        ClientRootsStale = false,
                    });
                return true;
            }
        }
    }

    /// <summary>
    /// Tracks initialize drafts for one wire frame until the exact success response that owns
    /// each draft has been serialized. The collection is frame-local but synchronized because
    /// isolated request dispatch can finish on a worker after its caller has timed out.
    /// initialize draft を wire frame 単位で追跡し、対応する success response の serialization
    /// 成功後にだけ commit する。timeout 後も worker が完了し得るため collection は同期する。
    /// </summary>
    private sealed class DeferredInitializeCommits
    {
        private readonly object _gate = new();
        private readonly List<Entry> _entries = [];

        internal void Register(JsonNode response, PendingInitializeState state)
        {
            lock (_gate)
                _entries.Add(new Entry(response, state));
        }

        internal bool TryGetRegisteredState(JsonNode response, out PendingInitializeState state)
        {
            lock (_gate)
            {
                foreach (var entry in _entries)
                {
                    if (!ReferenceEquals(entry.Response, response))
                        continue;

                    state = entry.State;
                    return true;
                }
            }

            state = null!;
            return false;
        }

        internal PendingInitializeState[] GetIncludedStates(JsonNode serializedResponse)
        {
            lock (_gate)
            {
                return _entries
                    .Where(entry => IsIncludedResponse(serializedResponse, entry.Response))
                    .Select(entry => entry.State)
                    .ToArray();
            }
        }

        private static bool IsIncludedResponse(JsonNode serializedResponse, JsonNode candidate)
        {
            if (ReferenceEquals(serializedResponse, candidate))
                return true;

            if (serializedResponse is not JsonArray batchResponse)
                return false;

            foreach (var item in batchResponse)
            {
                if (ReferenceEquals(item, candidate))
                    return true;
            }

            return false;
        }

        private sealed record Entry(JsonNode Response, PendingInitializeState State);
    }

    private static bool IsJsonNodeDepthWithinLimit(JsonNode node, int maxDepth)
        => IsJsonNodeDepthWithinLimit(node, depth: 0, maxDepth);

    private static bool IsJsonNodeDepthWithinLimit(JsonNode? node, int depth, int maxDepth)
    {
        if (node is null)
            return true;
        if (depth > maxDepth)
            return false;

        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (!IsJsonNodeDepthWithinLimit(kvp.Value, depth + 1, maxDepth))
                    return false;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (!IsJsonNodeDepthWithinLimit(item, depth + 1, maxDepth))
                    return false;
            }
        }

        return true;
    }

    private static ClientRootSnapshot BuildClientRootSnapshot(IEnumerable<string> roots)
    {
        var capturedRoots = new List<string>();
        var diagnostics = new List<string>();
        var truncated = false;
        foreach (var uri in roots)
        {
            capturedRoots.Add(uri);
            if (diagnostics.Count >= MaxClientRootCount)
            {
                truncated = true;
                continue;
            }

            var display = McpBoundedText.ForDisplay(uri, MaxClientRootUriChars);
            diagnostics.Add(display.Text);
            truncated |= display.Truncated;
        }

        return new ClientRootSnapshot(capturedRoots.ToArray(), diagnostics.ToArray(), truncated);
    }

    private void MarkClientRootsStale()
    {
        lock (_initializeStateGate)
        {
            var current = PublishedInitializeState;
            // Always replace the reference, even when already stale, so a notification that
            // races an in-flight roots/list refresh invalidates that refresh's expected state.
            // 既に stale でも必ず reference を置き換え、進行中の roots/list refresh と競合した
            // notification がその refresh の expected state を無効化できるようにする。
            Volatile.Write(ref _initializeState, current with { ClientRootsStale = true });
        }
    }

    private sealed record ClientRootSnapshot(string[] Roots, string[] Diagnostics, bool Truncated);

    internal JsonNode? ClientCapabilitiesForTests
    {
        get
        {
            var state = CurrentInitializeState;
            return McpJsonNode.Clone(state.ClientCapabilities);
        }
    }

    internal string[] ClientRootsForTests
    {
        get
        {
            var state = CurrentInitializeState;
            return state.ClientRoots.ToArray();
        }
    }

    internal bool ClientSupportsRootsForTests => CurrentInitializeState.ClientSupportsRoots;

    internal bool ClientSupportsSamplingForTests => CurrentInitializeState.ClientSupportsSampling;

    internal bool ClientRootsStaleForTests
    {
        get => CurrentInitializeState.ClientRootsStale;
        set
        {
            lock (_initializeStateGate)
            {
                var current = PublishedInitializeState;
                Volatile.Write(ref _initializeState, current with { ClientRootsStale = value });
            }
        }
    }

    internal string McpLogLevelForTests => _mcpLogLevel;

    internal Func<string, JsonObject?, JsonNode?>? ClientRequestHandlerForTests { get; set; }

    private static string? TryReadStringMember(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
            return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            return s.Trim();
        return null;
    }

    private static BoundedMcpText? TryReadBoundedClientInfoMember(JsonObject obj, string key)
    {
        var value = TryReadStringMember(obj, key);
        return value is null ? null : BoundClientInfoForDisplay(value);
    }

    private JsonNode HandleResourcesList(JsonNode? id, JsonNode? listParams)
    {
        var requestedMaxBytes = DefaultResourceListMaxBytes;
        if (listParams?["maxBytes"] is JsonNode maxBytesNode)
        {
            if (maxBytesNode is not JsonValue maxBytesValue
                || !maxBytesValue.TryGetValue<int>(out requestedMaxBytes)
                || requestedMaxBytes < MinResourceListMaxBytes
                || requestedMaxBytes > MaxResourceListMaxBytes)
            {
                return CreateResourcesListMaxBytesError(id);
            }
        }
        var effectiveMaxBytes = Math.Min(requestedMaxBytes, GetMaxResponseBytes());
        var activeTransportMaxResponseBytes = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (activeTransportMaxResponseBytes > 0)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, activeTransportMaxResponseBytes);
        if (_currentBatchResponseItemMaxBytes.Value is { } batchResponseItemMaxBytes)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, batchResponseItemMaxBytes);

        long? afterFileId = null;
        long? expectedGeneration = null;
        var legacyOffset = 0;
        if (listParams?["cursor"] is JsonNode cursorNode)
        {
            if (cursorNode is not JsonValue cursorValue
                || !cursorValue.TryGetValue<string>(out var cursor))
            {
                return CreateResourcesListCursorError(id);
            }

            if (cursor.Length > MaxResourceListCursorChars)
                return CreateResourcesListCursorError(id);

            if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLegacyOffset))
            {
                if (parsedLegacyOffset < 0 || parsedLegacyOffset > MaxMcpPaginationOffset)
                    return CreateResourcesListCursorError(id);
                if (parsedLegacyOffset != 0)
                    return CreateResourcesListRestartError(id);
            }
            else if (TryDecodeResourceListCursor(cursor, out var decodedCursor))
            {
                afterFileId = decodedCursor.AfterFileId;
                expectedGeneration = decodedCursor.Generation;
            }
            else
            {
                return CreateResourcesListCursorError(id);
            }
        }

        return WithDbReader(id, args: null, reader =>
        {
            var resourcePage = reader.ListResourceFiles(
                limit: ResourceListPageSize + 1,
                afterFileId: afterFileId,
                expectedGeneration: expectedGeneration,
                legacyOffset: legacyOffset);
            if (resourcePage.GenerationTrackingUnavailable)
                return CreateResourcesListGenerationUnavailableError(id);
            if (resourcePage.CursorRestartRequired)
                return CreateResourcesListRestartError(id);

            var page = resourcePage.Files.Take(ResourceListPageSize).ToArray();
            var resources = new JsonArray();
            var reservedResponse = CreateResourceListResponse(
                id,
                resources: [],
                generation: long.MaxValue,
                lastConsumedFileId: long.MaxValue,
                hasContinuation: true,
                requestedMaxBytes: MaxResourceListMaxBytes,
                effectiveMaxBytes: MaxResourceListMaxBytes,
                candidatesConsumed: ResourceListPageSize,
                uriTooLongCount: ResourceListPageSize,
                resourceExceedsMaxBytesCount: ResourceListPageSize,
                byteBudgetReached: true);
            _ = TryMeasureJsonUtf8BytesWithinLimit(
                reservedResponse,
                _jsonOptions,
                int.MaxValue,
                out var reservedResponseBytes);
            if (reservedResponseBytes > effectiveMaxBytes)
                return CreateResourcesListEffectiveMaxBytesError(id, requestedMaxBytes, effectiveMaxBytes);

            var acceptedResourceBytes = 0L;
            var candidatesConsumed = 0;
            var uriTooLongCount = 0;
            var resourceExceedsMaxBytesCount = 0;
            var byteBudgetReached = false;
            var stoppedForByteBudget = false;
            long? lastConsumedFileId = null;
            foreach (var file in page)
            {
                var uri = BuildResourceUri(file.Path);
                if (uri.Length > McpBoundedText.MaxResourceUriChars)
                {
                    uriTooLongCount++;
                    candidatesConsumed++;
                    lastConsumedFileId = file.Id;
                    continue;
                }

                var resource = new JsonObject
                {
                    ["uri"] = uri,
                    ["name"] = file.Path,
                    ["description"] = $"{file.Path} ({file.Lang ?? "unknown"}, {file.Lines} lines)",
                    ["mimeType"] = GetResourceMimeType(file.Lang),
                };
                var resourceFitsAlone = TryMeasureJsonUtf8BytesWithinLimit(
                    resource,
                    _jsonOptions,
                    effectiveMaxBytes,
                    out var resourceBytes);
                var commaBytes = resources.Count == 0 ? 0 : 1;
                var resourceFitsEmptyPage = resourceFitsAlone
                    && reservedResponseBytes + resourceBytes <= effectiveMaxBytes;
                var resourceFitsPage = resourceFitsEmptyPage
                    && reservedResponseBytes + acceptedResourceBytes + commaBytes + resourceBytes <= effectiveMaxBytes;
                if (!resourceFitsPage)
                {
                    byteBudgetReached = true;
                    if (resourceFitsEmptyPage || resources.Count > 0)
                    {
                        stoppedForByteBudget = true;
                        break;
                    }

                    // Consume resources that cannot fit even on an empty page so the cursor cannot livelock.
                    // 空ページにも収まらない resource は消費・報告し、cursor の livelock を防ぐ。
                    resourceExceedsMaxBytesCount++;
                    candidatesConsumed++;
                    lastConsumedFileId = file.Id;
                    continue;
                }

                resources.Add(resource);
                acceptedResourceBytes += commaBytes + resourceBytes;
                candidatesConsumed++;
                lastConsumedFileId = file.Id;
            }

            var hasContinuation = stoppedForByteBudget || resourcePage.Files.Count > ResourceListPageSize;
            var response = CreateResourceListResponse(
                id,
                resources,
                resourcePage.Generation,
                lastConsumedFileId,
                hasContinuation,
                requestedMaxBytes,
                effectiveMaxBytes,
                candidatesConsumed,
                uriTooLongCount,
                resourceExceedsMaxBytesCount,
                byteBudgetReached);

            if (!TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, effectiveMaxBytes, out _))
                return CreateResourcesListEffectiveMaxBytesError(id, requestedMaxBytes, effectiveMaxBytes);
            return response;
        });
    }

    private static JsonObject CreateResourcesListMaxBytesError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: $"resources/list maxBytes must be between {MinResourceListMaxBytes} and {MaxResourceListMaxBytes}.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use an integer params.maxBytes within the documented range, or omit it to use the default.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["min_max_bytes"] = MinResourceListMaxBytes,
                ["max_max_bytes"] = MaxResourceListMaxBytes,
                ["default_max_bytes"] = DefaultResourceListMaxBytes,
            });

    private static JsonObject CreateResourcesListEffectiveMaxBytesError(
        JsonNode? id,
        int requestedMaxBytes,
        int effectiveMaxBytes)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "resources/list response metadata does not fit within the effective byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Raise the MCP response byte limit or request a larger params.maxBytes value.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["requested_max_bytes"] = requestedMaxBytes,
                ["effective_max_bytes"] = effectiveMaxBytes,
            });

    private static JsonObject CreateResourceListResponse(
        JsonNode? id,
        JsonArray resources,
        long generation,
        long? lastConsumedFileId,
        bool hasContinuation,
        int requestedMaxBytes,
        int effectiveMaxBytes,
        int candidatesConsumed,
        int uriTooLongCount,
        int resourceExceedsMaxBytesCount,
        bool byteBudgetReached)
    {
        var result = new JsonObject
        {
            ["resources"] = resources,
            ["_meta"] = new JsonObject
            {
                ["response_controls"] = CreateResourceListResponseControls(
                    requestedMaxBytes,
                    effectiveMaxBytes,
                    candidatesConsumed,
                    resources.Count,
                    uriTooLongCount,
                    resourceExceedsMaxBytesCount,
                    byteBudgetReached,
                    hasContinuation),
            },
        };
        if (hasContinuation && lastConsumedFileId is not null)
            result["nextCursor"] = EncodeResourceListCursor(generation, lastConsumedFileId.Value);
        return CreateSuccessResponse(true, id, result);
    }

    private static JsonObject CreateResourceListResponseControls(
        int requestedMaxBytes,
        int effectiveMaxBytes,
        int candidatesConsumed,
        int resourcesReturned,
        int uriTooLongCount,
        int resourceExceedsMaxBytesCount,
        bool byteBudgetReached,
        bool hasContinuation)
        => new()
        {
            ["requested_max_bytes"] = requestedMaxBytes,
            ["effective_max_bytes"] = effectiveMaxBytes,
            ["page_item_limit"] = ResourceListPageSize,
            ["resource_candidates_consumed"] = candidatesConsumed,
            ["resources_returned"] = resourcesReturned,
            ["omitted_resource_count"] = uriTooLongCount + resourceExceedsMaxBytesCount,
            ["omitted_resource_reason_counts"] = new JsonObject
            {
                ["resource_uri_too_long"] = uriTooLongCount,
                ["resource_exceeds_max_bytes"] = resourceExceedsMaxBytesCount,
            },
            ["byte_budget_reached"] = byteBudgetReached,
            ["continuation_reason"] = hasContinuation
                ? byteBudgetReached ? "byte_budget" : "item_limit"
                : "completed",
        };

    private static JsonObject CreateResourcesListCursorError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "resources/list cursor is invalid or unsupported.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use the `nextCursor` value returned by the previous resources/list response, or omit params.cursor to start from the first page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["max_cursor_length"] = MaxResourceListCursorChars,
                ["max_legacy_pagination_offset"] = MaxMcpPaginationOffset,
            });

    private static JsonObject CreateResourcesListRestartError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeIndexStale,
            message: "The indexed file set changed after this resources/list cursor was issued.",
            category: McpErrorEnvelope.CategoryIndexStale,
            suggestion: "Omit params.cursor and restart resources/list from the first page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "resources_list_generation_changed",
                ["restart_required"] = true,
            });

    private static JsonObject CreateResourcesListGenerationUnavailableError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeIndexStale,
            message: "This database cannot prove a stable resources/list generation.",
            category: McpErrorEnvelope.CategoryIndexStale,
            suggestion: "Open the database on writable storage and run `cdidx index <projectPath>` with the current cdidx to install generation tracking. Use an `immutable=1` URI only for a snapshot guaranteed not to change.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "resources_list_generation_unavailable",
                ["migration_required"] = true,
                ["restart_required"] = false,
            });

    private static string EncodeResourceListCursor(long generation, long afterFileId)
    {
        Span<byte> payload = stackalloc byte[ResourceListCursorPayloadBytes];
        payload[0] = ResourceListCursorVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..9], generation);
        BinaryPrimitives.WriteInt64BigEndian(payload[9..17], afterFileId);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeResourceListCursor(string cursor, out ResourceListCursor decoded)
    {
        decoded = default;
        if (cursor.Length != MaxResourceListCursorChars
            || cursor.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            return false;
        }

        Span<char> base64 = stackalloc char[MaxResourceListCursorChars + 1];
        for (var i = 0; i < cursor.Length; i++)
        {
            base64[i] = cursor[i] switch
            {
                '-' => '+',
                '_' => '/',
                _ => cursor[i],
            };
        }
        base64[^1] = '=';

        Span<byte> payload = stackalloc byte[ResourceListCursorPayloadBytes];
        if (!Convert.TryFromBase64Chars(base64, payload, out var bytesWritten)
            || bytesWritten != ResourceListCursorPayloadBytes
            || payload[0] != ResourceListCursorVersion)
        {
            return false;
        }

        var generation = BinaryPrimitives.ReadInt64BigEndian(payload[1..9]);
        var afterFileId = BinaryPrimitives.ReadInt64BigEndian(payload[9..17]);
        if (generation < 0 || afterFileId <= 0)
            return false;

        decoded = new ResourceListCursor(generation, afterFileId);
        return true;
    }

    private readonly record struct ResourceListCursor(long Generation, long AfterFileId);

    private JsonNode HandleResourcesRead(JsonNode? id, JsonNode? readParams)
    {
        var uri = TryReadStringValue(readParams?["uri"]);
        if (string.IsNullOrWhiteSpace(uri))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing resource uri",
                category: McpErrorEnvelope.CategoryMissingParameter,
                suggestion: "resources/read requires `params.uri` from resources/list, such as `cdidx://file/src/app.cs`.",
                retrySafe: false);
        if (uri.Length > McpBoundedText.MaxResourceUriChars)
            return CreateResourceUriError(id, uri, messagePrefix: "Resource uri is too long",
                suggestion: "Use a resource URI returned by resources/list and keep it within the documented MCP resource URI length limit.",
                retrySafe: false,
                includeLengthLimit: true);

        if (!TryParseResourceUri(uri, out var path))
            return CreateResourceUriError(id, uri, messagePrefix: "Invalid resource uri",
                suggestion: "Use a cdidx file resource URI returned by resources/list (`cdidx://file/<indexed-path>`).",
                retrySafe: false);

        if (!TryReadOptionalResourceReadInteger(readParams, "startLine", out var requestedStartLine))
            return CreateResourceReadArgumentError(id, "startLine",
                "resources/read params.startLine must be a positive integer.",
                "Pass a 1-based line number, or omit startLine to begin at line 1.");
        if (!TryReadOptionalResourceReadInteger(readParams, "endLine", out var requestedEndLine))
            return CreateResourceReadArgumentError(id, "endLine",
                "resources/read params.endLine must be a positive integer.",
                "Pass an inclusive 1-based line number greater than or equal to startLine, or omit endLine to read through the resource.");
        if (!TryReadOptionalResourceReadInteger(readParams, "maxBytes", out var requestedMaxBytes))
            return CreateResourceReadArgumentError(id, "maxBytes",
                "resources/read params.maxBytes must be an integer.",
                $"Pass a UTF-8 text budget between {MinResourceReadMaxBytes} and {MaxResourceReadMaxBytes} bytes.");
        if (!TryReadOptionalResourceReadString(readParams, "cursor", out var cursorText))
            return CreateResourceReadArgumentError(id, "cursor",
                "resources/read params.cursor must be a non-empty string.",
                "Use the nextCursor returned in result._meta, or omit cursor to start a new range.");

        if (requestedStartLine is <= 0)
            return CreateResourceReadIntegerRangeError(id, "startLine", 1, int.MaxValue, requestedStartLine.Value);
        if (requestedEndLine is <= 0)
            return CreateResourceReadIntegerRangeError(id, "endLine", 1, int.MaxValue, requestedEndLine.Value);
        if (requestedStartLine.HasValue && requestedEndLine.HasValue && requestedEndLine.Value < requestedStartLine.Value)
            return CreateResourceReadArgumentError(id, "endLine",
                "resources/read params.endLine must be greater than or equal to params.startLine.",
                "Increase endLine or start a new range with matching 1-based boundaries.");

        var maxBytes = requestedMaxBytes ?? DefaultResourceReadMaxBytes;
        if (maxBytes < MinResourceReadMaxBytes || maxBytes > MaxResourceReadMaxBytes)
            return CreateResourceReadIntegerRangeError(id, "maxBytes", MinResourceReadMaxBytes, MaxResourceReadMaxBytes, maxBytes);

        ResourceReadCursor? cursor = null;
        if (cursorText is not null)
        {
            if (requestedStartLine.HasValue || requestedEndLine.HasValue)
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor cannot be combined with startLine or endLine.",
                    "Continue with cursor and an optional maxBytes value, or omit cursor to start a new line range.");
            if (cursorText.Length > MaxResourceReadCursorCharacters || !TryParseResourceReadCursor(cursorText, out var parsedCursor))
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor is invalid or expired.",
                    "Use the exact nextCursor returned by the previous resources/read response, or omit cursor to restart the range.",
                    new JsonObject
                    {
                        ["maxCursorCharacters"] = MaxResourceReadCursorCharacters,
                    });
            cursor = parsedCursor;
        }

        return WithDbReader(id, args: null, reader => reader.RunInReadSnapshot(() =>
        {
            var file = reader.GetResourceFileMetadata(path);
            if (file == null)
                return CreateResourceUriError(id, uri, messagePrefix: "Resource not found",
                    suggestion: "Call resources/list again and retry with one of the returned resource URIs.",
                    retrySafe: true);

            var fingerprint = BuildResourceReadFingerprint(file.Path, file.Checksum, file.Size, file.Lines, file.Modified);
            if (cursor is { } suppliedCursor && !string.Equals(suppliedCursor.Fingerprint, fingerprint, StringComparison.Ordinal))
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor no longer matches the indexed resource.",
                    "The resource changed after the previous page. Omit cursor and restart the range to avoid skipped or duplicated text.",
                    new JsonObject
                    {
                        ["cursorStale"] = true,
                    });

            ResourceReadMetadataLoadedForTests?.Invoke();

            var isEmpty = file.Size >= 0
                          && DbReader.IsAffirmativelyEmptyIndexedFile(file.Lines, file.Checksum);
            var totalLines = Math.Max(0, file.Lines);
            var hasReadableLines = !isEmpty && file.Lines > 0;
            if (isEmpty && cursor.HasValue)
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor does not identify a readable position in this empty resource.",
                    "Omit cursor and restart the resource read without line boundaries.");

            var startLine = isEmpty ? 0 : hasReadableLines ? cursor?.Line ?? requestedStartLine ?? 1 : 1;
            var endLine = isEmpty ? 0 : hasReadableLines ? cursor?.EndLine ?? requestedEndLine ?? totalLines : 1;
            if (hasReadableLines && startLine > totalLines)
                return CreateResourceReadArgumentError(id, "startLine",
                    $"resources/read params.startLine exceeds the resource line count ({file.Lines}).",
                    "Use a startLine from resources/read result._meta or restart at line 1.",
                    new JsonObject
                    {
                        ["totalLines"] = file.Lines,
                    });
            if (hasReadableLines)
                endLine = Math.Min(endLine, totalLines);
            if (hasReadableLines && endLine < startLine)
                return CreateResourceReadArgumentError(id, "endLine",
                    "resources/read effective endLine is before startLine.",
                    "Restart the range with an endLine greater than or equal to startLine.");

            var resourceUri = BuildResourceUri(file.Path);
            var mimeType = GetResourceMimeType(file.Lang);
            var effectiveMaxBytes = GetEffectiveResourceReadMaxBytes(
                id,
                resourceUri,
                mimeType,
                maxBytes);
            if (effectiveMaxBytes < MinResourceReadMaxBytes)
                return CreateErrorResponse(hasId: true, id: id, code: -32603,
                    message: "The configured MCP response limit is too small for a resources/read page.",
                    category: McpErrorEnvelope.CategoryInternalError,
                    suggestion: "Use a smaller JSON-RPC batch, or increase CDIDX_MCP_RESPONSE_MAX_BYTES or CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES, then retry.",
                    retrySafe: false,
                    extraData: new JsonObject
                    {
                        ["reason"] = "resource_response_budget_too_small",
                        ["minimumContentBytes"] = MinResourceReadMaxBytes,
                        ["responseLimitBytes"] = GetEffectiveResourceReadResponseLimit(),
                    });

            var page = reader.GetBoundedFileContent(
                file,
                isEmpty ? 1 : startLine,
                isEmpty ? 1 : endLine,
                effectiveMaxBytes,
                MaxResourceReadLinesPerPage,
                hasReadableLines ? cursor?.Line : null,
                hasReadableLines ? cursor?.ByteOffset ?? 0 : 0);
            switch (page.Status)
            {
                case BoundedFileReadStatus.FileNotFound:
                    return CreateResourceUriError(id, uri, messagePrefix: "Resource not found",
                        suggestion: "Call resources/list again and retry with one of the returned resource URIs.",
                        retrySafe: true);
                case BoundedFileReadStatus.InvalidContinuation:
                    return CreateResourceReadArgumentError(id, "cursor",
                        "resources/read params.cursor does not identify a readable UTF-8 position in this resource.",
                        "Omit cursor and restart the range to obtain a fresh continuation token.");
                case BoundedFileReadStatus.IncompleteCoverage:
                case BoundedFileReadStatus.ContentUnavailable:
                case BoundedFileReadStatus.InvalidTopology:
                    return CreateResourceReadStorageError(id, page.Status, page.FailureReason);
            }

            var text = page.Content;
            var returnedBytes = page.Utf8Bytes;
            var truncated = page.Truncated && page.NextLine.HasValue;
            var metadata = new JsonObject
            {
                ["startLine"] = startLine,
                ["startLineByteOffset"] = cursor?.ByteOffset ?? 0,
                ["endLine"] = endLine,
                ["totalLines"] = totalLines,
                ["maxBytes"] = maxBytes,
                ["maxLines"] = MaxResourceReadLinesPerPage,
                ["returnedStartLine"] = isEmpty ? 0 : page.StartLine,
                ["returnedEndLine"] = isEmpty ? 0 : page.EndLine,
                ["returnedBytes"] = returnedBytes,
                ["truncated"] = truncated,
            };
            if (effectiveMaxBytes != maxBytes)
                metadata["effectiveMaxBytes"] = effectiveMaxBytes;
            if (truncated)
            {
                metadata["truncationReason"] = page.TruncationReason switch
                {
                    "max_lines" => "maxLines",
                    "max_bytes" when effectiveMaxBytes < maxBytes => "maxResponseBytes",
                    _ => "maxBytes",
                };
                metadata["nextLine"] = page.NextLine!.Value;
                metadata["nextLineByteOffset"] = page.NextByteOffset ?? 0;
                metadata["nextCursor"] = BuildResourceReadCursor(
                    page.NextLine.Value,
                    page.NextByteOffset ?? 0,
                    endLine,
                    fingerprint);
            }

            var contents = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = resourceUri,
                    ["mimeType"] = mimeType,
                    ["text"] = text,
                }
            };
            return CreateSuccessResponse(true, id, new JsonObject
            {
                ["contents"] = contents,
                ["_meta"] = metadata,
            });
        }));
    }

    private JsonObject CreateResourceReadStorageError(
        JsonNode? id,
        BoundedFileReadStatus status,
        string? reason)
    {
        var normalizedReason = reason ?? status switch
        {
            BoundedFileReadStatus.IncompleteCoverage => "resource_chunk_coverage_incomplete",
            BoundedFileReadStatus.ContentUnavailable => "resource_content_unavailable",
            _ => "resource_chunk_topology_invalid",
        };
        var extraData = new JsonObject
        {
            ["reason"] = normalizedReason,
        };
        if (status == BoundedFileReadStatus.InvalidTopology)
        {
            extraData["maxChunks"] = DbReader.MaxBoundedFileReadChunks;
            extraData["maxScannedBytes"] = DbReader.MaxBoundedFileReadScannedUtf8Bytes;
        }

        return status switch
        {
            BoundedFileReadStatus.IncompleteCoverage => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexStale,
                message: "Indexed resource chunks do not cover the requested range.",
                category: McpErrorEnvelope.CategoryIndexStale,
                suggestion: "Refresh or rebuild the index, then call resources/list and retry the read.",
                retrySafe: true,
                extraData: extraData),
            BoundedFileReadStatus.ContentUnavailable => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexMissing,
                message: "Indexed content is unavailable for this non-empty resource.",
                category: McpErrorEnvelope.CategoryIndexMissing,
                suggestion: "Inspect file issues, resolve skipped-content diagnostics, and rebuild the index before retrying.",
                retrySafe: true,
                extraData: extraData),
            _ => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexCorrupted,
                message: "Indexed resource storage metadata is inconsistent or exceeds safe read limits.",
                category: McpErrorEnvelope.CategoryIndexCorrupted,
                suggestion: "Delete the index database, rebuild it, and retry with a resource URI from resources/list.",
                retrySafe: false,
                extraData: extraData),
        };
    }

    private int GetEffectiveResourceReadMaxBytes(
        JsonNode? id,
        string resourceUri,
        string mimeType,
        int requestedMaxBytes)
    {
        var worstCaseMetadata = new JsonObject
        {
            ["startLine"] = int.MaxValue,
            ["startLineByteOffset"] = int.MaxValue,
            ["endLine"] = int.MaxValue,
            ["totalLines"] = int.MaxValue,
            ["maxBytes"] = requestedMaxBytes,
            ["effectiveMaxBytes"] = int.MaxValue,
            ["maxLines"] = MaxResourceReadLinesPerPage,
            ["returnedStartLine"] = int.MaxValue,
            ["returnedEndLine"] = int.MaxValue,
            ["returnedBytes"] = int.MaxValue,
            ["truncated"] = true,
            ["truncationReason"] = "maxResponseBytes",
            ["nextLine"] = int.MaxValue,
            ["nextLineByteOffset"] = int.MaxValue,
            ["nextCursor"] = new string('x', MaxResourceReadCursorCharacters),
        };
        var worstCaseResponse = CreateSuccessResponse(true, id, new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = resourceUri,
                    ["mimeType"] = mimeType,
                    ["text"] = string.Empty,
                },
            },
            ["_meta"] = worstCaseMetadata,
        });
        var envelopeBytes = Encoding.UTF8.GetByteCount(worstCaseResponse.ToJsonString(_jsonOptions));
        var availableEncodedTextBytes = GetEffectiveResourceReadResponseLimit() - envelopeBytes;
        if (availableEncodedTextBytes <= 0)
            return 0;

        // System.Text.Json's default encoder expands any valid source UTF-8 byte by at most
        // six bytes (`\uXXXX` for an ASCII control or HTML-sensitive character).
        // System.Text.Json既定encoderで有効なsource UTF-8 1 byteが展開される最大は6 byte
        // （ASCII control/HTML-sensitive文字の`\uXXXX`）。
        const int worstCaseJsonExpansion = 6;
        return Math.Min(requestedMaxBytes, availableEncodedTextBytes / worstCaseJsonExpansion);
    }

    private int GetEffectiveResourceReadResponseLimit()
    {
        var responseLimit = GetMaxResponseBytes();
        var transportLimit = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (transportLimit > 0)
            responseLimit = Math.Min(responseLimit, transportLimit);
        if (_currentBatchResponseItemMaxBytes.Value is { } batchLimit)
            responseLimit = Math.Min(responseLimit, Math.Max(0, batchLimit));
        return responseLimit;
    }

    private readonly record struct ResourceReadCursor(int Line, int ByteOffset, int EndLine, string Fingerprint);

    private static bool TryReadOptionalResourceReadInteger(JsonNode? readParams, string name, out int? result)
    {
        result = null;
        if (readParams is not JsonObject obj || !obj.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var parsed))
            return false;
        result = parsed;
        return true;
    }

    private static bool TryReadOptionalResourceReadString(JsonNode? readParams, string name, out string? result)
    {
        result = null;
        if (readParams is not JsonObject obj || !obj.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var parsed) || string.IsNullOrWhiteSpace(parsed))
            return false;
        result = parsed;
        return true;
    }

    private static JsonObject CreateResourceReadIntegerRangeError(JsonNode? id, string argument, int minimum, int maximum, int actual)
        => CreateResourceReadArgumentError(id, argument,
            $"resources/read params.{argument} must be between {minimum} and {maximum}.",
            $"Choose a {argument} value inside the documented resources/read range.",
            new JsonObject
            {
                ["minimum"] = minimum,
                ["maximum"] = maximum,
                ["actual"] = actual,
            });

    private static JsonObject CreateResourceReadArgumentError(
        JsonNode? id,
        string argument,
        string message,
        string suggestion,
        JsonObject? extraData = null)
    {
        var data = extraData ?? new JsonObject();
        data["argument"] = argument;
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: false,
            extraData: data);
    }

    private static bool TryParseResourceReadCursor(string value, out ResourceReadCursor cursor)
    {
        cursor = default;
        var parts = value.Split(':');
        if (parts.Length != 5
            || !string.Equals(parts[0], "v1", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var endLine)
            || line <= 0
            || byteOffset < 0
            || byteOffset > DbReader.MaxBoundedFileReadScannedUtf8Bytes
            || endLine < line
            || parts[4].Length != 16)
        {
            return false;
        }

        cursor = new ResourceReadCursor(line, byteOffset, endLine, parts[4]);
        return true;
    }

    private static string BuildResourceReadCursor(int line, int byteOffset, int endLine, string fingerprint)
        => string.Create(CultureInfo.InvariantCulture, $"v1:{line}:{byteOffset}:{endLine}:{fingerprint}");

    private static string BuildResourceReadFingerprint(string path, string? checksum, long size, int lines, DateTime? modified)
    {
        var descriptor = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}\n{checksum ?? string.Empty}\n{size}\n{lines}\n{modified?.ToUniversalTime().Ticks ?? 0}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(descriptor), digest);
        return Convert.ToHexString(digest[..8]);
    }

    private static JsonNode CreateResourceUriError(JsonNode? id, string uri, string messagePrefix, string suggestion, bool retrySafe, bool includeLengthLimit = false)
    {
        var display = McpBoundedText.ForDisplay(uri, McpBoundedText.MaxResourceUriChars);
        var data = new JsonObject
        {
            ["uri"] = display.Text,
        };
        display.AddMetadata(data, "uri");
        if (includeLengthLimit)
        {
            data["max_length"] = McpBoundedText.MaxResourceUriChars;
            data["actual_length"] = uri.Length;
        }
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"{messagePrefix}: {display.Text}",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: retrySafe,
            extraData: data);
    }

    private JsonNode HandlePromptsList(JsonNode? id)
    {
        var prompts = new JsonArray
        {
            CreatePromptDefinition("summarize_file", "Summarize the API surface and responsibilities of an indexed file.", "path", "Indexed file path to summarize."),
            CreatePromptDefinition("find_unused", "Find likely unused symbols in an optional language or path scope.", "scope", "Optional language, module, or path scope."),
            CreatePromptDefinition("impact_of_changing", "Plan impact analysis for changing a symbol.", "symbol", "Symbol name to analyze."),
            CreatePromptDefinition("investigate_before_edit", "Investigate relevant code before making edits.", "topic", "Optional feature, symbol, file, or behavior to investigate."),
            CreatePromptDefinition("find_existing_pattern", "Find existing implementation and test patterns before adding code.", "topic", "Optional API, behavior, module, or feature pattern to search for."),
            CreatePromptDefinition("safe_symbol_change", "Plan a safe symbol rename or behavior change using graph-aware tools.", "symbol", "Symbol or behavior being changed."),
            CreatePromptDefinition("debug_failure", "Debug a failing build, test, or runtime error using indexed evidence.", "failure", "Optional error text, test name, or failing behavior."),
        };
        return CreateSuccessResponse(true, id, new JsonObject { ["prompts"] = prompts });
    }

    private JsonNode HandlePromptsGet(JsonNode? id, JsonNode? getParams)
    {
        var name = TryReadStringValue(getParams?["name"]);
        if (string.IsNullOrWhiteSpace(name))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing prompt name",
                category: McpErrorEnvelope.CategoryMissingParameter,
                suggestion: "prompts/get requires `params.name`; call prompts/list to enumerate available names.",
                retrySafe: false);
        name = name.Trim();
        if (name.Length > McpBoundedText.MaxPromptNameChars)
            return CreatePromptStringTooLongError(id, parameterName: "name", value: name, maxChars: McpBoundedText.MaxPromptNameChars,
                messagePrefix: "Prompt name is too long",
                suggestion: "Use one of the short prompt names returned by prompts/list.");

        var args = getParams?["arguments"] as JsonObject;
        string? ReadArg(string key, out JsonNode? error)
        {
            error = null;
            if (args == null
                || !args.TryGetPropertyValue(key, out var node)
                || node is not JsonValue value
                || !value.TryGetValue<string>(out var s))
            {
                return null;
            }
            if (s.Length > McpBoundedText.MaxPromptArgumentChars)
            {
                error = CreatePromptStringTooLongError(id, parameterName: key, value: s, maxChars: McpBoundedText.MaxPromptArgumentChars,
                    messagePrefix: $"Prompt argument '{key}' is too long",
                    suggestion: "Shorten prompt arguments before calling prompts/get; long source or path context should be fetched with tools instead.");
                return null;
            }
            return McpBoundedText.ForDisplay(s, McpBoundedText.MaxPromptArgumentChars).Text;
        }

        string text;
        switch (name)
        {
            case "summarize_file":
                {
                    var path = ReadArg("path", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use the `outline` tool for `{path ?? "<path>"}`, then use `excerpt` only for the ranges needed to summarize public API, key symbols, and responsibilities.";
                    break;
                }
            case "find_unused":
                {
                    var scope = ReadArg("scope", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use `unused_symbols` with the requested scope `{scope ?? "<scope>"}`. Cross-check surprising results with `references` or `callers` before recommending deletions.";
                    break;
                }
            case "impact_of_changing":
                {
                    var symbol = ReadArg("symbol", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use `impact_analysis` for `{symbol ?? "<symbol>"}`. Summarize direct callers, transitive callers, and files that likely need tests.";
                    break;
                }
            case "investigate_before_edit":
                {
                    var topic = ReadArg("topic", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Before editing `{topic ?? "<topic>"}`, use `map` for orientation if needed, `search` for broad discovery, `symbols` or `definition` for declarations, `references` for usage and tests, and focused `excerpt` calls for only the relevant ranges.";
                    break;
                }
            case "find_existing_pattern":
                {
                    var topic = ReadArg("topic", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Find existing patterns for `{topic ?? "<topic>"}` with `search` and `symbols`, inspect representative files with `outline`, then use focused `excerpt` ranges from implementation and tests before adding new code.";
                    break;
                }
            case "safe_symbol_change":
                {
                    var symbol = ReadArg("symbol", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"For `{symbol ?? "<symbol>"}`, confirm identity with `definition` or `symbols exactName:true`, inspect `references`, `callers`, and `callees`, then read focused `excerpt` ranges for declarations, call sites, and tests before changing behavior or names.";
                    break;
                }
            case "debug_failure":
                {
                    var failure = ReadArg("failure", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Debug `{failure ?? "<failure>"}` by searching exact error text with `search` or `exactSubstring`, finding related symbols with `definition` and `references`, checking callers/callees for the failing path, and reading focused `excerpt` ranges before proposing a fix.";
                    break;
                }
            default:
                return CreateUnknownPromptError(id, name);
        }

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
        };
        return CreateSuccessResponse(true, id, new JsonObject
        {
            ["description"] = name,
            ["messages"] = messages,
        });
    }

    private static JsonNode CreatePromptStringTooLongError(JsonNode? id, string parameterName, string value, int maxChars, string messagePrefix, string suggestion)
    {
        var display = McpBoundedText.ForDisplay(value, maxChars);
        var data = new JsonObject
        {
            ["parameter"] = parameterName,
            ["max_length"] = maxChars,
            ["actual_length"] = value.Length,
            ["value"] = display.Text,
        };
        display.AddMetadata(data, "value");
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"{messagePrefix}: '{display.Text}'",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: false,
            extraData: data);
    }

    private static JsonNode CreateUnknownPromptError(JsonNode? id, string name)
    {
        var display = McpBoundedText.ForDisplay(name, McpBoundedText.MaxPromptNameChars);
        var data = new JsonObject
        {
            ["prompt"] = display.Text,
        };
        display.AddMetadata(data, "prompt");
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"Unknown prompt: {display.Text}",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Call prompts/list and request one of the advertised prompt names.",
            retrySafe: false,
            extraData: data);
    }

    private async Task<JsonNode> HandleLoggingSetLevelAsync(JsonNode? id, JsonNode? setLevelParams)
    {
        var level = TryReadStringValue(setLevelParams?["level"]);
        if (!IsSupportedMcpLogLevel(level))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Invalid logging level",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "logging/setLevel requires params.level to be one of: debug, info, notice, warning, error, critical, alert, emergency.",
                retrySafe: false);

        var previous = Interlocked.Exchange(ref _mcpLogLevel, level!);
        await EmitLogNotificationAsync("info", $"MCP logging level changed from {previous} to {level}.").ConfigureAwait(false);
        return CreateSuccessResponse(true, id, new JsonObject());
    }

    private static JsonObject CreatePromptDefinition(string name, string description, string argumentName, string argumentDescription)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["arguments"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = argumentName,
                    ["description"] = argumentDescription,
                    ["required"] = false,
                },
            },
        };

    private static string BuildResourceUri(string path)
        => "cdidx://file/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool TryParseResourceUri(string uri, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "cdidx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, "file", StringComparison.OrdinalIgnoreCase)
            || !TryExtractRawResourcePath(uri, out var rawPath))
        {
            return false;
        }

        if (!PathUriNormalizer.TryDecodeRelativeUriPath(rawPath, allowBackslash: false, out var decoded))
            return false;

        path = decoded;
        return true;
    }

    private static bool TryExtractRawResourcePath(string uri, out string rawPath)
    {
        rawPath = string.Empty;
        var schemeSeparator = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
            return false;

        var hostStart = schemeSeparator + 3;
        var pathStart = uri.IndexOf('/', hostStart);
        if (pathStart < 0 || pathStart == uri.Length - 1)
            return false;

        rawPath = uri[(pathStart + 1)..];
        var terminator = rawPath.IndexOfAny(['?', '#']);
        if (terminator >= 0)
            rawPath = rawPath[..terminator];

        return !string.IsNullOrWhiteSpace(rawPath);
    }

    private static string? TryReadStringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string GetResourceMimeType(string? lang)
        => lang?.ToLowerInvariant() switch
        {
            "csharp" => "text/x-csharp",
            "fsharp" => "text/x-fsharp",
            "vb" => "text/x-vb",
            "javascript" => "text/javascript",
            "typescript" => "text/typescript",
            "json" => "application/json",
            "markdown" => "text/markdown",
            "python" => "text/x-python",
            "rust" => "text/x-rust",
            "shell" => "text/x-shellscript",
            "sql" => "application/sql",
            "yaml" => "application/yaml",
            "xml" => "application/xml",
            _ => "text/plain",
        };

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

    // Tool definitions are in McpToolDefinitions.cs / ツール定義は McpToolDefinitions.cs に分離


    /// <summary>
    /// Execute a tool call.
    /// ツール呼び出しを実行。
    /// </summary>
    private async Task<JsonNode> HandleToolsCallAsync(bool hasId, JsonNode? id, JsonNode? callParams)
    {
        _currentIndexAuditContext.Value = new IndexAuditContext();
        var callParamsObject = callParams as JsonObject;
        var args = callParamsObject?["arguments"];
        var toolName = callParamsObject?["name"] is JsonValue toolNameValue
            && toolNameValue.TryGetValue<string>(out var parsedToolName)
                ? parsedToolName
                : null;
        var observedToolName = toolName ?? "(missing)";

        Database.DbDebug.ResetContext();
        var metricsStartedAt = _timeProvider.GetUtcNow();
        var metricsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? metricsError = null;
        JsonNode response;
        JsonObject CreateUnknownToolResponseForMetrics()
        {
            metricsError = "unknown_tool";
            return CreateUnknownToolErrorResponse(hasId: true, id: id, observedToolName);
        }

        try
        {
            var caller = CurrentInitializeState.Caller;
            // Charge every direct tools/call to one caller-wide bucket before detailed
            // name, enablement, or argument validation. Canonical known tools then retain
            // their existing secondary per-tool limit. This prevents a caller from rotating
            // malformed requests across known names to multiply its effective burst (#4547).
            // direct tools/call はすべて、名前・enablement・argument の詳細検証前に caller-wide
            // bucket へ課金する。canonical な既知 tool は既存の secondary per-tool 制限も維持し、
            // malformed request の既知名ローテーションによる burst 増幅を防ぐ（#4547）。
            var decision = RateLimiter.TryAcquireHierarchy(
                RateLimiter.ToolsCallPreValidationBucketName,
                ResolveKnownRateLimitBucketName(toolName),
                caller);
            if (!decision.Allowed)
            {
                metricsError = "rate_limited";
                DeferFrameLog(BuildRateLimitedLog(observedToolName, caller, decision.RetryAfterMs));
                response = CreateRateLimitedErrorResponse(id, observedToolName, caller, decision.RetryAfterMs);
            }
            else if (toolName is null)
            {
                metricsError = "missing_tool_name";
                response = CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing tool name",
                    category: McpErrorEnvelope.CategoryMissingParameter,
                    suggestion: "tools/call requires `params.name`. Send the tool identifier (e.g. \"search\", \"definition\") as a string.",
                    retrySafe: false);
            }
            // Per-deployment enablement gate (#1561). The rate-limit check deliberately runs
            // first so disabled-tool retries cannot bypass request-cost protection (#4547).
            // デプロイ単位の有効化ゲート (#1561)。disabled tool の再試行で request-cost
            // protection を回避できないよう、rate-limit check を先に実行する（#4547）。
            else if (McpToolFilter.IsKnownTool(toolName) && !_toolFilter.IsEnabled(toolName))
            {
                metricsError = "tool_disabled";
                response = CreateErrorResponse(hasId: true, id: id, code: -32601, message: $"Tool not enabled: {toolName}",
                    category: McpErrorEnvelope.CategoryToolDisabled,
                    suggestion: "This tool is disabled on the server (CDIDX_MCP_TOOLS_ALLOW / CDIDX_MCP_TOOLS_DENY). Ask the operator to enable it or use a different tool.",
                    retrySafe: false,
                    extraData: new JsonObject { ["tool"] = toolName });
            }
            else
            {
                var progressToken = TryReadProgressToken(callParamsObject);
                var toolNameTooLong = toolName.Length > McpBoundedText.MaxToolNameChars;
                if (toolNameTooLong)
                {
                    response = CreateUnknownToolResponseForMetrics();
                }
                else if (ValidateToolArguments(toolName, args) is JsonObject argumentError)
                {
                    metricsError = "invalid_argument";
                    if (argumentError["jsonrpc_invalid_params"] is JsonValue invalidParamsMarker
                        && invalidParamsMarker.TryGetValue<bool>(out var invalidParams)
                        && invalidParams)
                    {
                        argumentError.Remove("jsonrpc_invalid_params");
                        response = CreateErrorResponse(hasId: true, id: id, code: -32602, message: argumentError["message"]!.GetValue<string>(),
                            category: McpErrorEnvelope.CategoryInvalidArgument,
                            suggestion: "Use the JSON types advertised by tools/list for this tool.",
                            retrySafe: false,
                            extraData: argumentError);
                    }
                    else
                    {
                        response = CreateToolErrorResponse(id, argumentError["message"]!.GetValue<string>(),
                            category: McpErrorEnvelope.CategoryInvalidArgument,
                            suggestion: "Use exactly the argument names advertised by tools/list for this tool.",
                            retrySafe: false,
                            extraData: argumentError);
                    }
                }
                else if (ValidateCommonListArguments(args) is JsonObject listArgumentError)
                {
                    metricsError = "invalid_list_argument";
                    response = CreateToolErrorResponse(id, listArgumentError["message"]!.GetValue<string>(),
                        category: McpErrorEnvelope.CategoryInvalidArgument,
                        suggestion: "Send only non-empty string entries within the documented MCP array bounds.",
                        retrySafe: false,
                        extraData: listArgumentError);
                }
                else if (ValidateProjectFilterArguments(args) is JsonObject projectFilterError)
                {
                    metricsError = "invalid_project_filter";
                    response = CreateToolErrorResponse(id, projectFilterError["message"]!.GetValue<string>(),
                        category: McpErrorEnvelope.CategoryInvalidArgument,
                        suggestion: "Use a project name or project path from the current workspace, or correct the solution filter.",
                        retrySafe: false,
                        extraData: projectFilterError);
                }
                else
                {
                    response = await DispatchToolCallAsync(
                        toolName,
                        id,
                        args,
                        progressToken,
                        CreateUnknownToolResponseForMetrics).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_currentRequestToken.Value.IsCancellationRequested)
        {
            metricsError = nameof(OperationCanceledException);
            throw;
        }
        catch (Exception ex)
        {
            // Stderr keeps a sanitized local diagnostic, while the JSON-RPC tool
            // result is reduced to the tool name + exception type. Raw exception
            // messages can echo bound parameter values (e.g. SQLite errors quote
            // the offending literal), paths, or content fragments, which would
            // otherwise leak through the MCP transcript (#1530 / #4124).
            // stderr には sanitize 済みのローカル診断だけを残し、JSON-RPC のツール結果は
            // tool 名 + 例外型に絞る。SQLite 例外などの生メッセージはバインド値、
            // 該当リテラル、パス、索引内容を含み得るため、MCP transcript へ流さない
            // (#1530 / #4124)。
            var dbDebugDump = Database.DbDebug.CaptureDump(ex);
            DeferFrameLog(() =>
            {
                WriteMcpLogLine(BuildToolErrorLog(observedToolName, ex));
                Database.DbDebug.WriteCapturedDumpToStderr(dbDebugDump);
            });
            metricsError = ex.GetType().Name;
            var classification = McpErrorEnvelope.ClassifyException(ex);
            response = CreateToolErrorResponse(true, id, BuildSanitizedToolErrorMessage(observedToolName, ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe,
                extraData: BuildToolExceptionData(observedToolName, ex.GetType().Name));
        }
        finally
        {
            Database.DbDebug.ResetContext();
            if (MetricsSink.IsActive)
            {
                metricsStopwatch.Stop();
                var metricsTool = BoundToolNameForDisplay(observedToolName).Text;
                var requestId = CurrentCorrelationContext.Value?.TelemetryRequestId;
                MetricsSink.Record(new MetricsEvent(
                    Timestamp: metricsStartedAt,
                    Tool: metricsTool,
                    Source: "mcp",
                    ElapsedMs: metricsStopwatch.Elapsed.TotalMilliseconds,
                    ExitCode: metricsError == null ? 0 : 1,
                    Language: TryReadMetricStringArg(args, "language") ?? TryReadMetricStringArg(args, "lang"),
                    Error: metricsError,
                    RequestId: requestId?.Token,
                    RequestIdType: requestId?.Type,
                    RequestIdLength: requestId?.Length));
            }
        }

        // Audit observes the wire response (for result_count / error_code / isError),
        // invocation-scoped authorization identity, and any sanitized exception type, so
        // emission happens after the metrics finally block. Stop the stopwatch idempotently
        // — the metrics path may have already stopped it. TryEmitAudit is best-effort internally (#1562).
        // audit は wire response、invocation-scoped authorization identity、例外型を参照するため
        // metrics finally の後で出力する。Stopwatch.Stop は冪等。
        // TryEmitAudit 内部でベストエフォート化済み (#1562)。
        metricsStopwatch.Stop();
        var auditErrorType = metricsError == "unknown_tool" ? null : metricsError;
        TryEmitAudit(hasId, observedToolName, id, args, response, metricsStartedAt, metricsStopwatch.Elapsed.TotalMilliseconds, errorType: auditErrorType);
        _currentIndexAuditContext.Value = null;
        EmitToolInvocationTelemetry(observedToolName, args, response, metricsStartedAt, metricsStopwatch.Elapsed.TotalMilliseconds, metricsError);
        return response;
    }

    private async Task<JsonNode> DispatchToolCallAsync(
        string toolName,
        JsonNode? id,
        JsonNode? args,
        JsonNode? progressToken,
        Func<JsonObject> createUnknownToolResponse)
    {
        if (toolName is "index" or "backfill_fold")
        {
            await _sharedDbWriteGate.WaitAsync(_currentRequestToken.Value).ConfigureAwait(false);
            try
            {
                return toolName == "index"
                    ? await ExecuteIndexAsync(id, args, progressToken).ConfigureAwait(false)
                    : await ExecuteBackfillFoldAsync(id, args, progressToken).ConfigureAwait(false);
            }
            finally
            {
                _sharedDbWriteGate.Release();
            }
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
            "status" => ExecuteStatus(id, args),
            "outline" => ExecuteOutline(id, args),
            "batch_query" => ExecuteBatchQuery(id, args),
            "deps" => ExecuteDeps(id, args),
            "impact_analysis" => ExecuteImpactAnalysis(id, args),
            "languages" => ExecuteLanguages(id, args),
            "validate" => ExecuteValidate(id, args),
            "unused_symbols" => ExecuteUnusedSymbols(id, args),
            "symbol_hotspots" => ExecuteSymbolHotspots(id, args),
            "ping" => ExecutePing(id),
            "suggest_improvement" => await ExecuteSuggestImprovementAsync(id, args).ConfigureAwait(false),
            _ => createUnknownToolResponse(),
        };
    }

    private void EmitToolInvocationTelemetry(string toolName, JsonNode? args, JsonNode response, DateTimeOffset startedAt, double elapsedMs, string? errorType)
    {
        var context = CurrentCorrelationContext.Value;
        var (errorCode, observedErrorType) = ExtractErrorCode(response);
        var resultCount = ExtractResultCount(response);
        var (argKeys, argLengths, argKeyLengths, _) = SanitizeArgs(
            args,
            includeValues: false,
            out _,
            out _,
            out _,
            out _,
            out var argKeysTruncated,
            out var argKeyTruncationReasons,
            out var argKeysOmittedCount,
            out var argKeyNamesTruncatedCount);
        var toolDisplay = BoundToolNameForDisplay(toolName);
        var argsObject = new JsonObject();
        foreach (var pair in argLengths)
            argsObject[pair.Key] = pair.Value;

        var evt = new JsonObject
        {
            ["event"] = "mcp.tool.invocation",
            ["timestamp"] = startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["tool"] = toolDisplay.Text,
            ["request_id"] = context?.TelemetryRequestId?.Token,
            ["request_id_type"] = context?.TelemetryRequestId?.Type,
            ["request_id_length"] = context?.TelemetryRequestId?.Length,
            ["correlation_id"] = context?.CorrelationId,
            ["elapsed_ms"] = Math.Round(elapsedMs, 3),
            ["status"] = errorCode == 0 ? "success" : "error",
            ["error_code"] = errorCode == 0 ? null : errorCode,
            ["error_type"] = errorType ?? observedErrorType,
            ["result_count"] = resultCount,
            ["arg_keys"] = JsonSerializer.SerializeToNode(argKeys, _jsonOptions),
            ["arg_lengths"] = argsObject,
        };
        toolDisplay.AddMetadata(evt, "tool");
        AddArgKeyMetadata(evt, argKeyLengths, argKeysOmittedCount, argKeyNamesTruncatedCount);
        if (argKeysTruncated)
            evt["arg_keys_truncated"] = true;
        if (argKeyTruncationReasons.Count > 0)
            evt["arg_key_truncation_reasons"] = JsonSerializer.SerializeToNode(argKeyTruncationReasons, _jsonOptions);
        DeferFrameLog(() => WriteMcpLogLine(evt.ToJsonString(_jsonOptions)));
    }

    private JsonNode? TryReadProgressToken(JsonNode? callParams)
    {
        var token = callParams?["_meta"]?["progressToken"];
        if (token is null)
            return null;

        if (!IsSupportedProgressToken(token))
            return null;

        return TryMeasureJsonUtf8BytesWithinLimit(token, _jsonOptions, McpBoundedText.MaxProgressTokenJsonBytes, out _)
            ? McpJsonNode.Clone(token)
            : null;
    }

    private static bool IsSupportedProgressToken(JsonNode token)
    {
        var nodeCount = 0;
        return IsSupportedProgressToken(token, depth: 0, ref nodeCount);
    }

    private static bool IsSupportedProgressToken(JsonNode token, int depth, ref int nodeCount)
    {
        if (depth > McpBoundedText.MaxProgressTokenDepth)
            return false;

        nodeCount++;
        if (nodeCount > McpBoundedText.MaxProgressTokenNodeCount)
            return false;

        return token switch
        {
            JsonValue value => IsSupportedProgressTokenScalar(value),
            JsonObject obj => IsSupportedProgressTokenObject(obj, depth, ref nodeCount),
            _ => false,
        };
    }

    private static bool IsSupportedProgressTokenScalar(JsonValue value)
        => value.GetValueKind() switch
        {
            JsonValueKind.String => value.TryGetValue<string>(out var text)
                && text.Length <= McpBoundedText.MaxProgressTokenStringChars,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
            _ => false,
        };

    private static bool IsSupportedProgressTokenObject(JsonObject obj, int depth, ref int nodeCount)
    {
        foreach (var pair in obj)
        {
            if (pair.Key.Length > McpBoundedText.MaxProgressTokenPropertyNameChars)
                return false;
            if (pair.Value is null)
            {
                nodeCount++;
                if (nodeCount > McpBoundedText.MaxProgressTokenNodeCount)
                    return false;
                continue;
            }

            if (!IsSupportedProgressToken(pair.Value, depth + 1, ref nodeCount))
                return false;
        }

        return true;
    }

    private async Task EmitProgressNotificationAsync(JsonNode? progressToken, long progress, long? total, string? message = null)
    {
        if (progressToken is null || _currentOutOfBandFrameWriter.Value is not { } writer)
            return;

        var parameters = new JsonObject
        {
            ["progressToken"] = McpJsonNode.Clone(progressToken),
            ["progress"] = progress,
        };
        if (total.HasValue)
            parameters["total"] = total.Value;
        if (!string.IsNullOrWhiteSpace(message))
            parameters["message"] = message;

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = parameters,
        };
        await writer(notification.ToJsonString(_jsonOptions), _currentRequestToken.Value).ConfigureAwait(false);
    }

    private async Task EmitLogNotificationAsync(string level, string message)
    {
        if (_currentOutOfBandFrameWriter.Value is not { } writer)
            return;

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/message",
            ["params"] = new JsonObject
            {
                ["level"] = level,
                ["logger"] = "cdidx",
                ["data"] = message,
            },
        };
        await writer(notification.ToJsonString(_jsonOptions), _currentRequestToken.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Emit a single audit record for the just-executed tool call. Inspects the wire
    /// response to derive the result count and error code, and uses invocation-scoped
    /// authorization state when available, so the audit trail preserves checks performed
    /// before later error paths build a response (#1562, #4606). Failures are swallowed
    /// because audit emission must never break the underlying tool call.
    /// 直前に実行したツール呼び出しを 1 レコード分監査出力する。クライアントが実際に観測する
    /// 値と一致させるため wire response から result count / error code を抽出し、後続の error
    /// path が response を生成する前の検証も残すため invocation-scoped authorization state を使う
    /// (#1562, #4606)。
    /// audit 失敗で本体ツール呼び出しを壊さないようベストエフォート化する。
    /// </summary>
    private void TryEmitAudit(bool hasId, string toolName, JsonNode? id, JsonNode? args, JsonNode response, DateTimeOffset startedAt, double elapsedMs, string? errorType)
    {
        if (_auditLog is null)
            return;

        try
        {
            var initializeState = CurrentInitializeState;
            var (errorCode, observedErrorType) = ExtractErrorCode(response);
            var resultCount = ExtractResultCount(response);
            var (argKeys, argLengths, argKeyLengths, argValuesEcho) =
                SanitizeArgs(args, _auditLog.IncludeValues,
                    out var argValuesRedacted,
                    out var argValuesTruncated,
                    out var argValueTruncationReasons,
                    out var argValuesSerializedBytes,
                    out var argKeysTruncated,
                    out var argKeyTruncationReasons,
                    out var argKeysOmittedCount,
                    out var argKeyNamesTruncatedCount);
            var toolDisplay = BoundToolNameForDisplay(toolName);
            McpRequestIdTelemetryData? requestId = hasId
                ? McpRequestIdTelemetry.Create(id)
                : null;
            var evt = new AuditLogSink.AuditEvent(
                Timestamp: startedAt,
                Tool: toolDisplay.Text,
                CallerName: initializeState.ClientName,
                CallerVersion: initializeState.ClientVersion,
                RequestId: requestId?.Token,
                ArgKeys: argKeys,
                ArgLengths: argLengths,
                ArgValues: argValuesEcho,
                ResultCount: resultCount,
                ElapsedMs: elapsedMs,
                ErrorCode: errorCode,
                ErrorType: errorType ?? observedErrorType,
                CheckedRootIdentity: _currentIndexAuditContext.Value?.CheckedRootIdentity ?? ExtractCheckedRootIdentity(response),
                ToolLength: toolDisplay.Truncated ? toolDisplay.OriginalLength : null,
                ToolTruncated: toolDisplay.Truncated,
                ArgKeyLengths: argKeyLengths,
                ArgKeysTruncated: argKeysTruncated,
                ArgKeyTruncationReasons: argKeyTruncationReasons,
                ArgKeysOmittedCount: argKeysOmittedCount,
                ArgKeyNamesTruncatedCount: argKeyNamesTruncatedCount,
                ArgValuesRedacted: argValuesRedacted,
                ArgValuesTruncated: argValuesTruncated,
                ArgValueTruncationReasons: argValueTruncationReasons,
                ArgValuesSerializedBytes: argValuesSerializedBytes,
                RequestIdType: requestId?.Type,
                RequestIdLength: requestId?.Length,
                CallerNameLength: initializeState.ClientNameDisplay?.Truncated == true ? initializeState.ClientNameDisplay.Value.OriginalLength : null,
                CallerNameTruncated: initializeState.ClientNameDisplay?.Truncated == true,
                CallerVersionLength: initializeState.ClientVersionDisplay?.Truncated == true ? initializeState.ClientVersionDisplay.Value.OriginalLength : null,
                CallerVersionTruncated: initializeState.ClientVersionDisplay?.Truncated == true);
            _auditLog.Record(evt);
        }
        catch
        {
            // Best-effort: an audit failure must not break the tool call.
            // ベストエフォート: audit 失敗で本体ツール呼び出しを壊さない。
        }
    }

    private static string? ExtractCheckedRootIdentity(JsonNode response)
    {
        var node = response["result"]?["structuredContent"]?["checked_root_identity"]
            ?? response["error"]?["data"]?["checked_root_identity"];
        return node is JsonValue value && value.TryGetValue<string>(out var identity)
            ? identity
            : null;
    }

    /// <summary>
    /// Translate the wire response into `(error_code, error_type)` for the audit record.
    /// 0 means success, positive means a tool-level error (isError=true), and negative is
    /// the verbatim JSON-RPC error code (e.g. -32602 invalid params).
    /// レスポンスを audit 用の `(error_code, error_type)` に変換する。0=成功、正値=
    /// tool エラー (isError=true)、負値=JSON-RPC エラーコード（例: -32602）。
    /// </summary>
    internal static (int Code, string? Type) ExtractErrorCode(JsonNode response)
    {
        if (response is not JsonObject obj)
            return (0, null);
        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject errorObj)
        {
            var code = -32603;
            if (errorObj.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue codeValue
                && codeValue.TryGetValue<int>(out var parsed))
                code = parsed;
            return (code, "jsonrpc_error");
        }
        if (obj.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject resultObj)
        {
            if (resultObj.TryGetPropertyValue("isError", out var isErrorNode)
                && isErrorNode is JsonValue isErrorValue
                && isErrorValue.TryGetValue<bool>(out var isError)
                && isError)
                return (1, "tool_error");
        }
        return (0, null);
    }

    /// <summary>
    /// Extract the result count from a successful tool response. Prefers
    /// `structuredContent.count`, falls back to the length of `structuredContent.results`,
    /// and returns null when neither shape is present (e.g. ping). Tool errors and JSON-RPC
    /// errors return null because there is no meaningful result-set count for those cases.
    /// 成功レスポンスから result count を抽出する。`structuredContent.count` を優先、
    /// `structuredContent.results` の長さに fallback。どちらも無い場合（例: ping）と
    /// tool/JSON-RPC エラー時は null を返す。
    /// </summary>
    internal static int? ExtractResultCount(JsonNode response)
    {
        if (response is not JsonObject obj)
            return null;
        if (obj["result"] is not JsonObject result)
            return null;
        if (result["isError"] is JsonValue isErrorValue
            && isErrorValue.TryGetValue<bool>(out var isError) && isError)
            return null;
        if (result["structuredContent"] is not JsonObject structured)
            return null;
        if (structured["count"] is JsonValue countValue && countValue.TryGetValue<int>(out var count))
            return count;
        if (structured["results"] is JsonArray results)
            return results.Count;
        return null;
    }

    /// <summary>
    /// Build the `(arg_keys, arg_lengths, arg_key_lengths, arg_values?)` audit triple. Values are echoed
    /// only when the operator has opted in via `--audit-log-include-values`; otherwise we
    /// keep keys + per-key length so AI argument shapes can be reconstructed without
    /// persisting query bodies that may contain sensitive substrings (#1562).
    /// audit 用の `(arg_keys, arg_lengths, arg_values?)` を組み立てる。値は
    /// `--audit-log-include-values` がオンの場合のみ転写し、それ以外はキーと長さだけ残す
    /// （secret 風の検索クエリを取り込まないため）。
    /// </summary>
    internal static (IReadOnlyList<string> Keys, IReadOnlyList<KeyValuePair<string, int>> Lengths, IReadOnlyList<KeyValuePair<string, int>> KeyLengths, JsonNode? ValuesEcho)
        SanitizeArgs(JsonNode? args, bool includeValues)
        => SanitizeArgs(args, includeValues, out _, out _, out _, out _, out _, out _, out _, out _);

    private static (IReadOnlyList<string> Keys, IReadOnlyList<KeyValuePair<string, int>> Lengths, IReadOnlyList<KeyValuePair<string, int>> KeyLengths, JsonNode? ValuesEcho)
        SanitizeArgs(
            JsonNode? args,
            bool includeValues,
            out bool argValuesRedacted,
            out bool argValuesTruncated,
            out IReadOnlyList<string> argValueTruncationReasons,
            out int? argValuesSerializedBytes,
            out bool argKeysTruncated,
            out IReadOnlyList<string> argKeyTruncationReasons,
            out int argKeysOmittedCount,
            out int argKeyNamesTruncatedCount)
    {
        argValuesRedacted = false;
        argValuesTruncated = false;
        argValueTruncationReasons = Array.Empty<string>();
        argValuesSerializedBytes = null;
        argKeysTruncated = false;
        argKeysOmittedCount = 0;
        argKeyNamesTruncatedCount = 0;
        var argKeyReasons = new List<string>();
        argKeyTruncationReasons = argKeyReasons;
        if (args is not JsonObject argsObj)
            return (Array.Empty<string>(), Array.Empty<KeyValuePair<string, int>>(), Array.Empty<KeyValuePair<string, int>>(), null);

        var keys = new List<string>(argsObj.Count);
        var lengths = new List<KeyValuePair<string, int>>(argsObj.Count);
        var keyLengths = new List<KeyValuePair<string, int>>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? echoObject = includeValues ? new JsonObject() : null;
        AuditLogSink.ArgValueSanitizationState? valueState = includeValues ? new AuditLogSink.ArgValueSanitizationState() : null;
        var argValueBudgetExhausted = false;
        var argumentCount = 0;
        foreach (var (key, value) in argsObj)
        {
            if (argumentCount >= AuditLogSink.MaxAuditArgumentCount)
            {
                argKeysTruncated = true;
                argKeysOmittedCount = argsObj.Count - argumentCount;
                AddUniqueReason(argKeyReasons, "arg_key_count_limit");
                break;
            }

            var keyDisplay = McpBoundedText.ForDisplay(key, AuditLogSink.MaxAuditArgumentKeyChars);
            var displayKey = MakeUniqueArgumentDisplayKey(key, keyDisplay, usedKeys);
            keys.Add(displayKey);
            lengths.Add(new KeyValuePair<string, int>(displayKey, AuditLogSink.MeasureArgLength(value)));
            if (keyDisplay.Truncated)
            {
                keyLengths.Add(new KeyValuePair<string, int>(displayKey, keyDisplay.OriginalLength));
                argKeysTruncated = true;
                argKeyNamesTruncatedCount++;
                AddUniqueReason(argKeyReasons, "arg_key_length_limit");
            }
            if (echoObject is not null && !argValueBudgetExhausted)
            {
                try
                {
                    if (!valueState!.TryReservePropertyName(displayKey))
                    {
                        argValueBudgetExhausted = true;
                    }
                    else
                    {
                        echoObject[displayKey] = AuditLogSink.SanitizeArgValue(key, value, valueState);
                        argValuesRedacted = valueState.Redacted;
                    }
                }
                catch
                {
                    echoObject = null;
                }
            }
            argumentCount++;
        }
        if (valueState is not null)
        {
            argValuesRedacted = valueState.Redacted;
            argValuesTruncated = valueState.Truncated;
            argValueTruncationReasons = valueState.TruncationReasons;
            argValuesSerializedBytes = valueState.SerializedBytes;
        }

        return (keys, lengths, keyLengths, includeValues ? echoObject : null);
    }

    private static void AddUniqueReason(List<string> reasons, string reason)
    {
        foreach (var existing in reasons)
        {
            if (StringComparer.Ordinal.Equals(existing, reason))
                return;
        }
        reasons.Add(reason);
    }

    private static string MakeUniqueArgumentDisplayKey(string rawKey, BoundedMcpText display, ISet<string> usedKeys)
    {
        if (usedKeys.Add(display.Text))
            return display.Text;

        var hashSuffix = "#" + ShortStableHash(rawKey);
        var candidate = ComposeDisplayKeyWithSuffix(rawKey, hashSuffix);
        var disambiguator = 2;
        while (!usedKeys.Add(candidate))
        {
            candidate = ComposeDisplayKeyWithSuffix(
                rawKey,
                $"{hashSuffix}-{disambiguator.ToString(CultureInfo.InvariantCulture)}");
            disambiguator++;
        }

        return candidate;
    }

    private static string ComposeDisplayKeyWithSuffix(string rawKey, string suffix)
    {
        const int maxDisplayTextChars = McpBoundedText.MaxDiagnosticDisplayChars + 3;
        var maxPrefixChars = Math.Max(0, maxDisplayTextChars - suffix.Length - 3);
        return McpBoundedText.ForDisplay(rawKey, maxPrefixChars).Text + suffix;
    }

    private static string ShortStableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return HexEncoding.ToLowerHexString(bytes, 0, 4);
    }

    private static void AddArgKeyMetadata(
        JsonObject target,
        IReadOnlyList<KeyValuePair<string, int>> argKeyLengths,
        int argKeysOmittedCount,
        int argKeyNamesTruncatedCount)
    {
        if (argKeyLengths.Count > 0)
        {
            var lengths = new JsonObject();
            foreach (var pair in argKeyLengths)
                lengths[pair.Key] = pair.Value;
            target["arg_key_lengths"] = lengths;
            target["arg_keys_truncated"] = true;
        }
        if (argKeysOmittedCount > 0)
            target["arg_keys_omitted_count"] = argKeysOmittedCount;
        if (argKeyNamesTruncatedCount > 0)
            target["arg_key_names_truncated_count"] = argKeyNamesTruncatedCount;
    }

    private static string? SerializeRequestId(JsonNode? id)
    {
        return TrySerializeRequestId(id, out var serialized, out _) ? serialized : null;
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

    private static string? TryReadMetricStringArg(JsonNode? args, string key)
    {
        var value = TryReadStringArg(args, key);
        return value is null ? null : McpBoundedText.ForDisplay(value).Text;
    }

    internal static string BuildOversizedMessageLog(int characterCount, int byteCount) =>
        $"[cdidx-mcp] Message too large ({characterCount} chars / {byteCount} bytes), rejecting. Split the request into smaller JSON-RPC messages or shorter arguments, then retry.";

    internal static string BuildJsonParseErrorLog(string detail) =>
        $"[cdidx-mcp] JSON parse error: {DiagnosticRedactor.BoundDiagnosticText(detail, JsonFrameParser.MaxParseDiagnosticChars)}. MCP stdio expects one UTF-8 JSON-RPC object per LF-delimited line; do not send LSP Content-Length framing.";

    internal static string BuildUnhandledLoopErrorLog(string detail) =>
        $"[cdidx-mcp] Error: {detail}. This request was skipped; fix the request or inspect the server environment, then retry.";

    internal static string BuildResponseSerializationErrorLog(string detail) =>
        $"[cdidx-mcp] Error serializing response: {detail}. Returning a minimal JSON-RPC error response when possible.";

    internal static string BuildResponseWriteErrorLog(string detail) =>
        $"[cdidx-mcp] Error writing response: {detail}. The request was handled but the client connection may already be closed.";

    internal static string BuildToolErrorLog(string toolName, Exception ex) =>
        $"[cdidx-mcp] Tool error ({BoundToolNameForDisplay(toolName).Text}): {BuildSanitizedExceptionLogDetail(ex)}. Fix the tool arguments, refresh the index if needed, then retry.";

    internal static string BuildSanitizedExceptionLogDetail(Exception ex)
    {
        var exceptionType = McpBoundedText.ForDisplay(ex.GetType().Name).Text;
        if (ex is CodeIndexException codeIndexEx)
        {
            var code = McpBoundedText.ForDisplay(codeIndexEx.Code).Text;
            var category = McpBoundedText.ForDisplay(codeIndexEx.Category).Text;
            return $"{exceptionType} code={code} category={category}{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}";
        }

        return exceptionType;
    }

    internal static string BuildClientResponseTooLargeLog(string member, int bytesWritten) =>
        $"[cdidx-mcp] Client response {member} exceeded the server byte limit ({bytesWritten} > {MaxClientResponseJsonBytes}); rejecting without retaining the payload.";

    private static string BuildClientResponseTooLargeMessage(int bytesWritten) =>
        $"MCP client response exceeded the server byte limit ({bytesWritten} > {MaxClientResponseJsonBytes}).";

    // Stderr log emitted when the rate limiter denies a tool call. Mirrors the JSON-RPC
    // `-32000` payload (tool + caller + retry_after_ms) so operators tailing the MCP log
    // can correlate spikes with the structured error returned on the wire (#1560).
    // レート制限で拒否されたツール呼び出しを stderr に記録する。配線上の JSON-RPC `-32000`
    // ペイロードと内容を揃え、運用側がログ追跡から状況把握できるようにする（#1560）。
    internal static string BuildRateLimitedLog(string toolName, string caller, long retryAfterMs) =>
        $"[cdidx-mcp] Rate limit exceeded: tool='{BoundToolNameForDisplay(toolName).Text}', caller='{BoundClientIdentityForDisplay(caller).Text}', retry_after_ms={retryAfterMs}. Increase {RateLimiterOptions.RpsEnvVar} / {RateLimiterOptions.BurstEnvVar} on the server, or back off and retry.";

    internal static string BuildCallerSwapRejectionLog(string current, string attempted) =>
        $"[cdidx-mcp] Ignoring re-initialize with new clientInfo identity '{BoundClientIdentityForDisplay(attempted).Text}': retaining original caller '{BoundClientIdentityForDisplay(current).Text}' so rate-limit buckets cannot be reset mid-session.";

    internal static string BuildUnknownNotificationLog(string method) =>
        $"[cdidx-mcp] Ignoring unknown notification: {method}";

    internal static bool IsSupportedMcpLogLevel(string? level)
        => level is "debug" or "info" or "notice" or "warning" or "error" or "critical" or "alert" or "emergency";

    internal static bool IsUnsafeDebugEnabled()
        => McpEnvironment.IsUnsafeDebugEnabled(DebugEnvironmentVariable);

    internal static string FormatDbPathForLog(string dbPath)
    {
        if (IsUnsafeDebugEnabled())
            return dbPath;

        try
        {
            var path = dbPath;
            if (Uri.TryCreate(dbPath, UriKind.Absolute, out var uri) && uri.IsFile)
                path = uri.LocalPath;
            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? "(configured db)" : fileName;
        }
        catch
        {
            return "(configured db)";
        }
    }

    // Wire-safe error body for the tool catch-all. Mentions the tool and the
    // exception type so the client can branch (retry vs. surface to user)
    // while keeping bound values or matched content out of the response (#1530).
    // For CodeIndexException (#1580) the Code / Category / Path / Hint fields
    // are author-controlled and therefore safe to echo verbatim, so the client
    // gets the structured failure metadata it needs without re-introducing the
    // ex.Message leak vector #1530 closed.
    // ツール catch-all のワイヤー向け本文。クライアントが分岐できるよう tool 名と
    // 例外型は残し、バインド値や一致内容は含めない（#1530）。CodeIndexException (#1580)
    // の Code / Category / Path / Hint は実装側で固定したフィールドなのでそのまま転写し、
    // #1530 で封じた ex.Message 漏れを再現させずに失敗詳細をクライアントへ届ける。
    internal static string BuildSanitizedToolErrorMessage(string toolName, Exception ex)
    {
        var toolDisplay = BoundToolNameForDisplay(toolName).Text;
        if (!IsUnsafeDebugEnabled())
            return $"Tool '{toolDisplay}' failed. See cdidx server stderr for details.";
        if (ex is CodeIndexException codeIndexEx)
            return $"Error executing {toolDisplay} ({ex.GetType().Name}) [{codeIndexEx.Code}/{codeIndexEx.Category}]{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}. See cdidx server stderr for details.";
        return $"Error executing {toolDisplay} ({ex.GetType().Name}). See cdidx server stderr for details.";
    }

    // Wire-safe error body for the JSON-RPC loop catch-all. Same rationale as
    // the tool catch-all (#1530, #1580).
    // JSON-RPC ループ catch-all のワイヤー向け本文。理由はツール catch-all と同じ（#1530, #1580）。
    internal static string BuildSanitizedLoopErrorMessage(Exception ex)
    {
        if (!IsUnsafeDebugEnabled())
            return "Internal MCP error. See cdidx server stderr for details.";
        if (ex is CodeIndexException codeIndexEx)
            return $"Internal error ({ex.GetType().Name}) [{codeIndexEx.Code}/{codeIndexEx.Category}]{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}. See cdidx server stderr for details.";
        return $"Internal error ({ex.GetType().Name}). See cdidx server stderr for details.";
    }

    // Quote so paths/hints with spaces stay one token. Single quotes are kept
    // for human readability — this is a display contract, not a shell-parsing one.
    // 空白を含む path / hint が 2 トークンに見えないよう単引用符でラップする。
    private static string BuildPathFragment(CodeIndexException ex) =>
        string.IsNullOrEmpty(ex.Path) ? string.Empty : $" path='{ex.Path}'";

    private static string BuildHintFragment(CodeIndexException ex) =>
        string.IsNullOrEmpty(ex.Hint) ? string.Empty : $" hint='{ex.Hint}'";

    // Tool implementations are in McpToolHandlers.cs / ツール実装は McpToolHandlers.cs に分離

    // --- DB helper / DBヘルパー ---

    private JsonNode WithDbReader(JsonNode? id, JsonNode? args, Func<DbReader, JsonNode> action)
    {
        var isolateRequestDb = _isolateDbForCurrentRequest.Value;
        // Accept SQLite file: URIs the same way the CLI does (QueryCommandRunner.WithDb),
        // so AI agents on read-only mounts can pass `--db file:///abs/path?immutable=1` and
        // reach the read-only escape hatch in DbContext. File.Exists is skipped for URI-
        // shaped values because they may carry query params meaningless to the filesystem.
        // CLI と同じく file: URI を受け付け、サンドボックス用の escape hatch に到達できるようにする。
        var isUri = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(LongPath.EnsureWindowsPrefix(_dbPath)))
        {
            // Drop any stale cached context so the next tool call can re-open after the user
            // creates the DB (e.g. via an external `cdidx index`). Without this, a missed
            // file lookup would leave a closed/disposed handle blocking later open attempts.
            // ユーザーが後から DB を作った場合に再オープンできるよう、キャッシュをここで破棄。
            if (!isolateRequestDb)
                CloseSharedDb();
            return CreateToolErrorResponse(true, id, $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first.",
                category: McpErrorEnvelope.CategoryIndexMissing,
                suggestion: "Run `cdidx index <projectPath>` to build the index before retrying. The DB lives at `.cdidx/codeindex.db` by default.",
                retrySafe: true);
        }

        var requestToken = _currentRequestToken.Value;
        requestToken.ThrowIfCancellationRequested();
        if (isolateRequestDb)
        {
            using var isolatedDb = new DbContext(DbOpenIntent.QueryOnly, _dbPath, requestToken);
            using var isolatedReader = new DbReader(isolatedDb, requestToken);
            isolatedReader.IncludeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
            return RunWithSqliteDiagnostics(isolatedReader, action);
        }

        // Artifact-preserving WAL reads use detached private snapshots. Refresh them between
        // MCP calls when the source generation changes so a long-lived server
        // observes commits made after the previous call while each individual call keeps one
        // stable SQLite snapshot.
        // artifact-preserving WAL read は切り離した private snapshot を使う。各呼び出し内の
        // 一貫性を保ちつつ、長時間動作する MCP が source generation の変更後に新しい
        // commit を観測できるよう、呼び出し間でそれらの handle を更新する。
        if (_sharedDb?.OpenIntent == DbOpenIntent.QueryOnly
            && _sharedDb.QueryOnlySnapshotRequiresRefresh
            && !_sharedDb.IsQueryOnlySnapshotCurrent(requestToken))
        {
            CloseSharedDb();
        }

        var db = GetOrOpenSharedDb(DbOpenIntent.QueryOnly);
        // Reuse the connection-scoped schema cache for single-threaded direct callers so each
        // call no longer re-runs PRAGMA table_info / PRAGMA index_list per DbReader (issue #1565),
        // and hand the per-request cancellation token to the reader so SQLite work
        // the tool kicks off can observe shutdown / client-disconnect cancellation
        // (#1567). The token is `CancellationToken.None` outside an in-flight request,
        // preserving the existing behaviour for ad-hoc callers like tests that drive
        // `WithDbReader` through internals.
        // MCP ツール呼び出しごとの schema 再走査を排除し (issue #1565)、
        // per-request cancellation token を reader に渡して SQLite 作業が
        // shutdown / 切断を観測できるようにする (#1567)。
        using var reader = new DbReader(db, requestToken);
        reader.IncludeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        return RunWithSqliteDiagnostics(reader, action);
    }

    private JsonNode RunWithSqliteDiagnostics(DbReader reader, Func<DbReader, JsonNode> action)
    {
        var previousReader = _activeSqliteDiagnosticsReader.Value;
        _activeSqliteDiagnosticsReader.Value = reader;
        try
        {
            return reader.RunWithGeneratedScope(() => action(reader));
        }
        finally
        {
            _activeSqliteDiagnosticsReader.Value = previousReader;
        }
    }

    private void AddConfiguredSqliteDiagnostics(JsonObject payload)
    {
        var diagnosticsReader = _activeSqliteDiagnosticsReader.Value;
        if (diagnosticsReader != null)
        {
            QueryCommandRunner.AddReadOnlyFallbackDiagnostics(payload, diagnosticsReader);
            return;
        }

        if (!SqliteFileUri.RequestsImmutableSnapshot(_dbPath))
            return;

        payload["wal_stale_snapshot_risk"] = true;
        payload["wal_stale_snapshot_reason"] = "explicit_immutable_read_only";
    }

    /// <summary>
    /// Open the per-session DbContext on first use and reuse it while the requested intent matches.
    /// Centralising the open lets us pay the connection setup, pragma application, and SQL
    /// function registration once per direct session instead of once per tool invocation
    /// (#1494). Transport requests that may time out independently use isolated DB contexts.
    /// 直接呼び出しセッション初回に DbContext を開き、以後は再利用する。timeout 後も独立して
    /// 継続し得る transport リクエストは、共有接続を避けるためリクエスト単位の DB context を使う。
    /// </summary>
    internal DbContext GetOrOpenSharedDb(DbOpenIntent openIntent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sharedDb?.OpenIntent == openIntent)
            return _sharedDb;

        CloseSharedDb();
        _sharedDb = new DbContext(openIntent, _dbPath, _currentRequestToken.Value);
        return _sharedDb;
    }

    private void CloseSharedDb()
    {
        _sharedDb?.Dispose();
        _sharedDb = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseSharedDb();
        var shutdownCancellationTask = RequestShutdownCancellation();
        if (shutdownCancellationTask.IsCompleted)
        {
            CompleteShutdownCleanup();
        }
        else
        {
            _ = shutdownCancellationTask.ContinueWith(
                static (_, state) => ((McpServer)state!).CompleteShutdownCleanup(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        // Bounded transport teardown can intentionally leave a late task that still releases
        // this gate. As with `_sharedDbWriteGate`, keep the managed semaphore undisposed so
        // eventual completion cannot fail with ObjectDisposedException (#3999, #4543).
        // bounded transport teardown 後も late task がこの gate を release し得るため、
        // `_sharedDbWriteGate` と同様に dispose せず、遅延完了時の例外を防ぐ (#3999, #4543)。
        _textWriterGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CompleteShutdownCleanup()
    {
        lock (s_serverLifecycleGate)
        {
            s_activeServerCount--;
            if (s_activeServerCount == 0)
                ExtractorPluginRegistry.ReleaseWorkspaceSnapshots();
        }
        DisposeShutdownCtsOnce();
    }

    internal static int ActiveServerCountForTests()
    {
        lock (s_serverLifecycleGate)
            return s_activeServerCount;
    }

    private void DisposeShutdownCtsOnce()
    {
        if (Interlocked.Exchange(ref _shutdownCtsDisposed, 1) == 0)
            _shutdownCts.Dispose();
    }

    // --- JSON-RPC helpers / JSON-RPCヘルパー ---

    private enum RequestIdValidationError
    {
        None,
        InvalidType,
        TooLong,
    }

    private static bool TryGetRequestId(JsonObject request, out bool hasId, out JsonNode? id)
        => TryGetRequestId(request, out hasId, out id, out _);

    private static bool TryGetRequestId(JsonObject request, out bool hasId, out JsonNode? id, out RequestIdValidationError error)
    {
        error = RequestIdValidationError.None;
        hasId = request.TryGetPropertyValue("id", out id);
        if (!hasId)
            return true;

        if (id is null)
            return true;

        return TrySerializeRequestId(id, out _, out error);
    }

    private static bool TrySerializeRequestId(JsonNode? id, out string? serialized, out RequestIdValidationError error)
    {
        serialized = null;
        error = RequestIdValidationError.None;
        if (id is null)
            return true;

        if (id is not JsonValue value)
        {
            error = RequestIdValidationError.InvalidType;
            return false;
        }

        return TrySerializeRequestIdValue(value, out serialized, out error);
    }

    private static bool TrySerializeRequestIdValue(JsonValue value, out string? serialized, out RequestIdValidationError error)
    {
        serialized = null;
        error = RequestIdValidationError.None;
        JsonValueKind kind;
        try
        {
            kind = value.GetValueKind();
        }
        catch
        {
            error = RequestIdValidationError.InvalidType;
            return false;
        }

        switch (kind)
        {
            case JsonValueKind.String:
                try
                {
                    var requestId = value.GetValue<string>();
                    if (!IsRequestIdWithinBounds(requestId))
                    {
                        error = RequestIdValidationError.TooLong;
                        return false;
                    }

                    serialized = JsonSerializer.Serialize(requestId);
                    return true;
                }
                catch
                {
                    error = RequestIdValidationError.InvalidType;
                    return false;
                }

            case JsonValueKind.Number:
                try
                {
                    serialized = value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number
                        ? element.GetRawText()
                        : value.ToJsonString();
                }
                catch
                {
                    error = RequestIdValidationError.InvalidType;
                    return false;
                }

                if (serialized.Length == 0 || !(serialized[0] == '-' || char.IsDigit(serialized[0])))
                {
                    error = RequestIdValidationError.InvalidType;
                    serialized = null;
                    return false;
                }

                if (!IsRequestIdWithinBounds(serialized))
                {
                    error = RequestIdValidationError.TooLong;
                    serialized = null;
                    return false;
                }

                return true;

            case JsonValueKind.Null:
                return true;

            default:
                error = RequestIdValidationError.InvalidType;
                return false;
        }
    }

    private static bool IsRequestIdWithinBounds(string value)
        => value.Length <= MaxRequestIdCharacterCount
            && Encoding.UTF8.GetByteCount(value) <= MaxRequestIdByteLength;

    private static string BuildInvalidRequestIdMessage(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? "Invalid request: id exceeds the request-id length limit"
            : "Invalid request: id must be string, number, or null";

    private static string BuildInvalidRequestIdSuggestion(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? $"JSON-RPC 2.0 `id` must be no more than {MaxRequestIdCharacterCount} characters and {MaxRequestIdByteLength} UTF-8 bytes. Use a compact string or number id."
            : "JSON-RPC 2.0 `id` must be a string, integer, or null. Booleans/objects/arrays are not allowed.";

    private static JsonObject? BuildInvalidRequestIdData(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? new JsonObject
            {
                ["max_request_id_chars"] = MaxRequestIdCharacterCount,
                ["max_request_id_bytes"] = MaxRequestIdByteLength,
            }
            : null;

    private static JsonObject CreateSuccessResponse(JsonNode? id, JsonNode result)
        => CreateSuccessResponse(id is not null, id, result);

    private static JsonObject CreateSuccessResponse(bool hasId, JsonNode? id, JsonNode result)
    {
        AddResponseMeta(result);
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        if (hasId)
            response["id"] = McpJsonNode.Clone(id);
        return response;
    }

    private static void AddResponseMeta(JsonNode result)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null || result is not JsonObject obj)
            return;

        var meta = obj["_meta"] as JsonObject ?? new JsonObject();
        meta["correlation_id"] = context.CorrelationId;
        if (context.WireRequestId != null)
            meta["request_id"] = context.WireRequestId;
        obj["_meta"] = meta;
    }

    private static JsonObject? AddCorrelationData(JsonObject? extraData)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return extraData;

        var data = extraData is null ? new JsonObject() : (JsonObject)extraData.DeepClone();
        data["correlation_id"] = context.CorrelationId;
        if (context.WireRequestId != null)
            data["request_id"] = context.WireRequestId;
        return data;
    }

    private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null)
        => CreateErrorResponse(id is not null, id, code, message, category, suggestion, retrySafe, extraData);

    private static BoundedMcpText BoundToolNameForDisplay(string toolName)
        => McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);

    private static void AddToolDisplayData(JsonObject target, string? toolName)
    {
        if (toolName is null)
        {
            target["tool"] = null;
            return;
        }

        var display = BoundToolNameForDisplay(toolName);
        target["tool"] = display.Text;
        display.AddMetadata(target, "tool");
    }

    internal static string BuildUnknownToolMessage(string toolName)
        => $"Unknown tool: {BoundToolNameForDisplay(toolName).Text}";

    private static JsonObject BuildUnknownToolData(string toolName)
    {
        var data = new JsonObject();
        AddToolDisplayData(data, toolName);
        return data;
    }

    private static JsonObject BuildToolExceptionData(string toolName, string exceptionType)
    {
        var data = new JsonObject
        {
            ["exception_type"] = exceptionType,
        };
        AddToolDisplayData(data, toolName);
        return data;
    }

    private static JsonObject CreateUnknownToolErrorResponse(bool hasId, JsonNode? id, string toolName)
        => CreateErrorResponse(hasId: hasId, id: id, code: -32602, message: BuildUnknownToolMessage(toolName),
            category: McpErrorEnvelope.CategoryToolUnknown,
            suggestion: "Call tools/list to enumerate the available tool names for this server. Tool name match is case-sensitive.",
            retrySafe: false,
            extraData: BuildUnknownToolData(toolName));

    // Issue #1581: every MCP error response carries a structured `data` envelope
    // (`category` / `suggestion` / `retry_safe`) so clients can branch on a stable
    // category instead of parsing the human-readable `message`. Category-specific
    // extras (e.g. rate-limited's `retry_after_ms`) merge in via `extraData`.
    // #1581: すべての MCP エラー応答に `category` / `suggestion` / `retry_safe` を含む
    // 構造化 `data` を載せ、クライアントが文字列解析せず分岐できるようにする。カテゴリ
    // 固有フィールド（rate-limited の `retry_after_ms` 等）は `extraData` で合流する。
    private static JsonObject CreateErrorResponse(bool hasId, JsonNode? id, int code, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = McpErrorEnvelope.BuildData(category, suggestion, retrySafe, AddCorrelationData(extraData)),
            }
        };
        if (hasId)
            response["id"] = McpJsonNode.Clone(id);
        return response;
    }

    private static JsonObject CreateCancelledResponse(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeRequestCancelled,
            message: "Request cancelled",
            category: McpErrorEnvelope.CategoryRequestCancelled,
            suggestion: "The client cancelled this request before completion. Reissue the call if the work is still needed.",
            retrySafe: true);

    /// <summary>
    /// Create a tool result response (MCP format).
    /// ツール結果レスポンスを作成（MCP形式）。
    /// </summary>
    private JsonObject CreateToolResult(JsonNode? id, string text, JsonNode? structuredContent = null, string? mimeType = null)
    {
        mimeType ??= structuredContent is null ? "text/plain" : "application/json";
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["mimeType"] = mimeType,
                    ["text"] = text
                }
            }
        };
        if (structuredContent is JsonObject structuredObject)
        {
            structuredObject.TryAdd("api_version", JsonOutputContract.ApiVersion);
            AddProjectFilterRootDiagnostics(structuredObject);
            AddConfiguredSqliteDiagnostics(structuredObject);
            result["structuredContent"] = structuredContent;
        }
        else if (structuredContent != null)
        {
            ClearProjectFilterRootDiagnostics();
            result["structuredContent"] = structuredContent;
        }
        else
        {
            ClearProjectFilterRootDiagnostics();
        }
        var response = CreateSuccessResponse(true, id, result);
        var responseLimit = GetMaxResponseBytes();
        if (TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, responseLimit, out var responseBytes))
            return response;

        return CreateResponseTooLargeError(true, id, responseBytes, responseLimit, actualBytesExact: false);
    }

    internal bool TrySerializeJsonNodeWithinByteLimitForTests(JsonNode node, int maxBytes, out string? serialized, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(node, _jsonOptions, maxBytes, captureSerialized: true, out serialized, out bytesWritten);

    private static bool TryMeasureJsonUtf8BytesWithinLimit(JsonNode node, JsonSerializerOptions options, int maxBytes, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(node, options, maxBytes, captureSerialized: false, out _, out bytesWritten);

    private static bool TrySerializeJsonNodeWithinByteLimit(JsonNode node, JsonSerializerOptions options, int maxBytes, bool captureSerialized, out string? serialized, out int bytesWritten)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "JSON byte limit must be non-negative.");

        serialized = null;
        using var stream = new BoundedJsonUtf8Stream(
            maxBytes,
            captureSerialized,
            bytes => new JsonResponseByteLimitExceededException(bytes));
        var writerOptions = new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
        };

        try
        {
            using var writer = new Utf8JsonWriter(stream, writerOptions);
            node.WriteTo(writer, options);
            writer.Flush();
            bytesWritten = stream.BytesWritten;
            serialized = stream.GetCapturedString();
            return true;
        }
        catch (JsonResponseByteLimitExceededException ex)
        {
            bytesWritten = ex.BytesWritten;
            return false;
        }
    }

    private sealed class JsonResponseByteLimitExceededException(int bytesWritten) : Exception
    {
        public int BytesWritten { get; } = bytesWritten;
    }

    private JsonObject CreateResponseTooLargeError(bool hasId, JsonNode? id, int responseBytes, int responseLimit, bool actualBytesExact = true)
    {
        var response = CreateErrorResponse(
            hasId: hasId,
            id: id,
            code: -32603,
            message: $"MCP response exceeded the server byte limit ({responseBytes} > {responseLimit}). Narrow the query or lower the result limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Narrow the query, add path/language filters, lower limit, or use countOnly for a summary-first probe.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "response_too_large",
                ["limit_bytes"] = responseLimit,
                ["actual_bytes"] = responseBytes,
                ["actual_bytes_exact"] = actualBytesExact,
            });
        AddConfiguredSqliteDiagnostics((JsonObject)response["error"]!["data"]!);
        return response;
    }

    private static int GetMaxResponseBytes()
        => ReadPositiveIntEnvironmentLimit(
            MaxResponseBytesEnvVar,
            DefaultMaxResponseBytes,
            MaxConfiguredResponseBytes,
            "MCP response byte limit");

    private static int ReadPositiveIntEnvironmentLimit(string envVar, int defaultValue, int maximumValue, string description)
    {
        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(envVar, raw);
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] Ignoring invalid {envVar}='{displayValue}'. Expected a positive integer for {description}. Using default {defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            return defaultValue;
        }

        if (limit > maximumValue)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(envVar, raw);
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] Clamping {envVar}='{displayValue}' to maximum {maximumValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} for {description}.");
            return maximumValue;
        }

        return limit;
    }

    /// <summary>
    /// Create a tool error response (MCP format with isError flag).
    /// Optional <paramref name="similarValues"/> attach a structured
    /// <c>data.similar_values</c> array to the result so MCP clients can offer
    /// recovery alternatives without parsing the human-readable message (#1582).
    /// ツールエラーレスポンスを作成（isError フラグ付き MCP 形式）。
    /// <paramref name="similarValues"/> を渡すと結果に構造化された
    /// <c>data.similar_values</c> 配列を添えるので、MCP クライアントは
    /// 人間向けメッセージを解析せずに代替候補を提示できる (#1582)。
    /// </summary>
    private JsonObject CreateToolErrorResponse(JsonNode? id, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null,
        IReadOnlyList<string>? similarValues = null)
        => CreateToolErrorResponse(id is not null, id, message, category, suggestion, retrySafe, extraData, similarValues);

    // Backward-compatible overload for tool handlers that return argument-validation
    // failures (#1581). These were all "missing parameter / invalid argument" call sites
    // before the envelope was introduced, so the default classification is `invalid_argument`
    // / retry_safe=false. The optional `similarValues` carries the structured did-you-mean
    // candidates for unknown enum values (#1582). Sites that have richer context should
    // call the explicit overload.
    // 引数バリデーション失敗を返す既存ツールハンドラ向けの互換オーバーロード（#1581）。
    // envelope 導入前の呼び出しは全て「引数不正」系だったため既定カテゴリを `invalid_argument`
    // / retry_safe=false とする。任意の `similarValues` は未知 enum 値に対する構造化された
    // did-you-mean 候補 (#1582)。より具体的なカテゴリを持てる呼び出し元は明示オーバーロード
    // を使う。
    private JsonObject CreateToolErrorResponse(JsonNode? id, string message,
        IReadOnlyList<string>? similarValues = null)
        => CreateToolErrorResponse(id, message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Tool argument validation failed. Inspect the tool's `inputSchema` via tools/list and adjust the call.",
            retrySafe: false,
            similarValues: similarValues);

    // Issue #1581: tool-result errors mirror the JSON-RPC error envelope by including
    // the same `category` / `suggestion` / `retry_safe` triple under `result.structuredContent`.
    // Existing clients that only read `content[0].text` + `isError` keep working; new clients
    // can read `structuredContent` to branch on the category.
    // #1581: ツール結果エラーにも JSON-RPC エラーと同じ `category` / `suggestion` / `retry_safe`
    // を `result.structuredContent` に載せる。既存の `content[0].text` + `isError` だけを読む
    // クライアントは互換のまま、新規クライアントは `structuredContent` でカテゴリ分岐できる。
    private JsonObject CreateToolErrorResponse(bool hasId, JsonNode? id, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null,
        IReadOnlyList<string>? similarValues = null)
    {
        ClearProjectFilterRootDiagnostics();
        var structuredContent = McpErrorEnvelope.BuildData(category, suggestion, retrySafe, AddCorrelationData(extraData));
        AddConfiguredSqliteDiagnostics(structuredContent);
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
            ["isError"] = true,
            ["structuredContent"] = structuredContent,
        };
        if (similarValues != null && similarValues.Count > 0)
        {
            var similarArray = new JsonArray();
            foreach (var value in similarValues)
                similarArray.Add(JsonValue.Create(value));
            result["data"] = new JsonObject
            {
                ["similar_values"] = similarArray,
            };
        }
        return CreateSuccessResponse(hasId, id, result);
    }

    private static JsonObject CreateToolDefinition(string name, string description, JsonObject inputSchema,
        JsonObject? annotations = null)
    {
        var def = new JsonObject
        {
            ["name"] = name,
            ["description"] = AppendLanguageSupportClause(name, description),
            ["inputSchema"] = inputSchema,
            ["examples"] = BuildToolExamples(name),
        };
        if (annotations != null)
            def["annotations"] = annotations;
        return def;
    }

    private static JsonArray BuildToolExamples(string name)
    {
        var args = name switch
        {
            "search" => new JsonObject { ["query"] = "Run", ["lang"] = "csharp", ["limit"] = 5 },
            "definition" => new JsonObject { ["query"] = "App", ["exactName"] = true },
            "references" => new JsonObject { ["query"] = "Run", ["kind"] = "call" },
            "callers" => new JsonObject { ["query"] = "Run", ["rankBy"] = "weighted" },
            "callees" => new JsonObject { ["query"] = "App.Run" },
            "symbols" => new JsonObject { ["query"] = "App", ["kind"] = "class" },
            "files" => new JsonObject { ["query"] = "app.cs", ["lang"] = "csharp" },
            "excerpt" => new JsonObject { ["path"] = "src/app.cs", ["startLine"] = 1, ["endLine"] = 5 },
            "find_in_file" => new JsonObject { ["path"] = "src/app.cs", ["query"] = "Run", ["before"] = 1, ["after"] = 1 },
            "map" => new JsonObject { ["limit"] = 5, ["excludeTests"] = true },
            "analyze_symbol" => new JsonObject { ["query"] = "Run", ["includeBody"] = true },
            "impact_analysis" => new JsonObject { ["query"] = "Run", ["maxHops"] = 2, ["withPaths"] = true },
            "status" => new JsonObject(),
            "outline" => new JsonObject { ["path"] = "src/app.cs" },
            "deps" => new JsonObject { ["path"] = "src/", ["reverse"] = false, ["limit"] = 10 },
            "languages" => new JsonObject(),
            "validate" => new JsonObject { ["kind"] = "line_too_long" },
            "ping" => new JsonObject(),
            "batch_query" => new JsonObject
            {
                ["queries"] = new JsonArray
                {
                    new JsonObject { ["tool"] = "search", ["arguments"] = new JsonObject { ["query"] = "Run", ["limit"] = 3 } },
                    new JsonObject { ["tool"] = "definition", ["arguments"] = new JsonObject { ["query"] = "App", ["limit"] = 3 } },
                },
            },
            "index" => new JsonObject { ["path"] = ".", ["rebuild"] = false },
            "backfill_fold" => new JsonObject { ["dry_run"] = false, ["force"] = false },
            "symbol_hotspots" => new JsonObject { ["lang"] = "csharp", ["limit"] = 10 },
            "unused_symbols" => new JsonObject { ["lang"] = "csharp", ["limit"] = 10 },
            "suggest_improvement" => new JsonObject
            {
                ["category"] = "output_format",
                ["description"] = "The tool response should make truncation easier to detect.",
                ["evidencePaths"] = new JsonArray { "src/CodeIndex/Mcp/McpToolHandlers.cs" },
            },
            _ => new JsonObject(),
        };

        return new JsonArray
        {
            new JsonObject
            {
                ["request"] = new JsonObject
                {
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = args,
                    },
                },
                ["response_excerpt"] = "A successful MCP tool result includes content and, when available, structuredContent.",
            },
        };
    }

    private static string AppendLanguageSupportClause(string name, string description)
    {
        var clause = name switch
        {
            "references" or "callers" or "callees" or "deps" or "impact_analysis" or "unused_symbols" or "symbol_hotspots"
                => $"Language support: Supports graph/reference extraction for: {GraphLanguageList()}. Unsupported `lang` values are reported with graph-support metadata when the tool returns graph-support fields; use `search`, `definition`, `excerpt`, or `files` for non-graph languages.",
            "definition" or "symbols" or "outline" or "analyze_symbol"
                => $"Language support: Supports symbol extraction for: {SymbolLanguageList()}. Search-only languages can still be indexed and filtered by file tools but may have no symbol rows.",
            "search"
                => "Language support: Supports indexed file/content filters for every detected language; call `languages` for the full catalog.",
            "find_in_file" or "files" or "map"
                => $"Language support: Supports indexed file/content filters for every detected language listed by `languages`: {DetectedLanguageList()}. Symbol and graph fields are available only for the languages whose capabilities are advertised by `languages`.",
            "excerpt" or "status" or "validate"
                => $"Language support: Language-agnostic over indexed files and diagnostics for every detected language listed by `languages`: {DetectedLanguageList()}. This tool does not interpret a `lang` filter.",
            "languages"
                => "Language support: This is the authoritative language catalog for MCP tools; it lists every detected language plus symbol_extraction, reference_extraction, graph_queries, and capability_gaps fields.",
            "index"
                => $"Language support: Indexes every detected language listed by `languages`: {DetectedLanguageList()}, then extracts symbols and graph references only where the catalog advertises those capabilities.",
            "batch_query"
                => "Language support: Language behavior is inherited from each nested read-only tool; consult each returned payload and the `languages` tool for capabilities.",
            "backfill_fold" or "ping" or "suggest_improvement"
                => "Language support: Language-independent tool; it does not interpret `lang` filters.",
            _ => "Language support: See the `languages` tool for detected languages and per-language symbol_extraction / reference_extraction / graph_queries capabilities.",
        };

        return $"{description} {clause}";
    }

    private static string DetectedLanguageList()
        => string.Join(", ", FileIndexer.GetDetectedLanguageNames());

    private static string SymbolLanguageList()
        => string.Join(", ", SymbolExtractor.GetSupportedLanguages()
            .OrderBy(lang => lang, StringComparer.Ordinal));

    private static string GraphLanguageList()
        => string.Join(", ", ReferenceExtractor.GetSupportedLanguages()
            .OrderBy(lang => lang, StringComparer.Ordinal));

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
