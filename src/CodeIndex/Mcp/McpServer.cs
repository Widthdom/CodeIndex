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
    private long _nextInitializeAttemptId;
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
    // "which client issued this call?" without a second log source. Captured by the one
    // accepted `initialize`; reconnecting clients establish a new server session.
    // `initialize.clientInfo` を audit に転写し、別ログを引かなくても呼び出し元を辿れるよう
    // にする。受理される唯一の `initialize` で確定し、再接続時は新しい server session を作る。
    // The same snapshot carries the sticky caller used by the per-(tool, caller) limiter.
    // 同じ snapshot に (tool, caller) 単位の limiter が使う sticky caller も保持する。
    // Publish negotiated initialize metadata through one immutable reference. Writers are
    // serialized by `_initializeStateGate`; readers capture this reference once so a draining
    // request cannot observe a partially committed initialization snapshot (#4540, #4848).
    // initialize で交渉した metadata は単一の immutable reference として公開する。writer は
    // `_initializeStateGate` で直列化し、reader は reference を一度だけ取得することで、drain 中の
    // request が部分的に commit された initialization state を観測しない (#4540, #4848)。
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
    internal const int LegacyResourceListCursorChars = 23;
    internal const int MaxResourceListCursorChars = 34;
    internal const int MaxResourceListPathFilterCount = 100;
    internal const int MaxResourceListPathFilterChars = DbReader.MaxPathLikePatternLength;
    internal const int MaxResourceListPathFilterWildcards = DbReader.MaxPathLikePatternWildcards;
    internal const int MaxResourceListLanguageFilterChars = McpBoundedText.MaxScalarArgumentChars;
    internal const int MinResourceListMaxBytes = 4 * 1024;
    internal const int DefaultResourceListMaxBytes = HttpMcpTransport.DefaultMaxResponseBodyBytes;
    internal const int MaxResourceListMaxBytes = HttpMcpTransport.DefaultMaxResponseBodyBytes;
    internal const int ResourceListPageSize = 200;
    private const int LegacyResourceListCursorPayloadBytes = 17;
    private const int ResourceListCursorPayloadBytes = 25;
    private const byte LegacyResourceListCursorVersion = 1;
    private const byte ResourceListCursorVersion = 2;
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
    internal const int MaxToolsListNameFilters = 24;
    internal const int MaxToolsListNameCharacters = 128;
    internal const int MaxToolsListCursorCharacters = 8_192;
    internal const int MaxStatusProjectionFields = 32;
    internal const int MaxStatusProjectionFieldCharacters = 128;
    internal const int MaxStatusProjectionCharacters = 2_048;
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

}
