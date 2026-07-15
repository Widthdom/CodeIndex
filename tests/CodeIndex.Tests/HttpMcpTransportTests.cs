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
using CodeIndex.Diagnostics;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

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
    private readonly string _dbDir;
    private readonly string _dbPath;
    private readonly DbContext _db;

    public HttpMcpTransportTests()
    {
        _dbDir = TestProjectHelper.CreateTempProject("cdidx_mcp_http");
        _dbPath = Path.Combine(_dbDir, "codeindex.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public void HttpTransport_Ctor_RejectsCommaBearerToken_Issue3756()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<ArgumentException>(() => new HttpMcpTransport(listen.Prefix, listen.Host, listen.Port, bearerToken: "abc,def"));

        Assert.Contains("commas", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_LoopbackWithoutBearerToken_RequiresExplicitUnsafeOptIn_Issue4549()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");

        var ex = Assert.Throws<ArgumentException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Equal("bearerToken", ex.ParamName);
        Assert.Contains("requires bearer authentication by default", ex.Message, StringComparison.Ordinal);
        Assert.Contains("explicit unsafe opt-in", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBindFailureDiagnostic_RedactsExceptionMessage_Issue4124()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var exception = new HttpListenerException(
            5,
            "access denied at /tmp/private/repo with --token=ghp_abcdefghijklmnopqrstuvwxyz");

        var diagnostic = HttpMcpTransport.FormatBindFailureDiagnostic(listen, exception);

        Assert.Contains("failed to bind HTTP listener", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<path>", diagnostic, StringComparison.Ordinal);
        Assert.Contains("--token=<redacted>", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/private", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_DisposeAsync_ReleasesOwnedResourcesForConcurrentCallers_Issues3985And4176()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var response = await harness.InitializeAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var client = CreateHttpClient(TimeSpan.FromSeconds(5));
        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.HasEventStreams, "the event stream to be registered before disposal");

        var disposeTasks = new Task[8];
        for (var i = 0; i < disposeTasks.Length; i++)
            disposeTasks[i] = Task.Run(async () => await harness.DisposeTransportAsync());

        await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DisposeTransportAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await harness.WaitForServerLoopAsync();
        await WaitUntilAsync(() => !harness.HasEventStreams, "disposal to remove the event stream");

        Assert.True(harness.OwnedSemaphoreGatesDisposed);
    }

    [Fact]
    public void HttpTransport_TimeoutDiagnosticsUseStableCategories_Issue3990()
    {
        Assert.Equal(
            "timeout:http_response_write",
            HttpMcpTransport.FormatTimeoutDiagnosticForTests(OperationTimeoutCategories.HttpResponseWrite));
        Assert.Equal(
            "timeout:sse_write",
            HttpMcpTransport.FormatTimeoutDiagnosticForTests(OperationTimeoutCategories.SseWrite));
    }

    [Fact]
    public async Task HttpTransport_ResponseWriteTimeout_AbortsNonCooperativeWrite_Issue3990()
    {
        using var stream = new NonCancellableHangingStream(hangWrite: true, hangFlush: false);
        var abortCount = 0;

        var ex = await Assert.ThrowsAnyAsync<TimeoutException>(() =>
            HttpMcpTransport.WriteBytesWithTimeoutForTestsAsync(
                stream,
                [1, 2, 3],
                TimeSpan.FromMilliseconds(25),
                OperationTimeoutCategories.HttpResponseWrite,
                () => abortCount++));

        Assert.Contains("category=http_response_write", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, abortCount);
        Assert.Equal(1, stream.WriteCalls);
    }

    [Fact]
    public async Task HttpTransport_ResponseWriteCancellationPropagatesCallerToken_Issue3928()
    {
        using var stream = new NonCancellableHangingStream(hangWrite: true, hangFlush: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        var abortCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpMcpTransport.WriteBytesWithTimeoutForTestsAsync(
                stream,
                [1, 2, 3],
                TimeSpan.FromSeconds(5),
                OperationTimeoutCategories.HttpResponseWrite,
                () => abortCount++,
                cts.Token));

        Assert.Equal(0, abortCount);
        Assert.Equal(1, stream.WriteCalls);
    }

    [Fact]
    public async Task HttpTransport_SseFlushTimeout_AbortsNonCooperativeFlush_Issue3990()
    {
        using var stream = new NonCancellableHangingStream(hangWrite: false, hangFlush: true);
        var abortCount = 0;

        var ex = await Assert.ThrowsAnyAsync<TimeoutException>(() =>
            HttpMcpTransport.FlushWithTimeoutForTestsAsync(
                stream,
                TimeSpan.FromMilliseconds(25),
                OperationTimeoutCategories.SseWrite,
                () => abortCount++));

        Assert.Contains("category=sse_write", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, abortCount);
        Assert.Equal(1, stream.FlushCalls);
    }

    [Fact]
    public async Task HttpTransport_PostInitialize_CoversExplicitAndDefaultHandshakeResults()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using (var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, root.GetProperty("id").GetInt32());
            Assert.Equal("2025-03-26", root.GetProperty("result").GetProperty("protocolVersion").GetString());
        }

        using var defaultResponse = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}""");
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        var defaultBody = await defaultResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("notifications/initialized", defaultBody, StringComparison.Ordinal);
        using var defaultDoc = JsonDocument.Parse(defaultBody);
        Assert.Equal(2, defaultDoc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_McpSessionIdIsolatesTwoClientsAcrossPostAndEvents_Issue4539()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        using var initializer = CreateHttpClient();
        using var otherClient = CreateHttpClient();

        using var initializeResponse = await PostAsync(
            initializer,
            harness.Endpoint,
            """{"jsonrpc":"2.0","id":"client-a-init","method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"client-a","version":"1.0"},"capabilities":{"roots":{},"sampling":{}},"roots":[{"uri":"file:///client-a"}]}}""");
        Assert.Equal(HttpStatusCode.OK, initializeResponse.StatusCode);
        Assert.True(initializeResponse.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var sessionValues));
        var sessionId = Assert.Single(sessionValues);
        Assert.Equal(64, sessionId.Length);
        Assert.All(sessionId, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.Equal("client-a/1.0", harness.CurrentCaller);
        Assert.Equal(["file:///client-a"], harness.ClientRoots);
        Assert.True(harness.ClientSupportsRoots);
        Assert.True(harness.ClientSupportsSampling);

        using var missingSessionInitialize = await PostAsync(
            otherClient,
            harness.Endpoint,
            """{"jsonrpc":"2.0","id":"client-b-init","method":"initialize","params":{"clientInfo":{"name":"client-b","version":"9.9"},"capabilities":{},"roots":[{"uri":"file:///client-b"}]}}""");
        AssertSessionRejected(
            missingSessionInitialize,
            HttpStatusCode.BadRequest,
            HttpMcpTransport.SessionRequiredRejection);

        var wrongSessionId = (sessionId[0] == '0' ? '1' : '0') + sessionId[1..];
        using var wrongSessionInitialize = await PostAsync(
            otherClient,
            harness.Endpoint,
            """{"jsonrpc":"2.0","id":"client-b-wrong","method":"initialize","params":{"clientInfo":{"name":"client-b"}}}""",
            wrongSessionId);
        AssertSessionRejected(
            wrongSessionInitialize,
            HttpStatusCode.NotFound,
            HttpMcpTransport.SessionNotFoundRejection);

        using var ambiguousSessionRequest = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":"client-b-ambiguous","method":"ping"}""",
                Encoding.UTF8,
                "application/json"),
        };
        ambiguousSessionRequest.Headers.TryAddWithoutValidation(
            HttpMcpTransport.SessionIdHeaderName,
            [sessionId, sessionId]);
        using var ambiguousSessionResponse = await otherClient.SendAsync(ambiguousSessionRequest);
        AssertSessionRejected(
            ambiguousSessionResponse,
            HttpStatusCode.NotFound,
            HttpMcpTransport.SessionNotFoundRejection);

        // The logical session is header-bound, not TCP-connection-bound: a fresh HttpClient with
        // client A's session can continue, while client B's missing/wrong headers never reach
        // McpServer state.
        // logical session は TCP connection ではなく header に結び付く。新しい HttpClient でも
        // client A の session なら継続でき、client B の欠落・誤 header は McpServer state に届かない。
        using var resumedClient = CreateHttpClient();
        using var pingResponse = await PostAsync(
            resumedClient,
            harness.Endpoint,
            """{"jsonrpc":"2.0","id":"client-a-ping","method":"ping"}""",
            sessionId);
        Assert.Equal(HttpStatusCode.OK, pingResponse.StatusCode);
        Assert.Equal("client-a/1.0", harness.CurrentCaller);
        Assert.Equal(["file:///client-a"], harness.ClientRoots);

        using var missingEvents = await otherClient.GetAsync(
            new Uri(new Uri(harness.Endpoint), "events"),
            HttpCompletionOption.ResponseHeadersRead);
        AssertSessionRejected(
            missingEvents,
            HttpStatusCode.BadRequest,
            HttpMcpTransport.SessionRequiredRejection);

        using var wrongEventsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(harness.Endpoint), "events"));
        wrongEventsRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, wrongSessionId);
        using var wrongEvents = await otherClient.SendAsync(
            wrongEventsRequest,
            HttpCompletionOption.ResponseHeadersRead);
        AssertSessionRejected(
            wrongEvents,
            HttpStatusCode.NotFound,
            HttpMcpTransport.SessionNotFoundRejection);

        using var validEventsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(harness.Endpoint), "events"));
        validEventsRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var validEvents = await resumedClient.SendAsync(
            validEventsRequest,
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, validEvents.StatusCode);

        static async Task<HttpResponseMessage> PostAsync(
            HttpClient client,
            string endpoint,
            string body,
            string? sessionId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (sessionId is not null)
                request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
            return await client.SendAsync(request);
        }
    }

    [Fact]
    public async Task HttpTransport_ConcurrentInitializersHaveSinglePendingOwner_Issue4539()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            allowUnauthenticatedLoopback: true);
        using var firstClient = CreateHttpClient(TimeSpan.FromSeconds(5));
        using var secondClient = CreateHttpClient(TimeSpan.FromSeconds(5));

        var firstPost = firstClient.PostAsync(
            listen.Prefix,
            new StringContent(
                """{"jsonrpc":"2.0","id":"first-init","method":"initialize","params":{}}""",
                Encoding.UTF8,
                "application/json"));
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "the first initializer to own the pending session");

        using var secondResponse = await secondClient.PostAsync(
            listen.Prefix,
            new StringContent(
                """{"jsonrpc":"2.0","id":"second-init","method":"initialize","params":{}}""",
                Encoding.UTF8,
                "application/json"));
        AssertSessionRejected(
            secondResponse,
            HttpStatusCode.Conflict,
            HttpMcpTransport.SessionInitializationInProgressRejection);
        Assert.Equal(1, transport.QueuedRequestCount);

        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("first-init", frame, StringComparison.Ordinal);
        await transport.WriteFrameAsync(
            """{"jsonrpc":"2.0","id":"first-init","result":{"protocolVersion":"2025-03-26"}}""",
            CancellationToken.None);
        using var firstResponse = await firstPost.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.True(firstResponse.Headers.Contains(HttpMcpTransport.SessionIdHeaderName));
    }

    [Fact]
    public async Task HttpTransport_FailedInitializeReleasesClaimForCorrectedRetry_Issue4539()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using (var fabricatedClient = CreateHttpClient())
        using (var fabricatedRequest = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":"fabricated-init","method":"initialize","params":{"clientInfo":{"name":"fabricated-client"}}}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            fabricatedRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, "fabricated-session");
            using var fabricatedResponse = await fabricatedClient.SendAsync(fabricatedRequest);
            AssertSessionRejected(
                fabricatedResponse,
                HttpStatusCode.NotFound,
                HttpMcpTransport.SessionNotFoundRejection);
        }

        using var failed = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":"failed-init","method":"initialize","params":{"protocolVersion":"2099-01-01","clientInfo":{"name":"failed-client"},"roots":[{"uri":"file:///failed"}]}}""");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.False(failed.Headers.Contains(HttpMcpTransport.SessionIdHeaderName));
        using (var failedDocument = JsonDocument.Parse(await failed.Content.ReadAsStringAsync()))
            Assert.Equal(-32602, failedDocument.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("unknown", harness.CurrentCaller);
        Assert.Empty(harness.ClientRoots);

        using var corrected = await harness.InitializeAsync("corrected-client");
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.True(corrected.Headers.Contains(HttpMcpTransport.SessionIdHeaderName));
        Assert.Equal("corrected-client/1.0", harness.CurrentCaller);

        using var ping = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":"corrected-ping","method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_NullIdInitializeEstablishesSession_Issue4539()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var initialize = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":null,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        Assert.True(initialize.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var sessionValues));
        Assert.Equal(harness.SessionId, Assert.Single(sessionValues));
        using (var initializeDocument = JsonDocument.Parse(await initialize.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, initializeDocument.RootElement.GetProperty("id").ValueKind);
            Assert.Equal(
                "2025-03-26",
                initializeDocument.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        }

        using var ping = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":"null-id-ping","method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_MalformedSessionHeadersAreRejected_Issue4539()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var client = CreateHttpClient();
        var malformedValues = new[]
        {
            "short",
            new string('a', harness.SessionId.Length - 1),
            new string('b', harness.SessionId.Length + 1),
            harness.SessionId + "," + harness.SessionId,
        };
        foreach (var malformedValue in malformedValues)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":"malformed-session","method":"ping"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, malformedValue);
            using var response = await client.SendAsync(request);
            AssertSessionRejected(
                response,
                HttpStatusCode.NotFound,
                HttpMcpTransport.SessionNotFoundRejection);
        }
    }

    [Fact]
    public async Task HttpTransport_PostInitializeWithEventsStream_DoesNotEmitClientInitializedNotification_Issue4433()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var client = CreateHttpClient();
        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        await using var eventStream = await events.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(eventStream, Encoding.UTF8, leaveOpen: true);
        var initializedTask = ReadUntilAsync(reader, "notifications/initialized");

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<TimeoutException>(
            () => initializedTask.WaitAsync(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task HttpTransport_BasicEndpoints_CoverNotificationMethodAndStructuredHealth()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var notificationResponse = await harness.PostJsonAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(HttpStatusCode.NoContent, notificationResponse.StatusCode);

        using var client = CreateHttpClient();
        using (var methodResponse = await client.GetAsync(harness.Endpoint))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, methodResponse.StatusCode);
            // RFC 9110 §15.5.6: 405 responses must advertise the supported methods so generic
            // clients can react without parsing the body.
            // RFC 9110 §15.5.6 により 405 はサポートメソッドを `Allow` で示す必要がある。
            Assert.Contains("POST", methodResponse.Content.Headers.Allow);
        }

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
        Assert.Equal(0, root.GetProperty("http_event_stream_count").GetInt32());
        Assert.True(root.GetProperty("http_event_stream_limit").GetInt32() >= 1);
        Assert.True(root.GetProperty("http_max_concurrent_handlers").GetInt32() >= 1);
        Assert.Equal(0, root.GetProperty("http_queued_request_count").GetInt32());
        Assert.True(root.GetProperty("http_request_queue_limit").GetInt32() >= 1);
        Assert.Equal(0, root.GetProperty("http_request_log_queue_depth").GetInt32());
        Assert.Equal(0, root.GetProperty("http_request_log_queue_capacity").GetInt32());
        Assert.Equal(0, root.GetProperty("http_request_log_dropped_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_request_log_queue_full_drop_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_request_log_callback_failure_count").GetInt64());
        Assert.False(root.GetProperty("http_request_log_degraded").GetBoolean());
        Assert.Equal(0, root.GetProperty("http_concurrent_handler_rejection_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_request_queue_rejection_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_event_stream_rejection_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_event_stream_drop_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_event_stream_write_failure_drop_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_missing_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_ambiguous_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_wrong_scheme_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_malformed_token_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_oversized_token_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_auth_denial_wrong_token_count").GetInt64());
        Assert.False(root.GetProperty("http_auth_required").GetBoolean());
        Assert.True(root.GetProperty("http_auth_disabled").GetBoolean());
        Assert.Equal(
            HttpMcpTransport.LoopbackAuthDisabledWarning,
            root.GetProperty("http_auth_disabled_warning").GetString());
        Assert.False(root.GetProperty("http_response_cleanup_degraded").GetBoolean());
        Assert.Equal(0, root.GetProperty("http_response_abort_cleanup_failure_count").GetInt64());
        Assert.Equal(0, root.GetProperty("http_response_close_cleanup_failure_count").GetInt64());
    }

    [Fact]
    public async Task HttpTransport_OriginPolicy_AllowsNativeAndSameOriginRequests_Issue4549()
    {
        const string token = "issue-4549-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);
        using var client = CreateHttpClient();

        using var nativeResponse = await harness.InitializeAsync("origin-policy-client");
        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);

        using var sameOriginRequest = CreateAuthorizedRequest(2);
        harness.AddSessionHeader(sameOriginRequest);
        sameOriginRequest.Headers.TryAddWithoutValidation(
            "Origin",
            new Uri(harness.Endpoint).GetLeftPart(UriPartial.Authority));
        using var sameOriginResponse = await client.SendAsync(sameOriginRequest);

        Assert.Equal(HttpStatusCode.OK, sameOriginResponse.StatusCode);

        HttpRequestMessage CreateAuthorizedRequest(int id)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                    $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}""")),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }
    }

    [Fact]
    public async Task HttpTransport_OriginPolicy_RejectsUntrustedAmbiguousAndNullOrigins_Issue4549()
    {
        const string token = "issue-4549-token";
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            bearerToken: token,
            requestLogger: records.Enqueue);
        using var client = CreateHttpClient();

        var cases = new (string Name, Action<HttpRequestMessage> SetOrigin)[]
        {
            ("untrusted", request => request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example")),
            ("null", request => request.Headers.TryAddWithoutValidation("Origin", "null")),
            ("duplicate", request => request.Headers.TryAddWithoutValidation(
                "Origin",
                new[] { "https://attacker.example", "https://other.example" })),
            ("comma-folded", request => request.Headers.TryAddWithoutValidation(
                "Origin",
                "https://attacker.example, https://other.example")),
        };

        foreach (var (name, setOrigin) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"ping"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            setOrigin(request);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, name);
            Assert.Equal("Origin is not allowed for this MCP HTTP listener.\n", body);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.DoesNotContain("attacker.example", body, StringComparison.Ordinal);
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "healthz", "events" })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(harness.Endpoint), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var logged = await WaitForRequestLogRecordsAsync(records, cases.Length + 2);
        Assert.All(logged, record =>
        {
            Assert.Equal("not-checked", record.AuthOutcome);
            Assert.Equal(HttpMcpTransport.OriginRejectedDiagnostic, record.Diagnostic);
        });
    }

    [Fact]
    public async Task HttpTransport_BrowserSimpleRequestAndPreflight_AreRejectedBeforeAuth_Issue4549()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token");
        using var client = CreateHttpClient();

        using (var simpleRequest = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"ping"}""",
                Encoding.UTF8,
                "text/plain"),
        })
        {
            simpleRequest.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
            using var simpleResponse = await client.SendAsync(simpleRequest);
            Assert.Equal(HttpStatusCode.Forbidden, simpleResponse.StatusCode);
        }

        using var preflight = new HttpRequestMessage(HttpMethod.Options, harness.Endpoint);
        preflight.Headers.TryAddWithoutValidation(
            "Origin",
            new Uri(harness.Endpoint).GetLeftPart(UriPartial.Authority));
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        preflight.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization, content-type");
        using var preflightResponse = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.Forbidden, preflightResponse.StatusCode);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Headers"));
    }

    [Theory]
    [InlineData("", "POST", "MCP HTTP transport only accepts POST.\n")]
    [InlineData("healthz", "GET", "MCP health endpoint only accepts GET.\n")]
    [InlineData("events", "GET", "MCP HTTP event stream only accepts GET.\n")]
    public async Task HttpTransport_AuthenticatedNativeOptions_UsesRouteSpecificMethodHandling_Issue4549(
        string path,
        string allowedMethod,
        string expectedBody)
    {
        const string token = "issue-4549-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);
        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            new Uri(new Uri(harness.Endpoint), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Collection(
            response.Content.Headers.Allow,
            value => Assert.Equal(allowedMethod, value));
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HttpTransport_AuthenticatedOptions_WithOnlyOnePreflightHeader_UsesNativeHandling_Issue4549(
        bool includeOrigin,
        bool includeAccessControlRequestMethod)
    {
        const string token = "issue-4549-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);
        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, harness.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (includeOrigin)
        {
            request.Headers.TryAddWithoutValidation(
                "Origin",
                new Uri(harness.Endpoint).GetLeftPart(UriPartial.Authority));
        }
        if (includeAccessControlRequestMethod)
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Collection(
            response.Content.Headers.Allow,
            value => Assert.Equal("POST", value));
        Assert.Equal(
            "MCP HTTP transport only accepts POST.\n",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/problem+json")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; charset=utf-16")]
    public async Task HttpTransport_PostRejectsUnsupportedJsonMediaTypesAndCharsets_Issue4549(string? contentType)
    {
        const string token = "issue-4549-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);
        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                """{"jsonrpc":"2.0","id":1,"method":"ping"}""")),
        };
        if (contentType is not null)
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_PostAcceptsUtf8JsonAndRejectsInvalidUtf8_Issue4549()
    {
        const string token = "issue-4549-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);
        using var client = CreateHttpClient();

        using var initializeResponse = await harness.InitializeAsync("utf8-client");
        Assert.Equal(HttpStatusCode.OK, initializeResponse.StatusCode);

        using (var invalidRequest = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new ByteArrayContent([0xC3, 0x28]),
        })
        {
            invalidRequest.Content.Headers.TryAddWithoutValidation(
                "Content-Type",
                "application/json; charset=\"UTF-8\"");
            invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            harness.AddSessionHeader(invalidRequest);
            using var invalidResponse = await client.SendAsync(invalidRequest);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            Assert.Equal(
                "MCP HTTP request body must be valid UTF-8.\n",
                await invalidResponse.Content.ReadAsStringAsync());
        }

        using var validRequest = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                """{"jsonrpc":"2.0","id":2,"method":"ping"}""")),
        };
        validRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=\"UTF-8\"");
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        harness.AddSessionHeader(validRequest);
        using var validResponse = await client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReportsEventDropsThenResponseCleanupFailures_Issues3452And3966()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetKeepAlive(
            TimeSpan.FromMilliseconds(1),
            () => new string('x', HttpMcpTransport.MaxSseEventFrameBytes + 1));

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var client = CreateHttpClient();
        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.EventStreamDropCount > 0, "event stream drop counter");

        using (var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal(1, root.GetProperty("http_event_stream_drop_count").GetInt64());
            Assert.Equal(1, root.GetProperty("http_event_stream_write_failure_drop_count").GetInt64());
            Assert.Equal("write_failure:exception_message_redacted:InvalidDataException", root.GetProperty("http_event_stream_last_drop_reason").GetString());
        }

        harness.RecordResponseCleanupFailure("abort", "test abort cleanup", new IOException("abort cleanup failed"));
        harness.RecordResponseCleanupFailure("close", "test close cleanup", new InvalidOperationException("close cleanup failed"));
        using var degradedResponse = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));
        Assert.Equal(HttpStatusCode.OK, degradedResponse.StatusCode);
        var degradedBody = await degradedResponse.Content.ReadAsStringAsync();
        using var degradedDocument = JsonDocument.Parse(degradedBody);
        var degradedRoot = degradedDocument.RootElement;
        Assert.Equal("degraded", degradedRoot.GetProperty("status").GetString());
        Assert.True(degradedRoot.GetProperty("http_response_cleanup_degraded").GetBoolean());
        Assert.Equal(1, degradedRoot.GetProperty("http_response_abort_cleanup_failure_count").GetInt64());
        Assert.Equal(1, degradedRoot.GetProperty("http_response_close_cleanup_failure_count").GetInt64());
        Assert.Equal("test abort cleanup:io_error:IOException", degradedRoot.GetProperty("http_response_abort_cleanup_last_error").GetString());
        Assert.Equal("test close cleanup:invalid_operation:InvalidOperationException", degradedRoot.GetProperty("http_response_close_cleanup_last_error").GetString());
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReplacesInvalidAndOversizedProviderJson_Issue3815()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetHealthJsonProvider(() => "not-json");

        using var client = CreateHttpClient();
        using (var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("degraded", root.GetProperty("status").GetString());
            Assert.Equal("health_provider_invalid", root.GetProperty("error").GetString());
        }

        var oversizedJson = $$"""{"status":"{{new string('x', HttpMcpTransport.MaxHealthJsonBytes)}}","db_open":true}""";
        harness.SetHealthJsonProvider(() => oversizedJson);
        using var oversizedResponse = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));
        Assert.Equal(HttpStatusCode.OK, oversizedResponse.StatusCode);
        var oversizedBody = await oversizedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(new string('x', 128), oversizedBody, StringComparison.Ordinal);
        using var oversizedDocument = JsonDocument.Parse(oversizedBody);
        Assert.Equal("health_provider_invalid", oversizedDocument.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_RecordsMethodStatusDurationAndAuthOutcome()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token", requestLogger: records.Enqueue);

        using var client = CreateHttpClient();
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
            Content = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{}}""", Encoding.UTF8, "application/json"),
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
        var unauthorizedPost = Assert.Single(snapshot, record =>
            record.AuthOutcome == "unauthorized" &&
            record.StatusCode == (int)HttpStatusCode.Unauthorized &&
            record.Method == "POST");
        Assert.Equal("/", unauthorizedPost.Path);
        Assert.Null(unauthorizedPost.RequestId);
        Assert.True(unauthorizedPost.DurationMs >= 0);
        Assert.False(string.IsNullOrWhiteSpace(unauthorizedPost.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(unauthorizedPost.RemotePeer));

        var unauthorizedGet = Assert.Single(snapshot, record =>
            record.AuthOutcome == "unauthorized" &&
            record.Method == "GET");
        Assert.Equal((int)HttpStatusCode.Unauthorized, unauthorizedGet.StatusCode);

        var okPost = Assert.Single(snapshot, record =>
            record.AuthOutcome == "ok" &&
            record.RequestId == "7");
        Assert.Equal("POST", okPost.Method);
        Assert.Equal("/", okPost.Path);
        Assert.Equal((int)HttpStatusCode.OK, okPost.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_DropsWhenQueueSaturated_Issue3747()
    {
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            requestLogger: record =>
            {
                records.Enqueue(record);
                writerEntered.Set();
                releaseWriter.Wait();
            },
            requestLogQueueCapacity: 1);

        var healthUri = new Uri(new Uri(harness.Endpoint), "healthz");
        using var client = CreateHttpClient();
        try
        {
            using var first = await client.GetAsync(healthUri);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)), "request log writer should enter the blocking callback");

            using var second = await client.GetAsync(healthUri);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var third = await client.GetAsync(healthUri);
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);

            await WaitForRequestLogDropAsync(harness);
            Assert.True(harness.RequestLogQueueFullDropCount > 0);
        }
        finally
        {
            releaseWriter.Set();
        }

        using var degraded = await client.GetAsync(healthUri);
        Assert.Equal(HttpStatusCode.OK, degraded.StatusCode);
        using var document = JsonDocument.Parse(await degraded.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("degraded", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("http_request_log_dropped_count").GetInt64() > 0);
        Assert.True(root.GetProperty("http_request_log_queue_full_drop_count").GetInt64() > 0);
        Assert.Equal(0, root.GetProperty("http_request_log_callback_failure_count").GetInt64());
        Assert.True(root.GetProperty("http_request_log_degraded").GetBoolean());
        Assert.Contains("queue_full", root.GetProperty("http_request_log_last_drop_reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_RequestLogFieldLimiter_CapsMetadataFields()
    {
        var longValue = new string('x', HttpMcpTransport.MaxRequestLogFieldCharacters + 100);

        var limited = HttpMcpTransport.LimitRequestLogField(longValue);

        Assert.NotNull(limited);
        Assert.Equal(HttpMcpTransport.MaxRequestLogFieldCharacters, limited.Length);
        Assert.EndsWith(HttpMcpTransport.RequestLogTruncationMarker, limited, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_BoundsPathAndJsonRpcIdMetadata_Issue3014()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue);

        using var client = CreateHttpClient();
        var longPath = string.Join('/', Enumerable.Repeat("segment", 50));
        using var pathResponse = await client.GetAsync(new Uri(new Uri(harness.Endpoint), longPath));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, pathResponse.StatusCode);
        var pathRecord = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal(HttpMcpTransport.MaxRequestLogFieldCharacters, pathRecord.Path.Length);
        Assert.EndsWith(HttpMcpTransport.RequestLogTruncationMarker, pathRecord.Path, StringComparison.Ordinal);

        var oversizedId = new string('i', HttpMcpTransport.MaxRequestLogFieldCharacters + 100);
        var body = """{"jsonrpc":"2.0","id":"""
            + JsonSerializer.Serialize(oversizedId)
            + ""","method":"initialize","params":{}}""";

        using var oversizedIdResponse = await harness.PostJsonAsync(body);

        Assert.Equal(HttpStatusCode.OK, oversizedIdResponse.StatusCode);
        var oversizedIdRecords = await WaitForRequestLogRecordsAsync(records, 2);
        var oversizedIdRecord = Assert.Single(oversizedIdRecords, record => record.Method == "POST");
        Assert.NotNull(oversizedIdRecord.RequestId);
        Assert.Equal(HttpMcpTransport.MaxRequestLogFieldCharacters, oversizedIdRecord.RequestId.Length);
        Assert.EndsWith(HttpMcpTransport.RequestLogTruncationMarker, oversizedIdRecord.RequestId, StringComparison.Ordinal);

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var deepIdResponse = await harness.PostJsonAsync(BuildNestedJsonRpcRequest(McpServer.MaxJsonDepth + 1));

        var deepIdBody = await deepIdResponse.Content.ReadAsStringAsync();
        Assert.True(
            deepIdResponse.StatusCode == HttpStatusCode.OK,
            $"Expected deep JSON-RPC payload to reach normal handling; status={deepIdResponse.StatusCode}; body={deepIdBody}");
        var deepIdRecords = await WaitForRequestLogRecordsAsync(records, 4);
        var deepIdRecord = Assert.Single(deepIdRecords, record => record.Method == "POST" && record.RequestId is null);
        Assert.Null(deepIdRecord.RequestId);
    }

    [Fact]
    public async Task HttpTransport_WarmServer_HandlesSequentialConcurrentAndEmptyBodyRequests()
    {
        // Issue #1558: AI clients should be able to keep a single MCP server warm across
        // multiple JSON-RPC requests instead of paying subprocess-spawn cost per call.
        // Issue #1558: AI クライアントが MCP サーバーを温めた状態で複数 JSON-RPC を扱えること。
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var first = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var listBody = await second.Content.ReadAsStringAsync();
        using var listDoc = JsonDocument.Parse(listBody);
        Assert.True(listDoc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);

        var concurrentFirst = harness.PostJsonAsync("""{"jsonrpc":"2.0","id":21,"method":"ping"}""");
        var concurrentSecond = harness.PostJsonAsync("""{"jsonrpc":"2.0","id":22,"method":"ping"}""");
        var responses = await Task.WhenAll(concurrentFirst, concurrentSecond);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ids = new List<int>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            ids.Add(doc.RootElement.GetProperty("id").GetInt32());
            response.Dispose();
        }

        Assert.Contains(21, ids);
        Assert.Contains(22, ids);

        using var empty = await harness.PostJsonAsync(string.Empty);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var followBody = await follow.Content.ReadAsStringAsync();
        using var followDoc = JsonDocument.Parse(followBody);
        Assert.Equal(7, followDoc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_NonOutOfBandPayloads_AreQueuedForNormalHandling_Issue3711()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var outOfBandHandlerCalls = 0;
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            allowUnauthenticatedLoopback: true);
        transport.OutOfBandFrameHandler = (_, _) =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return Task.FromResult<string?>(null);
        };

        using var client = CreateHttpClient(TimeSpan.FromSeconds(5));
        var initializePost = client.PostAsync(
            listen.Prefix,
            new StringContent(
                """{"jsonrpc":"2.0","id":"transport-init","method":"initialize","params":{}}""",
                Encoding.UTF8,
                "application/json"));
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "initialize to enter the normal HTTP MCP queue");
        _ = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await transport.WriteFrameAsync(
            """{"jsonrpc":"2.0","id":"transport-init","result":{"protocolVersion":"2025-03-26"}}""",
            CancellationToken.None);
        using var initializeResponse = await initializePost.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(initializeResponse.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var sessionValues));
        var sessionId = Assert.Single(sessionValues);

        async Task AssertQueuedNormallyAsync(string body, string reply, string description)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, listen.Prefix)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
            var post = client.SendAsync(request);
            await WaitUntilAsync(() => transport.QueuedRequestCount == 1, description);
            Assert.Equal(0, Volatile.Read(ref outOfBandHandlerCalls));
            var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(body, frame);
            await transport.WriteFrameAsync(reply, CancellationToken.None);
            using var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await AssertQueuedNormallyAsync(
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":""",
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""",
            "malformed cancellation notification to stay in the normal HTTP MCP queue");
        await AssertQueuedNormallyAsync(
            BuildNestedCancellationNotification(McpServer.MaxJsonDepth + 1),
            """{"jsonrpc":"2.0","id":null,"result":{}}""",
            "deep cancellation notification to stay in the normal HTTP MCP queue");
        await AssertQueuedNormallyAsync(
            BuildNestedJsonRpcResponse(McpServer.MaxJsonDepth + 1),
            """{"jsonrpc":"2.0","id":null,"result":{}}""",
            "deep JSON-RPC response to stay in the normal HTTP MCP queue");
    }

    [Fact]
    public async Task HttpTransport_RequestBodyOverLimit_Returns413AndDoesNotKillServer()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue, maxRequestBodyBytes: 64);

        using var oversized = await harness.PostJsonAsync(new string('x', 65));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var rejectedBody = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("64 byte limit", rejectedBody, StringComparison.Ordinal);
        var rejectedRecord = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("request_body_limit_exceeded", rejectedRecord.Diagnostic);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":7,"method":"initialize"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var body = await follow.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpTransport_UnknownLengthRequestBody_LogsBoundedDiagnostic_Issue3755()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue);

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new UnknownLengthStringContent("""{"jsonrpc":"2.0","id":3755,"method":"initialize","params":{}}"""),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("request_body_length_unknown", record.Diagnostic);
        Assert.Equal("3755", record.RequestId);
    }

    [Fact]
    public async Task HttpTransport_ResponseBodyOverLimit_Returns500AndLogsDiagnostic_Issue3755()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue, maxResponseBodyBytes: 1024);

        using var oversized = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"response-limit-client","version":"1.0"},"capabilities":{"roots":{},"sampling":{}},"roots":[{"uri":"file:///response-limit"}]}}""");

        Assert.Equal(HttpStatusCode.InternalServerError, oversized.StatusCode);
        Assert.True(oversized.Headers.Contains(HttpMcpTransport.SessionIdHeaderName));
        var rejectedBody = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("response body exceeds", rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", rejectedBody, StringComparison.Ordinal);
        var rejectedRecord = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("response_body_limit_exceeded", rejectedRecord.Diagnostic);
        Assert.Equal("response-limit-client/1.0", harness.CurrentCaller);
        Assert.Equal(["file:///response-limit"], harness.ClientRoots);

        // McpServer committed the successful initialize before the transport replaced its
        // oversized wire response with HTTP 500. The transport must remain fail-closed: a
        // headerless second client cannot inherit or replace that committed state.
        // McpServer は oversized wire response が HTTP 500 に置換される前に initialize を
        // commit 済み。transport は fail-closed を維持し、header 無しの第2 client に
        // commit 済み state を継承・置換させない。
        using var otherClient = CreateHttpClient();
        using var secondInitialize = await otherClient.PostAsync(
            harness.Endpoint,
            new StringContent(
                """{"jsonrpc":"2.0","id":"replacement","method":"initialize","params":{"clientInfo":{"name":"replacement-client"},"roots":[{"uri":"file:///replacement"}]}}""",
                Encoding.UTF8,
                "application/json"));
        AssertSessionRejected(
            secondInitialize,
            HttpStatusCode.BadRequest,
            HttpMcpTransport.SessionRequiredRejection);
        Assert.Equal("response-limit-client/1.0", harness.CurrentCaller);
        Assert.Equal(["file:///response-limit"], harness.ClientRoots);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var body = await follow.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500_000)]
    public async Task HttpTransport_ResourcesListHonorsConfiguredTransportBudget_Issue4542(int requestedMaxBytes)
    {
        const int responseBudget = 100_000;
        InsertLongResourceFilesForResponseBudget("http-budget");
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            maxResponseBodyBytes: responseBudget);
        using (var initialize = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":"init","method":"initialize","params":{}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        }

        var requestBody = requestedMaxBytes == 0
            ? """{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{}}"""
            : """{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{"maxBytes":"""
                + requestedMaxBytes.ToString(CultureInfo.InvariantCulture)
                + "}}";
        using var response = await harness.PostJsonAsync(requestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.InRange(body.Length, 90_000, responseBudget);
        using var document = JsonDocument.Parse(body);
        var result = document.RootElement.GetProperty("result");
        var controls = result.GetProperty("_meta").GetProperty("response_controls");
        Assert.Equal(
            requestedMaxBytes == 0 ? McpServer.DefaultResourceListMaxBytes : requestedMaxBytes,
            controls.GetProperty("requested_max_bytes").GetInt32());
        Assert.Equal(responseBudget, controls.GetProperty("effective_max_bytes").GetInt32());
        Assert.Equal("byte_budget", controls.GetProperty("continuation_reason").GetString());
        Assert.True(result.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task HttpTransport_ResourcesListBatchHonorsConfiguredTransportBudget_Issue4542()
    {
        const int responseBudget = 100_000;
        const int requestedMaxBytes = 500_000;
        InsertLongResourceFilesForResponseBudget("http-batch-budget");
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            maxResponseBodyBytes: responseBudget);
        using (var initialize = await harness.PostJsonAsync(
            """{"jsonrpc":"2.0","id":"init","method":"initialize","params":{}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        }

        using var response = await harness.PostJsonAsync(
            """
            [
              {"jsonrpc":"2.0","id":1,"method":"resources/list","params":{"maxBytes":500000}},
              {"jsonrpc":"2.0","id":2,"method":"resources/list","params":{"maxBytes":500000}}
            ]
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.InRange(body.Length, 90_000, responseBudget);
        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetInt32());
        Assert.Equal(2, items.Count);
        foreach (var id in new[] { 1, 2 })
        {
            var result = items[id].GetProperty("result");
            Assert.NotEmpty(result.GetProperty("resources").EnumerateArray());
            var controls = result.GetProperty("_meta").GetProperty("response_controls");
            Assert.Equal(requestedMaxBytes, controls.GetProperty("requested_max_bytes").GetInt32());
            Assert.InRange(
                controls.GetProperty("effective_max_bytes").GetInt32(),
                McpServer.MinResourceListMaxBytes,
                responseBudget - 1);
            Assert.Equal("byte_budget", controls.GetProperty("continuation_reason").GetString());
            Assert.Equal(
                McpServer.MaxResourceListCursorChars,
                result.GetProperty("nextCursor").GetString()!.Length);
        }
    }

    [Fact]
    public async Task HttpTransport_DefaultLimitOptions_UseBoundedDefaults()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            allowUnauthenticatedLoopback: true);

        Assert.Equal(HttpMcpTransport.DefaultMaxRequestBodyBytes, transport.MaxRequestBodyBytes);
        Assert.Equal(HttpMcpTransport.DefaultMaxResponseBodyBytes, transport.MaxResponseBodyBytes);
        Assert.Equal(HttpMcpTransport.DefaultMaxQueuedRequests, transport.MaxQueuedRequests);
        Assert.Equal(HttpMcpTransport.DefaultMaxConcurrentHandlers, transport.MaxConcurrentHandlers);
        Assert.Equal(HttpMcpTransport.DefaultMaxEventStreams, transport.MaxEventStreams);
        Assert.InRange(transport.MaxRequestBodyBytes, 1, HttpMcpTransport.MaxConfiguredRequestBodyBytes);
        Assert.InRange(transport.MaxResponseBodyBytes, 1, HttpMcpTransport.MaxConfiguredResponseBodyBytes);
        Assert.InRange(transport.MaxQueuedRequests, 1, HttpMcpTransport.MaxConfiguredQueuedRequests);
        Assert.InRange(transport.MaxConcurrentHandlers, 1, HttpMcpTransport.MaxConfiguredConcurrentHandlers);
        Assert.InRange(transport.MaxEventStreams, 1, HttpMcpTransport.MaxConfiguredEventStreams);
        Assert.True(transport.IsLoopbackBind);
        Assert.True(transport.AuthDisabled);
        Assert.Equal(HttpMcpTransport.LoopbackAuthDisabledWarning, transport.AuthDisabledWarning);
    }

    [Fact]
    public void HttpTransport_NonLoopbackWithoutBearerToken_ThrowsBeforeBinding_Issue3754()
    {
        var ex = Assert.Throws<ArgumentException>(() => new HttpMcpTransport(
            "http://127.0.0.1:1/",
            "0.0.0.0",
            1,
            bearerToken: null,
            allowUnauthenticatedLoopback: true));

        Assert.Contains("requires bearer authentication", ex.Message, StringComparison.Ordinal);
        Assert.Equal("bearerToken", ex.ParamName);
    }

    [Fact]
    public async Task HttpTransport_ValidEnvironmentLimitOptions_AreApplied()
    {
        using var env = EnvironmentVariableScope.Capture(
            HttpMcpTransport.MaxRequestBodyBytesEnvVar,
            HttpMcpTransport.MaxResponseBodyBytesEnvVar,
            HttpMcpTransport.MaxQueueDepthEnvVar,
            HttpMcpTransport.MaxConcurrentHandlersEnvVar,
            HttpMcpTransport.MaxEventStreamsEnvVar);
        env.Set(HttpMcpTransport.MaxRequestBodyBytesEnvVar, (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        env.Set(HttpMcpTransport.MaxResponseBodyBytesEnvVar, (3 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        env.Set(HttpMcpTransport.MaxQueueDepthEnvVar, "128");
        env.Set(HttpMcpTransport.MaxConcurrentHandlersEnvVar, "32");
        env.Set(HttpMcpTransport.MaxEventStreamsEnvVar, "8");

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            allowUnauthenticatedLoopback: true);

        Assert.Equal(2 * 1024 * 1024, transport.MaxRequestBodyBytes);
        Assert.Equal(3 * 1024 * 1024, transport.MaxResponseBodyBytes);
        Assert.Equal(128, transport.MaxQueuedRequests);
        Assert.Equal(32, transport.MaxConcurrentHandlers);
        Assert.Equal(8, transport.MaxEventStreams);
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
    public void HttpTransport_OversizedConcurrentHandlersEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxConcurrentHandlersEnvVar);
        env.Set(
            HttpMcpTransport.MaxConcurrentHandlersEnvVar,
            (HttpMcpTransport.MaxConfiguredConcurrentHandlers + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxConcurrentHandlersEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredConcurrentHandlers.ToString(CultureInfo.InvariantCulture)}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_OversizedEventStreamsEnvironment_ThrowsWithRange()
    {
        using var env = EnvironmentVariableScope.Capture(HttpMcpTransport.MaxEventStreamsEnvVar);
        env.Set(
            HttpMcpTransport.MaxEventStreamsEnvVar,
            (HttpMcpTransport.MaxConfiguredEventStreams + 1).ToString(CultureInfo.InvariantCulture));

        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<FormatException>(() => new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null));

        Assert.Contains(HttpMcpTransport.MaxEventStreamsEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredEventStreams.ToString(CultureInfo.InvariantCulture)}",
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
            maxQueuedRequests: 1,
            allowUnauthenticatedLoopback: true);

        using var client = CreateHttpClient(TimeSpan.FromSeconds(5));
        var sessionId = await EstablishTransportSessionAsync(transport, client, listen.Prefix);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, listen.Prefix)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        firstRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        var first = client.SendAsync(firstRequest);
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "the first request to fill the HTTP MCP queue");

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, listen.Prefix)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        secondRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var second = await client.SendAsync(secondRequest);

        AssertTooManyRequests(second, HttpMcpTransport.RequestQueueLimitRejection);
        Assert.Equal(1, transport.RequestQueueLimitRejectionCount);
        Assert.Equal(1, transport.QueuedRequestCount);

        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame);
        Assert.Contains("\"id\":1", frame, StringComparison.Ordinal);
        Assert.Equal(0, transport.QueuedRequestCount);
        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_ConcurrentHandlerLimit_Returns429()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            maxConcurrentHandlers: 1,
            allowUnauthenticatedLoopback: true);

        using var client = CreateHttpClient(TimeSpan.FromSeconds(5));
        var sessionId = await EstablishTransportSessionAsync(transport, client, listen.Prefix);
        using var eventsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(listen.Prefix), "events"));
        eventsRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var events = await client.SendAsync(eventsRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => transport.HasEventStreams, "the first event stream to occupy the only handler slot");

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, listen.Prefix)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        secondRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var second = await client.SendAsync(secondRequest);

        AssertTooManyRequests(second, HttpMcpTransport.ConcurrentHandlerLimitRejection);
        Assert.Equal(1, transport.ConcurrentHandlerLimitRejectionCount);
    }

    [Fact]
    public async Task HttpTransport_EventsStreamLimit_Returns429()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null,
            maxEventStreams: 1,
            allowUnauthenticatedLoopback: true);

        using var client = CreateHttpClient(TimeSpan.FromSeconds(5));
        var sessionId = await EstablishTransportSessionAsync(transport, client, listen.Prefix);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(listen.Prefix), "events"));
        firstRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var first = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await WaitUntilAsync(() => transport.EventStreamCount == 1, "the first event stream to fill the stream limit");

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(listen.Prefix), "events"));
        secondRequest.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        using var second = await client.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead);

        AssertTooManyRequests(second, HttpMcpTransport.EventStreamLimitRejection);
        Assert.Equal(1, transport.EventStreamLimitRejectionCount);
    }

    [Fact]
    public async Task HttpTransport_EventsStream_EmitsOptInKeepAliveNotifications()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "1");
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var client = CreateHttpClient();
        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        await using var eventStream = await events.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(eventStream, Encoding.UTF8, leaveOpen: true);

        var frame = await ReadUntilAsync(reader, "notifications/keep_alive").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("\"method\":\"notifications/keep_alive\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"uptime_s\":", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_EventsStream_AllowsPostsAndRemovesDisconnectedStreams_Issue3815()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetKeepAlive(TimeSpan.FromSeconds(1), () => new string('x', HttpMcpTransport.MaxSseEventFrameBytes));
        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        using var client = CreateHttpClient();
        using var oversizedEvents = await harness.GetEventsAsync(client);

        Assert.Equal(HttpStatusCode.OK, oversizedEvents.StatusCode);
        Assert.Equal("text/event-stream", oversizedEvents.Content.Headers.ContentType!.MediaType);
        Assert.True(oversizedEvents.Headers.TryGetValues("X-Accel-Buffering", out var bufferingValues));
        Assert.Contains("no", bufferingValues);
        Assert.True(oversizedEvents.Headers.Contains("X-Cdidx-Mcp-Event-Stream-Id"));

        using var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":11,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(11, doc.RootElement.GetProperty("id").GetInt32());

        await WaitUntilAsync(() => harness.EventStreamCount == 0, "oversized keep-alive frame to close the event stream");

        harness.SetKeepAlive(
            TimeSpan.FromMilliseconds(10),
            () => """{"jsonrpc":"2.0","method":"notifications/test"}""");

        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.HasEventStreams, "the event stream to be registered");

        events.Dispose();

        await WaitUntilAsync(() => !harness.HasEventStreams, "the disconnected event stream to be removed");
    }

    [Fact]
    public async Task HttpTransport_OutOfBandSseWriteTimeout_LogsStableDiagnostic_Issue3990()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            requestLogger: records.Enqueue,
            eventStreamWriteTimeout: TimeSpan.FromMilliseconds(10));
        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        harness.BeforeEventStreamWriteForTests = token => Task.Delay(Timeout.InfiniteTimeSpan, token);

        using var client = CreateHttpClient();
        using var events = await harness.GetEventsAsync(client);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.HasEventStreams, "the event stream to be registered");

        using var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":3990,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => !harness.HasEventStreams, "the timed-out event stream to be removed");
        var snapshot = await WaitForRequestLogRecordsAsync(records, 3);
        var eventRecord = Assert.Single(snapshot.Where(record => record.Method == "GET" && record.Path == "/events"));
        Assert.Equal("timeout:sse_write", eventRecord.Diagnostic);
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

            using (var initialize = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":"http-progress-init","method":"initialize","params":{}}"""))
                Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

            using var client = CreateHttpClient();
            using var events = await harness.GetEventsAsync(client);
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
    public async Task HttpTransport_IndexWithProgressToken_BroadcastsProgressToMultipleEventStreams_Issue3522()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_http_multistream_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "two.cs"), "public class Two { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await using var harness = await McpHttpHarness.StartAsync(dbPath);

            using (var initialize = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":"http-progress-multi-init","method":"initialize","params":{}}"""))
                Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

            using var client = CreateHttpClient();
            using var firstEvents = await harness.GetEventsAsync(client);
            using var secondEvents = await harness.GetEventsAsync(client);
            Assert.Equal(HttpStatusCode.OK, firstEvents.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondEvents.StatusCode);
            Assert.NotEqual(
                firstEvents.Headers.GetValues("X-Cdidx-Mcp-Event-Stream-Id").Single(),
                secondEvents.Headers.GetValues("X-Cdidx-Mcp-Event-Stream-Id").Single());
            await WaitUntilAsync(() => harness.EventStreamCount == 2, "two event streams to be registered");

            await using var firstStream = await firstEvents.Content.ReadAsStreamAsync();
            await using var secondStream = await secondEvents.Content.ReadAsStreamAsync();
            using var firstReader = new StreamReader(firstStream, Encoding.UTF8, leaveOpen: true);
            using var secondReader = new StreamReader(secondStream, Encoding.UTF8, leaveOpen: true);
            var firstProgressTask = ReadUntilAsync(firstReader, "notifications/progress");
            var secondProgressTask = ReadUntilAsync(secondReader, "notifications/progress");

            var body = "{\"jsonrpc\":\"2.0\",\"id\":3522,\"method\":\"tools/call\",\"params\":{\"name\":\"index\",\"arguments\":{\"path\":"
                + JsonSerializer.Serialize(projectRoot)
                + "},\"_meta\":{\"progressToken\":\"http-progress-multi\"}}}";
            using var response = await harness.PostJsonAsync(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var firstProgressFrame = await firstProgressTask.WaitAsync(TimeSpan.FromSeconds(5));
            var secondProgressFrame = await secondProgressTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("\"progressToken\":\"http-progress-multi\"", firstProgressFrame, StringComparison.Ordinal);
            Assert.Contains("\"progressToken\":\"http-progress-multi\"", secondProgressFrame, StringComparison.Ordinal);
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

        using (var initialize = await harness.InitializeAsync())
            Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        using var client = CreateHttpClient();
        using var unauthorized = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(harness.Endpoint), "events"));
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        harness.AddSessionHeader(authorizedRequest);
        using var authorized = await client.SendAsync(authorizedRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("text/event-stream", authorized.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task HttpTransport_GenericAuthTokenFallback_AcceptsBearerHeaderWithoutBodyToken()
    {
        using var env = EnvironmentVariableScope.Capture(
            ProgramRunner.McpHttpTokenEnvVar,
            McpAuthenticatorFactory.AuthTokenEnvVar);
        env.Set(ProgramRunner.McpHttpTokenEnvVar, null);
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, "generic-token");

        var bearerToken = ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment();
        var authenticator = ProgramRunner.CreateMcpAuthenticatorForTransport("http");
        await using var harness = await McpHttpHarness.StartAsync(
            _dbPath,
            bearerToken: bearerToken,
            authenticator: authenticator);

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "generic-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Unauthorized", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_AcceptsCaseInsensitiveSchemes()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = CreateHttpClient();
        string? sessionId = null;
        foreach (var scheme in new[] { "Bearer", "bearer" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
            {
                Content = new StringContent(
                    sessionId is null
                        ? """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""
                        : """{"jsonrpc":"2.0","id":2,"method":"ping"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
            if (sessionId is not null)
                request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (sessionId is null)
            {
                Assert.True(response.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var sessionValues));
                sessionId = Assert.Single(sessionValues);
            }
        }
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsAuthorizationFailureCasesWithoutLeakingValues_Issue4299()
    {
        const string token = "s3cret-token";
        const string wrongToken = "wrong-token";
        var oversizedToken = new string('x', McpAuthenticationLimits.MaxTokenCharacters + 1);
        Assert.Equal(token.Length, "wrongTokenAa".Length);
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token, requestLogger: records.Enqueue);
        using var client = CreateHttpClient();

        var cases = new (string Name, Action<HttpRequestMessage> Configure)[]
        {
            ("missing", static _ => { }),
            ("wrong scheme", request => request.Headers.TryAddWithoutValidation("Authorization", "Basic " + token)),
            ("wrong token", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongToken)),
            ("same-length wrong token", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrongTokenAa")),
            ("multiple values", request => request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + token, "Bearer " + token })),
            ("comma-separated values", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token + ", Bearer " + token)),
            ("whitespace-padded token", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer  " + token)),
            ("oversized token", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oversizedToken)),
        };

        foreach (var (name, configure) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
            };
            configure(request);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, name);
            Assert.Equal("Missing or invalid bearer token.\n", body);
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(oversizedToken, body, StringComparison.Ordinal);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        }

        var logged = await WaitForRequestLogRecordsAsync(records, cases.Length);
        Assert.All(logged, record => Assert.Equal("unauthorized", record.AuthOutcome));
    }

    [Fact]
    public async Task HttpTransport_BearerToken_AcceptsMaxLengthHeader_Issue3798()
    {
        var token = new string('t', McpAuthenticationLimits.MaxTokenCharacters);
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReportsAuthDenialClassesWithoutChangingWireResponse_Issue3966()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = CreateHttpClient();

        using (var request = CreatePostRequest())
        {
            await AssertUnauthorizedAsync(client, request);
        }

        using (var request = CreatePostRequest())
        {
            request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Bearer {token}", $"Bearer {token}" });
            await AssertUnauthorizedAsync(client, request);
        }

        using (var request = CreatePostRequest())
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Basic abc");
            await AssertUnauthorizedAsync(client, request);
        }

        using (var request = CreatePostRequest())
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer  " + token);
            await AssertUnauthorizedAsync(client, request);
        }

        using (var request = CreatePostRequest())
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + new string('x', McpAuthenticationLimits.MaxTokenCharacters + 1));
            await AssertUnauthorizedAsync(client, request);
        }

        using (var request = CreatePostRequest())
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
            await AssertUnauthorizedAsync(client, request);
        }

        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(harness.Endpoint), "healthz"));
        healthRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var healthResponse = await client.SendAsync(healthRequest);

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        var body = await healthResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(6, root.GetProperty("http_auth_denial_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_missing_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_ambiguous_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_wrong_scheme_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_malformed_token_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_oversized_token_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_auth_denial_wrong_token_count").GetInt64());
        Assert.Equal(HttpMcpTransport.AuthDenialWrongToken, root.GetProperty("http_auth_denial_last_reason").GetString());

        HttpRequestMessage CreatePostRequest()
            => new(HttpMethod.Post, harness.Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
            };

        static async Task AssertUnauthorizedAsync(HttpClient client, HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Missing or invalid bearer token.\n", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_DetailedAuthOutcomeRequiresUnsafeDebug_Issue3469()
    {
        using var env = EnvironmentVariableScope.Capture(McpServer.DebugEnvironmentVariable);
        env.Set(McpServer.DebugEnvironmentVariable, "unsafe");
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token", requestLogger: records.Enqueue);

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("wrong-token", record.AuthOutcome);
    }

    [Fact]
    public void ResolveListenSpec_DefaultListen_ResolvesToLoopback()
    {
        var spec = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        Assert.True(spec.IsLoopback);
        Assert.Equal("127.0.0.1", spec.Host);
        Assert.True(spec.Port > 0);
        Assert.True(spec.PortWasEphemeral);
        Assert.EndsWith("/", spec.Prefix);
    }

    [Fact]
    public void ResolveListenSpec_ExplicitPort_IsNotEphemeral_Issue3754()
    {
        var spec = HttpMcpTransport.ResolveListenSpec("127.0.0.1:38080");

        Assert.True(spec.IsLoopback);
        Assert.Equal(38080, spec.Port);
        Assert.False(spec.PortWasEphemeral);
    }

    [Fact]
    public void FormatBindFailureDiagnostic_EphemeralPortMentionsRace_Issue3754()
    {
        var spec = new HttpMcpTransport.HttpListenSpec(
            "http://127.0.0.1:45678/",
            "127.0.0.1",
            45678,
            IsLoopback: true,
            PortWasEphemeral: true);

        var diagnostic = HttpMcpTransport.FormatBindFailureDiagnostic(spec, new IOException("Address already in use"));

        Assert.Contains("port-0 probe", diagnostic, StringComparison.Ordinal);
        Assert.Contains("another process may have claimed it", diagnostic, StringComparison.Ordinal);
        Assert.Contains("--http-listen", diagnostic, StringComparison.Ordinal);
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
        SqliteConnection.ClearAllPools();
        TestProjectHelper.DeleteDirectory(_dbDir);
        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        // #4264: these loopback MCP tests never use cookies. Disabling them avoids
        // environment-dependent CookieContainer initialization in sandboxed full-suite runs.
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false });
        if (timeout.HasValue)
            client.Timeout = timeout.Value;
        return client;
    }

    private static async Task<HttpMcpTransport.HttpRequestLogRecord[]> WaitForRequestLogRecordsAsync(
        ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord> records,
        int expectedCount)
    {
        await TestDeterminism.WaitUntilAsync(
            () => records.Count >= expectedCount,
            $"{expectedCount} request log records",
            getDiagnostics: () =>
            {
                var snapshot = records.ToArray();
                var observed = string.Join(
                    ", ",
                    snapshot.Select(record => $"{record.Method} {record.Path} {record.StatusCode} auth={record.AuthOutcome} id={record.RequestId ?? "<none>"}"));
                return $"observed={snapshot.Length}: {observed}";
            });

        return records.ToArray();
    }

    private static async Task WaitForRequestLogDropAsync(McpHttpHarness harness)
        => await TestDeterminism.WaitUntilAsync(
            () => harness.RequestLogDroppedCount > 0,
            "request log queue to report at least one dropped record",
            getDiagnostics: () => $"dropped={harness.RequestLogDroppedCount}");

    private static async Task<string> EstablishTransportSessionAsync(
        HttpMcpTransport transport,
        HttpClient client,
        string endpoint)
    {
        var post = client.PostAsync(
            endpoint,
            new StringContent(
                """{"jsonrpc":"2.0","id":"transport-session-init","method":"initialize","params":{}}""",
                Encoding.UTF8,
                "application/json"));
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "transport initialize request to enter the queue");
        _ = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await transport.WriteFrameAsync(
            """{"jsonrpc":"2.0","id":"transport-session-init","result":{"protocolVersion":"2025-03-26"}}""",
            CancellationToken.None);
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var sessionValues));
        return Assert.Single(sessionValues);
    }

    private static void AssertTooManyRequests(HttpResponseMessage response, string rejectionReason)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.True(response.Headers.TryGetValues(HttpMcpTransport.RejectionReasonHeader, out var rejectionValues));
        Assert.Contains(rejectionReason, rejectionValues);
    }

    private static void AssertSessionRejected(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string rejectionReason)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(HttpMcpTransport.RejectionReasonHeader, out var rejectionValues));
        Assert.Equal(rejectionReason, Assert.Single(rejectionValues));
        Assert.False(response.Headers.Contains(HttpMcpTransport.SessionIdHeaderName));
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
        => await TestDeterminism.WaitUntilAsync(condition, description);

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

    private void InsertLongResourceFilesForResponseBudget(string checksumPrefix)
    {
        var writer = new DbWriter(_db.Connection);
        using var transaction = writer.BeginTransaction();
        var longPrefix = "src/" + new string('x', 1_800);
        for (var i = 0; i < 30; i++)
        {
            writer.UpsertFile(new FileRecord
            {
                Path = $"{longPrefix}-{i:D2}.cs",
                Lang = "csharp",
                Size = 1,
                Lines = 1,
                Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
                Checksum = $"{checksumPrefix}-{i}",
            });
        }
        transaction.Commit();
    }

    private sealed class McpHttpHarness : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private readonly McpServer _server;
        private readonly HttpMcpTransport _transport;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loopTask;
        private readonly string? _bearerToken;
        private string? _sessionId;
        private int _initializeRequestId;

        private McpHttpHarness(
            McpServer server,
            HttpMcpTransport transport,
            CancellationTokenSource cts,
            Task loopTask,
            string endpoint,
            string? bearerToken)
        {
            _server = server;
            _transport = transport;
            _cts = cts;
            _loopTask = loopTask;
            _bearerToken = bearerToken;
            Endpoint = endpoint;
        }

        public string Endpoint { get; }

        public bool HasEventStreams => _transport.HasEventStreams;

        public bool OwnedSemaphoreGatesDisposed => _transport.OwnedSemaphoreGatesDisposedForTests;

        public int EventStreamCount => _transport.EventStreamCount;

        public long EventStreamDropCount => _transport.EventStreamDropCount;

        public long RequestLogDroppedCount => _transport.RequestLogDroppedCount;

        public long RequestLogQueueFullDropCount => _transport.RequestLogQueueFullDropCount;

        public string SessionId => Volatile.Read(ref _sessionId)
            ?? throw new InvalidOperationException("The test harness has not completed initialize yet.");

        public string CurrentCaller => _server.CurrentCaller;

        public string[] ClientRoots => _server.ClientRootsForTests;

        public bool ClientSupportsRoots => _server.ClientSupportsRootsForTests;

        public bool ClientSupportsSampling => _server.ClientSupportsSamplingForTests;

        public Func<CancellationToken, Task>? BeforeEventStreamWriteForTests
        {
            get => _transport.BeforeEventStreamWriteForTests;
            set => _transport.BeforeEventStreamWriteForTests = value;
        }

        public void RecordResponseCleanupFailure(string kind, string operation, Exception exception)
            => _transport.RecordResponseCleanupFailure(kind, operation, exception);

        public void SetHealthJsonProvider(Func<string> provider)
            => _transport.HealthJsonProvider = provider;

        public void SetKeepAlive(TimeSpan interval, Func<string> provider)
        {
            _transport.KeepAliveInterval = interval;
            _transport.KeepAliveFrameProvider = provider;
        }

        public static async Task<McpHttpHarness> StartAsync(
            string dbPath,
            string? bearerToken = null,
            IMcpAuthenticator? authenticator = null,
            Action<HttpMcpTransport.HttpRequestLogRecord>? requestLogger = null,
            int? maxRequestBodyBytes = null,
            int? maxResponseBodyBytes = null,
            int? maxQueuedRequests = null,
            int? requestLogQueueCapacity = null,
            TimeSpan? eventStreamWriteTimeout = null)
        {
            var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
            var transport = new HttpMcpTransport(
                listen.Prefix,
                listen.Host,
                listen.Port,
                bearerToken,
                requestLogger: requestLogger,
                maxRequestBodyBytes: maxRequestBodyBytes,
                maxResponseBodyBytes: maxResponseBodyBytes,
                maxQueuedRequests: maxQueuedRequests,
                requestLogQueueCapacity: requestLogQueueCapacity,
                eventStreamWriteTimeout: eventStreamWriteTimeout,
                allowUnauthenticatedLoopback: bearerToken is null);
            var server = authenticator is null
                ? new McpServer(dbPath, ConsoleUi.LoadVersion())
                : new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, authenticator);
            var cts = new CancellationTokenSource();
            var loopTask = Task.Run(() => server.RunAsync(transport, cts.Token));
            // Give the listener a tick to start accepting; HttpListener.Start is synchronous but the
            // background task may not have entered GetContextAsync yet by the time the test posts.
            // listener が GetContextAsync に入る前に POST が来ないよう、ごく短い待機を挟む。
            await Task.Yield();
            await TestDeterminism.WaitUntilAsync(
                () => transport.HealthJsonProvider is not null || loopTask.IsCompleted,
                "MCP HTTP health provider to become ready",
                timeout: TimeSpan.FromSeconds(5),
                getDiagnostics: () => $"health_provider_ready={transport.HealthJsonProvider is not null}, loop_completed={loopTask.IsCompleted}");
            if (loopTask.IsCompleted)
                await loopTask.ConfigureAwait(false);
            if (transport.HealthJsonProvider is null)
                throw new TimeoutException("Timed out waiting for the MCP HTTP health provider to become ready.");
            return new McpHttpHarness(server, transport, cts, loopTask, listen.Prefix, bearerToken);
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string body)
        {
            if (_loopTask.IsCompleted)
                await _loopTask.ConfigureAwait(false);

            using var client = HttpMcpTransportTests.CreateHttpClient(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AddBearerHeader(request);
            AddSessionHeaderIfEstablished(request);
            var response = await client.SendAsync(request);
            CaptureSessionId(response);
            return response;
        }

        public Task<HttpResponseMessage> InitializeAsync(string clientName = "http-test-harness")
        {
            var requestId = Interlocked.Increment(ref _initializeRequestId);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = $"harness-init-{requestId.ToString(CultureInfo.InvariantCulture)}",
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = clientName, version = "1.0" },
                },
            });
            return PostJsonAsync(body);
        }

        public async Task<HttpResponseMessage> GetEventsAsync(HttpClient client)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(Endpoint), "events"));
            AddBearerHeader(request);
            AddSessionHeader(request);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public void AddSessionHeader(HttpRequestMessage request)
            => request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, SessionId);

        private void AddSessionHeaderIfEstablished(HttpRequestMessage request)
        {
            var sessionId = Volatile.Read(ref _sessionId);
            if (sessionId is not null)
                request.Headers.TryAddWithoutValidation(HttpMcpTransport.SessionIdHeaderName, sessionId);
        }

        private void AddBearerHeader(HttpRequestMessage request)
        {
            if (_bearerToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }

        private void CaptureSessionId(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues(HttpMcpTransport.SessionIdHeaderName, out var values))
                return;

            var sessionId = Assert.Single(values);
            var previous = Interlocked.CompareExchange(ref _sessionId, sessionId, null);
            Assert.True(previous is null || string.Equals(previous, sessionId, StringComparison.Ordinal));
        }

        public ValueTask DisposeTransportAsync()
            => _transport.DisposeAsync();

        public Task WaitForServerLoopAsync()
            => _loopTask.WaitAsync(TimeSpan.FromSeconds(5));

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

    private sealed class UnknownLengthStringContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthStringContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class NonCancellableHangingStream(bool hangWrite, bool hangFlush) : Stream
    {
        private readonly TaskCompletionSource _writeBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _flushBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            FlushCalls++;
            if (hangFlush)
                _flushBlocker.Task.GetAwaiter().GetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return hangFlush ? _flushBlocker.Task : Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            if (hangWrite)
                _writeBlocker.Task.GetAwaiter().GetResult();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return hangWrite ? new ValueTask(_writeBlocker.Task) : ValueTask.CompletedTask;
        }
    }
}
