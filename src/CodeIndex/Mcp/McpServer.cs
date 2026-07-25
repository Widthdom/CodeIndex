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
            || !TryExtractRawResourcePath(uri, out var rawPath))
        {
            return false;
        }

        var isCanonicalFile = string.Equals(parsed.Host, "file", StringComparison.OrdinalIgnoreCase);
        var isTemplateFilePath = string.Equals(parsed.Host, "file-path", StringComparison.OrdinalIgnoreCase);
        if (!isCanonicalFile && !isTemplateFilePath)
            return false;
        if (isTemplateFilePath
            && (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)))
        {
            return false;
        }

        var decodedSuccessfully = isTemplateFilePath
            ? PathUriNormalizer.TryDecodeTemplateRelativeUriPath(rawPath, out var decoded)
            : PathUriNormalizer.TryDecodeRelativeUriPath(rawPath, allowBackslash: false, out decoded);
        if (!decodedSuccessfully)
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
    private JsonObject CreateToolResult(
        JsonNode? id,
        string text,
        JsonNode? structuredContent = null,
        string? mimeType = null,
        bool enrichStructuredContent = true)
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
            if (enrichStructuredContent)
                EnrichToolStructuredContent(structuredObject);
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

    private void EnrichToolStructuredContent(JsonObject structuredContent)
    {
        structuredContent.TryAdd("api_version", JsonOutputContract.ApiVersion);
        AddProjectFilterRootDiagnostics(structuredContent);
        AddConfiguredSqliteDiagnostics(structuredContent);
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
