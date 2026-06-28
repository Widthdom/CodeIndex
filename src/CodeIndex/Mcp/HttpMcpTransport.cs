using System.Globalization;
using System.Collections.Specialized;
using System.Net;
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
/// for notifications). The implementation is intentionally single-session — one in-flight request
/// at a time — to mirror the existing stdio loop's request/response pairing and to keep the
/// JSON-RPC ordering invariant the rest of the MCP server depends on. Server-initiated JSON-RPC
/// notifications are exposed through `/events` as a bounded, multi-client SSE fan-out channel.
/// HTTP MCP トランスポート (issue #1558)。HTTP POST 1 件が JSON-RPC リクエスト 1 件と対応し、
/// 応答も同じ HTTP レスポンスのボディに乗せる（通知の場合は 204 No Content）。stdio ループと
/// 同様にシングルセッションで「リクエスト 1 件 → レスポンス 1 件」の順序不変条件を維持する。
/// サーバー起点の JSON-RPC 通知は `/events` で bounded な multi-client SSE fan-out channel
/// として公開する。
/// </summary>
internal sealed class HttpMcpTransport : IMcpTransport, IOutOfBandMcpTransport
{
    internal const int DefaultMaxRequestBodyBytes = 1_000_000;
    internal const int DefaultMaxResponseBodyBytes = 1_000_000;
    internal const int MaxConfiguredRequestBodyBytes = 16 * 1024 * 1024;
    internal const int MaxConfiguredResponseBodyBytes = 16 * 1024 * 1024;
    internal const int DefaultMaxQueuedRequests = 64;
    internal const int MaxConfiguredQueuedRequests = 1024;
    internal const int DefaultMaxConcurrentHandlers = 64;
    internal const int MaxConfiguredConcurrentHandlers = 1024;
    internal const int DefaultMaxEventStreams = 16;
    internal const int MaxConfiguredEventStreams = 1024;
    internal const int MaxRequestLogFieldCharacters = 256;
    internal const int DefaultRequestLogQueueCapacity = 1024;
    internal const int MaxConfiguredRequestLogQueueCapacity = 16 * 1024;
    internal const int MaxHealthJsonBytes = 64 * 1024;
    internal const int MaxSseEventFrameBytes = 64 * 1024;
    internal const string RequestLogTruncationMarker = "...<truncated>";
    internal const string MaxRequestBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_REQUEST_BYTES";
    internal const string MaxResponseBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES";
    internal const string MaxQueueDepthEnvVar = "CDIDX_MCP_HTTP_MAX_QUEUE_DEPTH";
    internal const string MaxConcurrentHandlersEnvVar = "CDIDX_MCP_HTTP_MAX_CONCURRENT_HANDLERS";
    internal const string MaxEventStreamsEnvVar = "CDIDX_MCP_HTTP_MAX_EVENT_STREAMS";
    internal const string RejectionReasonHeader = "X-Cdidx-Mcp-Rejection";
    internal const string ConcurrentHandlerLimitRejection = "concurrent_handler_limit";
    internal const string RequestQueueLimitRejection = "request_queue_limit";
    internal const string EventStreamLimitRejection = "event_stream_limit";
    internal const string EventStreamWriteFailureDrop = "write_failure";
    internal const string AuthDenialMissing = "missing";
    internal const string AuthDenialAmbiguous = "ambiguous";
    internal const string AuthDenialWrongScheme = "wrong-scheme";
    internal const string AuthDenialMalformedToken = "malformed-token";
    internal const string AuthDenialOversizedToken = "oversized-token";
    internal const string AuthDenialWrongToken = "wrong-token";
    internal const string TimeoutDiagnosticPrefix = "timeout:";
    internal const string LoopbackAuthDisabledWarning = "HTTP MCP is running on a loopback listener without bearer authentication; local processes can connect.";
    private const string BearerPrefix = "Bearer ";
    private const string DefaultStartingHealthJson = """{"status":"starting","db_open":false}""";
    private const string InvalidHealthJson = """{"status":"degraded","db_open":false,"error":"health_provider_invalid"}""";
    private static readonly TimeSpan EventStreamDisconnectProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EventStreamWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeAcceptLoopTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpListener _listener;
    private readonly string _endpoint;
    private readonly Action<HttpRequestLogRecord>? _requestLogger;
    private readonly int _requestLogQueueCapacity;
    private readonly Channel<HttpRequestLogRecord>? _requestLogQueue;
    private readonly Task? _requestLogTask;
    private readonly ConcurrentDictionary<Guid, EventStream> _eventStreams = new();
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly Channel<PendingRequest> _requestQueue;
    private readonly SemaphoreSlim _queueSlots;
    private readonly SemaphoreSlim _handlerSemaphore;
    private readonly int _maxRequestBodyBytes;
    private readonly int _maxResponseBodyBytes;
    private readonly int _maxQueuedRequests;
    private readonly int _maxConcurrentHandlers;
    private readonly int _maxEventStreams;
    private readonly TimeSpan _eventStreamWriteTimeout;
    private readonly Task _acceptLoop;
    // The configured bearer token's SHA-256 digest, precomputed once at construction so the
    // per-request auth path never hashes the secret. Storing the digest (not the token) keeps the
    // per-request work proportional only to the attacker-supplied input length, eliminating the
    // configured-token length side channel that a per-request hash would still leak.
    // 設定トークンの SHA-256 をコンストラクタで一度だけ計算し、リクエスト毎の auth では
    // 攻撃者入力のみハッシュ計算する。これにより設定トークン長による timing 漏洩を排除する。
    private readonly byte[]? _bearerTokenHash;
    private PendingRequest? _pendingRequest;
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
    private long _eventStreamLimitRejectionCount;
    private long _eventStreamDropCount;
    private long _eventStreamWriteFailureDropCount;
    private long _authDenialMissingCount;
    private long _authDenialAmbiguousCount;
    private long _authDenialWrongSchemeCount;
    private long _authDenialMalformedTokenCount;
    private long _authDenialOversizedTokenCount;
    private long _authDenialWrongTokenCount;
    private string? _lastResponseAbortCleanupFailure;
    private string? _lastResponseCloseCleanupFailure;
    private string? _lastEventStreamDropReason;
    private string? _lastAuthDenialReason;
    private string? _lastRequestLogDropReason;
    private bool _disposed;
    private bool _ownedSemaphoreGatesDisposed;

    /// <summary>
    /// Build an HTTP transport bound to the supplied loopback prefix. If <paramref name="bearerToken"/>
    /// is non-empty, every request must carry a matching `Authorization: Bearer ...` header; otherwise
    /// the transport refuses to bind to non-loopback hosts to avoid exposing the MCP catalog to the
    /// local network without an explicit secret.
    /// 指定された loopback プレフィックスに HTTP トランスポートを bind する。<paramref name="bearerToken"/>
    /// が空でない場合、すべてのリクエストに `Authorization: Bearer ...` ヘッダーが必要。トークン未指定で
    /// loopback 以外に bind しようとした場合は明示的に拒否し、秘密情報なしの LAN 露出を防ぐ。
    /// </summary>
    internal HttpMcpTransport(
        string prefix,
        string host,
        int boundPort,
        string? bearerToken,
        Action<HttpRequestLogRecord>? requestLogger = null,
        int? maxRequestBodyBytes = null,
        int? maxResponseBodyBytes = null,
        int? maxQueuedRequests = null,
        int? maxConcurrentHandlers = null,
        int? maxEventStreams = null,
        int? requestLogQueueCapacity = null,
        TimeSpan? eventStreamWriteTimeout = null)
    {
        _maxRequestBodyBytes = ResolvePositiveIntOption(
            maxRequestBodyBytes,
            nameof(maxRequestBodyBytes),
            MaxRequestBodyBytesEnvVar,
            DefaultMaxRequestBodyBytes,
            MaxConfiguredRequestBodyBytes,
            "HTTP MCP request body byte limit");
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
        _requestLogQueueCapacity = ResolveRequestLogQueueCapacity(requestLogQueueCapacity);
        _requestQueue = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(_maxQueuedRequests)
        {
            SingleReader = true,
            SingleWriter = false,
            // Queue slots are acquired before TryWrite. FullMode.Wait is a defensive
            // channel contract; a full queue is rejected via request_queue_limit rather
            // than blocking an HTTP handler indefinitely.
            // TryWrite 前に queue slot を取得する。FullMode.Wait は防御的な channel 契約で、
            // 満杯時は HTTP handler を無期限 block せず request_queue_limit で拒否する。
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _queueSlots = new SemaphoreSlim(_maxQueuedRequests, _maxQueuedRequests);
        if (bearerToken is { Length: > 0 } && !McpAuthenticationLimits.IsTokenShapeValid(bearerToken))
            throw new ArgumentException(McpAuthenticationLimits.FormatTokenShapeError("Token"), nameof(bearerToken));
        if (bearerToken is { Length: > 0 } && bearerToken.Contains(',', StringComparison.Ordinal))
            throw new ArgumentException("HTTP bearer token must not contain commas; commas are reserved for rejecting ambiguous Authorization headers.", nameof(bearerToken));
        IsLoopbackBind = IsLoopbackHost(host);
        if (string.IsNullOrEmpty(bearerToken) && !IsLoopbackBind)
            throw new ArgumentException("HTTP MCP requires bearer authentication when binding outside loopback.", nameof(bearerToken));
        _bearerTokenHash = string.IsNullOrEmpty(bearerToken)
            ? null
            : McpAuthenticationLimits.HashTokenToArray(bearerToken);
        _handlerSemaphore = new SemaphoreSlim(_maxConcurrentHandlers, _maxConcurrentHandlers);
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
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

    public string Name => "http";

    public string Endpoint => _endpoint;

    internal bool RequiresBearerToken => _bearerTokenHash is not null;

    internal bool IsLoopbackBind { get; }

    internal bool AuthDisabled => !RequiresBearerToken;

    internal string? AuthDisabledWarning => AuthDisabled ? LoopbackAuthDisabledWarning : null;

    internal bool OwnedSemaphoreGatesDisposedForTests => Volatile.Read(ref _ownedSemaphoreGatesDisposed);

    internal Func<string, CancellationToken, Task<string?>>? OutOfBandFrameHandler { get; set; }

    internal Func<string>? HealthJsonProvider { get; set; }

    internal TimeSpan? KeepAliveInterval { get; set; }

    internal Func<string>? KeepAliveFrameProvider { get; set; }

    internal bool HasEventStreams => EventStreamCount > 0;

    internal int MaxRequestBodyBytes => _maxRequestBodyBytes;

    internal int MaxResponseBodyBytes => _maxResponseBodyBytes;

    internal int MaxQueuedRequests => _maxQueuedRequests;

    internal int MaxConcurrentHandlers => _maxConcurrentHandlers;

    internal int MaxEventStreams => _maxEventStreams;

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
        var message = LimitRequestLogField(exception.Message) ?? exception.GetType().Name;
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
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            if (parsed > maximumValue)
                throw new FormatException($"{envVar} must be between 1 and {maximumValue.ToString(CultureInfo.InvariantCulture)} for {description}; got {parsed.ToString(CultureInfo.InvariantCulture)}.");
            return (int)parsed;
        }

        return defaultValue;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingRequest is not null)
            throw new InvalidOperationException("HttpMcpTransport: ReadFrameAsync called twice without an intervening WriteFrameAsync.");

        try
        {
            var request = await _requestQueue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _queuedRequestCount);
            _queueSlots.Release();
            _pendingRequest = request;
            return request.Body;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
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
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || _disposed)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (!_handlerSemaphore.Wait(0))
                {
                    await RejectHandlerLimitAsync(context).ConfigureAwait(false);
                    continue;
                }

                // The handler owns a semaphore slot. Do not use the shutdown token as the
                // scheduling token here, because a pre-canceled Task.Run would skip the
                // handler's finally block and leak the slot.
                // handler は semaphore slot を所有する。pre-canceled Task.Run で finally が
                // 走らず slot が漏れないよう、shutdown token は handler 内だけに渡す。
                _ = BackgroundTaskObserver.Run(
                    () => RunHandlerAsync(context, cancellationToken),
                    "cdidx-mcp-http",
                    "request handler");
            }
        }
        finally
        {
            _requestQueue.Writer.TryComplete();
        }
    }

    private async Task RunHandlerAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleContextAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _handlerSemaphore.Release();
        }
    }

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

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = BeginRequest(context, cancellationToken);

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

        var body = await TryReadRequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null)
            return;

        if (string.IsNullOrWhiteSpace(body))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            CloseResponseOrThrow(context.Response, "empty request body");
            LogRequest(request, (int)HttpStatusCode.NoContent);
            return;
        }

        request.Body = body;
        request.RequestId = TryExtractJsonRpcId(body);
        if (await TryHandleOutOfBandFrameAsync(request, body, cancellationToken).ConfigureAwait(false))
            return;

        if (!TryQueueRequest(request))
        {
            MarkRejected(request, RequestQueueLimitRejection);
            context.Response.AddHeader("Retry-After", "1");
            context.Response.AddHeader(RejectionReasonHeader, RequestQueueLimitRejection);
            await RespondAsync(request, (int)HttpStatusCode.TooManyRequests, "MCP HTTP request queue is full.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.TooManyRequests);
        }
    }

    private async Task<string?> TryReadRequestBodyAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        if (context.Request.ContentLength64 > _maxRequestBodyBytes)
        {
            request.Diagnostic = "request_body_limit_exceeded";
            await RespondAsync(request, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
            return null;
        }
        if (context.Request.ContentLength64 < 0)
            request.Diagnostic = "request_body_length_unknown";

        using var buffer = new MemoryStream();
        var scratch = new byte[Math.Min(8192, _maxRequestBodyBytes)];
        while (true)
        {
            var read = await context.Request.InputStream.ReadAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > _maxRequestBodyBytes)
            {
                request.Diagnostic = "request_body_limit_exceeded";
                await RespondAsync(request, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
                return null;
            }

            buffer.Write(scratch, 0, read);
        }

        // ToArray/GetString materializes only after Content-Length and streaming reads have both
        // enforced _maxRequestBodyBytes.
        return (context.Request.ContentEncoding ?? Encoding.UTF8).GetString(buffer.ToArray());
    }

    private bool TryQueueRequest(PendingRequest request)
    {
        if (!_queueSlots.Wait(0))
            return false;

        Interlocked.Increment(ref _queuedRequestCount);
        if (_requestQueue.Writer.TryWrite(request))
            return true;

        Interlocked.Decrement(ref _queuedRequestCount);
        _queueSlots.Release();
        return false;
    }

    private async Task<bool> TryHandleOutOfBandFrameAsync(PendingRequest request, string body, CancellationToken cancellationToken)
    {
        if (OutOfBandFrameHandler is null || (!IsCancellationNotification(body) && !IsJsonRpcResponse(body)))
            return false;

        var context = request.Context;
        try
        {
            var frame = await OutOfBandFrameHandler(body, cancellationToken).ConfigureAwait(false);
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
        catch
        {
            AbortResponseBestEffort(context.Response, "out-of-band response failure");
            LogRequest(request, 499);
            return true;
        }
    }

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

    public async Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = _pendingRequest
            ?? throw new InvalidOperationException("HttpMcpTransport: WriteFrameAsync called without a pending ReadFrameAsync.");
        _pendingRequest = null;
        var context = request.Context;

        try
        {
            if (frame is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                CloseResponseOrThrow(context.Response, "request no-content response");
                LogRequest(request, (int)HttpStatusCode.NoContent);
                return;
            }

            var payload = Encoding.UTF8.GetBytes(frame);
            if (!await TryRejectOversizedResponseAsync(request, payload.LongLength).ConfigureAwait(false))
                return;
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.LongLength;
            await WriteResponseBytesAsync(
                context.Response,
                payload,
                cancellationToken,
                "request response body timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            CloseOutputStreamOrThrow(context.Response.OutputStream, "request response body");
            LogRequest(request, (int)HttpStatusCode.OK);
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
    }

    private async Task<bool> TryRejectOversizedResponseAsync(PendingRequest request, long payloadBytes)
    {
        if (payloadBytes <= _maxResponseBodyBytes)
            return true;

        request.Diagnostic = "response_body_limit_exceeded";
        await RespondAsync(
            request,
            (int)HttpStatusCode.InternalServerError,
            $"MCP HTTP response body exceeds the configured {_maxResponseBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.InternalServerError);
        return false;
    }

    private async Task WriteResponseBytesAsync(
        HttpListenerResponse response,
        byte[] bytes,
        CancellationToken cancellationToken,
        string timeoutOperation,
        string timeoutCategory)
    {
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, ResponseWriteTimeout, cancellationToken);
        await AwaitOutputOperationAsync(
            response.OutputStream.WriteAsync(bytes.AsMemory(), writeScope.Token).AsTask(),
            writeScope,
            cancellationToken,
            timeoutCategory,
            ResponseWriteTimeout,
            () => AbortResponseBestEffort(response, timeoutOperation)).ConfigureAwait(false);
    }

    private async Task FlushResponseOutputAsync(
        HttpListenerResponse response,
        CancellationToken cancellationToken,
        string timeoutOperation,
        string timeoutCategory)
    {
        using var writeScope = OperationTimeoutScope.Create(timeoutCategory, ResponseWriteTimeout, cancellationToken);
        await AwaitOutputOperationAsync(
            response.OutputStream.FlushAsync(writeScope.Token),
            writeScope,
            cancellationToken,
            timeoutCategory,
            ResponseWriteTimeout,
            () => AbortResponseBestEffort(response, timeoutOperation)).ConfigureAwait(false);
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
        Action onTimeout)
    {
        try
        {
            await operationTask.WaitAsync(writeScope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (writeScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            onTimeout();
            ObserveAbandonedOutputOperation(operationTask);
            throw new HttpMcpTimeoutException(timeoutCategory, timeout, ex);
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
            RemoveEventStream(id, stream);
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
            await WriteResponseBytesAsync(
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
            response => AbortResponseBestEffort(response, "sse write timeout"));
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.SendChunked = true;
            context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.AddHeader("X-Accel-Buffering", "no");
            context.Response.AddHeader("X-Cdidx-Mcp-Event-Stream-Id", streamId.ToString("N", CultureInfo.InvariantCulture));
            _eventStreams[streamId] = stream;

            var prelude = Encoding.UTF8.GetBytes(": cdidx mcp event stream ready\n\n");
            await WriteResponseBytesAsync(
                context.Response,
                prelude,
                cancellationToken,
                "event stream prelude timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);
            await FlushResponseOutputAsync(
                context.Response,
                cancellationToken,
                "event stream prelude flush timeout",
                OperationTimeoutCategories.HttpResponseWrite).ConfigureAwait(false);

            await RunKeepAliveLoopAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        Action<HttpListenerResponse> abortResponseOnTimeout) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private string? _diagnostic;
        private int _released;

        public HttpListenerResponse Response { get; } = response;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

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
            => Interlocked.Exchange(ref _released, 1) == 0;

        private async Task WriteSseBytesAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            using var writeScope = OperationTimeoutScope.Create(
                OperationTimeoutCategories.SseWrite,
                writeTimeout,
                cancellationToken);
            try
            {
                await _writeGate.WaitAsync(writeScope.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (writeScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpMcpTimeoutException(OperationTimeoutCategories.SseWrite, writeTimeout, ex);
            }

            try
            {
                if (beforeWriteForTests is not null)
                    await beforeWriteForTests(writeScope.Token).ConfigureAwait(false);
                await AwaitOutputOperationAsync(
                    Response.OutputStream.WriteAsync(bytes.AsMemory(), writeScope.Token).AsTask(),
                    writeScope,
                    cancellationToken,
                    OperationTimeoutCategories.SseWrite,
                    writeTimeout,
                    () => abortResponseOnTimeout(Response)).ConfigureAwait(false);
                await AwaitOutputOperationAsync(
                    Response.OutputStream.FlushAsync(writeScope.Token),
                    writeScope,
                    cancellationToken,
                    OperationTimeoutCategories.SseWrite,
                    writeTimeout,
                    () => abortResponseOnTimeout(Response)).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (writeScope.IsTimeoutCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpMcpTimeoutException(OperationTimeoutCategories.SseWrite, writeTimeout, ex);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose() => _writeGate.Dispose();
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _acceptCts.Cancel(); } catch { /* ignore */ }
        try
        {
            if (_pendingRequest is not null)
            {
                AbortResponseBestEffort(_pendingRequest.Context.Response, "pending request disposal");
                _pendingRequest = null;
            }
            _listener.Close();
        }
        catch
        {
            // Disposal must not throw — the parent server is already on its way down.
            // dispose は例外を投げない方針: 親サーバーは既に終了処理中なので。
        }
        var acceptLoopCompleted = false;
        try
        {
            await _acceptLoop.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false);
            acceptLoopCompleted = true;
        }
        catch (TimeoutException)
        {
            // Disposal is best-effort; a platform-delayed listener teardown must not hang shutdown.
            // dispose は best-effort。プラットフォーム都合で listener 終了が遅れても shutdown を止めない。
        }
        catch
        {
            acceptLoopCompleted = true;
        }

        if (acceptLoopCompleted)
            _acceptCts.Dispose();
        if (_requestLogQueue is not null)
        {
            _requestLogQueue.Writer.TryComplete();
            try
            {
                if (_requestLogTask is not null)
                    await _requestLogTask.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false);
            }
            catch
            {
                // Request logging is best-effort; shutdown must not wait indefinitely.
                // request log は best-effort。shutdown を無期限に待たせない。
            }
        }
        if (acceptLoopCompleted && await WaitForOwnedSemaphoreGatesIdleAsync().ConfigureAwait(false))
        {
            _queueSlots.Dispose();
            _handlerSemaphore.Dispose();
            Volatile.Write(ref _ownedSemaphoreGatesDisposed, true);
        }
    }

    private async Task<bool> WaitForOwnedSemaphoreGatesIdleAsync()
    {
        var deadline = DateTimeOffset.UtcNow.Add(DisposeAcceptLoopTimeout);
        while (_queueSlots.CurrentCount != _maxQueuedRequests
            || _handlerSemaphore.CurrentCount != _maxConcurrentHandlers)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }

    private void MarkRejected(PendingRequest request, string reason)
    {
        request.RejectionReason = reason;
        if (string.Equals(reason, ConcurrentHandlerLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _concurrentHandlerLimitRejectionCount);
        else if (string.Equals(reason, RequestQueueLimitRejection, StringComparison.Ordinal))
            Interlocked.Increment(ref _requestQueueLimitRejectionCount);
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
        string? Diagnostic);

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
        if (_requestLogger is null || _requestLogQueue is null || request.Logged)
            return;

        request.Logged = true;
        var record = new HttpRequestLogRecord(
            request.CorrelationId,
            request.RequestId,
            request.RemotePeer,
            request.Method,
            request.Path,
            statusCode,
            request.Elapsed.TotalMilliseconds,
            request.AuthOutcome,
            request.RejectionReason,
            request.Diagnostic);
        Interlocked.Increment(ref _pendingRequestLogCount);
        if (_requestLogQueue.Writer.TryWrite(record))
            return;

        Interlocked.Decrement(ref _pendingRequestLogCount);
        if (!_disposed)
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

    private static string? TryExtractJsonRpcId(string body)
    {
        try
        {
            // HandleContextAsync calls this only with a body returned by TryReadRequestBodyAsync,
            // so the full JSON parse is bounded by the HTTP request-body byte limit.
            using var doc = JsonDocument.Parse(body, JsonFrameParser.CreateDocumentOptions(McpServer.MaxJsonDepth));
            if (!doc.RootElement.TryGetProperty("id", out var id))
                return null;

            var requestId = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => id.GetRawText(),
            };
            return LimitRequestLogField(requestId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class PendingRequest
    {
        private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        internal PendingRequest(HttpListenerContext context, string correlationId, string remotePeer, string method, string path, CancellationToken cancellationToken)
        {
            Context = context;
            CorrelationId = correlationId;
            RemotePeer = remotePeer;
            Method = method;
            Path = path;
            CancellationToken = cancellationToken;
        }

        internal HttpListenerContext Context { get; }

        internal CancellationToken CancellationToken { get; }

        internal string CorrelationId { get; }

        internal string? RequestId { get; set; }

        internal string? Body { get; set; }

        internal string RemotePeer { get; }

        internal string Method { get; }

        internal string Path { get; }

        internal string AuthOutcome { get; set; } = "none";

        internal string? RejectionReason { get; set; }

        internal string? Diagnostic { get; set; }

        internal bool Logged { get; set; }

        internal TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp);
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
