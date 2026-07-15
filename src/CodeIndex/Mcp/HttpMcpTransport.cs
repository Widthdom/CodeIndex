using System.Globalization;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Threading.Channels;
using CodeIndex.Diagnostics;

namespace CodeIndex.Mcp;

/// <summary>
/// HTTP MCP transport (issue #1558). Each HTTP POST carries one JSON-RPC request frame in the
/// body and the matching JSON-RPC response is returned as the response body (or 204 No Content
/// for notifications). Concurrent POSTs carry request-scoped response writers so completion order
/// cannot attach a response to the wrong HTTP request. The transport serves one logical client
/// session identified by `Mcp-Session-Id`; concurrent requests and `/events` subscriptions all
/// belong to that session. Server-initiated JSON-RPC notifications are exposed through `/events`
/// as a bounded SSE fan-out channel for the same logical session.
/// HTTP MCP トランスポート (issue #1558)。HTTP POST 1 件が JSON-RPC リクエスト 1 件と対応し、
/// 応答も同じ HTTP レスポンスのボディに乗せる（通知の場合は 204 No Content）。並行 POST は
/// request-scoped response writer を持つため、完了順が前後しても別 request に応答が結び付かない。
/// transport は `Mcp-Session-Id` で識別する 1 logical client session を扱い、並行 request と
/// `/events` subscription はすべて同じ session に属する。サーバー起点の JSON-RPC 通知は
/// 同じ logical session 向けの bounded SSE fan-out channel `/events` で公開する。
/// </summary>
internal sealed partial class HttpMcpTransport :
    IMcpTransport,
    IOutOfBandMcpTransport,
    IConcurrentMcpTransport,
    IMcpResponseSizeLimitProvider
{
    internal const int DefaultMaxRequestBodyBytes = 1_000_000;
    internal const int DefaultMaxInFlightRequestBodyBytes = 64 * 1024 * 1024;
    internal const int DefaultMaxResponseBodyBytes = 1_000_000;
    internal const int MaxConfiguredRequestBodyBytes = 16 * 1024 * 1024;
    internal const int MaxConfiguredInFlightRequestBodyBytes = 1024 * 1024 * 1024;
    internal const int DefaultRequestBodyIdleTimeoutMilliseconds = 30_000;
    internal const int MaxRequestBodyIdleTimeoutMilliseconds = 600_000;
    internal const int DefaultRequestLifetimeTimeoutMilliseconds = 120_000;
    internal const int MaxRequestLifetimeTimeoutMilliseconds = 3_600_000;
    internal const int MaxConfiguredResponseBodyBytes = 16 * 1024 * 1024;
    internal const int DefaultMaxQueuedRequests = 64;
    internal const int MaxConfiguredQueuedRequests = 1024;
    internal const int DefaultMaxConcurrentHandlers = 64;
    internal const int MaxConfiguredConcurrentHandlers = 1024;
    internal const int DefaultMaxEventStreams = 16;
    internal const int MaxConfiguredEventStreams = 1024;
    internal const int EventStreamRejectionConcurrency = 8;
    internal const int RetainedResponseOutputOperationCapacity = 1024;
    internal const int MaxRequestLogFieldCharacters = 256;
    internal const int DefaultRequestLogQueueCapacity = 1024;
    internal const int MaxConfiguredRequestLogQueueCapacity = 16 * 1024;
    internal const int MaxHealthJsonBytes = 64 * 1024;
    internal const int MaxSseEventFrameBytes = 64 * 1024;
    internal const string RequestLogTruncationMarker = "...<truncated>";
    internal const string MaxRequestBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_REQUEST_BYTES";
    internal const string MaxInFlightRequestBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES";
    internal const string RequestBodyIdleTimeoutMillisecondsEnvVar = "CDIDX_MCP_HTTP_BODY_IDLE_TIMEOUT_MS";
    internal const string RequestLifetimeTimeoutMillisecondsEnvVar = "CDIDX_MCP_HTTP_REQUEST_TIMEOUT_MS";
    internal const string MaxResponseBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES";
    internal const string MaxQueueDepthEnvVar = "CDIDX_MCP_HTTP_MAX_QUEUE_DEPTH";
    internal const string MaxConcurrentHandlersEnvVar = "CDIDX_MCP_HTTP_MAX_CONCURRENT_HANDLERS";
    internal const string MaxEventStreamsEnvVar = "CDIDX_MCP_HTTP_MAX_EVENT_STREAMS";
    internal const string SessionIdHeaderName = "Mcp-Session-Id";
    internal const string RejectionReasonHeader = "X-Cdidx-Mcp-Rejection";
    internal const string ConcurrentHandlerLimitRejection = "concurrent_handler_limit";
    internal const string RequestQueueLimitRejection = "request_queue_limit";
    internal const string RequestBodyBudgetLimitRejection = "request_body_budget_limit";
    internal const string EventStreamLimitRejection = "event_stream_limit";
    internal const string SessionRequiredRejection = "session_required";
    internal const string SessionNotFoundRejection = "session_not_found";
    internal const string SessionInitializationInProgressRejection = "session_initialization_in_progress";
    internal const string EventStreamWriteFailureDrop = "write_failure";
    internal const string AuthDenialMissing = "missing";
    internal const string AuthDenialAmbiguous = "ambiguous";
    internal const string AuthDenialWrongScheme = "wrong-scheme";
    internal const string AuthDenialMalformedToken = "malformed-token";
    internal const string AuthDenialOversizedToken = "oversized-token";
    internal const string AuthDenialWrongToken = "wrong-token";
    internal const string TimeoutDiagnosticPrefix = "timeout:";
    internal const string LoopbackAuthDisabledWarning = "HTTP MCP is running in explicit unsafe mode without bearer authentication; local processes can connect.";
    internal const string OriginRejectedDiagnostic = "origin_not_allowed";
    internal const string PreflightRejectedDiagnostic = "cors_preflight_rejected";
    internal const string UnsupportedMediaTypeDiagnostic = "unsupported_media_type";
    internal const string UnsupportedCharsetDiagnostic = "unsupported_charset";
    internal const string InvalidUtf8Diagnostic = "invalid_utf8";
    internal const string RequestBodyIdleTimeoutDiagnostic = "timeout:http_request_body_idle";
    internal const string RequestLifetimeTimeoutDiagnostic = "timeout:http_request_lifetime";
    internal const string RequestDisconnectProbeWriteTimeoutDiagnostic = "timeout:http_disconnect_probe_write";
    internal const string ClientDisconnectedDiagnostic = "client_disconnected";
    internal const string TransportShutdownDiagnostic = "transport_shutdown";
    private const string BearerPrefix = "Bearer ";
    private const int SessionIdByteCount = 32;
    private const string DefaultStartingHealthJson = """{"status":"starting","db_open":false}""";
    private const string InvalidHealthJson = """{"status":"degraded","db_open":false,"error":"health_provider_invalid"}""";
    private static readonly TimeSpan EventStreamDisconnectProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EventStreamWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeAcceptLoopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestDisconnectProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly byte[] RequestDisconnectProbePayload = [(byte)' '];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly RequestBodyBudget ProcessRequestBodyBudget = new();

    private readonly HttpListener _listener;
    private readonly string _endpoint;
    private readonly string _allowedOrigin;
    private readonly Action<HttpRequestLogRecord>? _requestLogger;
    private readonly int _requestLogQueueCapacity;
    private readonly Channel<HttpRequestLogRecord>? _requestLogQueue;
    private readonly Task? _requestLogTask;
    private readonly ConcurrentDictionary<Guid, EventStream> _eventStreams = new();
    private readonly ConcurrentDictionary<Task, byte> _abandonedResponseOutputOperations = new();
    private readonly ConcurrentDictionary<Task, byte> _requestCancellationDeliveries = new();
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly LinkedList<PendingRequest> _requestQueue = new();
    private readonly object _requestQueueSync = new();
    private TaskCompletionSource<bool> _requestAvailable = CreateRequestAvailableSignal();
    private readonly SemaphoreSlim _requestQueueReaderSemaphore = new(1, 1);
    private readonly SemaphoreSlim _queueSlots;
    private readonly SemaphoreSlim _handlerSemaphore;
    private readonly SemaphoreSlim _eventStreamHandlerSemaphore;
    private readonly SemaphoreSlim _eventStreamRejectionSemaphore =
        new(EventStreamRejectionConcurrency, EventStreamRejectionConcurrency);
    private readonly SemaphoreSlim _retainedResponseOutputOperationSlots =
        new(RetainedResponseOutputOperationCapacity, RetainedResponseOutputOperationCapacity);
    private readonly int _maxRequestBodyBytes;
    private readonly int _maxInFlightRequestBodyBytes;
    private readonly TimeSpan _requestBodyIdleTimeout;
    private readonly TimeSpan _requestLifetimeTimeout;
    private readonly TimeSpan _requestDisconnectProbeInterval;
    private readonly TimeSpan _responseWriteTimeout;
    private readonly int _maxResponseBodyBytes;
    private readonly int _maxQueuedRequests;
    private readonly int _maxConcurrentHandlers;
    private readonly int _maxEventStreams;
    private readonly TimeSpan _eventStreamWriteTimeout;
    private readonly Task _acceptLoop;
    private readonly object _disposeSync = new();
    private readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(SessionIdByteCount)).ToLowerInvariant();
    // The configured bearer token's SHA-256 digest, precomputed once at construction so the
    // per-request auth path never hashes the secret. Storing the digest (not the token) keeps the
    // per-request work proportional only to the attacker-supplied input length, eliminating the
    // configured-token length side channel that a per-request hash would still leak.
    // 設定トークンの SHA-256 をコンストラクタで一度だけ計算し、リクエスト毎の auth では
    // 攻撃者入力のみハッシュ計算する。これにより設定トークン長による timing 漏洩を排除する。
    private readonly byte[]? _bearerTokenHash;
    private readonly ConcurrentDictionary<PendingRequest, byte> _activeConcurrentRequests = new();
    private PendingRequest? _pendingRequest;
    private PendingRequest? _pendingInitializeRequest;
    private int _queuedRequestCount;
    private int _pendingRequestLogCount;
    private int _eventStreamCount;
    private long _requestLogDroppedCount;
    private long _requestLogQueueFullDropCount;
    private long _requestLogCallbackFailureCount;
    private long _responseAbortCleanupFailureCount;
    private long _responseCloseCleanupFailureCount;
    private long _concurrentHandlerLimitRejectionCount;
    private long _requestQueueLimitRejectionCount;
    private long _requestBodyBudgetLimitRejectionCount;
    private long _requestBodyIdleTimeoutCount;
    private long _requestLifetimeTimeoutCount;
    private long _clientDisconnectCount;
    private long _queuedRequestCancellationCount;
    private long _eventStreamLimitRejectionCount;
    private long _eventStreamDropCount;
    private long _eventStreamWriteFailureDropCount;
    private long _authDenialMissingCount;
    private long _authDenialAmbiguousCount;
    private long _authDenialWrongSchemeCount;
    private long _authDenialMalformedTokenCount;
    private long _authDenialOversizedTokenCount;
    private long _authDenialWrongTokenCount;
    private long _inFlightRequestBodyBytes;
    private long _peakInFlightRequestBodyBytes;
    private Task? _disposeTask;
    private string? _lastResponseAbortCleanupFailure;
    private string? _lastResponseCloseCleanupFailure;
    private string? _lastEventStreamDropReason;
    private string? _lastAuthDenialReason;
    private string? _lastRequestLogDropReason;
    private int _disposeStarted;
    private int _sessionEstablished;
    private bool _requestQueueCompleted;
    private bool _ownedSemaphoreGatesDisposed;

    /// <summary>
    /// Build an HTTP transport bound to the supplied prefix. Every request requires a matching
    /// `Authorization: Bearer ...` header unless <paramref name="allowUnauthenticatedLoopback"/>
    /// explicitly opts a loopback-only listener into unsafe mode. Non-loopback listeners always
    /// require a bearer secret.
    /// 指定プレフィックスに HTTP transport を bind する。すべての request に bearer secret を要求し、
    /// <paramref name="allowUnauthenticatedLoopback"/> が明示的に unsafe mode を選んだ loopback listener
    /// だけを例外とする。non-loopback listener は常に bearer secret が必要。
    /// </summary>
    internal HttpMcpTransport(
        string prefix,
        string host,
        int boundPort,
        string? bearerToken,
        Action<HttpRequestLogRecord>? requestLogger = null,
        int? maxRequestBodyBytes = null,
        int? maxInFlightRequestBodyBytes = null,
        TimeSpan? requestBodyIdleTimeout = null,
        TimeSpan? requestLifetimeTimeout = null,
        int? maxResponseBodyBytes = null,
        int? maxQueuedRequests = null,
        int? maxConcurrentHandlers = null,
        int? maxEventStreams = null,
        int? requestLogQueueCapacity = null,
        TimeSpan? eventStreamWriteTimeout = null,
        TimeSpan? requestDisconnectProbeInterval = null,
        TimeSpan? responseWriteTimeout = null,
        bool allowUnauthenticatedLoopback = false)
    {
        _maxRequestBodyBytes = ResolvePositiveIntOption(
            maxRequestBodyBytes,
            nameof(maxRequestBodyBytes),
            MaxRequestBodyBytesEnvVar,
            DefaultMaxRequestBodyBytes,
            MaxConfiguredRequestBodyBytes,
            "HTTP MCP request body byte limit");
        _maxInFlightRequestBodyBytes = ResolvePositiveIntOption(
            maxInFlightRequestBodyBytes,
            nameof(maxInFlightRequestBodyBytes),
            MaxInFlightRequestBodyBytesEnvVar,
            DefaultMaxInFlightRequestBodyBytes,
            MaxConfiguredInFlightRequestBodyBytes,
            "HTTP MCP process-wide in-flight request body byte budget");
        if (_maxInFlightRequestBodyBytes < _maxRequestBodyBytes)
        {
            throw new FormatException(
                $"{MaxInFlightRequestBodyBytesEnvVar} ({_maxInFlightRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}) must be greater than or equal to {MaxRequestBodyBytesEnvVar} ({_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}).");
        }
        _requestBodyIdleTimeout = ResolveTimeoutOption(
            requestBodyIdleTimeout,
            nameof(requestBodyIdleTimeout),
            RequestBodyIdleTimeoutMillisecondsEnvVar,
            DefaultRequestBodyIdleTimeoutMilliseconds,
            MaxRequestBodyIdleTimeoutMilliseconds,
            "HTTP MCP request body idle timeout");
        _requestLifetimeTimeout = ResolveTimeoutOption(
            requestLifetimeTimeout,
            nameof(requestLifetimeTimeout),
            RequestLifetimeTimeoutMillisecondsEnvVar,
            DefaultRequestLifetimeTimeoutMilliseconds,
            MaxRequestLifetimeTimeoutMilliseconds,
            "HTTP MCP total request timeout");
        if (_requestLifetimeTimeout < _requestBodyIdleTimeout)
        {
            throw new FormatException(
                $"{RequestLifetimeTimeoutMillisecondsEnvVar} ({_requestLifetimeTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}) must be greater than or equal to {RequestBodyIdleTimeoutMillisecondsEnvVar} ({_requestBodyIdleTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}).");
        }
        _maxResponseBodyBytes = ResolvePositiveIntOption(
            maxResponseBodyBytes,
            nameof(maxResponseBodyBytes),
            MaxResponseBodyBytesEnvVar,
            DefaultMaxResponseBodyBytes,
            MaxConfiguredResponseBodyBytes,
            "HTTP MCP response body byte limit");
        _maxQueuedRequests = ResolvePositiveIntOption(
            maxQueuedRequests,
            nameof(maxQueuedRequests),
            MaxQueueDepthEnvVar,
            DefaultMaxQueuedRequests,
            MaxConfiguredQueuedRequests,
            "HTTP MCP request queue depth");
        _maxConcurrentHandlers = ResolvePositiveIntOption(
            maxConcurrentHandlers,
            nameof(maxConcurrentHandlers),
            MaxConcurrentHandlersEnvVar,
            DefaultMaxConcurrentHandlers,
            MaxConfiguredConcurrentHandlers,
            "HTTP MCP concurrent handler limit");
        _maxEventStreams = ResolvePositiveIntOption(
            maxEventStreams,
            nameof(maxEventStreams),
            MaxEventStreamsEnvVar,
            DefaultMaxEventStreams,
            MaxConfiguredEventStreams,
            "HTTP MCP event stream limit");
        _eventStreamWriteTimeout = eventStreamWriteTimeout ?? EventStreamWriteTimeout;
        _requestDisconnectProbeInterval = requestDisconnectProbeInterval ?? RequestDisconnectProbeInterval;
        if (_requestDisconnectProbeInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestDisconnectProbeInterval), "HTTP MCP disconnect probe interval must be positive.");
        _responseWriteTimeout = responseWriteTimeout ?? ResponseWriteTimeout;
        if (_responseWriteTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseWriteTimeout), "HTTP MCP response write timeout must be positive.");
        _requestLogQueueCapacity = ResolveRequestLogQueueCapacity(requestLogQueueCapacity);
        _queueSlots = new SemaphoreSlim(_maxQueuedRequests, _maxQueuedRequests);
        if (bearerToken is { Length: > 0 } && !McpAuthenticationLimits.IsTokenShapeValid(bearerToken))
            throw new ArgumentException(McpAuthenticationLimits.FormatTokenShapeError("Token"), nameof(bearerToken));
        if (bearerToken is { Length: > 0 } && bearerToken.Contains(',', StringComparison.Ordinal))
            throw new ArgumentException("HTTP bearer token must not contain commas; commas are reserved for rejecting ambiguous Authorization headers.", nameof(bearerToken));
        IsLoopbackBind = IsLoopbackHost(host);
        if (string.IsNullOrEmpty(bearerToken) && (!IsLoopbackBind || !allowUnauthenticatedLoopback))
        {
            var message = IsLoopbackBind
                ? "HTTP MCP requires bearer authentication by default; unauthenticated loopback requires an explicit unsafe opt-in."
                : "HTTP MCP requires bearer authentication when binding outside loopback.";
            throw new ArgumentException(message, nameof(bearerToken));
        }
        _bearerTokenHash = string.IsNullOrEmpty(bearerToken)
            ? null
            : McpAuthenticationLimits.HashTokenToArray(bearerToken);
        _handlerSemaphore = new SemaphoreSlim(_maxConcurrentHandlers, _maxConcurrentHandlers);
        // Event streams have their own admission gate so long-lived SSE connections cannot
        // consume the handler capacity reserved for POST and other short-lived requests (#4550).
        // 長寿命 SSE 接続が POST 等の短命 request 用 handler capacity を消費しないよう、
        // event stream は独立した admission gate で制御する (#4550)。
        _eventStreamHandlerSemaphore = new SemaphoreSlim(_maxEventStreams, _maxEventStreams);
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _allowedOrigin = new Uri(prefix, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
        _listener.Start();
        _requestLogger = requestLogger;
        if (_requestLogger is not null)
        {
            _requestLogQueue = Channel.CreateBounded<HttpRequestLogRecord>(new BoundedChannelOptions(_requestLogQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // Request logging is best-effort. Producers use TryWrite and increment
                // http_request_log_queue_full_drop_count when this bounded channel is full.
                // request log は best-effort。producer は TryWrite を使い、満杯時は
                // http_request_log_queue_full_drop_count を増やして drop する。
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _requestLogTask = BackgroundTaskObserver.Run(DrainRequestLogQueueAsync, "cdidx-mcp-http", "request log writer");
        }
        _endpoint = $"http://{host}:{boundPort}/";
        _acceptLoop = BackgroundTaskObserver.Run(
            token => AcceptLoopAsync(token),
            "cdidx-mcp-http",
            "accept loop",
            _acceptCts.Token);
    }

    internal Func<CancellationToken, Task>? BeforeEventStreamWriteForTests { get; set; }

    internal Func<CancellationToken, Task>? BeforeEventStreamPublishForTests { get; set; }

    internal Func<CancellationToken, Task>? BeforeResponseWriteForTests { get; set; }

    internal Func<CancellationToken, Task>? ResponseOutputWriteForTests { get; set; }

    internal Func<byte[], CancellationToken, Task>? EventStreamOutputWriteForTests { get; set; }

    internal Func<CancellationToken, Task>? BeforeRequestDisconnectProbeWriteForTests { get; set; }

    internal Func<CancellationToken, Task>? RequestDisconnectProbeOutputWriteForTests { get; set; }

    internal Action? BeforeRequestCancellationQueueRemovalForTests { get; set; }

    public string Name => "http";

    public string Endpoint => _endpoint;

    internal bool RequiresBearerToken => _bearerTokenHash is not null;

    internal bool IsLoopbackBind { get; }

    internal bool AuthDisabled => !RequiresBearerToken;

    internal string? AuthDisabledWarning => AuthDisabled ? LoopbackAuthDisabledWarning : null;

    internal bool OwnedSemaphoreGatesDisposedForTests => Volatile.Read(ref _ownedSemaphoreGatesDisposed);

    internal bool RequestQueueSignalCompletedForTests
    {
        get
        {
            lock (_requestQueueSync)
                return _requestAvailable.Task.IsCompleted;
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposeStarted) != 0;

    private bool IsSessionEstablished => Volatile.Read(ref _sessionEstablished) != 0;

    internal Func<string, CancellationToken, Task<string?>>? OutOfBandFrameHandler { get; set; }

    internal Func<string>? HealthJsonProvider { get; set; }

    internal TimeSpan? KeepAliveInterval { get; set; }

    internal Func<string>? KeepAliveFrameProvider { get; set; }

    internal bool HasEventStreams => !_eventStreams.IsEmpty;

    internal int MaxRequestBodyBytes => _maxRequestBodyBytes;

    internal int MaxInFlightRequestBodyBytes => _maxInFlightRequestBodyBytes;

    internal TimeSpan RequestBodyIdleTimeout => _requestBodyIdleTimeout;

    internal TimeSpan RequestLifetimeTimeout => _requestLifetimeTimeout;

    internal int MaxResponseBodyBytes => _maxResponseBodyBytes;

    int IMcpResponseSizeLimitProvider.MaxResponseFrameBytes => _maxResponseBodyBytes;

    internal int MaxQueuedRequests => _maxQueuedRequests;

    internal int MaxConcurrentHandlers => _maxConcurrentHandlers;

    internal int MaxEventStreams => _maxEventStreams;

    internal int PostHandlerCapacity => _maxConcurrentHandlers;

    internal int EventStreamHandlerCapacity => _maxEventStreams;

    internal int EventStreamHandlerSlotsAvailableForTests
        => _eventStreamHandlerSemaphore.CurrentCount;

    internal int AbandonedResponseOutputOperationCount
        => _abandonedResponseOutputOperations.Count;

    internal bool UsesSeparateEventStreamHandlers => true;

    internal int RequestLogQueueCapacity => _requestLogQueue is null ? 0 : _requestLogQueueCapacity;

    internal int QueuedRequestCount => Volatile.Read(ref _queuedRequestCount);

    internal int RequestLogQueueDepth => _requestLogQueue is null ? 0 : Math.Max(0, Volatile.Read(ref _pendingRequestLogCount));

    internal int EventStreamCount => Volatile.Read(ref _eventStreamCount);

    internal long RequestLogDroppedCount => Interlocked.Read(ref _requestLogDroppedCount);

    internal long RequestLogQueueFullDropCount => Interlocked.Read(ref _requestLogQueueFullDropCount);

    internal long RequestLogCallbackFailureCount => Interlocked.Read(ref _requestLogCallbackFailureCount);

    internal long ResponseAbortCleanupFailureCount => Interlocked.Read(ref _responseAbortCleanupFailureCount);

    internal long ResponseCloseCleanupFailureCount => Interlocked.Read(ref _responseCloseCleanupFailureCount);

    internal long ConcurrentHandlerLimitRejectionCount => Interlocked.Read(ref _concurrentHandlerLimitRejectionCount);

    internal long RequestQueueLimitRejectionCount => Interlocked.Read(ref _requestQueueLimitRejectionCount);

    internal long RequestBodyBudgetLimitRejectionCount => Interlocked.Read(ref _requestBodyBudgetLimitRejectionCount);

    internal long RequestBodyIdleTimeoutCount => Interlocked.Read(ref _requestBodyIdleTimeoutCount);

    internal long RequestLifetimeTimeoutCount => Interlocked.Read(ref _requestLifetimeTimeoutCount);

    internal long ClientDisconnectCount => Interlocked.Read(ref _clientDisconnectCount);

    internal long QueuedRequestCancellationCount => Interlocked.Read(ref _queuedRequestCancellationCount);

    internal long InFlightRequestBodyBytes => Interlocked.Read(ref _inFlightRequestBodyBytes);

    internal long PeakInFlightRequestBodyBytes => Interlocked.Read(ref _peakInFlightRequestBodyBytes);

    internal long ProcessInFlightRequestBodyBytes => ProcessRequestBodyBudget.CurrentBytes;

    internal CancellationToken CurrentRequestCancellationToken
        => Volatile.Read(ref _pendingRequest)?.CancellationToken ?? CancellationToken.None;

    internal long EventStreamLimitRejectionCount => Interlocked.Read(ref _eventStreamLimitRejectionCount);

    internal long EventStreamDropCount => Interlocked.Read(ref _eventStreamDropCount);

    internal long EventStreamWriteFailureDropCount => Interlocked.Read(ref _eventStreamWriteFailureDropCount);

    internal long AuthDenialCount => AuthDenialMissingCount
        + AuthDenialAmbiguousCount
        + AuthDenialWrongSchemeCount
        + AuthDenialMalformedTokenCount
        + AuthDenialOversizedTokenCount
        + AuthDenialWrongTokenCount;

    internal long AuthDenialMissingCount => Interlocked.Read(ref _authDenialMissingCount);

    internal long AuthDenialAmbiguousCount => Interlocked.Read(ref _authDenialAmbiguousCount);

    internal long AuthDenialWrongSchemeCount => Interlocked.Read(ref _authDenialWrongSchemeCount);

    internal long AuthDenialMalformedTokenCount => Interlocked.Read(ref _authDenialMalformedTokenCount);

    internal long AuthDenialOversizedTokenCount => Interlocked.Read(ref _authDenialOversizedTokenCount);

    internal long AuthDenialWrongTokenCount => Interlocked.Read(ref _authDenialWrongTokenCount);

    internal bool ResponseCleanupDegraded => ResponseAbortCleanupFailureCount > 0 || ResponseCloseCleanupFailureCount > 0;

    internal bool RequestLogDegraded => RequestLogDroppedCount > 0;

    internal string? LastRequestLogDropReason => Volatile.Read(ref _lastRequestLogDropReason);

    internal string? LastResponseAbortCleanupFailure => Volatile.Read(ref _lastResponseAbortCleanupFailure);

    internal string? LastResponseCloseCleanupFailure => Volatile.Read(ref _lastResponseCloseCleanupFailure);

    internal string? LastEventStreamDropReason => Volatile.Read(ref _lastEventStreamDropReason);

    internal string? LastAuthDenialReason => Volatile.Read(ref _lastAuthDenialReason);

    /// <summary>
    /// Resolve a `host:port` listen spec into the corresponding HTTP prefix. Ephemeral ports
    /// (port `0`) are resolved up-front by binding a temporary <see cref="TcpListener"/> so the
    /// caller can immediately log the bound port — there is a small TOCTOU window between
    /// closing the probe and binding <see cref="HttpListener"/>, accepted because the HTTP
    /// transport is documented as local-only / single-tenant.
    /// `host:port` の listen 仕様を HTTP プレフィックスに解決する。ポート 0 (ephemeral) は
    /// <see cref="TcpListener"/> を一時的に bind して空きポートを取得してから返すため、呼び出し側は
    /// 即座にバインドされたポートを stderr に出せる。TOCTOU は存在するが、HTTP トランスポートは
    /// ローカル単独利用を想定しているため許容する。
    /// </summary>
    internal static HttpListenSpec ResolveListenSpec(string listenSpec)
    {
        if (string.IsNullOrWhiteSpace(listenSpec))
            throw new FormatException("--http-listen value must not be empty.");

        var (host, port) = ParseHostPort(listenSpec);
        var displayHost = host;
        var prefixHost = NormalizePrefixHost(host);
        var ipAddress = ResolveLoopbackIp(host);
        var portWasEphemeral = port == 0;

        var isLoopback = ipAddress is not null && IPAddress.IsLoopback(ipAddress);

        if (portWasEphemeral)
            port = FindFreePort(ipAddress ?? IPAddress.Loopback);

        var prefix = $"http://{prefixHost}:{port.ToString(CultureInfo.InvariantCulture)}/";
        return new HttpListenSpec(prefix, displayHost, port, isLoopback, portWasEphemeral);
    }

    private static (string host, int port) ParseHostPort(string spec)
    {
        // Accept `host:port` and `[ipv6]:port`. Reject anything else so we don't silently
        // bind to surprising endpoints. The default listen string `127.0.0.1:38080` keeps
        // `cdidx mcp --transport http` usable without any extra flags.
        // `host:port` と `[ipv6]:port` を受け付ける。それ以外は黙って予想外のアドレスに
        // bind しないよう拒否する。既定 `127.0.0.1:38080` でフラグ追加なしに使えるようにする。
        if (spec.StartsWith('['))
        {
            var close = spec.IndexOf(']');
            if (close <= 1 || close + 2 >= spec.Length || spec[close + 1] != ':')
                throw new FormatException($"--http-listen value '{spec}' is not a valid host:port (expected '[ipv6]:port').");
            var host6 = spec.Substring(1, close - 1);
            var portText6 = spec.Substring(close + 2);
            if (!int.TryParse(portText6, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port6) || port6 < 0 || port6 > 65535)
                throw new FormatException($"--http-listen value '{spec}' has an invalid port '{portText6}'.");
            return (host6, port6);
        }

        var colon = spec.LastIndexOf(':');
        if (colon <= 0 || colon >= spec.Length - 1)
            throw new FormatException($"--http-listen value '{spec}' is not a valid host:port.");
        var host = spec.Substring(0, colon);
        var portText = spec.Substring(colon + 1);
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port < 0 || port > 65535)
            throw new FormatException($"--http-listen value '{spec}' has an invalid port '{portText}'.");
        return (host, port);
    }

    private static string NormalizePrefixHost(string host)
    {
        // HttpListener accepts `localhost`, `127.0.0.1`, or `+` / `*` (which we reject up-front
        // to avoid surprise public bind). IPv6 hosts must be wrapped in `[...]` to satisfy the
        // prefix grammar.
        if (host is "+" or "*")
            throw new FormatException("--http-listen rejects wildcard hosts; bind to a loopback address explicitly.");
        if (host.Contains(':'))
            return $"[{host}]";
        return host;
    }

    private static IPAddress? ResolveLoopbackIp(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        return IPAddress.TryParse(host, out var ip) ? ip : null;
    }

    private static bool IsLoopbackHost(string host)
    {
        var ip = ResolveLoopbackIp(host);
        return ip is not null && IPAddress.IsLoopback(ip);
    }

    internal static string FormatBindFailureDiagnostic(HttpListenSpec listenSpec, Exception exception)
    {
        var prefix = LimitRequestLogField(listenSpec.Prefix) ?? "<unknown>";
        var message = DiagnosticRedactor.FormatExceptionMessage(
            exception,
            MaxRequestLogFieldCharacters);
        var diagnostic = $"failed to bind HTTP listener on {prefix}: {message}";
        if (listenSpec.PortWasEphemeral)
        {
            diagnostic += " The listener port came from a port-0 probe; another process may have claimed it before the final bind. Retry or choose an explicit --http-listen port.";
        }
        return diagnostic;
    }

    private static int FindFreePort(IPAddress address)
    {
        var probe = new TcpListener(address, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static int ResolvePositiveIntOption(
        int? explicitValue,
        string explicitValueName,
        string envVar,
        int defaultValue,
        int maximumValue,
        string description)
    {
        if (explicitValue is { } configured)
        {
            if (configured <= 0)
                throw new ArgumentOutOfRangeException(
                    explicitValueName,
                    configured,
                    $"{description} must be between 1 and {maximumValue.ToString(CultureInfo.InvariantCulture)}.");
            if (configured > maximumValue)
                throw new ArgumentOutOfRangeException(
                    explicitValueName,
                    configured,
                    $"{description} must be between 1 and {maximumValue.ToString(CultureInfo.InvariantCulture)}.");
            return configured;
        }

        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(envVar);
        if (raw is null)
            return defaultValue;

        if (!BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0
            || parsed > maximumValue)
        {
            throw new FormatException(
                $"{envVar} must be an integer between 1 and {maximumValue.ToString(CultureInfo.InvariantCulture)} for {description}.");
        }

        return (int)parsed;
    }

    private static TimeSpan ResolveTimeoutOption(
        TimeSpan? explicitValue,
        string explicitValueName,
        string envVar,
        int defaultMilliseconds,
        int maximumMilliseconds,
        string description)
    {
        if (explicitValue is { } configured)
        {
            if (configured < TimeSpan.FromMilliseconds(1)
                || configured > TimeSpan.FromMilliseconds(maximumMilliseconds))
            {
                throw new ArgumentOutOfRangeException(
                    explicitValueName,
                    configured,
                    $"{description} must be between 1 and {maximumMilliseconds.ToString(CultureInfo.InvariantCulture)} milliseconds.");
            }

            return configured;
        }

        return TimeSpan.FromMilliseconds(ResolvePositiveIntOption(
            null,
            explicitValueName,
            envVar,
            defaultMilliseconds,
            maximumMilliseconds,
            description));
    }

    private static int ResolveRequestLogQueueCapacity(int? explicitValue)
    {
        var capacity = explicitValue ?? DefaultRequestLogQueueCapacity;
        if (capacity <= 0 || capacity > MaxConfiguredRequestLogQueueCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(explicitValue),
                capacity,
                $"HTTP MCP request log queue depth must be between 1 and {MaxConfiguredRequestLogQueueCapacity.ToString(CultureInfo.InvariantCulture)}.");
        return capacity;
    }

    public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _requestQueueReaderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _pendingRequest) is not null)
                throw new InvalidOperationException("HttpMcpTransport: ReadFrameAsync called twice without an intervening WriteFrameAsync.");

            var request = await DequeueRequestAsync(cancellationToken).ConfigureAwait(false);
            if (request is null)
                return null;
            if (Interlocked.CompareExchange(ref _pendingRequest, request, null) is not null)
                throw new InvalidOperationException("HttpMcpTransport: pending request handoff was not empty.");
            if (IsDisposed)
            {
                var disposedRequest = Interlocked.Exchange(ref _pendingRequest, null);
                if (disposedRequest is not null)
                    _ = ReleaseRequestForDispose(disposedRequest, "dequeued request disposal");
                return null;
            }
            return request.Body;
        }
        finally
        {
            _requestQueueReaderSemaphore.Release();
        }
    }

    public async Task<McpTransportFrame?> ReadConcurrentFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _requestQueueReaderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = await DequeueRequestAsync(cancellationToken).ConfigureAwait(false);
            if (request is null)
                return null;
            if (IsDisposed)
            {
                _ = ReleaseRequestForDispose(request, "concurrent dequeued request disposal");
                return null;
            }

            // Attach a request-local placeholder before publishing the frame. Disposal or the
            // response writer may request body-budget release immediately after handoff, but the
            // placeholder keeps that release deferred until the server identifies detached work.
            // frame 公開前に request-local placeholder を接続する。handoff 直後に dispose / writer が
            // body budget 解放を要求しても、server が detached work を確定するまで解放を遅延する。
            var retentionBarrier = new RequestResourceRetentionBarrier();
            if (!request.TryRetainRequestResourcesUntil(retentionBarrier.Completion))
            {
                _ = ReleaseRequestForDispose(request, "concurrent request retention handoff failure");
                throw new InvalidOperationException(
                    "HTTP MCP request resources were released before the frame-local retention barrier could be attached.");
            }

            if (!_activeConcurrentRequests.TryAdd(request, 0))
            {
                retentionBarrier.CompleteWhen(Task.CompletedTask);
                _ = ReleaseRequestForDispose(request, "duplicate concurrent request handoff");
                throw new InvalidOperationException("HTTP MCP concurrent request handoff was already active.");
            }

            if (IsDisposed)
            {
                _activeConcurrentRequests.TryRemove(request, out _);
                retentionBarrier.CompleteWhen(Task.CompletedTask);
                _ = ReleaseRequestForDispose(request, "concurrent request disposal after handoff");
                return null;
            }

            return new McpTransportFrame(
                request.Body ?? string.Empty,
                (frame, writeToken) => WriteFrameAsync(request, frame, writeToken),
                request.CancellationToken,
                retentionBarrier.CompleteWhen);
        }
        finally
        {
            _requestQueueReaderSemaphore.Release();
        }
    }

    private async Task<PendingRequest?> DequeueRequestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? requestAvailableTask = null;
            PendingRequest? request = null;
            string? cancellationReason = null;
            lock (_requestQueueSync)
            {
                if (_requestQueue.First is { } node)
                {
                    _requestQueue.Remove(node);
                    ResetRequestAvailableSignalIfQueueEmpty();
                    request = node.Value;
                    request.QueueNode = null;
                    Interlocked.Decrement(ref _queuedRequestCount);
                    _queueSlots.Release();
                    cancellationReason = request.CancellationReason;
                }
                else if (_requestQueueCompleted)
                {
                    return null;
                }
                else
                {
                    requestAvailableTask = _requestAvailable.Task;
                }
            }

            if (request is not null)
            {
                if (cancellationReason is null)
                    return request;

                Interlocked.Increment(ref _queuedRequestCancellationCount);
                request.Body = null;
                AbortResponseBestEffort(request.Context.Response, "cancelled dequeued request");
                ReleasePendingInitialize(request);
                ReleaseRequestBodyReservation(request);
                LogRequest(request, CancellationStatusCode(cancellationReason));
                request.DisposeLifetime();
                continue;
            }

            await requestAvailableTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> CreateRequestAvailableSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetRequestAvailableSignalIfQueueEmpty()
    {
        if (_requestQueue.Count == 0 && !_requestQueueCompleted && _requestAvailable.Task.IsCompleted)
            _requestAvailable = CreateRequestAvailableSignal();
    }
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var eventStreamRequest = IsEventStreamRequest(context.Request);
                var admissionGate = eventStreamRequest
                    ? _eventStreamHandlerSemaphore
                    : _handlerSemaphore;
                if (!admissionGate.Wait(0))
                {
                    if (eventStreamRequest)
                        BeginEventStreamLimitRejection(context);
                    else
                        await RejectHandlerLimitAsync(context).ConfigureAwait(false);
                    continue;
                }

                // The handler owns a semaphore slot. Do not use the shutdown token as the
                // scheduling token here, because a pre-canceled Task.Run would skip the
                // handler's finally block and leak the slot.
                // handler は semaphore slot を所有する。pre-canceled Task.Run で finally が
                // 走らず slot が漏れないよう、shutdown token は handler 内だけに渡す。
                _ = BackgroundTaskObserver.Run(
                    () => RunHandlerAsync(context, cancellationToken, admissionGate),
                    "cdidx-mcp-http",
                    "request handler");
            }
        }
        finally
        {
            CompleteRequestQueue();
        }
    }

    private void CompleteRequestQueue()
    {
        lock (_requestQueueSync)
        {
            if (_requestQueueCompleted)
                return;
            _requestQueueCompleted = true;
            _requestAvailable.TrySetResult(true);
        }
    }

    private async Task RunHandlerAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken,
        SemaphoreSlim admissionGate)
    {
        try
        {
            await HandleContextAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            admissionGate.Release();
        }
    }

    private static bool IsEventStreamRequest(HttpListenerRequest request)
        => string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            && IsEventsPath(request.Url?.AbsolutePath);

    private async Task RejectHandlerLimitAsync(HttpListenerContext context)
    {
        var request = BeginRequest(context);
        request.AuthOutcome = "not-checked";
        MarkRejected(request, ConcurrentHandlerLimitRejection);
        context.Response.AddHeader("Retry-After", "1");
        context.Response.AddHeader(RejectionReasonHeader, ConcurrentHandlerLimitRejection);
        await RespondAsync(request, (int)HttpStatusCode.TooManyRequests, "MCP HTTP concurrent handler limit is full.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.TooManyRequests);
    }

    private async Task RejectEventStreamLimitAsync(HttpListenerContext context)
    {
        var request = BeginRequest(context);
        request.AuthOutcome = "not-checked";
        MarkRejected(request, EventStreamLimitRejection);
        context.Response.AddHeader("Retry-After", "1");
        context.Response.AddHeader(RejectionReasonHeader, EventStreamLimitRejection);
        await RespondAsync(request, (int)HttpStatusCode.TooManyRequests, "MCP HTTP event stream limit is full.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.TooManyRequests);
    }

    private void BeginEventStreamLimitRejection(HttpListenerContext context)
    {
        // A saturated long-lived SSE gate must never make the single accept loop await a client
        // that does not read its 429 response. A small independent pool owns bounded rejection
        // writes; excess rejected connections are aborted immediately (#4550).
        // 長寿命 SSE gate 飽和時に 429 を読まない client が単一 accept loop を塞がないよう、
        // 独立した小さな pool が bounded rejection write を所有し、超過分は即時 abort する (#4550)。
        if (!_eventStreamRejectionSemaphore.Wait(0))
        {
            Interlocked.Increment(ref _eventStreamLimitRejectionCount);
            AbortResponseBestEffort(context.Response, "event stream rejection capacity full");
            return;
        }

        _ = BackgroundTaskObserver.Run(
            async () =>
            {
                try
                {
                    await RejectEventStreamLimitAsync(context).ConfigureAwait(false);
                }
                finally
                {
                    _eventStreamRejectionSemaphore.Release();
                }
            },
            "cdidx-mcp-http",
            "event stream limit rejection");
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = BeginRequest(context, cancellationToken);
        var reservationTransferredToQueue = false;
        try
        {
            if (!TryValidateOrigin(context.Request))
            {
                request.AuthOutcome = "not-checked";
                request.Diagnostic = OriginRejectedDiagnostic;
                await RespondAsync(request, (int)HttpStatusCode.Forbidden, "Origin is not allowed for this MCP HTTP listener.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.Forbidden);
                return;
            }

            if (IsCorsPreflightRequest(context.Request))
            {
                request.AuthOutcome = "not-checked";
                request.Diagnostic = PreflightRejectedDiagnostic;
                await RespondAsync(request, (int)HttpStatusCode.Forbidden, "CORS preflight is not supported by this MCP HTTP listener.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.Forbidden);
                return;
            }

            if (!await TryAuthorizeAsync(request).ConfigureAwait(false))
                return;

            if (IsHealthPath(context.Request.Url?.AbsolutePath))
            {
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.AddHeader("Allow", "GET");
                    await RespondAsync(request, (int)HttpStatusCode.MethodNotAllowed, "MCP health endpoint only accepts GET.\n").ConfigureAwait(false);
                    LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
                    return;
                }

                var healthJson = ResolveHealthJson(HealthJsonProvider);
                await RespondJsonAsync(request, (int)HttpStatusCode.OK, healthJson).ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.OK);
                return;
            }

            if (IsEventsPath(context.Request.Url?.AbsolutePath))
            {
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.AddHeader("Allow", "GET");
                    await RespondAsync(request, (int)HttpStatusCode.MethodNotAllowed, "MCP HTTP event stream only accepts GET.\n").ConfigureAwait(false);
                    LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
                    return;
                }

                if (!await TryRequireEstablishedSessionAsync(request).ConfigureAwait(false))
                    return;

                await RunEventStreamAsync(request, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.AddHeader("Allow", "POST");
                await RespondAsync(request, (int)HttpStatusCode.MethodNotAllowed, "MCP HTTP transport only accepts POST.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
                return;
            }

            request.StartLifetime(
                _requestLifetimeTimeout,
                OnRequestCancellation,
                TrackRequestCancellationDelivery);
            if (!await TryValidateJsonContentTypeAsync(request).ConfigureAwait(false))
                return;

            // Once initialize succeeds, reject missing or foreign sessions before reading a POST
            // body so an unrelated HTTP client cannot consume request-body or JSON-RPC resources.
            // initialize 成功後は POST body を読む前に欠落・別 session を拒否し、無関係な HTTP
            // client に request-body / JSON-RPC resource を消費させない。
            if (IsSessionEstablished
                && !await TryValidateSessionHeaderAsync(request).ConfigureAwait(false))
            {
                return;
            }

            var body = await TryReadRequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
                return;

            request.Body = body;
            request.RequestId = TryExtractJsonRpcIdTelemetry(body, _maxRequestBodyBytes);
            if (!request.SessionValidated)
            {
                // The session can become established while this handler is reading its body.
                // Recheck after the bounded read so no headerless frame can slip behind
                // initialize in the single-reader queue. Before establishment, only one
                // response-bearing initialize request may claim the pending session.
                // body 読み込み中に session が確立し得るため、bounded read 後に再確認する。
                // これにより header 無し frame が initialize の後ろへ queue される race を防ぐ。
                // 確立前に pending session を claim できるのは応答対象 initialize 1 件だけ。
                if (IsSessionEstablished)
                {
                    if (!await TryValidateSessionHeaderAsync(request).ConfigureAwait(false))
                        return;
                }
                else if (!await TryClaimPendingInitializeAsync(request, body).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                CloseResponseOrThrow(context.Response, "empty request body");
                LogRequest(request, (int)HttpStatusCode.NoContent);
                return;
            }

            request.ExpectsResponse = ExpectsJsonRpcResponse(body, _maxRequestBodyBytes);
            if (await TryHandleOutOfBandFrameAsync(request, body, request.CancellationToken).ConfigureAwait(false))
            {
                ReleasePendingInitialize(request);
                return;
            }

            if (!TryQueueRequest(request))
            {
                ReleasePendingInitialize(request);
                if (request.CancellationToken.IsCancellationRequested)
                {
                    await CompletePreQueueCancellationAsync(request).ConfigureAwait(false);
                    return;
                }

                MarkRejected(request, RequestQueueLimitRejection);
                context.Response.AddHeader("Retry-After", "1");
                context.Response.AddHeader(RejectionReasonHeader, RequestQueueLimitRejection);
                await RespondAsync(request, (int)HttpStatusCode.TooManyRequests, "MCP HTTP request queue is full.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.TooManyRequests);
            }
            else
            {
                reservationTransferredToQueue = true;
            }
        }
        finally
        {
            if (!reservationTransferredToQueue)
            {
                ReleasePendingInitialize(request);
                request.Body = null;
                ReleaseRequestBodyReservation(request);
                request.DisposeLifetime();
            }
        }
    }

    private bool OnRequestCancellation(PendingRequest request, string reason)
    {
        switch (reason)
        {
            case RequestBodyIdleTimeoutDiagnostic:
                Interlocked.Increment(ref _requestBodyIdleTimeoutCount);
                break;
            case RequestLifetimeTimeoutDiagnostic:
                Interlocked.Increment(ref _requestLifetimeTimeoutCount);
                break;
            case RequestDisconnectProbeWriteTimeoutDiagnostic:
                break;
            case ClientDisconnectedDiagnostic:
                Interlocked.Increment(ref _clientDisconnectCount);
                break;
            case TransportShutdownDiagnostic:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown HTTP MCP request cancellation reason.");
        }

        request.Diagnostic = reason;
        try { request.Context.Request.InputStream.Close(); } catch { /* unblock is best-effort */ }
        BeforeRequestCancellationQueueRemovalForTests?.Invoke();

        if (TryRemoveQueuedRequest(request))
        {
            Interlocked.Increment(ref _queuedRequestCancellationCount);
            request.Body = null;
            AbortResponseBestEffort(request.Context.Response, "cancelled queued request");
            ReleasePendingInitialize(request);
            ReleaseRequestBodyReservation(request);
            LogRequest(request, CancellationStatusCode(reason));
            return true;
        }

        if (ReferenceEquals(Volatile.Read(ref _pendingRequest), request)
            || _activeConcurrentRequests.ContainsKey(request))
            AbortResponseBestEffort(request.Context.Response, "cancelled executing request");
        return false;
    }

    private bool TryRemoveQueuedRequest(PendingRequest request)
    {
        lock (_requestQueueSync)
        {
            if (request.QueueNode is not { List: not null } node)
                return false;

            _requestQueue.Remove(node);
            ResetRequestAvailableSignalIfQueueEmpty();
            request.QueueNode = null;
            Interlocked.Decrement(ref _queuedRequestCount);
            _queueSlots.Release();
            return true;
        }
    }

    private async Task CompletePreQueueCancellationAsync(PendingRequest request)
    {
        var reason = request.CancellationReason ?? TransportShutdownDiagnostic;
        if (reason is RequestBodyIdleTimeoutDiagnostic
            or RequestLifetimeTimeoutDiagnostic
            or RequestDisconnectProbeWriteTimeoutDiagnostic)
        {
            await RespondAsync(
                request.Context,
                (int)HttpStatusCode.RequestTimeout,
                "MCP HTTP request deadline expired.\n").ConfigureAwait(false);
        }
        else
        {
            AbortResponseBestEffort(request.Context.Response, "cancelled request before queue handoff");
        }

        LogRequest(request, CancellationStatusCode(reason));
    }

    private static int CancellationStatusCode(string reason)
        => reason is RequestBodyIdleTimeoutDiagnostic
            or RequestLifetimeTimeoutDiagnostic
            or RequestDisconnectProbeWriteTimeoutDiagnostic
            ? (int)HttpStatusCode.RequestTimeout
            : 499;

    private async Task<string?> TryReadRequestBodyAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await TryReadRequestBodyCoreAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRequestDeadlineCancellation(request, ex))
        {
            await CompletePreQueueCancellationAsync(request).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.TryCancel(TransportShutdownDiagnostic);
            AbortResponseBestEffort(request.Context.Response, "request body read shutdown");
            LogRequest(request, 499);
            return null;
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or SocketException)
        {
            request.TryCancel(ClientDisconnectedDiagnostic);
            AbortResponseBestEffort(request.Context.Response, "request body client disconnect");
            LogRequest(request, 499);
            return null;
        }
    }

    private async Task<string?> TryReadRequestBodyCoreAsync(PendingRequest request)
    {
        var context = request.Context;
        var contentLength = context.Request.ContentLength64;
        if (contentLength > _maxRequestBodyBytes)
        {
            request.Diagnostic = "request_body_limit_exceeded";
            await RespondAsync(request, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
            return null;
        }
        if (contentLength < 0)
            request.Diagnostic = "request_body_length_unknown";

        if (contentLength > 0 && !TryReserveRequestBodyBytesExact(request, checked((int)contentLength)))
        {
            await RejectRequestBodyBudgetAsync(request).ConfigureAwait(false);
            return null;
        }

        using var buffer = new MemoryStream(contentLength > 0 ? checked((int)contentLength) : 0);
        var scratch = new byte[Math.Min(8192, _maxRequestBodyBytes)];
        if (contentLength >= 0)
        {
            while (buffer.Length < contentLength)
            {
                var readSize = (int)Math.Min(scratch.Length, contentLength - buffer.Length);
                var read = await ReadRequestBodyChunkAsync(request, scratch.AsMemory(0, readSize)).ConfigureAwait(false);
                if (read == 0)
                {
                    request.TryCancel(ClientDisconnectedDiagnostic);
                    throw new IOException("HTTP MCP request body ended before the declared Content-Length was received.");
                }
                buffer.Write(scratch, 0, read);
            }
        }
        else
        {
            while (true)
            {
                var remaining = _maxRequestBodyBytes - checked((int)buffer.Length);
                if (remaining == 0)
                {
                    var overflowRead = await ReadRequestBodyChunkAsync(request, scratch.AsMemory(0, 1)).ConfigureAwait(false);
                    if (overflowRead == 0)
                        break;

                    request.Diagnostic = "request_body_limit_exceeded";
                    await RespondAsync(request, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
                    LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
                    return null;
                }

                var reservedForRead = TryReserveRequestBodyBytesUpTo(
                    request,
                    Math.Min(scratch.Length, remaining));
                if (reservedForRead == 0)
                {
                    // A full budget may mean this unknown-length body fit exactly. Probe EOF
                    // without retaining the byte and reject only when more body data exists.
                    // budget 満杯は body がちょうど収まった場合もある。保持しない 1 byte で
                    // EOF を確認し、追加データが実在するときだけ拒否する。
                    var overBudgetRead = await ReadRequestBodyChunkAsync(request, scratch.AsMemory(0, 1)).ConfigureAwait(false);
                    if (overBudgetRead == 0)
                        break;

                    await RejectRequestBodyBudgetAsync(request).ConfigureAwait(false);
                    return null;
                }

                var read = await ReadRequestBodyChunkAsync(request, scratch.AsMemory(0, reservedForRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    ReleaseRequestBodyReservationBytes(request, reservedForRead);
                    break;
                }

                if (read < reservedForRead)
                    ReleaseRequestBodyReservationBytes(request, reservedForRead - read);
                buffer.Write(scratch, 0, read);
            }
        }

        // Decode directly from the bounded MemoryStream buffer so the raw body is not copied a
        // second time before the queued string takes ownership of the reservation (#4548).
        // bounded MemoryStream の buffer から直接 decode し、queue 用 string が reservation を
        // 引き継ぐ前に raw body を二重 copy しない (#4548)。
        try
        {
            if (!buffer.TryGetBuffer(out var bytes) || bytes.Array is null)
                throw new InvalidOperationException("HTTP MCP request body buffer is unavailable.");
            return StrictUtf8.GetString(bytes.Array, bytes.Offset, checked((int)buffer.Length));
        }
        catch (DecoderFallbackException)
        {
            request.Diagnostic = InvalidUtf8Diagnostic;
            await RespondAsync(request, (int)HttpStatusCode.BadRequest, "MCP HTTP request body must be valid UTF-8.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.BadRequest);
            return null;
        }
    }

    private async ValueTask<int> ReadRequestBodyChunkAsync(PendingRequest request, Memory<byte> buffer)
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        idleCts.CancelAfter(_requestBodyIdleTimeout);
        var readTask = request.Context.Request.InputStream.ReadAsync(buffer, CancellationToken.None).AsTask();
        try
        {
            return await readTask.WaitAsync(idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            idleCts.IsCancellationRequested
            && !request.CancellationToken.IsCancellationRequested)
        {
            request.TryCancel(RequestBodyIdleTimeoutDiagnostic);
            ObserveAbandonedRequestBodyRead(readTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            ObserveAbandonedRequestBodyRead(readTask);
            throw;
        }
    }

    private static void ObserveAbandonedRequestBodyRead(Task<int> readTask)
    {
        _ = readTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsRequestDeadlineCancellation(PendingRequest request, Exception exception)
        => request.CancellationReason is RequestBodyIdleTimeoutDiagnostic or RequestLifetimeTimeoutDiagnostic
            && exception is OperationCanceledException or IOException or HttpListenerException or ObjectDisposedException or SocketException;

    private bool TryReserveRequestBodyBytesExact(PendingRequest request, int bytes)
    {
        if (bytes <= 0)
            return true;
        if (!ProcessRequestBodyBudget.TryReserveExact(bytes, _maxInFlightRequestBodyBytes))
            return false;

        TrackRequestBodyReservation(request, bytes);
        return true;
    }

    private int TryReserveRequestBodyBytesUpTo(PendingRequest request, int requestedBytes)
    {
        var reserved = ProcessRequestBodyBudget.TryReserveUpTo(requestedBytes, _maxInFlightRequestBodyBytes);
        if (reserved > 0)
            TrackRequestBodyReservation(request, reserved);
        return reserved;
    }

    private void TrackRequestBodyReservation(PendingRequest request, int bytes)
    {
        Interlocked.Add(ref request.ReservedBodyBytes, bytes);
        var current = Interlocked.Add(ref _inFlightRequestBodyBytes, bytes);
        var peak = Interlocked.Read(ref _peakInFlightRequestBodyBytes);
        while (current > peak)
        {
            var observed = Interlocked.CompareExchange(ref _peakInFlightRequestBodyBytes, current, peak);
            if (observed == peak)
                break;
            peak = observed;
        }
    }

    private void ReleaseRequestBodyReservationBytes(PendingRequest request, int bytes)
    {
        if (bytes <= 0)
            return;

        ReleaseRequestBodyReservationBytesCore(request, bytes);
    }

    private void ReleaseRequestBodyReservation(PendingRequest request)
    {
        if (!request.TryRequestBodyReservationRelease(out var retainedCompletion))
            return;

        if (retainedCompletion is null || retainedCompletion.IsCompleted)
        {
            if (retainedCompletion is { IsFaulted: true })
                _ = retainedCompletion.Exception;
            ReleaseRequestBodyReservationBytesCore(request, long.MaxValue);
            return;
        }

        _ = retainedCompletion.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted)
                    _ = completed.Exception;
                var (transport, retainedRequest) = ((HttpMcpTransport, PendingRequest))state!;
                transport.ReleaseRequestBodyReservationBytesCore(retainedRequest, long.MaxValue);
            },
            (this, request),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseRequestBodyReservationBytesCore(PendingRequest request, long requestedBytes)
    {
        var releasedBytes = request.ReleaseReservedBodyBytes(requestedBytes);
        if (releasedBytes <= 0)
            return;

        Interlocked.Add(ref _inFlightRequestBodyBytes, -releasedBytes);
        ProcessRequestBodyBudget.Release(releasedBytes);
    }

    private async Task RejectRequestBodyBudgetAsync(PendingRequest request)
    {
        request.Diagnostic = RequestBodyBudgetLimitRejection;
        MarkRejected(request, RequestBodyBudgetLimitRejection);
        request.Context.Response.AddHeader("Retry-After", "1");
        request.Context.Response.AddHeader(RejectionReasonHeader, RequestBodyBudgetLimitRejection);
        await RespondAsync(
            request,
            (int)HttpStatusCode.TooManyRequests,
            $"MCP HTTP process-wide in-flight request body budget of {_maxInFlightRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} bytes is full.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.TooManyRequests);
    }

    private bool TryValidateOrigin(HttpListenerRequest request)
    {
        var values = request.Headers.GetValues("Origin");
        if (values is not { Length: > 0 })
            return true;

        if (values!.Length != 1)
            return false;

        var value = values[0];
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.Equals(origin.GetLeftPart(UriPartial.Authority), _allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsCorsPreflightRequest(HttpListenerRequest request)
        => string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase)
            && request.Headers.GetValues("Origin") is { Length: > 0 }
            && request.Headers.GetValues("Access-Control-Request-Method") is { Length: > 0 };

    private async Task<bool> TryValidateJsonContentTypeAsync(PendingRequest request)
    {
        var contentTypes = request.Context.Request.Headers.GetValues("Content-Type");
        if (contentTypes is not { Length: 1 }
            || !MediaTypeHeaderValue.TryParse(contentTypes[0], out var contentType)
            || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            request.Diagnostic = UnsupportedMediaTypeDiagnostic;
            await RespondAsync(request, (int)HttpStatusCode.UnsupportedMediaType, "MCP HTTP POST requires Content-Type: application/json.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.UnsupportedMediaType);
            return false;
        }

        var charsetParameters = contentType.Parameters
            .Where(parameter => string.Equals(parameter.Name, "charset", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var charset = charsetParameters.Length == 1
            ? charsetParameters[0].Value?.Trim().Trim('"')
            : null;
        if (charsetParameters.Length > 1
            || (charsetParameters.Length == 1
                && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            request.Diagnostic = UnsupportedCharsetDiagnostic;
            await RespondAsync(request, (int)HttpStatusCode.UnsupportedMediaType, "MCP HTTP POST requires UTF-8 JSON.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.UnsupportedMediaType);
            return false;
        }

        return true;
    }

    private async Task<bool> TryRequireEstablishedSessionAsync(PendingRequest request)
    {
        if (!IsSessionEstablished)
        {
            var headerResult = TryReadSessionIdHeader(request.Context.Request.Headers, out _);
            var statusCode = headerResult == SessionIdHeaderReadResult.Missing
                ? HttpStatusCode.BadRequest
                : HttpStatusCode.NotFound;
            var rejection = headerResult == SessionIdHeaderReadResult.Missing
                ? SessionRequiredRejection
                : SessionNotFoundRejection;
            await RejectSessionAsync(request, statusCode, rejection).ConfigureAwait(false);
            return false;
        }

        return await TryValidateSessionHeaderAsync(request).ConfigureAwait(false);
    }

    private async Task<bool> TryValidateSessionHeaderAsync(PendingRequest request)
    {
        var headerResult = TryReadSessionIdHeader(request.Context.Request.Headers, out var providedSessionId);
        if (headerResult == SessionIdHeaderReadResult.Success
            && SessionIdMatches(providedSessionId!))
        {
            request.SessionValidated = true;
            return true;
        }

        var statusCode = headerResult == SessionIdHeaderReadResult.Missing
            ? HttpStatusCode.BadRequest
            : HttpStatusCode.NotFound;
        var rejection = headerResult == SessionIdHeaderReadResult.Missing
            ? SessionRequiredRejection
            : SessionNotFoundRejection;
        await RejectSessionAsync(request, statusCode, rejection).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryClaimPendingInitializeAsync(PendingRequest request, string body)
    {
        // The initial initialize request has no session header. Any supplied value before
        // establishment is stale or fabricated and must not become a session oracle.
        // 最初の initialize は session header を持たない。確立前の提示値は stale / forged
        // として扱い、session oracle にしない。
        if (TryReadSessionIdHeader(request.Context.Request.Headers, out _) != SessionIdHeaderReadResult.Missing)
        {
            await RejectSessionAsync(request, HttpStatusCode.NotFound, SessionNotFoundRejection).ConfigureAwait(false);
            return false;
        }

        if (!IsResponseBearingInitialize(body))
        {
            if (Volatile.Read(ref _pendingInitializeRequest) is not null)
            {
                await RejectSessionAsync(
                    request,
                    HttpStatusCode.Conflict,
                    SessionInitializationInProgressRejection).ConfigureAwait(false);
            }
            else
            {
                await RejectSessionAsync(request, HttpStatusCode.BadRequest, SessionRequiredRejection).ConfigureAwait(false);
            }
            return false;
        }

        if (Interlocked.CompareExchange(ref _pendingInitializeRequest, request, null) is not null)
        {
            await RejectSessionAsync(
                request,
                HttpStatusCode.Conflict,
                SessionInitializationInProgressRejection).ConfigureAwait(false);
            return false;
        }

        request.OwnsInitializeClaim = true;
        if (!IsSessionEstablished)
            return true;

        // A successful initializer publishes the established flag before releasing its claim.
        // Recheck after CAS so a handler that raced with that release cannot claim a second
        // headerless initialize request.
        // 成功 initializer は claim 解放前に established flag を公開する。CAS 後に再確認し、
        // claim 解放と競合した handler が 2 件目の header 無し initialize を取るのを防ぐ。
        ReleasePendingInitialize(request);
        await RejectSessionAsync(request, HttpStatusCode.BadRequest, SessionRequiredRejection).ConfigureAwait(false);
        return false;
    }

    private async Task RejectSessionAsync(
        PendingRequest request,
        HttpStatusCode statusCode,
        string rejectionReason)
    {
        MarkRejected(request, rejectionReason);
        request.Context.Response.AddHeader(RejectionReasonHeader, rejectionReason);
        var message = rejectionReason switch
        {
            SessionRequiredRejection => "MCP HTTP session header is required.\n",
            SessionInitializationInProgressRejection => "MCP HTTP session initialization is already in progress.\n",
            _ => "MCP HTTP session was not found.\n",
        };
        await RespondAsync(request, (int)statusCode, message).ConfigureAwait(false);
        LogRequest(request, (int)statusCode);
    }

    private static SessionIdHeaderReadResult TryReadSessionIdHeader(
        NameValueCollection headers,
        out string? sessionId)
    {
        sessionId = null;
        var values = headers.GetValues(SessionIdHeaderName);
        if (values is null || values.Length == 0)
            return SessionIdHeaderReadResult.Missing;
        if (values.Length != 1
            || string.IsNullOrEmpty(values[0])
            || values[0].IndexOf(',', StringComparison.Ordinal) >= 0)
        {
            return SessionIdHeaderReadResult.Invalid;
        }

        sessionId = values[0];
        return SessionIdHeaderReadResult.Success;
    }

    private bool SessionIdMatches(string provided)
    {
        if (provided.Length != _sessionId.Length)
            return false;

        var difference = 0;
        for (var i = 0; i < provided.Length; i++)
            difference |= provided[i] ^ _sessionId[i];
        return difference == 0;
    }

    private static bool IsResponseBearingInitialize(string body)
    {
        if (!JsonFrameParser.TryParseNode(body, McpServer.MaxJsonDepth, out var node, out _)
            || node is not JsonObject obj
            || obj["jsonrpc"] is not JsonValue jsonRpcValue
            || !jsonRpcValue.TryGetValue<string>(out var jsonRpc)
            || !string.Equals(jsonRpc, "2.0", StringComparison.Ordinal)
            || obj["method"] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method)
            || !string.Equals(method, "initialize", StringComparison.Ordinal)
            || !obj.TryGetPropertyValue("id", out _))
        {
            return false;
        }

        return true;
    }

    private void ReleasePendingInitialize(PendingRequest request)
    {
        if (!request.OwnsInitializeClaim)
            return;

        request.OwnsInitializeClaim = false;
        Interlocked.CompareExchange(ref _pendingInitializeRequest, null, request);
    }

    private enum SessionIdHeaderReadResult
    {
        Missing,
        Success,
        Invalid,
    }

    private bool TryQueueRequest(PendingRequest request)
    {
        var requestCancellationToken = request.CancellationToken;
        if (requestCancellationToken.IsCancellationRequested)
            return false;
        // A pending initialize cannot start a chunked probe: the first response headers must
        // not be committed before a successful frame can publish Mcp-Session-Id. Its bounded
        // total lifetime still cancels abandoned initialization work.
        // pending initialize は成功 frame が Mcp-Session-Id を公開する前に response header を
        // commit できないため probe 対象外とし、放棄 work は total lifetime で cancel する。
        var shouldStartDisconnectProbe = request.ExpectsResponse && !request.OwnsInitializeClaim;
        var disconnectProbeToken = shouldStartDisconnectProbe
            ? requestCancellationToken
            : CancellationToken.None;
        if (!_queueSlots.Wait(0))
            return false;

        lock (_requestQueueSync)
        {
            if (_requestQueueCompleted || requestCancellationToken.IsCancellationRequested)
            {
                _queueSlots.Release();
                return false;
            }

            request.QueueNode = _requestQueue.AddLast(request);
            Interlocked.Increment(ref _queuedRequestCount);
            if (shouldStartDisconnectProbe)
                request.StartDisconnectProbe(disconnectProbeToken, RunRequestDisconnectProbeAsync);
            _requestAvailable.TrySetResult(true);
            return true;
        }
    }

    private async Task RunRequestDisconnectProbeAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_requestDisconnectProbeInterval, cancellationToken).ConfigureAwait(false);
                await request.ResponseWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref request.ResponseCompletedState) != 0)
                        return;

                    var response = request.Context.Response;
                    if (!request.ProbeResponseStarted)
                    {
                        response.StatusCode = (int)HttpStatusCode.OK;
                        response.ContentType = "application/json; charset=utf-8";
                        response.SendChunked = true;
                        request.ProbeResponseStarted = true;
                    }

                    if (BeforeRequestDisconnectProbeWriteForTests is { } beforeProbeWrite)
                        await beforeProbeWrite(cancellationToken).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteAndFlushRequestDisconnectProbeAsync(request).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (HttpMcpTimeoutException)
                {
                    // Publish terminal cancellation before the serialization gate is released.
                    // This prevents the paired response writer from reusing an already-aborted
                    // output while a non-cooperative probe operation is still unwinding.
                    // serialization gate 解放前に terminal cancellation を公開し、abort 済み
                    // output を paired response が再利用する race を防ぐ。
                    request.TryCancel(RequestDisconnectProbeWriteTimeoutDiagnostic);
                    return;
                }
                finally
                {
                    request.ResponseWriteGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal response completion, request deadline, and listener shutdown stop the probe.
        }
        catch (Exception ex) when (
            cancellationToken.IsCancellationRequested
            && ex is IOException or HttpListenerException or SocketException or ObjectDisposedException or HttpMcpTimeoutException)
        {
            // Some HttpListener implementations surface a cancelled response write as an I/O or
            // disposed-object failure. The cancelled probe token, rather than that platform-specific
            // exception shape, determines that this is normal probe shutdown.
            // HttpListener 実装によっては、cancel 済み response write が I/O または disposed-object
            // failure になる。platform 固有の例外型ではなく probe token の取消しを正常終了判定に使う。
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or SocketException or ObjectDisposedException)
        {
            request.TryCancel(ClientDisconnectedDiagnostic);
        }
        catch (Exception)
        {
            if (request.CancellationReason is null)
                request.Diagnostic = "http_disconnect_probe_failure";
        }
    }

    private async Task WriteAndFlushRequestDisconnectProbeAsync(PendingRequest request)
    {
        var response = request.Context.Response;
        if (!_retainedResponseOutputOperationSlots.Wait(0))
            throw new InvalidOperationException("HTTP MCP retained response output operation capacity is full.");

        // Attach an incomplete lease before any probe I/O starts. Request lifetime/shutdown
        // cancellation can otherwise release the body reservation before a later output timeout
        // discovers that the underlying operation is non-cooperative (#4546).
        // probe I/O 開始前に未完了 lease を接続する。request lifetime / shutdown cancellation が
        // 先行しても、後続 timeout で非協調 I/O を検出する前に body reservation を解放させない。
        var retentionBarrier = new RequestResourceRetentionBarrier();
        if (!request.TryRetainRequestResourcesUntil(retentionBarrier.Completion))
        {
            _retainedResponseOutputOperationSlots.Release();
            throw new InvalidOperationException(
                "HTTP MCP request resources were released before the disconnect probe retention barrier could be attached.");
        }

        var outputSlotTransferred = false;
        Task retainedOutputOperation = Task.CompletedTask;
        using var writeScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.HttpResponseWrite,
            _responseWriteTimeout,
            CancellationToken.None);
        try
        {
            var operationTask = RequestDisconnectProbeOutputWriteForTests is { } writeForTests
                ? writeForTests(writeScope.Token)
                : response.OutputStream.WriteAsync(RequestDisconnectProbePayload.AsMemory(), writeScope.Token).AsTask();
            retainedOutputOperation = operationTask;
            await AwaitOutputOperationAsync(
                operationTask,
                writeScope,
                CancellationToken.None,
                OperationTimeoutCategories.HttpResponseWrite,
                _responseWriteTimeout,
                () => AbortResponseBestEffort(response, "request disconnect probe timeout"),
                operation =>
                {
                    outputSlotTransferred = true;
                    RetainAbandonedResponseOutputOperation(request: null, operation);
                }).ConfigureAwait(false);
            var flushTask = response.OutputStream.FlushAsync(writeScope.Token);
            retainedOutputOperation = flushTask;
            await AwaitOutputOperationAsync(
                flushTask,
                writeScope,
                CancellationToken.None,
                OperationTimeoutCategories.HttpResponseWrite,
                _responseWriteTimeout,
                () => AbortResponseBestEffort(response, "request disconnect probe flush timeout"),
                operation =>
                {
                    outputSlotTransferred = true;
                    RetainAbandonedResponseOutputOperation(request: null, operation);
                }).ConfigureAwait(false);
        }
        finally
        {
            retentionBarrier.CompleteWhen(retainedOutputOperation);
            if (!outputSlotTransferred)
                _retainedResponseOutputOperationSlots.Release();
        }
    }

    private static async Task StopRequestDisconnectProbeAsync(PendingRequest request)
    {
        Interlocked.Exchange(ref request.ResponseCompletedState, 1);
        if (request.DisconnectProbeCts is { } probeCts)
        {
            try { probeCts.Cancel(); } catch (ObjectDisposedException) { /* request already finalized */ }
        }

        if (request.DisconnectProbeTask is { } probeTask)
        {
            try { await probeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* probe cancellation is expected */ }
            catch (Exception)
            {
                if (request.CancellationReason is null)
                    request.Diagnostic = "http_disconnect_probe_failure";
            }
        }
    }

    private async Task<bool> TryHandleOutOfBandFrameAsync(PendingRequest request, string body, CancellationToken cancellationToken)
    {
        if (OutOfBandFrameHandler is null)
            return false;

        var hasCancellationBatch = TrySplitCancellationBatch(
            body,
            out var cancellationBatch,
            out var remainingBatch);
        if (!hasCancellationBatch && !IsCancellationNotification(body) && !IsJsonRpcResponse(body))
            return false;

        var outOfBandBody = cancellationBatch ?? body;

        var context = request.Context;
        try
        {
            var frame = await OutOfBandFrameHandler(outOfBandBody, cancellationToken).ConfigureAwait(false);
            if (remainingBatch is not null)
            {
                if (frame is not null)
                    throw new InvalidDataException("Cancellation notifications in a mixed JSON-RPC batch must not produce a response.");

                // The extracted cancellation notifications have already been dispatched. Queue only
                // the remaining raw batch items so cancellation side effects cannot be replayed.
                request.Body = remainingBatch;
                request.RequestId = TryExtractJsonRpcIdTelemetry(remainingBatch, _maxRequestBodyBytes);
                return false;
            }

            if (frame is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                CloseResponseOrThrow(context.Response, "out-of-band no-content response");
                LogRequest(request, (int)HttpStatusCode.NoContent);
                return true;
            }

            var payload = Encoding.UTF8.GetBytes(frame);
            if (!await TryRejectOversizedResponseAsync(request, payload.LongLength).ConfigureAwait(false))
                return true;
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.LongLength;
            await WriteResponseBytesAsync(
                request,
                context.Response,
                payload,
                cancellationToken,
                "out-of-band response body timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            CloseOutputStreamOrThrow(context.Response.OutputStream, "out-of-band response body");
            LogRequest(request, (int)HttpStatusCode.OK);
            return true;
        }
        catch (HttpMcpTimeoutException ex)
        {
            request.Diagnostic = FormatTimeoutDiagnostic(ex.Category);
            AbortResponseBestEffort(context.Response, "out-of-band response timeout");
            LogRequest(request, (int)HttpStatusCode.InternalServerError);
            return true;
        }
        catch (Exception) when (request.CancellationReason is { } cancellationReason)
        {
            AbortResponseBestEffort(context.Response, "cancelled out-of-band response");
            LogRequest(request, CancellationStatusCode(cancellationReason));
            return true;
        }
        catch
        {
            AbortResponseBestEffort(context.Response, "out-of-band response failure");
            LogRequest(request, 499);
            return true;
        }
    }

    private bool TrySplitCancellationBatch(
        string body,
        out string? cancellationBatch,
        out string? remainingBatch)
    {
        cancellationBatch = null;
        remainingBatch = null;

        try
        {
            using var document = BoundedJson.ParseDocument(body, _maxRequestBodyBytes, McpServer.MaxJsonDepth);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() is 0 or > McpServer.MaxBatchRequestCount)
            {
                return false;
            }

            var batchRequestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id)
                    && TryCanonicalizeJsonRpcId(id, out var requestId))
                {
                    batchRequestIds.Add(requestId);
                }
            }

            var cancellations = new List<string>();
            var remaining = new List<string>();
            foreach (var item in root.EnumerateArray())
            {
                var rawItem = item.GetRawText();
                if (IsValidCancellationNotification(item)
                    && (!TryGetCancellationTargetId(item, out var targetId)
                        || !batchRequestIds.Contains(targetId)))
                {
                    cancellations.Add(rawItem);
                }
                else
                {
                    // A cancellation targeting an item in this same batch must remain with the
                    // raw batch. The server pre-registers all unique IDs before its eager control
                    // pass, avoiding the short tombstone TTL/cap without weakening cross-frame
                    // cancellation before HTTP queue admission (#4545).
                    // 同じ batch 内 item を対象にする cancellation は raw batch に残す。server が
                    // eager control pass 前に unique ID を事前登録し、cross-frame cancellation の
                    // queue admission 前処理を保ったまま tombstone TTL/cap 依存を除く (#4545)。
                    remaining.Add(rawItem);
                }
            }

            if (cancellations.Count == 0)
                return false;

            cancellationBatch = BuildRawBatch(cancellations);
            remainingBatch = remaining.Count == 0 ? null : BuildRawBatch(remaining);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsValidCancellationNotification(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || item.TryGetProperty("id", out _)
            || !item.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal)
            || !item.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var methodName = method.GetString();
        if (!string.Equals(methodName, "$/cancelRequest", StringComparison.Ordinal)
            && !string.Equals(methodName, "notifications/cancelled", StringComparison.Ordinal))
        {
            return false;
        }

        return !item.TryGetProperty("params", out var parameters)
            || parameters.ValueKind is JsonValueKind.Null or JsonValueKind.Object;
    }

    private static bool TryGetCancellationTargetId(JsonElement item, out string targetId)
    {
        targetId = string.Empty;
        if (!item.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (parameters.TryGetProperty("id", out var id)
            && id.ValueKind != JsonValueKind.Null
            && TryCanonicalizeJsonRpcId(id, out targetId))
        {
            return true;
        }

        return parameters.TryGetProperty("requestId", out var requestId)
            && TryCanonicalizeJsonRpcId(requestId, out targetId);
    }

    private static bool TryCanonicalizeJsonRpcId(JsonElement id, out string canonicalId)
    {
        canonicalId = string.Empty;
        switch (id.ValueKind)
        {
            case JsonValueKind.String:
                var stringId = id.GetString() ?? string.Empty;
                if (stringId.Length > McpServer.MaxRequestIdCharacterCount
                    || Encoding.UTF8.GetByteCount(stringId) > McpServer.MaxRequestIdByteLength)
                {
                    return false;
                }
                canonicalId = JsonSerializer.Serialize(stringId);
                return true;

            case JsonValueKind.Number:
                var numberId = id.GetRawText();
                if (numberId.Length > McpServer.MaxRequestIdCharacterCount
                    || Encoding.UTF8.GetByteCount(numberId) > McpServer.MaxRequestIdByteLength)
                {
                    return false;
                }
                canonicalId = numberId;
                return true;

            default:
                return false;
        }
    }

    private static string BuildRawBatch(IReadOnlyList<string> items)
        => "[" + string.Join(',', items) + "]";

    private static bool IsCancellationNotification(string body)
    {
        if (!JsonFrameParser.TryParseNode(body, McpServer.MaxJsonDepth, out var node, out _)
            || node is not JsonObject obj)
            return false;

        try
        {
            var method = obj["method"]?.GetValue<string>();
            return string.Equals(method, "$/cancelRequest", StringComparison.Ordinal)
                || string.Equals(method, "notifications/cancelled", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJsonRpcResponse(string body)
    {
        if (!JsonFrameParser.TryParseNode(body, McpServer.MaxJsonDepth, out var node, out _))
            return false;

        return node is JsonObject obj
            && obj.ContainsKey("id")
            && obj["method"] is null
            && (obj.ContainsKey("result") || obj.ContainsKey("error"));
    }

    private static bool IsSuccessfulInitializeResponse(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, McpServer.MaxJsonDepth, out var node, out _)
            || node is not JsonObject obj
            || obj["jsonrpc"] is not JsonValue jsonRpcValue
            || !jsonRpcValue.TryGetValue<string>(out var jsonRpc)
            || !string.Equals(jsonRpc, "2.0", StringComparison.Ordinal)
            || !obj.ContainsKey("id")
            || obj.ContainsKey("error")
            || obj["result"] is not JsonObject result
            || result["protocolVersion"] is not JsonValue protocolValue
            || !protocolValue.TryGetValue<string>(out var protocolVersion))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(protocolVersion);
    }

    public async Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
    {
        var request = Volatile.Read(ref _pendingRequest)
            ?? throw new InvalidOperationException("HttpMcpTransport: WriteFrameAsync called without a pending ReadFrameAsync.");
        await WriteFrameAsync(request, frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(PendingRequest request, string? frame, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref request.WriteStartedState, 1, 0) != 0)
            throw new InvalidOperationException("HttpMcpTransport: WriteFrameAsync called more than once for the request.");
        var context = request.Context;
        var responseWriteGateHeld = false;
        CancellationTokenSource? responseCts = null;

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (request.OwnsInitializeClaim
                && frame is not null
                && IsSuccessfulInitializeResponse(frame))
            {
                // McpServer has already committed its initialize state before handing us this
                // success frame. Publish the transport session fail-closed before any response
                // write: if header/body delivery fails, no second client may inherit that state.
                // McpServer はこの success frame を渡す前に initialize state を commit 済み。
                // response write 前に transport session を fail-closed で公開し、header/body
                // 配送失敗時にも別 client が確立済み state を継承できないようにする。
                Volatile.Write(ref _sessionEstablished, 1);
                context.Response.AddHeader(SessionIdHeaderName, _sessionId);
            }

            await StopRequestDisconnectProbeAsync(request).ConfigureAwait(false);
            await request.ResponseWriteGate.WaitAsync().ConfigureAwait(false);
            responseWriteGateHeld = true;
            if (request.CancellationReason is { } cancellationReason)
            {
                AbortResponseBestEffort(context.Response, "cancelled request response");
                LogRequest(request, CancellationStatusCode(cancellationReason));
                return;
            }
            responseCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                request.CancellationToken);
            var responseToken = responseCts.Token;
            if (request.CancellationReason is { } cancellationAfterSetup)
            {
                AbortResponseBestEffort(context.Response, "cancelled request response setup");
                LogRequest(request, CancellationStatusCode(cancellationAfterSetup));
                return;
            }

            if (frame is null)
            {
                responseToken.ThrowIfCancellationRequested();
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                CloseResponseOrThrow(context.Response, "request no-content response");
                CompleteAndLogResponse(request, (int)HttpStatusCode.NoContent);
                return;
            }

            var payload = Encoding.UTF8.GetBytes(frame);
            if (!await TryRejectOversizedResponseAsync(request, payload.LongLength).ConfigureAwait(false))
                return;
            if (!request.ProbeResponseStarted)
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = payload.LongLength;
            }
            if (BeforeResponseWriteForTests is { } beforeResponseWrite)
                await beforeResponseWrite(responseToken).ConfigureAwait(false);
            await WriteResponseBytesAsync(
                request,
                context.Response,
                payload,
                responseToken,
                "request response body timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            CloseOutputStreamOrThrow(context.Response.OutputStream, "request response body");
            CompleteAndLogResponse(request, (int)HttpStatusCode.OK);
        }
        catch (Exception) when (request.CancellationReason is { } cancellationReason)
        {
            AbortResponseBestEffort(context.Response, "cancelled request response failure");
            LogRequest(request, CancellationStatusCode(cancellationReason));
        }
        catch (HttpMcpTimeoutException ex)
        {
            request.Diagnostic = FormatTimeoutDiagnostic(ex.Category);
            AbortResponseBestEffort(context.Response, "request response timeout");
            LogRequest(request, (int)HttpStatusCode.InternalServerError);
            throw;
        }
        catch
        {
            // Best-effort: close the response so the listener doesn't leak the context.
            // best-effort で response を閉じる。listener が context を持ち続けないようにする。
            AbortResponseBestEffort(context.Response, "request response failure");
            throw;
        }
        finally
        {
            ReleasePendingInitialize(request);
            responseCts?.Dispose();
            if (responseWriteGateHeld)
                request.ResponseWriteGate.Release();
            request.Body = null;
            var retainedResourceCompletion = request.RetainedResourceCompletion;
            ReleaseRequestBodyReservation(request);
            request.DisposeLifetime();
            ReleaseActiveConcurrentRequestWhenCompleted(request, retainedResourceCompletion);
            _ = Interlocked.CompareExchange(ref _pendingRequest, null, request);
        }
    }

    private void ReleaseActiveConcurrentRequestWhenCompleted(
        PendingRequest request,
        Task? retainedResourceCompletion)
    {
        if (retainedResourceCompletion is null || retainedResourceCompletion.IsCompleted)
        {
            _activeConcurrentRequests.TryRemove(request, out _);
            return;
        }

        _ = retainedResourceCompletion.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted)
                    _ = completed.Exception;
                var (transport, retainedRequest) = ((HttpMcpTransport, PendingRequest))state!;
                transport._activeConcurrentRequests.TryRemove(retainedRequest, out _);
            },
            (this, request),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteAndLogResponse(PendingRequest request, int statusCode)
    {
        if (request.TryCompleteLifetime())
        {
            LogRequest(request, statusCode);
            return;
        }

        var cancellationReason = request.CancellationReason ?? TransportShutdownDiagnostic;
        AbortResponseBestEffort(request.Context.Response, "request cancelled at response completion");
        LogRequest(request, CancellationStatusCode(cancellationReason));
    }

    private async Task<bool> TryRejectOversizedResponseAsync(PendingRequest request, long payloadBytes)
    {
        if (payloadBytes <= _maxResponseBodyBytes)
            return true;

        request.Diagnostic = "response_body_limit_exceeded";
        if (request.ProbeResponseStarted)
        {
            AbortResponseBestEffort(request.Context.Response, "oversized response after disconnect probe");
            CompleteAndLogResponse(request, (int)HttpStatusCode.InternalServerError);
            return false;
        }
        await RespondAsync(
            request,
            (int)HttpStatusCode.InternalServerError,
            $"MCP HTTP response body exceeds the configured {_maxResponseBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
        CompleteAndLogResponse(request, (int)HttpStatusCode.InternalServerError);
        return false;
    }

    private async Task WriteResponseBytesAsync(
        PendingRequest? request,
        HttpListenerResponse response,
        byte[] bytes,
        CancellationToken cancellationToken,
        string timeoutOperation,
        string timeoutCategory)
    {
        if (!_retainedResponseOutputOperationSlots.Wait(0))
        {
            AbortResponseBestEffort(response, "retained response output capacity full");
            throw new InvalidOperationException(
                "HTTP MCP retained response output operation capacity is full.");
        }

        var outputSlotTransferred = false;
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, _responseWriteTimeout, cancellationToken);
        try
        {
            var operation = ResponseOutputWriteForTests is { } writeForTests
                ? writeForTests(writeScope.Token)
                : response.OutputStream.WriteAsync(bytes.AsMemory(), writeScope.Token).AsTask();
            await AwaitOutputOperationAsync(
                operation,
                writeScope,
                cancellationToken,
                timeoutCategory,
                _responseWriteTimeout,
                () => AbortResponseBestEffort(response, timeoutOperation),
                operation =>
                {
                    outputSlotTransferred = true;
                    RetainAbandonedResponseOutputOperation(request, operation);
                }).ConfigureAwait(false);
        }
        finally
        {
            if (!outputSlotTransferred)
                _retainedResponseOutputOperationSlots.Release();
        }
    }

    private void RetainAbandonedResponseOutputOperation(PendingRequest? request, Task operation)
    {
        // The output task may still own the response, payload, and the request body budget after
        // its bounded caller has returned. Retain both the request lease and a transport-level
        // drain entry until the underlying I/O actually settles (#4546).
        // bounded caller の終了後も output task が response / payload / request body budget を
        // 所有し得るため、実 I/O 完了まで request lease と transport drain entry を保持する (#4546)。
        request?.TryRetainRequestResourcesUntil(operation);
        if (!_abandonedResponseOutputOperations.TryAdd(operation, 0))
        {
            _retainedResponseOutputOperationSlots.Release();
            return;
        }

        _ = operation.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                var (transport, trackedOperation) = ((HttpMcpTransport, Task))state!;
                transport._retainedResponseOutputOperationSlots.Release();
                transport._abandonedResponseOutputOperations.TryRemove(trackedOperation, out _);
            },
            (this, operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task FlushResponseOutputAsync(
        PendingRequest? request,
        HttpListenerResponse response,
        CancellationToken cancellationToken,
        string timeoutOperation,
        string timeoutCategory)
    {
        if (!_retainedResponseOutputOperationSlots.Wait(0))
        {
            AbortResponseBestEffort(response, "retained response flush capacity full");
            throw new InvalidOperationException(
                "HTTP MCP retained response output operation capacity is full.");
        }

        var outputSlotTransferred = false;
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, _responseWriteTimeout, cancellationToken);
        try
        {
            await AwaitOutputOperationAsync(
                response.OutputStream.FlushAsync(writeScope.Token),
                writeScope,
                cancellationToken,
                timeoutCategory,
                _responseWriteTimeout,
                () => AbortResponseBestEffort(response, timeoutOperation),
                operation =>
                {
                    outputSlotTransferred = true;
                    RetainAbandonedResponseOutputOperation(request, operation);
                }).ConfigureAwait(false);
        }
        finally
        {
            if (!outputSlotTransferred)
                _retainedResponseOutputOperationSlots.Release();
        }
    }

    internal static async Task WriteSseBytesWithGateAsync(
        Stream outputStream,
        SemaphoreSlim writeGate,
        byte[] bytes,
        TimeSpan writeTimeout,
        Func<CancellationToken, Task>? beforeWrite,
        Func<bool>? canWrite,
        Action onTimeout,
        CancellationToken cancellationToken,
        Func<bool>? tryAcquireOutputSlot = null,
        Action<Task>? retainAbandonedOutputOperation = null,
        Action? releaseOutputSlot = null,
        Func<byte[], CancellationToken, Task>? outputWriteForTests = null)
    {
        using var gateScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.SseWrite,
            writeTimeout,
            cancellationToken);
        try
        {
            await writeGate.WaitAsync(gateScope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (gateScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpMcpTimeoutException(OperationTimeoutCategories.SseWrite, writeTimeout, ex);
        }

        Task? abandonedOutputOperation = null;
        var outputSlotAcquired = false;
        var outputSlotTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canWrite is not null && !canWrite())
                throw new ObjectDisposedException("HTTP MCP SSE output is no longer writable.");
            if (beforeWrite is not null)
                await beforeWrite(gateScope.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (canWrite is not null && !canWrite())
                throw new ObjectDisposedException("HTTP MCP SSE output is no longer writable.");

            if (tryAcquireOutputSlot is not null)
            {
                if (!tryAcquireOutputSlot())
                    throw new InvalidOperationException("HTTP MCP retained response output operation capacity is full.");
                outputSlotAcquired = true;
            }

            // Once shared response I/O begins, it owns an independent bounded lifetime. Caller
            // cancellation is observed only after write+flush settles. A transport-owned slot is
            // transferred to any non-cooperative output so abandoned SSE operations are globally
            // bounded even after their stream admission slots are released (#4546).
            // shared response I/O 開始後は独立した bounded lifetime に所有権を移す。write+flush
            // 完了後に caller cancellation を観測する。非協調 output には transport 所有 slot を
            // 引き継ぎ、stream admission slot 解放後も放棄 SSE operation の総数を制限する (#4546)。
            using var outputScope = OperationTimeoutScope.Create(
                OperationTimeoutCategories.SseWrite,
                writeTimeout,
                CancellationToken.None);
            var writeOperation = outputWriteForTests is { } writeForTests
                ? writeForTests(bytes, outputScope.Token)
                : outputStream.WriteAsync(bytes.AsMemory(), outputScope.Token).AsTask();
            await AwaitOutputOperationAsync(
                writeOperation,
                outputScope,
                CancellationToken.None,
                OperationTimeoutCategories.SseWrite,
                writeTimeout,
                onTimeout,
                operation =>
                {
                    abandonedOutputOperation = operation;
                    if (!outputSlotAcquired || retainAbandonedOutputOperation is null)
                        return;
                    outputSlotTransferred = true;
                    retainAbandonedOutputOperation(operation);
                }).ConfigureAwait(false);
            await AwaitOutputOperationAsync(
                outputStream.FlushAsync(outputScope.Token),
                outputScope,
                CancellationToken.None,
                OperationTimeoutCategories.SseWrite,
                writeTimeout,
                onTimeout,
                operation =>
                {
                    abandonedOutputOperation = operation;
                    if (!outputSlotAcquired || retainAbandonedOutputOperation is null)
                        return;
                    outputSlotTransferred = true;
                    retainAbandonedOutputOperation(operation);
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex) when (gateScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpMcpTimeoutException(OperationTimeoutCategories.SseWrite, writeTimeout, ex);
        }
        finally
        {
            if (outputSlotAcquired && !outputSlotTransferred)
                releaseOutputSlot?.Invoke();
            if (abandonedOutputOperation is { IsCompleted: false } abandoned)
                _ = ReleaseOutputGateWhenCompletedAsync(abandoned, writeGate);
            else
                writeGate.Release();
        }
    }

    private static async Task ReleaseOutputGateWhenCompletedAsync(Task operation, SemaphoreSlim writeGate)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The caller already received the bounded write failure. This continuation only owns
            // the shared serialization gate and must release it after the abandoned I/O settles.
            // caller には bounded write failure を返却済み。この continuation は shared gate の
            // 所有権だけを持ち、放棄 I/O が終了した後に必ず解放する。
        }
        finally
        {
            writeGate.Release();
        }
    }

    internal static async Task WriteBytesWithTimeoutForTestsAsync(
        Stream stream,
        byte[] bytes,
        TimeSpan timeout,
        string timeoutCategory,
        Action onTimeout,
        CancellationToken cancellationToken = default)
    {
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, timeout, cancellationToken);
        await AwaitOutputOperationAsync(
            stream.WriteAsync(bytes.AsMemory(), writeScope.Token).AsTask(),
            writeScope,
            cancellationToken,
            timeoutCategory,
            timeout,
            onTimeout).ConfigureAwait(false);
    }

    internal static async Task FlushWithTimeoutForTestsAsync(
        Stream stream,
        TimeSpan timeout,
        string timeoutCategory,
        Action onTimeout)
    {
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, timeout, CancellationToken.None);
        await AwaitOutputOperationAsync(
            stream.FlushAsync(writeScope.Token),
            writeScope,
            CancellationToken.None,
            timeoutCategory,
            timeout,
            onTimeout).ConfigureAwait(false);
    }

    private static async Task AwaitOutputOperationAsync(
        Task operationTask,
        OperationTimeoutScope writeScope,
        CancellationToken cancellationToken,
        string timeoutCategory,
        TimeSpan timeout,
        Action onTimeout,
        Action<Task>? onAbandoned = null)
    {
        try
        {
            await operationTask.WaitAsync(writeScope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (writeScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ObserveAbandonedOutputOperation(operationTask);
            onAbandoned?.Invoke(operationTask);
            onTimeout();
            throw new HttpMcpTimeoutException(timeoutCategory, timeout, ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveAbandonedOutputOperation(operationTask);
            onAbandoned?.Invoke(operationTask);
            throw;
        }
    }

    private static void ObserveAbandonedOutputOperation(Task operationTask)
    {
        if (operationTask.IsCompleted)
            return;

        _ = operationTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken)
    {
        if (_eventStreams.IsEmpty)
            return;

        var writes = new List<Task>(_eventStreams.Count);
        foreach (var (id, stream) in _eventStreams)
            writes.Add(WriteOutOfBandFrameToStreamAsync(id, stream, frame, cancellationToken));
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    private async Task WriteOutOfBandFrameToStreamAsync(Guid id, EventStream stream, string frame, CancellationToken cancellationToken)
    {
        try
        {
            await stream.WriteJsonRpcEventAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpMcpTimeoutException ex)
        {
            stream.RecordDiagnostic(FormatTimeoutDiagnostic(ex.Category));
            RecordEventStreamDrop(EventStreamWriteFailureDrop, ex);
            RemoveEventStream(id, stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This token belongs to the POST that emitted the out-of-band frame. Cancelling that
            // request must not evict an otherwise healthy shared SSE subscriber (#4546).
            // out-of-band frame を発行した POST の token なので、その取消しで正常な共有 SSE
            // subscriber を削除しない (#4546)。
        }
        catch (Exception ex)
        {
            RecordEventStreamDrop(EventStreamWriteFailureDrop, ex);
            RemoveEventStream(id, stream);
        }
    }

    private async Task<bool> TryAuthorizeAsync(PendingRequest request)
    {
        var context = request.Context;
        if (_bearerTokenHash is null)
        {
            request.AuthOutcome = "ok";
            return true;
        }

        // RFC 6750 §2.1: the auth-scheme token is case-insensitive — clients sending
        // `authorization: bearer ...` are valid and must be accepted.
        // RFC 6750 §2.1 で auth-scheme は case-insensitive と規定されているため、
        // `bearer ...` のような小文字スキームも受理する。
        string authFailure;
        if (!TryReadSingleAuthorizationHeader(context.Request.Headers, out var header, out var headerFailure))
        {
            authFailure = headerFailure;
        }
        else
        {
            switch (ExtractBearerToken(header, out var provided))
            {
                case BearerTokenReadResult.Success:
                    if (HashEqualsConfiguredToken(provided!))
                    {
                        request.AuthOutcome = "ok";
                        return true;
                    }

                    authFailure = AuthDenialWrongToken;
                    break;
                case BearerTokenReadResult.WrongScheme:
                    authFailure = AuthDenialWrongScheme;
                    break;
                case BearerTokenReadResult.MalformedToken:
                    authFailure = AuthDenialMalformedToken;
                    break;
                case BearerTokenReadResult.OversizedToken:
                    authFailure = AuthDenialOversizedToken;
                    break;
                default:
                    throw new InvalidOperationException("Unhandled bearer token read result.");
            }
        }

        RecordAuthDenial(authFailure);
        request.AuthOutcome = FormatAuthFailureOutcome(authFailure);

        // RFC 7235 §4.1: 401 responses SHOULD carry a WWW-Authenticate challenge so
        // generic HTTP clients (and humans poking at the listener) know which scheme
        // and (optionally) realm to use.
        // RFC 7235 §4.1 に従い 401 には WWW-Authenticate を付け、汎用 HTTP クライアントや
        // 手動デバッグ時に必要なスキームを示す。
        context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"cdidx-mcp\"");
        await RespondAsync(request, (int)HttpStatusCode.Unauthorized, "Missing or invalid bearer token.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.Unauthorized);
        return false;
    }

    private static string FormatAuthFailureOutcome(string detailedOutcome)
        => McpServer.IsUnsafeDebugEnabled() ? detailedOutcome : "unauthorized";

    private enum BearerTokenReadResult
    {
        Success,
        WrongScheme,
        MalformedToken,
        OversizedToken,
    }

    private static bool TryReadSingleAuthorizationHeader(NameValueCollection headers, out string header, out string failure)
    {
        header = string.Empty;
        var values = headers.GetValues("Authorization");
        if (values is null || values.Length == 0 || values.All(string.IsNullOrEmpty))
        {
            failure = AuthDenialMissing;
            return false;
        }

        if (values.Length != 1 || values[0].IndexOf(',', StringComparison.Ordinal) >= 0)
        {
            failure = AuthDenialAmbiguous;
            return false;
        }

        header = values[0];
        failure = string.Empty;
        return true;
    }

    private static BearerTokenReadResult ExtractBearerToken(string header, out string? token)
    {
        token = null;
        if (header.Length < BearerPrefix.Length || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return BearerTokenReadResult.WrongScheme;

        var candidate = header.AsSpan(BearerPrefix.Length);
        if (McpAuthenticationLimits.IsTokenOversized(candidate))
            return BearerTokenReadResult.OversizedToken;

        if (!McpAuthenticationLimits.IsTokenShapeValid(candidate))
            return BearerTokenReadResult.MalformedToken;

        token = candidate.ToString();
        return BearerTokenReadResult.Success;
    }

    private Task RespondAsync(PendingRequest request, int statusCode, string body)
        => RespondAsync(request.Context, statusCode, body, request);

    private async Task RespondAsync(HttpListenerContext context, int statusCode, string body, PendingRequest? request = null)
    {
        try
        {
            var cancellationToken = request?.CancellationToken ?? CancellationToken.None;
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.LongLength;
            if (request is not null && BeforeResponseWriteForTests is { } beforeResponseWrite)
                await beforeResponseWrite(cancellationToken).ConfigureAwait(false);
            await WriteResponseBytesAsync(
                request,
                context.Response,
                bytes,
                cancellationToken,
                "plain-text response body timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            CloseOutputStreamOrThrow(context.Response.OutputStream, "plain-text response body");
        }
        catch (HttpMcpTimeoutException ex)
        {
            if (request is not null)
                request.Diagnostic = FormatTimeoutDiagnostic(ex.Category);
            AbortResponseBestEffort(context.Response, "plain-text response timeout");
        }
        catch
        {
            AbortResponseBestEffort(context.Response, "plain-text response failure");
        }
    }

    private Task RespondJsonAsync(PendingRequest request, int statusCode, string body)
        => RespondJsonAsync(request.Context, statusCode, body, request);

    private async Task RespondJsonAsync(HttpListenerContext context, int statusCode, string body, PendingRequest? request = null)
    {
        try
        {
            var cancellationToken = request?.CancellationToken ?? CancellationToken.None;
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.LongLength;
            await WriteResponseBytesAsync(
                request,
                context.Response,
                bytes,
                cancellationToken,
                "json response body timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            CloseOutputStreamOrThrow(context.Response.OutputStream, "json response body");
        }
        catch (HttpMcpTimeoutException ex)
        {
            if (request is not null)
                request.Diagnostic = FormatTimeoutDiagnostic(ex.Category);
            AbortResponseBestEffort(context.Response, "json response timeout");
        }
        catch
        {
            AbortResponseBestEffort(context.Response, "json response failure");
        }
    }

    private static string ResolveHealthJson(Func<string>? provider)
    {
        if (provider is null)
            return DefaultStartingHealthJson;

        string candidate;
        try
        {
            candidate = provider();
        }
        catch
        {
            return InvalidHealthJson;
        }

        if (string.IsNullOrWhiteSpace(candidate)
            || Encoding.UTF8.GetByteCount(candidate) > MaxHealthJsonBytes
            || !JsonFrameParser.TryParseNode(candidate, McpServer.MaxJsonDepth, out var node, out _)
            || node is not JsonObject)
        {
            return InvalidHealthJson;
        }

        return candidate;
    }

    private static bool IsEventsPath(string? path)
        => string.Equals(path, "/events", StringComparison.Ordinal);

    private static bool IsHealthPath(string? path)
        => string.Equals(path, "/healthz", StringComparison.Ordinal);

    private async Task RunEventStreamAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        if (Interlocked.Increment(ref _eventStreamCount) > _maxEventStreams)
        {
            Interlocked.Decrement(ref _eventStreamCount);
            MarkRejected(request, EventStreamLimitRejection);
            context.Response.AddHeader("Retry-After", "1");
            context.Response.AddHeader(RejectionReasonHeader, EventStreamLimitRejection);
            await RespondAsync(request, (int)HttpStatusCode.TooManyRequests, "MCP HTTP event stream limit is full.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.TooManyRequests);
            return;
        }

        var streamId = Guid.NewGuid();
        var stream = new EventStream(
            context.Response,
            _eventStreamWriteTimeout,
            BeforeEventStreamWriteForTests,
            response => AbortResponseBestEffort(response, "sse write timeout"),
            () => _retainedResponseOutputOperationSlots.Wait(0),
            operation => RetainAbandonedResponseOutputOperation(request: null, operation),
            () => _retainedResponseOutputOperationSlots.Release(),
            EventStreamOutputWriteForTests);
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.SendChunked = true;
            context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.AddHeader("X-Accel-Buffering", "no");
            context.Response.AddHeader("X-Cdidx-Mcp-Event-Stream-Id", streamId.ToString("N", CultureInfo.InvariantCulture));

            var prelude = Encoding.UTF8.GetBytes(": cdidx mcp event stream ready\n\n");
            await WriteResponseBytesAsync(
                request,
                context.Response,
                prelude,
                cancellationToken,
                "event stream prelude timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            await FlushResponseOutputAsync(
                request,
                context.Response,
                cancellationToken,
                "event stream prelude flush timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            // Do not publish the stream to out-of-band writers until the prelude has settled.
            // The prelude uses the raw response path, while published SSE frames use the stream's
            // write gate; publishing earlier would let both paths touch OutputStream concurrently.
            // prelude 完了前は out-of-band writer に stream を公開しない。raw response 経路と
            // SSE write gate 経路が OutputStream へ同時に書き込む race を防ぐ。
            if (BeforeEventStreamPublishForTests is { } beforeEventStreamPublish)
                await beforeEventStreamPublish(cancellationToken).ConfigureAwait(false);
            _eventStreams[streamId] = stream;

            using var streamLifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                stream.TerminationToken);
            await RunKeepAliveLoopAsync(stream, streamLifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || stream.IsReleased)
        {
            // Normal server shutdown.
        }
        catch (HttpMcpTimeoutException ex)
        {
            request.Diagnostic = FormatTimeoutDiagnostic(ex.Category);
            AbortResponseBestEffort(context.Response, "event stream timeout");
            if (!stream.IsReleased)
                RecordEventStreamDrop(EventStreamWriteFailureDrop, ex);
        }
        catch (Exception ex)
        {
            // Client disconnects are expected for long-lived SSE streams.
            if (!stream.IsReleased)
                RecordEventStreamDrop(EventStreamWriteFailureDrop, ex);
        }
        finally
        {
            RemoveEventStream(streamId, stream);
            request.Diagnostic ??= stream.Diagnostic;
            LogRequest(request, (int)HttpStatusCode.OK);
            CloseResponseBestEffort(context.Response, "event stream response cleanup");
        }
    }

    private void RemoveEventStream(Guid streamId, EventStream stream)
    {
        _eventStreams.TryRemove(streamId, out _);
        if (stream.TryReleaseSlot())
            Interlocked.Decrement(ref _eventStreamCount);
        AbortResponseBestEffort(stream.Response, "event stream response cleanup");
        stream.Dispose();
    }

    private async Task RunKeepAliveLoopAsync(EventStream stream, CancellationToken cancellationToken)
    {
        var interval = KeepAliveInterval;
        if (interval is null || interval.Value <= TimeSpan.Zero || KeepAliveFrameProvider is null)
        {
            await RunEventStreamDisconnectProbeLoopAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval.Value, cancellationToken).ConfigureAwait(false);
                var frame = KeepAliveFrameProvider();
                await stream.WriteJsonRpcEventAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
    }

    private static async Task RunEventStreamDisconnectProbeLoopAsync(EventStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(EventStreamDisconnectProbeInterval, cancellationToken).ConfigureAwait(false);
                await stream.WriteCommentAsync("cdidx mcp event stream heartbeat", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
    }

    private sealed class EventStream(
        HttpListenerResponse response,
        TimeSpan writeTimeout,
        Func<CancellationToken, Task>? beforeWriteForTests,
        Action<HttpListenerResponse> abortResponseOnTimeout,
        Func<bool> tryAcquireOutputSlot,
        Action<Task> retainAbandonedOutputOperation,
        Action releaseOutputSlot,
        Func<byte[], CancellationToken, Task>? outputWriteForTests) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _terminationCts = new();
        private string? _diagnostic;
        private int _released;
        private int _outputTerminal;

        public HttpListenerResponse Response { get; } = response;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public CancellationToken TerminationToken => _terminationCts.Token;

        public string? Diagnostic => Volatile.Read(ref _diagnostic);

        public void RecordDiagnostic(string diagnostic)
            => Volatile.Write(ref _diagnostic, diagnostic);

        public async Task WriteJsonRpcEventAsync(string frame, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.Append("event: message\n");
            foreach (var line in frame.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                builder.Append("data: ").Append(line).Append('\n');
            builder.Append('\n');
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            if (bytes.Length > MaxSseEventFrameBytes)
                throw new InvalidDataException($"SSE event frame exceeds {MaxSseEventFrameBytes.ToString(CultureInfo.InvariantCulture)} bytes.");

            await WriteSseBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        public Task WriteCommentAsync(string comment, CancellationToken cancellationToken)
        {
            var payload = ": " + comment.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ') + "\n\n";
            return WriteSseBytesAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
        }

        public bool TryReleaseSlot()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return false;

            // Wake the keep-alive/disconnect-probe delay immediately. Aborting the response alone
            // does not wake Task.Delay, which otherwise retains the SSE admission slot for up to
            // the configured keep-alive interval (#4546).
            // response abort だけでは Task.Delay は起きないため、keep-alive / disconnect probe
            // を即時 cancel し SSE admission slot の長時間保持を防ぐ (#4546)。
            try
            {
                var delivery = _terminationCts.CancelAsync();
                ObserveLateCancellationDeliveryFailure(delivery);
            }
            catch { /* stream termination is best-effort */ }
            return true;
        }

        private async Task WriteSseBytesAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await WriteSseBytesWithGateAsync(
                Response.OutputStream,
                _writeGate,
                bytes,
                writeTimeout,
                beforeWriteForTests,
                () => !IsReleased && Volatile.Read(ref _outputTerminal) == 0,
                () =>
                {
                    Volatile.Write(ref _outputTerminal, 1);
                    abortResponseOnTimeout(Response);
                },
                cancellationToken,
                tryAcquireOutputSlot,
                retainAbandonedOutputOperation,
                releaseOutputSlot,
                outputWriteForTests).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // SemaphoreSlim owns no native resource unless its wait handle is requested (it is
            // not here). A concurrent timeout/removal can race an in-progress writer's finally;
            // leaving this private gate for GC avoids Dispose/Release overlap on that path.
            // wait handle を使わない SemaphoreSlim は native resource を所有しない。timeout
            // removal と writer finally の Dispose/Release race を避け、private gate は GC に任せる。
        }
    }

    private bool HashEqualsConfiguredToken(string provided)
    {
        // Hash only the attacker-supplied input and compare to the pre-computed configured-token
        // digest via FixedTimeEquals. Hashing the configured token on every request would still
        // leak its length through SHA-256's per-block work; pre-computing in the constructor and
        // hashing only the request side eliminates that channel. The digest is unsalted on
        // purpose — the goal is constant-time equality of two same-length 32-byte buffers, not
        // password storage.
        // 攻撃者入力のみハッシュ計算し、コンストラクタで事前計算した設定トークン digest と
        // FixedTimeEquals で比較する。リクエスト毎に設定トークンをハッシュすると SHA-256 の
        // ブロック処理量で設定トークン長が漏れるため、設定側を事前計算しておく。
        // salt 無しは「同じ長さの 32 byte 配列の定数時間比較」が目的だから。
        Span<byte> providedHash = stackalloc byte[McpAuthenticationLimits.Sha256HashBytes];
        McpAuthenticationLimits.HashToken(provided, providedHash);
        return CryptographicOperations.FixedTimeEquals(providedHash, _bearerTokenHash);
    }

    private void CloseResponseOrThrow(HttpListenerResponse response, string operation)
    {
        try
        {
            response.Close();
        }
        catch (Exception ex)
        {
            RecordResponseCleanupFailure("close", operation, ex);
            throw;
        }
    }

    private void CloseOutputStreamOrThrow(Stream stream, string operation)
    {
        try
        {
            stream.Close();
        }
        catch (Exception ex)
        {
            RecordResponseCleanupFailure("close", operation, ex);
            throw;
        }
    }

    private void CloseResponseBestEffort(HttpListenerResponse response, string operation)
    {
        try
        {
            response.Close();
        }
        catch (Exception ex)
        {
            RecordResponseCleanupFailure("close", operation, ex);
        }
    }

    private void AbortResponseBestEffort(HttpListenerResponse response, string operation)
    {
        try
        {
            response.Abort();
        }
        catch (Exception ex)
        {
            RecordResponseCleanupFailure("abort", operation, ex);
        }
    }

    internal void RecordResponseCleanupFailure(string kind, string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var normalizedOperation = DiagnosticRedactor.RedactSensitiveText(DiagnosticSanitizer.ForMessage(operation), redactPaths: true);
        if (string.IsNullOrWhiteSpace(normalizedOperation))
            normalizedOperation = "response cleanup";
        var failure = $"{normalizedOperation}:{DiagnosticRedactor.ClassifyException(exception)}:{exception.GetType().Name}";
        switch (kind)
        {
            case "abort":
                Interlocked.Increment(ref _responseAbortCleanupFailureCount);
                Volatile.Write(ref _lastResponseAbortCleanupFailure, failure);
                break;
            case "close":
                Interlocked.Increment(ref _responseCloseCleanupFailureCount);
                Volatile.Write(ref _lastResponseCloseCleanupFailure, failure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Expected 'abort' or 'close'.");
        }
    }

    internal void RecordEventStreamDrop(string reason, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (reason)
        {
            case EventStreamWriteFailureDrop:
                Interlocked.Increment(ref _eventStreamDropCount);
                Interlocked.Increment(ref _eventStreamWriteFailureDropCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, $"Expected {EventStreamWriteFailureDrop}.");
        }

        var category = DiagnosticRedactor.ClassifyException(exception);
        Volatile.Write(ref _lastEventStreamDropReason, $"{reason}:{category}:{exception.GetType().Name}");
    }

    private void MarkRejected(PendingRequest request, string reason)
    {
        request.RejectionReason = reason;
        if (string.Equals(reason, ConcurrentHandlerLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _concurrentHandlerLimitRejectionCount);
        else if (string.Equals(reason, RequestQueueLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _requestQueueLimitRejectionCount);
        else if (string.Equals(reason, RequestBodyBudgetLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _requestBodyBudgetLimitRejectionCount);
        else if (string.Equals(reason, EventStreamLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _eventStreamLimitRejectionCount);
    }

    private void RecordAuthDenial(string reason)
    {
        switch (reason)
        {
            case AuthDenialMissing:
                Interlocked.Increment(ref _authDenialMissingCount);
                break;
            case AuthDenialAmbiguous:
                Interlocked.Increment(ref _authDenialAmbiguousCount);
                break;
            case AuthDenialWrongScheme:
                Interlocked.Increment(ref _authDenialWrongSchemeCount);
                break;
            case AuthDenialMalformedToken:
                Interlocked.Increment(ref _authDenialMalformedTokenCount);
                break;
            case AuthDenialOversizedToken:
                Interlocked.Increment(ref _authDenialOversizedTokenCount);
                break;
            case AuthDenialWrongToken:
                Interlocked.Increment(ref _authDenialWrongTokenCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown auth denial reason.");
        }

        Volatile.Write(ref _lastAuthDenialReason, reason);
    }

    internal static string FormatTimeoutDiagnosticForTests(string category)
        => FormatTimeoutDiagnostic(category);

    private static string FormatTimeoutDiagnostic(string category)
        => TimeoutDiagnosticPrefix + category;

    /// <summary>Resolved listen spec returned by <see cref="ResolveListenSpec"/>.</summary>
    internal readonly record struct HttpListenSpec(string Prefix, string Host, int Port, bool IsLoopback, bool PortWasEphemeral);

    internal sealed record HttpRequestLogRecord(
        string CorrelationId,
        string? RequestId,
        string RemotePeer,
        string Method,
        string Path,
        int StatusCode,
        double DurationMs,
        string AuthOutcome,
        string? RejectionReason,
        string? Diagnostic,
        string? RequestIdType = null,
        int? RequestIdLength = null);

    private PendingRequest BeginRequest(HttpListenerContext context, CancellationToken cancellationToken = default)
    {
        var remotePeer = context.Request.RemoteEndPoint is { } endpoint
            ? endpoint.ToString()
            : "<unknown>";
        return new PendingRequest(
            context,
            Guid.NewGuid().ToString("N"),
            LimitRequestLogField(remotePeer) ?? "<unknown>",
            LimitRequestLogField(context.Request.HttpMethod) ?? string.Empty,
            LimitRequestLogField(context.Request.Url?.AbsolutePath ?? "/") ?? "/",
            cancellationToken);
    }

    internal static string? LimitRequestLogField(string? value)
    {
        if (value is null || value.Length <= MaxRequestLogFieldCharacters)
            return value;

        return value.Substring(0, MaxRequestLogFieldCharacters - RequestLogTruncationMarker.Length)
            + RequestLogTruncationMarker;
    }

    private void LogRequest(PendingRequest request, int statusCode)
    {
        if (!request.TryCompleteLifetime() && request.CancellationReason is { } cancellationReason)
        {
            request.Diagnostic = cancellationReason;
            statusCode = CancellationStatusCode(cancellationReason);
        }

        if (_requestLogger is null || _requestLogQueue is null)
            return;

        if (Interlocked.Exchange(ref request.LoggedState, 1) != 0)
            return;
        var record = new HttpRequestLogRecord(
            request.CorrelationId,
            request.RequestId?.Token,
            request.RemotePeer,
            request.Method,
            request.Path,
            statusCode,
            request.Elapsed.TotalMilliseconds,
            request.AuthOutcome,
            request.RejectionReason,
            request.Diagnostic,
            request.RequestId?.Type,
            request.RequestId?.Length);
        Interlocked.Increment(ref _pendingRequestLogCount);
        if (_requestLogQueue.Writer.TryWrite(record))
            return;

        Interlocked.Decrement(ref _pendingRequestLogCount);
        if (!IsDisposed)
            RecordRequestLogDrop("queue_full", null);
    }

    private async Task DrainRequestLogQueueAsync()
    {
        if (_requestLogger is null || _requestLogQueue is null)
            return;

        await foreach (var record in _requestLogQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _requestLogger(record);
            }
            catch (Exception ex)
            {
                RecordRequestLogDrop("callback_failure", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingRequestLogCount);
            }
        }
    }

    private void RecordRequestLogDrop(string reason, Exception? exception)
    {
        Interlocked.Increment(ref _requestLogDroppedCount);
        switch (reason)
        {
            case "queue_full":
                Interlocked.Increment(ref _requestLogQueueFullDropCount);
                break;
            case "callback_failure":
                Interlocked.Increment(ref _requestLogCallbackFailureCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Expected queue_full or callback_failure.");
        }

        var category = exception is null ? "resource" : DiagnosticRedactor.ClassifyException(exception);
        var exceptionType = exception?.GetType().Name ?? "RequestLogQueueFull";
        Volatile.Write(ref _lastRequestLogDropReason, $"{reason}:{category}:{exceptionType}");
    }

    private static McpRequestIdTelemetryData? TryExtractJsonRpcIdTelemetry(string body, int maxRequestBodyBytes)
    {
        try
        {
            // HandleContextAsync calls this only with a body returned by TryReadRequestBodyAsync,
            // so the full JSON parse is bounded by the HTTP request-body byte limit.
            using var doc = BoundedJson.ParseDocument(body, maxRequestBodyBytes, McpServer.MaxJsonDepth);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("id", out var id))
            {
                return null;
            }

            var requestId = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString() ?? string.Empty,
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.Null => string.Empty,
                _ => null,
            };
            if (requestId is null)
                return null;
            if (id.ValueKind != JsonValueKind.Null
                && (requestId.Length > McpServer.MaxRequestIdCharacterCount
                    || Encoding.UTF8.GetByteCount(requestId) > McpServer.MaxRequestIdByteLength))
                return null;

            return McpRequestIdTelemetry.Create(id);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool ExpectsJsonRpcResponse(string body, int maxRequestBodyBytes)
    {
        try
        {
            using var document = BoundedJson.ParseDocument(body, maxRequestBodyBytes, McpServer.MaxJsonDepth);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => !IsValidJsonRpcNotification(document.RootElement),
                JsonValueKind.Array => document.RootElement.GetArrayLength() == 0
                    || document.RootElement.EnumerateArray().Any(item => !IsValidJsonRpcNotification(item)),
                _ => true,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Parse errors produce a JSON-RPC error response, so they need disconnect probing too.
            // parse error も JSON-RPC error response を返すため disconnect probe 対象にする。
            return true;
        }
    }

    private static bool IsValidJsonRpcNotification(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && !element.TryGetProperty("id", out _)
            && element.TryGetProperty("jsonrpc", out var jsonrpc)
            && jsonrpc.ValueKind == JsonValueKind.String
            && string.Equals(jsonrpc.GetString(), "2.0", StringComparison.Ordinal)
            && element.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String;

    private sealed class RequestBodyBudget
    {
        private long _currentBytes;

        internal long CurrentBytes => Interlocked.Read(ref _currentBytes);

        internal bool TryReserveExact(int bytes, int limitBytes)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _currentBytes);
                if (bytes > limitBytes - current)
                    return false;
                if (Interlocked.CompareExchange(ref _currentBytes, current + bytes, current) == current)
                    return true;
            }
        }

        internal int TryReserveUpTo(int requestedBytes, int limitBytes)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _currentBytes);
                var available = limitBytes - current;
                if (available <= 0)
                    return 0;

                var reserved = (int)Math.Min(requestedBytes, available);
                if (Interlocked.CompareExchange(ref _currentBytes, current + reserved, current) == current)
                    return reserved;
            }
        }

        internal void Release(long bytes)
            => Interlocked.Add(ref _currentBytes, -bytes);
    }

    private sealed class RequestResourceRetentionBarrier
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completionRegistered;

        internal Task Completion => _completion.Task;

        internal void CompleteWhen(Task retainedWork)
        {
            ArgumentNullException.ThrowIfNull(retainedWork);
            if (Interlocked.Exchange(ref _completionRegistered, 1) != 0)
                return;

            _ = retainedWork.ContinueWith(
                static (completed, state) =>
                {
                    if (completed.IsFaulted)
                        _ = completed.Exception;
                    ((TaskCompletionSource<bool>)state!).TrySetResult(true);
                },
                _completion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class PendingRequest
    {
        private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly CancellationToken _transportCancellationToken;
        private readonly object _lifetimeSync = new();
        private readonly object _requestResourceSync = new();
        private readonly TaskCompletionSource<bool> _cancellationDeliveryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _lifetimeCts;
        private CancellationToken _requestCancellationToken;
        private CancellationTokenRegistration _transportCancellationRegistration;
        private Timer? _lifetimeTimer;
        private Func<PendingRequest, string, bool>? _cancellationHandler;
        private Action<Task>? _cancellationDeliveryTracker;
        private string? _cancellationReason;
        private Task? _retainedResourceCompletion;
        private bool _bodyReservationReleaseRequested;
        private bool _lifetimeDisposeContinuationScheduled;
        private int _lifetimeDisposed;
        private int _terminalState;

        internal PendingRequest(HttpListenerContext context, string correlationId, string remotePeer, string method, string path, CancellationToken cancellationToken)
        {
            Context = context;
            CorrelationId = correlationId;
            RemotePeer = remotePeer;
            Method = method;
            Path = path;
            _transportCancellationToken = cancellationToken;
            _requestCancellationToken = cancellationToken;
        }

        internal HttpListenerContext Context { get; }

        internal CancellationToken CancellationToken
        {
            get
            {
                lock (_lifetimeSync)
                    return _requestCancellationToken;
            }
        }

        internal string? CancellationReason => Volatile.Read(ref _cancellationReason);

        internal Task CancellationDelivery => _cancellationDeliveryCompleted.Task;

        internal string CorrelationId { get; }

        internal McpRequestIdTelemetryData? RequestId { get; set; }

        internal string? Body { get; set; }

        internal bool ExpectsResponse { get; set; }

        internal LinkedListNode<PendingRequest>? QueueNode { get; set; }

        internal SemaphoreSlim ResponseWriteGate { get; } = new(1, 1);

        internal CancellationTokenSource? DisconnectProbeCts { get; private set; }

        internal Task? DisconnectProbeTask { get; private set; }

        internal bool ProbeResponseStarted { get; set; }

        internal int ResponseCompletedState;

        internal int WriteStartedState;

        internal string RemotePeer { get; }

        internal string Method { get; }

        internal string Path { get; }

        internal string AuthOutcome { get; set; } = "none";

        internal string? RejectionReason { get; set; }

        internal string? Diagnostic { get; set; }

        internal bool SessionValidated { get; set; }

        internal bool OwnsInitializeClaim { get; set; }

        internal int LoggedState;

        internal long ReservedBodyBytes;

        internal TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp);

        internal Task? RetainedResourceCompletion
        {
            get
            {
                lock (_requestResourceSync)
                    return _retainedResourceCompletion;
            }
        }

        internal bool TryRetainRequestResourcesUntil(Task completion)
        {
            lock (_requestResourceSync)
            {
                if (_bodyReservationReleaseRequested)
                    return false;

                _retainedResourceCompletion = _retainedResourceCompletion is null
                    ? completion
                    : Task.WhenAll(_retainedResourceCompletion, completion);
                return true;
            }
        }

        internal bool TryRequestBodyReservationRelease(out Task? retainedCompletion)
        {
            lock (_requestResourceSync)
            {
                if (_bodyReservationReleaseRequested)
                {
                    retainedCompletion = null;
                    return false;
                }

                _bodyReservationReleaseRequested = true;
                retainedCompletion = _retainedResourceCompletion;
                return true;
            }
        }

        internal long ReleaseReservedBodyBytes(long requestedBytes)
        {
            while (true)
            {
                var reservedBytes = Interlocked.Read(ref ReservedBodyBytes);
                if (reservedBytes <= 0)
                    return 0;

                var releasedBytes = Math.Min(reservedBytes, requestedBytes);
                if (Interlocked.CompareExchange(
                    ref ReservedBodyBytes,
                    reservedBytes - releasedBytes,
                    reservedBytes) == reservedBytes)
                {
                    return releasedBytes;
                }
            }
        }

        internal void StartDisconnectProbe(
            CancellationToken requestCancellationToken,
            Func<PendingRequest, CancellationToken, Task> probe)
        {
            DisconnectProbeCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
            DisconnectProbeTask = probe(this, DisconnectProbeCts.Token);
        }

        internal void StartLifetime(
            TimeSpan timeout,
            Func<PendingRequest, string, bool> cancellationHandler,
            Action<Task> cancellationDeliveryTracker)
        {
            Timer lifetimeTimer;
            lock (_lifetimeSync)
            {
                if (_lifetimeCts is not null)
                    throw new InvalidOperationException("HTTP MCP request lifetime was already started.");
                if (_lifetimeDisposed != 0)
                    throw new ObjectDisposedException(nameof(PendingRequest));

                _cancellationHandler = cancellationHandler;
                _cancellationDeliveryTracker = cancellationDeliveryTracker;
                _lifetimeCts = new CancellationTokenSource();
                _requestCancellationToken = _lifetimeCts.Token;
                lifetimeTimer = new Timer(
                    static state => ((PendingRequest)state!).TryCancel(RequestLifetimeTimeoutDiagnostic),
                    this,
                    timeout,
                    Timeout.InfiniteTimeSpan);
                _lifetimeTimer = lifetimeTimer;
            }

            var transportRegistration = _transportCancellationToken.Register(
                static state => ((PendingRequest)state!).TryCancel(TransportShutdownDiagnostic),
                this);
            lock (_lifetimeSync)
            {
                if (_lifetimeDisposed == 0)
                {
                    _transportCancellationRegistration = transportRegistration;
                    return;
                }
            }

            transportRegistration.Unregister();
            lifetimeTimer.Dispose();
        }

        internal bool TryCancel(string reason)
        {
            Task cancellationCallbacks;
            Func<PendingRequest, string, bool>? cancellationHandler;
            Action<Task>? cancellationDeliveryTracker;
            lock (_lifetimeSync)
            {
                if (_terminalState != 0)
                    return false;

                _terminalState = 1;
                Volatile.Write(ref _cancellationReason, reason);
                // CancelAsync marks the token before returning but runs callbacks without holding
                // this lifetime lock. This closes the publish/cancel window without reintroducing
                // lifetime -> queue or user-callback lock inversions.
                // CancelAsync は return 前に token を cancel 状態へ遷移させ、callback は lifetime
                // lock 外で実行する。publish/cancel 間の race と callback lock inversion を防ぐ。
                cancellationCallbacks = _lifetimeCts?.CancelAsync() ?? Task.CompletedTask;
                cancellationHandler = _cancellationHandler;
                cancellationDeliveryTracker = _cancellationDeliveryTracker;
            }

            // Never wait for cancellation callbacks or transport cleanup on the timer, listener,
            // or disposal caller. CancelAsync has already published token cancellation; a separate
            // worker owns callback delivery and the queue/response cleanup that follows (#4546).
            // timer / listener / dispose caller 上では cancellation callback や transport cleanup
            // を待たない。token cancel は公開済みで、後続配送と queue/response cleanup は
            // 独立 worker が所有する (#4546)。
            _ = Task.Run(() => DeliverCancellationAsync(cancellationCallbacks, cancellationHandler, reason));
            cancellationDeliveryTracker?.Invoke(_cancellationDeliveryCompleted.Task);
            return true;
        }

        private async Task DeliverCancellationAsync(
            Task cancellationCallbacks,
            Func<PendingRequest, string, bool>? cancellationHandler,
            string reason)
        {
            var disposeAfterDelivery = false;
            try
            {
                try { await cancellationCallbacks.ConfigureAwait(false); }
                catch { /* cancellation cleanup must still reach the transport owner */ }

                try { disposeAfterDelivery = cancellationHandler?.Invoke(this, reason) ?? false; }
                catch { /* request cancellation is best-effort during terminal cleanup */ }
            }
            finally
            {
                _cancellationDeliveryCompleted.TrySetResult(true);
            }

            if (disposeAfterDelivery)
                DisposeLifetime();
        }

        internal bool TryCompleteLifetime()
        {
            lock (_lifetimeSync)
            {
                if (_terminalState != 0)
                    return _terminalState == 2;

                _terminalState = 2;
                return true;
            }
        }

        internal void DisposeLifetime()
        {
            Timer? lifetimeTimer = null;
            CancellationTokenRegistration transportCancellationRegistration = default;
            CancellationTokenSource? lifetimeCts = null;
            CancellationTokenSource? disconnectProbeCts = null;
            Task? disconnectProbeTask = null;
            Task? cancellationDeliveryTask = null;
            lock (_lifetimeSync)
            {
                if (_lifetimeDisposed != 0)
                    return;

                if (_terminalState == 1 && !_cancellationDeliveryCompleted.Task.IsCompleted)
                {
                    if (_lifetimeDisposeContinuationScheduled)
                        return;
                    _lifetimeDisposeContinuationScheduled = true;
                    cancellationDeliveryTask = _cancellationDeliveryCompleted.Task;
                }
                else
                {
                    if (_terminalState == 0)
                        _terminalState = 2;
                    _lifetimeDisposed = 1;
                    lifetimeTimer = _lifetimeTimer;
                    transportCancellationRegistration = _transportCancellationRegistration;
                    lifetimeCts = _lifetimeCts;
                    disconnectProbeCts = DisconnectProbeCts;
                    disconnectProbeTask = DisconnectProbeTask;
                }
            }

            if (cancellationDeliveryTask is not null)
            {
                // Never synchronously wait for request cancellation delivery during shutdown.
                // A callback may be non-cooperative; resume idempotent cleanup after the single
                // delivery signal settles instead of bypassing ProgramRunner's bounded wait.
                // shutdown 中に request cancellation delivery を同期 wait せず、single delivery
                // signal 完了後の continuation で idempotent cleanup を再開する。
                _ = cancellationDeliveryTask.ContinueWith(
                    static (_, state) => ((PendingRequest)state!).DisposeLifetime(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            lifetimeTimer?.Dispose();
            transportCancellationRegistration.Unregister();
            lifetimeCts?.Dispose();
            if (disconnectProbeCts is { } probeCts)
            {
                try { probeCts.Cancel(); } catch (ObjectDisposedException) { /* probe already stopped */ }
                if (disconnectProbeTask is { IsCompleted: false } probeTask)
                {
                    _ = probeTask.ContinueWith(
                        static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                        probeCts,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    probeCts.Dispose();
                }
            }
        }
    }

    private sealed class HttpMcpTimeoutException : TimeoutException
    {
        internal HttpMcpTimeoutException(string category, TimeSpan timeout, Exception innerException)
            : base(
                $"HTTP MCP operation timed out; category={category}; timeout_ms={timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}.",
                innerException)
        {
            Category = category;
            Timeout = timeout;
        }

        internal string Category { get; }

        internal TimeSpan Timeout { get; }
    }
}
