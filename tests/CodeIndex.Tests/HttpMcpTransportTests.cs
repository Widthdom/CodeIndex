using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

/// <summary>
/// Issue #1558: end-to-end coverage for the optional HTTP MCP transport. Each test binds a
/// loopback HTTP listener on an ephemeral port, runs the McpServer loop on a background task,
/// and exercises the JSON-RPC catalog through HttpClient to ensure the transport is wire-
/// compatible with the existing stdio behavior.
/// Issue #1558: 任意の HTTP MCP トランスポートの end-to-end カバレッジ。各テストは loopback
/// 上の ephemeral ポートで HTTP listener を bind し、バックグラウンドで McpServer ループを動かして
/// HttpClient 経由で JSON-RPC を叩き、stdio と同じワイヤー互換性を確認する。
/// </summary>
[Collection("SQLite pool sensitive")]
public class HttpMcpTransportTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;

    public HttpMcpTransportTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_http_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public async Task HttpTransport_PostInitialize_ReturnsHandshakeResult()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("2025-03-26", root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task HttpTransport_PostInitializeWithoutEventsStream_ReturnsOnlyHandshakeResult()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("notifications/initialized", body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_PostInitializeWithEventsStream_EmitsInitializedNotification()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        await using var eventStream = await events.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(eventStream, Encoding.UTF8, leaveOpen: true);
        var initializedTask = ReadUntilAsync(reader, "notifications/initialized");

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var initializedFrame = await initializedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"method\":\"notifications/initialized\"", initializedFrame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_PostNotification_Returns204NoContent()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_GetRequest_Returns405()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var response = await client.GetAsync(harness.Endpoint);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        // RFC 9110 §15.5.6: 405 responses must advertise the supported methods so generic
        // clients can react without parsing the body.
        // RFC 9110 §15.5.6 により 405 はサポートメソッドを `Allow` で示す必要がある。
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReturnsStructuredHealth()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("uptime_s").GetInt64() >= 0);
        Assert.True(root.GetProperty("db_open").GetBoolean());
        Assert.True(root.GetProperty("transport_ready").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("last_request_at").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("last_db_check_at").GetString(), out _));
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_RecordsMethodStatusDurationAndAuthOutcome()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token", requestLogger: records.Enqueue);

        using var client = new HttpClient();
        using (var missingAuth = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        })
        using (var missingAuthResponse = await client.SendAsync(missingAuth))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missingAuthResponse.StatusCode);
        }

        using (var getResponse = await client.GetAsync(harness.Endpoint))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        }

        using (var ok = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", Encoding.UTF8, "application/json"),
        })
        {
            ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
            using var okResponse = await client.SendAsync(ok);
            Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
        }

        // Issue #2419: full-suite runs can be slow enough that the successful POST response
        // reaches the client noticeably before its best-effort request log callback runs, so
        // assert after the async sink catches up.
        var snapshot = await WaitForRequestLogRecordsAsync(records, 3);
        Assert.Equal(3, snapshot.Length);

        // Request logging can be observed from independently handled HTTP requests in any order.
        var missingPost = Assert.Single(snapshot, record =>
            record.AuthOutcome == "missing" &&
            record.StatusCode == (int)HttpStatusCode.Unauthorized &&
            record.Method == "POST");
        Assert.Equal("/", missingPost.Path);
        Assert.Null(missingPost.RequestId);
        Assert.True(missingPost.DurationMs >= 0);
        Assert.False(string.IsNullOrWhiteSpace(missingPost.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(missingPost.RemotePeer));

        var missingGet = Assert.Single(snapshot, record =>
            record.AuthOutcome == "missing" &&
            record.Method == "GET");
        Assert.Equal((int)HttpStatusCode.Unauthorized, missingGet.StatusCode);

        var okPost = Assert.Single(snapshot, record =>
            record.AuthOutcome == "ok" &&
            record.RequestId == "7");
        Assert.Equal("POST", okPost.Method);
        Assert.Equal("/", okPost.Path);
        Assert.Equal((int)HttpStatusCode.OK, okPost.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_TooDeepJsonRpcIdReturnsNull_Issue3014()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue);

        using var response = await harness.PostJsonAsync(BuildNestedJsonRpcRequest(McpServer.MaxJsonDepth + 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await WaitForRequestLogRecordsAsync(records, 1);
        var record = Assert.Single(snapshot, record => record.Method == "POST");
        Assert.Null(record.RequestId);
    }

    [Fact]
    public async Task HttpTransport_TwoSequentialRequests_ShareWarmServer()
    {
        // Issue #1558: AI clients should be able to keep a single MCP server warm across
        // multiple JSON-RPC requests instead of paying subprocess-spawn cost per call.
        // Issue #1558: AI クライアントが MCP サーバーを温めた状態で複数 JSON-RPC を扱えること。
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var first = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    [Fact]
    public async Task HttpTransport_ConcurrentPosts_AreAcceptedAndCorrelatedToResponses()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var first = harness.PostJsonAsync("""{"jsonrpc":"2.0","id":21,"method":"ping"}""");
        var second = harness.PostJsonAsync("""{"jsonrpc":"2.0","id":22,"method":"ping"}""");
        var responses = await Task.WhenAll(first, second);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ids = new List<int>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            ids.Add(doc.RootElement.GetProperty("id").GetInt32());
        }

        Assert.Contains(21, ids);
        Assert.Contains(22, ids);
    }

    [Fact]
    public async Task HttpTransport_EmptyBody_Returns204AndDoesNotKillServer()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        var empty = await harness.PostJsonAsync(string.Empty);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);

        var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var body = await follow.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_CancellationNotificationBeyondJsonDepth_IsQueuedForNormalHandling()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var outOfBandHandlerCalls = 0;
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null);
        transport.OutOfBandFrameHandler = _ =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return null;
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = BuildNestedCancellationNotification(McpServer.MaxJsonDepth + 1);
        var post = client.PostAsync(
            listen.Prefix,
            new StringContent(body, Encoding.UTF8, "application/json"));

        await WaitUntilAsync(
            () => transport.QueuedRequestCount == 1,
            "deep cancellation notification to stay in the normal HTTP MCP queue");

        Assert.Equal(0, Volatile.Read(ref outOfBandHandlerCalls));
        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(body, frame);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":null,"result":{}}""", CancellationToken.None);
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_JsonRpcResponseBeyondJsonDepth_IsQueuedForNormalHandling()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var outOfBandHandlerCalls = 0;
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null);
        transport.OutOfBandFrameHandler = _ =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return null;
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = BuildNestedJsonRpcResponse(McpServer.MaxJsonDepth + 1);
        var post = client.PostAsync(
            listen.Prefix,
            new StringContent(body, Encoding.UTF8, "application/json"));

        await WaitUntilAsync(
            () => transport.QueuedRequestCount == 1,
            "deep JSON-RPC response to stay in the normal HTTP MCP queue");

        Assert.Equal(0, Volatile.Read(ref outOfBandHandlerCalls));
        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(body, frame);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":null,"result":{}}""", CancellationToken.None);
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_RequestBodyOverLimit_Returns413AndDoesNotKillServer()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, maxRequestBodyBytes: 64);

        using var oversized = await harness.PostJsonAsync(new string('x', 65));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var rejectedBody = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("64 byte limit", rejectedBody, StringComparison.Ordinal);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var body = await follow.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_DefaultLimitOptions_UseBoundedDefaults()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null);

        Assert.Equal(HttpMcpTransport.DefaultMaxRequestBodyBytes, transport.MaxRequestBodyBytes);
        Assert.Equal(HttpMcpTransport.DefaultMaxQueuedRequests, transport.MaxQueuedRequests);
        Assert.InRange(transport.MaxRequestBodyBytes, 1, HttpMcpTransport.MaxConfiguredRequestBodyBytes);
        Assert.InRange(transport.MaxQueuedRequests, 1, HttpMcpTransport.MaxConfiguredQueuedRequests);
    }

    [Fact]
    public async Task HttpTransport_ValidEnvironmentLimitOptions_AreApplied()
    {
        using var env = EnvironmentVariableScope.Capture(
            HttpMcpTransport.MaxRequestBodyBytesEnvVar,
            HttpMcpTransport.MaxQueueDepthEnvVar);
        env.Set(HttpMcpTransport.MaxRequestBodyBytesEnvVar, (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        env.Set(HttpMcpTransport.MaxQueueDepthEnvVar, "128");

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null);

        Assert.Equal(2 * 1024 * 1024, transport.MaxRequestBodyBytes);
        Assert.Equal(128, transport.MaxQueuedRequests);
    }

    [Fact]
    public void HttpTransport_OversizedRequestBytesEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxRequestBodyBytesEnvVar);
        env.Set(
            HttpMcpTransport.MaxRequestBodyBytesEnvVar,
            (HttpMcpTransport.MaxConfiguredRequestBodyBytes + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxRequestBodyBytesEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_PositiveOverflowRequestBytesEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxRequestBodyBytesEnvVar);
        env.Set(HttpMcpTransport.MaxRequestBodyBytesEnvVar, ((long)int.MaxValue + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxRequestBodyBytesEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_OversizedQueueDepthEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxQueueDepthEnvVar);
        env.Set(
            HttpMcpTransport.MaxQueueDepthEnvVar,
            (HttpMcpTransport.MaxConfiguredQueuedRequests + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxQueueDepthEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredQueuedRequests.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_PositiveOverflowQueueDepthEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxQueueDepthEnvVar);
        env.Set(HttpMcpTransport.MaxQueueDepthEnvVar, ((long)int.MaxValue + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxQueueDepthEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredQueuedRequests.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_OversizedExplicitLimitOption_ThrowsWithRange()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            maxRequestBodyBytes: HttpMcpTransport.MaxConfiguredRequestBodyBytes + 1));

        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_RequestQueueFull_Returns429()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            maxQueuedRequests: 1);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var first = client.PostAsync(
            listen.Prefix,
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "the first request to fill the HTTP MCP queue");

        using var second = await client.PostAsync(
            listen.Prefix,
            new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains(second.Headers, header => header.Key == "Retry-After");

        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame);
        Assert.Contains("\"id\":1", frame, StringComparison.Ordinal);
        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_EventsStream_DoesNotBlockPostRequests()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType!.MediaType);

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":11,"method":"ping"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(11, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_EventsStream_EmitsOptInKeepAliveNotifications()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "1");
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        await using var eventStream = await events.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(eventStream, Encoding.UTF8, leaveOpen: true);

        var frame = await ReadUntilAsync(reader, "notifications/keep_alive").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("\"method\":\"notifications/keep_alive\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"uptime_s\":", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_EventsStream_RemovesDisconnectedStreams()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "1");
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.HasEventStreams, "the event stream to be registered");

        events.Dispose();

        await WaitUntilAsync(() => !harness.HasEventStreams, "the disconnected event stream to be removed");
    }

    [Fact]
    public async Task HttpTransport_IndexWithProgressToken_EmitsProgressOnEventsStreamAndReturnsResult()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_http_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "two.cs"), "public class Two { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await using var harness = await McpHttpHarness.StartAsync(dbPath);

            using var client = new HttpClient();
            using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);

            await using var eventStream = await events.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(eventStream, Encoding.UTF8, leaveOpen: true);
            var progressTask = ReadUntilAsync(reader, "notifications/progress");

            var body = "{\"jsonrpc\":\"2.0\",\"id\":1684,\"method\":\"tools/call\",\"params\":{\"name\":\"index\",\"arguments\":{\"path\":"
                + JsonSerializer.Serialize(projectRoot)
                + "},\"_meta\":{\"progressToken\":\"http-progress\"}}}";
            using var response = await harness.PostJsonAsync(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseBody = await response.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseBody);
            Assert.Equal(1684, responseDoc.RootElement.GetProperty("id").GetInt32());

            var progressFrame = await progressTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("\"method\":\"notifications/progress\"", progressFrame, StringComparison.Ordinal);
            Assert.Contains("\"progressToken\":\"http-progress\"", progressFrame, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task HttpTransport_EventsStream_UsesBearerAuth()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token");

        using var client = new HttpClient();
        using var unauthorized = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(harness.Endpoint), "events"));
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        using var authorized = await client.SendAsync(authorizedRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("text/event-stream", authorized.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsMissingHeader()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_AcceptsMatchingHeader()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsWrongToken()
    {
        // Verifies the rejection path covers the actual constant-time compare, not just the
        // "missing header" branch — a regression where the comparison short-circuited on the
        // first matching byte would still pass the missing-header test but fail this one.
        // 不一致トークンの拒否経路も検証する（ヘッダー欠落だけでなく定数時間比較が機能していることを担保）。
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsSameLengthWrongToken()
    {
        // Same-length wrong token: covers the constant-time-compare branch *after* the SHA-256
        // hashing seam, since an early length-mismatch return would still allow this to pass on
        // pre-fix code. The behavior change is observable as "401, not 200" — the timing
        // invariant itself cannot be asserted from a unit test.
        // 同じ長さの不一致トークン: SHA-256 経由の定数時間比較分岐をカバーする。
        // 旧実装の length-mismatch 早期 return が消えていることを 401/200 で観察する。
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        Assert.Equal(token.Length, "wrongTokenAa".Length);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrongTokenAa");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_AcceptsLowerCaseScheme()
    {
        // RFC 6750 §2.1: auth-scheme tokens are case-insensitive. Clients that send
        // `authorization: bearer ...` (lowercase) must still authenticate successfully.
        // RFC 6750 §2.1 により auth-scheme は case-insensitive なので、`bearer ...` 表記でも認証成功。
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void ResolveListenSpec_DefaultListen_ResolvesToLoopback()
    {
        var spec = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        Assert.True(spec.IsLoopback);
        Assert.Equal("127.0.0.1", spec.Host);
        Assert.True(spec.Port > 0);
        Assert.EndsWith("/", spec.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:notaport")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("[::1]:notaport")]
    public void ResolveListenSpec_InvalidInput_Throws(string input)
    {
        Assert.Throws<FormatException>(() => HttpMcpTransport.ResolveListenSpec(input));
    }

    [Fact]
    public void ResolveListenSpec_WildcardHost_IsRejected()
    {
        Assert.Throws<FormatException>(() => HttpMcpTransport.ResolveListenSpec("+:0"));
        Assert.Throws<FormatException>(() => HttpMcpTransport.ResolveListenSpec("*:0"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static async Task<HttpMcpTransport.HttpRequestLogRecord[]> WaitForRequestLogRecordsAsync(
        ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord> records,
        int expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (records.Count >= expectedCount)
                return records.ToArray();

            await Task.Delay(10);
        }

        var snapshot = records.ToArray();
        var observed = string.Join(
            ", ",
            snapshot.Select(record => $"{record.Method} {record.Path} {record.StatusCode} auth={record.AuthOutcome} id={record.RequestId ?? "<none>"}"));
        Assert.Fail($"Expected {expectedCount} request log records, but observed {snapshot.Length}: {observed}");
        return snapshot;
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, string expected)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
                break;
            builder.AppendLine(line);
            if (line.Contains(expected, StringComparison.Ordinal))
                return builder.ToString();
        }

        return builder.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    private static string BuildNestedCancellationNotification(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":""");
        AppendNestedObject(builder, nestedObjectCount);
        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildNestedJsonRpcResponse(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","id":1,"result":""");
        AppendNestedObject(builder, nestedObjectCount);
        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildNestedJsonRpcRequest(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","id":1,"method":"ping","params":""");
        AppendNestedObject(builder, nestedObjectCount);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendNestedObject(StringBuilder builder, int nestedObjectCount)
    {
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("""{"next":""");

        builder.Append('0');

        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
    }

    private sealed class McpHttpHarness : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private readonly McpServer _server;
        private readonly HttpMcpTransport _transport;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loopTask;

        private McpHttpHarness(McpServer server, HttpMcpTransport transport, CancellationTokenSource cts, Task loopTask, string endpoint)
        {
            _server = server;
            _transport = transport;
            _cts = cts;
            _loopTask = loopTask;
            Endpoint = endpoint;
        }

        public string Endpoint { get; }

        public bool HasEventStreams => _transport.HasEventStreams;

        public static async Task<McpHttpHarness> StartAsync(
            string dbPath,
            string? bearerToken = null,
            Action<HttpMcpTransport.HttpRequestLogRecord>? requestLogger = null,
            int? maxRequestBodyBytes = null,
            int? maxQueuedRequests = null)
        {
            var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
            var transport = new HttpMcpTransport(
                listen.Prefix,
                listen.Host,
                listen.Port,
                bearerToken,
                requestLogger,
                maxRequestBodyBytes,
                maxQueuedRequests);
            var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var cts = new CancellationTokenSource();
            var loopTask = Task.Run(() => server.RunAsync(transport, cts.Token));
            // Give the listener a tick to start accepting; HttpListener.Start is synchronous but the
            // background task may not have entered GetContextAsync yet by the time the test posts.
            // listener が GetContextAsync に入る前に POST が来ないよう、ごく短い待機を挟む。
            await Task.Yield();
            if (loopTask.IsCompleted)
                await loopTask.ConfigureAwait(false);
            return new McpHttpHarness(server, transport, cts, loopTask, listen.Prefix);
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string body)
        {
            if (_loopTask.IsCompleted)
                await _loopTask.ConfigureAwait(false);

            using var client = new HttpClient { Timeout = RequestTimeout };
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return await client.PostAsync(Endpoint, content);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _transport.DisposeAsync();
            }
            catch { /* ignored */ }
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { /* timeouts / cancellations expected when the listener stops mid-accept */ }
            _server.Dispose();
            _cts.Dispose();
        }
    }
}
