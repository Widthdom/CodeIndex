using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CodeIndex.Mcp;

/// <summary>
/// HTTP MCP transport (issue #1558). Each HTTP POST carries one JSON-RPC request frame in the
/// body and the matching JSON-RPC response is returned as the response body (or 204 No Content
/// for notifications). The implementation is intentionally single-session — one in-flight request
/// at a time — to mirror the existing stdio loop's request/response pairing and to keep the
/// JSON-RPC ordering invariant the rest of the MCP server depends on. SSE / multi-client
/// fan-out is left as a follow-up because the underlying handler today never emits unsolicited
/// server→client messages.
/// HTTP MCP トランスポート (issue #1558)。HTTP POST 1 件が JSON-RPC リクエスト 1 件と対応し、
/// 応答も同じ HTTP レスポンスのボディに乗せる（通知の場合は 204 No Content）。stdio ループと
/// 同様にシングルセッションで「リクエスト 1 件 → レスポンス 1 件」の順序不変条件を維持する。
/// SSE / マルチクライアント対応は将来作業として切り出す（現サーバーは自発的なサーバー→クライアント
/// メッセージを発生させないため、最小単位として POST/response で十分）。
/// </summary>
internal sealed class HttpMcpTransport : IMcpTransport, IOutOfBandMcpTransport
{
    internal const int DefaultMaxRequestBodyBytes = 1_000_000;
    internal const int MaxConfiguredRequestBodyBytes = 16 * 1024 * 1024;
    internal const int DefaultMaxQueuedRequests = 64;
    internal const int MaxConfiguredQueuedRequests = 1024;
    internal const int DefaultMaxConcurrentHandlers = 64;
    internal const int MaxConfiguredConcurrentHandlers = 1024;
    internal const int DefaultMaxEventStreams = 16;
    internal const int MaxConfiguredEventStreams = 1024;
    internal const int MaxRequestLogFieldCharacters = 256;
    internal const string RequestLogTruncationMarker = "...<truncated>";
    internal const string MaxRequestBodyBytesEnvVar = "CDIDX_MCP_HTTP_MAX_REQUEST_BYTES";
    internal const string MaxQueueDepthEnvVar = "CDIDX_MCP_HTTP_MAX_QUEUE_DEPTH";
    internal const string MaxConcurrentHandlersEnvVar = "CDIDX_MCP_HTTP_MAX_CONCURRENT_HANDLERS";
    internal const string MaxEventStreamsEnvVar = "CDIDX_MCP_HTTP_MAX_EVENT_STREAMS";
    private const string BearerPrefix = "Bearer ";
    private static readonly TimeSpan EventStreamDisconnectProbeInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonDocumentOptions HttpProbeJsonDocumentOptions = new()
    {
        MaxDepth = McpServer.MaxJsonDepth,
    };

    private readonly HttpListener _listener;
    private readonly string _endpoint;
    private readonly Action<HttpRequestLogRecord>? _requestLogger;
    private readonly object _requestLoggerGate = new();
    private readonly ConcurrentDictionary<Guid, EventStream> _eventStreams = new();
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly Channel<PendingRequest> _requestQueue;
    private readonly SemaphoreSlim _handlerSemaphore;
    private readonly int _maxRequestBodyBytes;
    private readonly int _maxQueuedRequests;
    private readonly int _maxConcurrentHandlers;
    private readonly int _maxEventStreams;
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
    private int _eventStreamCount;
    private bool _disposed;

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
        int? maxQueuedRequests = null,
        int? maxConcurrentHandlers = null,
        int? maxEventStreams = null)
    {
        _maxRequestBodyBytes = ResolvePositiveIntOption(
            maxRequestBodyBytes,
            nameof(maxRequestBodyBytes),
            MaxRequestBodyBytesEnvVar,
            DefaultMaxRequestBodyBytes,
            MaxConfiguredRequestBodyBytes,
            "HTTP MCP request body byte limit");
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
        _requestQueue = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(_maxQueuedRequests)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        if (McpAuthenticationLimits.IsTokenOversized(bearerToken))
            throw new ArgumentException($"Token must not exceed {McpAuthenticationLimits.MaxTokenCharacters.ToString(CultureInfo.InvariantCulture)} characters.", nameof(bearerToken));
        _handlerSemaphore = new SemaphoreSlim(_maxConcurrentHandlers, _maxConcurrentHandlers);
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _requestLogger = requestLogger;
        _bearerTokenHash = string.IsNullOrEmpty(bearerToken)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
        _endpoint = $"http://{host}:{boundPort}/";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token), CancellationToken.None);
    }

    public string Name => "http";

    public string Endpoint => _endpoint;

    internal bool RequiresBearerToken => _bearerTokenHash is not null;

    internal Func<string, string?>? OutOfBandFrameHandler { get; set; }

    internal Func<string>? HealthJsonProvider { get; set; }

    internal TimeSpan? KeepAliveInterval { get; set; }

    internal Func<string>? KeepAliveFrameProvider { get; set; }

    internal bool HasEventStreams => EventStreamCount > 0;

    internal int MaxRequestBodyBytes => _maxRequestBodyBytes;

    internal int MaxQueuedRequests => _maxQueuedRequests;

    internal int MaxConcurrentHandlers => _maxConcurrentHandlers;

    internal int MaxEventStreams => _maxEventStreams;

    internal int QueuedRequestCount => Volatile.Read(ref _queuedRequestCount);

    internal int EventStreamCount => Volatile.Read(ref _eventStreamCount);

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

        var isLoopback = ipAddress is not null && IPAddress.IsLoopback(ipAddress);

        if (port == 0)
            port = FindFreePort(ipAddress ?? IPAddress.Loopback);

        var prefix = $"http://{prefixHost}:{port.ToString(CultureInfo.InvariantCulture)}/";
        return new HttpListenSpec(prefix, displayHost, port, isLoopback);
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

        var raw = Environment.GetEnvironmentVariable(envVar);
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            if (parsed > maximumValue)
                throw new FormatException($"{envVar} must be between 1 and {maximumValue.ToString(CultureInfo.InvariantCulture)} for {description}; got {parsed.ToString(CultureInfo.InvariantCulture)}.");
            return (int)parsed;
        }

        return defaultValue;
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

                _ = Task.Run(() => RunHandlerAsync(context, cancellationToken), CancellationToken.None);
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
        context.Response.AddHeader("Retry-After", "1");
        await RespondAsync(context, (int)HttpStatusCode.TooManyRequests, "MCP HTTP concurrent handler limit is full.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.TooManyRequests);
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = BeginRequest(context);

        if (!await TryAuthorizeAsync(request).ConfigureAwait(false))
            return;

        if (IsHealthPath(context.Request.Url?.AbsolutePath))
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.AddHeader("Allow", "GET");
                await RespondAsync(context, (int)HttpStatusCode.MethodNotAllowed, "MCP health endpoint only accepts GET.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
                return;
            }

            var healthJson = HealthJsonProvider?.Invoke() ?? """{"status":"starting","db_open":false}""";
            await RespondJsonAsync(context, (int)HttpStatusCode.OK, healthJson).ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.OK);
            return;
        }

        if (IsEventsPath(context.Request.Url?.AbsolutePath))
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.AddHeader("Allow", "GET");
                await RespondAsync(context, (int)HttpStatusCode.MethodNotAllowed, "MCP HTTP event stream only accepts GET.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
                return;
            }

            await RunEventStreamAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.AddHeader("Allow", "POST");
            await RespondAsync(context, (int)HttpStatusCode.MethodNotAllowed, "MCP HTTP transport only accepts POST.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.MethodNotAllowed);
            return;
        }

        var body = await TryReadRequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null)
            return;

        if (string.IsNullOrWhiteSpace(body))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Close();
            LogRequest(request, (int)HttpStatusCode.NoContent);
            return;
        }

        request.Body = body;
        request.RequestId = TryExtractJsonRpcId(body);
        if (TryHandleOutOfBandFrame(request, body))
            return;

        if (!TryQueueRequest(request))
        {
            context.Response.AddHeader("Retry-After", "1");
            await RespondAsync(context, (int)HttpStatusCode.TooManyRequests, "MCP HTTP request queue is full.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.TooManyRequests);
        }
    }

    private async Task<string?> TryReadRequestBodyAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        if (context.Request.ContentLength64 > _maxRequestBodyBytes)
        {
            await RespondAsync(context, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
            return null;
        }

        using var buffer = new MemoryStream();
        var scratch = new byte[Math.Min(8192, _maxRequestBodyBytes)];
        while (true)
        {
            var read = await context.Request.InputStream.ReadAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > _maxRequestBodyBytes)
            {
                await RespondAsync(context, (int)HttpStatusCode.RequestEntityTooLarge, $"MCP HTTP request body exceeds the configured {_maxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.\n").ConfigureAwait(false);
                LogRequest(request, (int)HttpStatusCode.RequestEntityTooLarge);
                return null;
            }

            buffer.Write(scratch, 0, read);
        }

        return (context.Request.ContentEncoding ?? Encoding.UTF8).GetString(buffer.ToArray());
    }

    private bool TryQueueRequest(PendingRequest request)
    {
        Interlocked.Increment(ref _queuedRequestCount);
        if (_requestQueue.Writer.TryWrite(request))
            return true;

        Interlocked.Decrement(ref _queuedRequestCount);
        return false;
    }

    private bool TryHandleOutOfBandFrame(PendingRequest request, string body)
    {
        if (OutOfBandFrameHandler is null || (!IsCancellationNotification(body) && !IsJsonRpcResponse(body)))
            return false;

        var context = request.Context;
        try
        {
            var frame = OutOfBandFrameHandler(body);
            if (frame is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                LogRequest(request, (int)HttpStatusCode.NoContent);
                return true;
            }

            var payload = Encoding.UTF8.GetBytes(frame);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.LongLength;
            context.Response.OutputStream.Write(payload);
            context.Response.OutputStream.Close();
            LogRequest(request, (int)HttpStatusCode.OK);
            return true;
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
            LogRequest(request, 499);
            return true;
        }
    }

    private static bool IsCancellationNotification(string body)
    {
        try
        {
            var node = JsonNode.Parse(body, documentOptions: HttpProbeJsonDocumentOptions);
            if (node is not JsonObject obj)
                return false;
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
        try
        {
            var node = JsonNode.Parse(body, documentOptions: HttpProbeJsonDocumentOptions);
            return node is JsonObject obj
                && obj.ContainsKey("id")
                && obj["method"] is null
                && (obj.ContainsKey("result") || obj.ContainsKey("error"));
        }
        catch
        {
            return false;
        }
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
                context.Response.Close();
                LogRequest(request, (int)HttpStatusCode.NoContent);
                return;
            }

            var payload = Encoding.UTF8.GetBytes(frame);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.LongLength;
            await context.Response.OutputStream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            context.Response.OutputStream.Close();
            LogRequest(request, (int)HttpStatusCode.OK);
        }
        catch
        {
            // Best-effort: close the response so the listener doesn't leak the context.
            // best-effort で response を閉じる。listener が context を持ち続けないようにする。
            try { context.Response.Abort(); } catch { /* ignore */ }
            throw;
        }
    }

    public async Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken)
    {
        if (_eventStreams.IsEmpty)
            return;

        foreach (var (id, stream) in _eventStreams)
        {
            try
            {
                await stream.WriteJsonRpcEventAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RemoveEventStream(id, stream);
            }
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
        var header = context.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
        {
            request.AuthOutcome = "missing";
        }
        else if (TryExtractBearerToken(header, out var provided))
        {
            if (provided is not null && HashEqualsConfiguredToken(provided))
            {
                request.AuthOutcome = "ok";
                return true;
            }

            request.AuthOutcome = "wrong-token";
        }
        else
        {
            request.AuthOutcome = "wrong-scheme";
        }

        // RFC 7235 §4.1: 401 responses SHOULD carry a WWW-Authenticate challenge so
        // generic HTTP clients (and humans poking at the listener) know which scheme
        // and (optionally) realm to use.
        // RFC 7235 §4.1 に従い 401 には WWW-Authenticate を付け、汎用 HTTP クライアントや
        // 手動デバッグ時に必要なスキームを示す。
        context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"cdidx-mcp\"");
        await RespondAsync(context, (int)HttpStatusCode.Unauthorized, "Missing or invalid bearer token.\n").ConfigureAwait(false);
        LogRequest(request, (int)HttpStatusCode.Unauthorized);
        return false;
    }

    private static bool TryExtractBearerToken(string header, out string? token)
    {
        token = null;
        if (header.Length < BearerPrefix.Length || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = header.AsSpan(BearerPrefix.Length).Trim();
        if (candidate.Length > McpAuthenticationLimits.MaxTokenCharacters)
            return true;

        token = candidate.ToString();
        return true;
    }

    private static async Task RespondAsync(HttpListenerContext context, int statusCode, string body)
    {
        try
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static async Task RespondJsonAsync(HttpListenerContext context, int statusCode, string body)
    {
        try
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
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
            context.Response.AddHeader("Retry-After", "1");
            await RespondAsync(context, (int)HttpStatusCode.TooManyRequests, "MCP HTTP event stream limit is full.\n").ConfigureAwait(false);
            LogRequest(request, (int)HttpStatusCode.TooManyRequests);
            return;
        }

        var streamId = Guid.NewGuid();
        var stream = new EventStream(context.Response);
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.SendChunked = true;
            context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.AddHeader("Connection", "keep-alive");
            _eventStreams[streamId] = stream;

            var prelude = Encoding.UTF8.GetBytes(": cdidx mcp event stream ready\n\n");
            await context.Response.OutputStream.WriteAsync(prelude.AsMemory(), cancellationToken).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            await RunKeepAliveLoopAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Client disconnects are expected for long-lived SSE streams.
        }
        finally
        {
            RemoveEventStream(streamId, stream);
            LogRequest(request, (int)HttpStatusCode.OK);
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private void RemoveEventStream(Guid streamId, EventStream stream)
    {
        _eventStreams.TryRemove(streamId, out _);
        if (stream.TryReleaseSlot())
            Interlocked.Decrement(ref _eventStreamCount);
        try { stream.Response.Abort(); } catch { /* ignore */ }
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

    private sealed class EventStream(HttpListenerResponse response)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _released;

        public HttpListenerResponse Response { get; } = response;

        public async Task WriteJsonRpcEventAsync(string frame, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.Append("event: message\n");
            foreach (var line in frame.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                builder.Append("data: ").Append(line).Append('\n');
            builder.Append('\n');
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());

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
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Response.OutputStream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                await Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
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
        Span<byte> providedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), providedHash);
        return CryptographicOperations.FixedTimeEquals(providedHash, _bearerTokenHash);
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
                try { _pendingRequest.Context.Response.Abort(); } catch { /* ignore */ }
                _pendingRequest = null;
            }
            _listener.Close();
        }
        catch
        {
            // Disposal must not throw — the parent server is already on its way down.
            // dispose は例外を投げない方針: 親サーバーは既に終了処理中なので。
        }
        try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _acceptCts.Dispose();
    }

    /// <summary>Resolved listen spec returned by <see cref="ResolveListenSpec"/>.</summary>
    internal readonly record struct HttpListenSpec(string Prefix, string Host, int Port, bool IsLoopback);

    internal sealed record HttpRequestLogRecord(
        string CorrelationId,
        string? RequestId,
        string RemotePeer,
        string Method,
        string Path,
        int StatusCode,
        double DurationMs,
        string AuthOutcome);

    private PendingRequest BeginRequest(HttpListenerContext context)
    {
        var remotePeer = context.Request.RemoteEndPoint is { } endpoint
            ? endpoint.ToString()
            : "<unknown>";
        return new PendingRequest(
            context,
            Guid.NewGuid().ToString("N"),
            LimitRequestLogField(remotePeer) ?? "<unknown>",
            LimitRequestLogField(context.Request.HttpMethod) ?? string.Empty,
            LimitRequestLogField(context.Request.Url?.AbsolutePath ?? "/") ?? "/");
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
        if (_requestLogger is null || request.Logged)
            return;

        request.Logged = true;
        try
        {
            var record = new HttpRequestLogRecord(
                    request.CorrelationId,
                    request.RequestId,
                    request.RemotePeer,
                    request.Method,
                    request.Path,
                    statusCode,
                    request.Elapsed.TotalMilliseconds,
                    request.AuthOutcome);
            lock (_requestLoggerGate)
            {
                _requestLogger(record);
            }
        }
        catch
        {
            // Logging is best-effort and must not affect the HTTP wire path.
        }
    }

    private static string? TryExtractJsonRpcId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, HttpProbeJsonDocumentOptions);
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

        internal PendingRequest(HttpListenerContext context, string correlationId, string remotePeer, string method, string path)
        {
            Context = context;
            CorrelationId = correlationId;
            RemotePeer = remotePeer;
            Method = method;
            Path = path;
        }

        internal HttpListenerContext Context { get; }

        internal string CorrelationId { get; }

        internal string? RequestId { get; set; }

        internal string? Body { get; set; }

        internal string RemotePeer { get; }

        internal string Method { get; }

        internal string Path { get; }

        internal string AuthOutcome { get; set; } = "none";

        internal bool Logged { get; set; }

        internal TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp);
    }
}
