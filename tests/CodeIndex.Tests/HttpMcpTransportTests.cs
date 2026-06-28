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
    public void HttpTransport_Ctor_RejectsCommaBearerToken_Issue3756()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var ex = Assert.Throws<ArgumentException>(() => new HttpMcpTransport(listen.Prefix, listen.Host, listen.Port, bearerToken: "abc,def"));

        Assert.Contains("commas", ex.Message, StringComparison.Ordinal);
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
    public async Task HttpTransport_DisposeAsync_DisposesOwnedSemaphoreGates_Issue3985()
    {
        var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await harness.DisposeAsync();

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
    public async Task HttpTransport_Healthz_ReportsResponseCleanupFailures_Issue3452()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.RecordResponseCleanupFailure("abort", "test abort cleanup", new IOException("abort cleanup failed"));
        harness.RecordResponseCleanupFailure("close", "test close cleanup", new InvalidOperationException("close cleanup failed"));

        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("degraded", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("http_response_cleanup_degraded").GetBoolean());
        Assert.Equal(1, root.GetProperty("http_response_abort_cleanup_failure_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_response_close_cleanup_failure_count").GetInt64());
        Assert.Equal("test abort cleanup:io_error:IOException", root.GetProperty("http_response_abort_cleanup_last_error").GetString());
        Assert.Equal("test close cleanup:invalid_operation:InvalidOperationException", root.GetProperty("http_response_close_cleanup_last_error").GetString());
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReportsEventStreamDropReasons_Issue3966()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetKeepAlive(
            TimeSpan.FromMilliseconds(1),
            () => new string('x', HttpMcpTransport.MaxSseEventFrameBytes + 1));

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.EventStreamDropCount > 0, "event stream drop counter");

        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("http_event_stream_drop_count").GetInt64());
        Assert.Equal(1, root.GetProperty("http_event_stream_write_failure_drop_count").GetInt64());
        Assert.Equal("write_failure:exception_message_redacted:InvalidDataException", root.GetProperty("http_event_stream_last_drop_reason").GetString());
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReplacesInvalidProviderJson_Issue3815()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetHealthJsonProvider(() => "not-json");

        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("degraded", root.GetProperty("status").GetString());
        Assert.Equal("health_provider_invalid", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReplacesOversizedProviderJson_Issue3815()
    {
        var oversizedJson = $$"""{"status":"{{new string('x', HttpMcpTransport.MaxHealthJsonBytes)}}","db_open":true}""";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetHealthJsonProvider(() => oversizedJson);

        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(new string('x', 128), body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("health_provider_invalid", document.RootElement.GetProperty("error").GetString());
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
        using var client = new HttpClient();
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
    public async Task HttpTransport_RequestLogger_CapsLongPathBeforeLogging()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue);

        using var client = new HttpClient();
        var longPath = string.Join('/', Enumerable.Repeat("segment", 50));
        using var response = await client.GetAsync(new Uri(new Uri(harness.Endpoint), longPath));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal(HttpMcpTransport.MaxRequestLogFieldCharacters, record.Path.Length);
        Assert.EndsWith(HttpMcpTransport.RequestLogTruncationMarker, record.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_RequestLogger_CapsLongJsonRpcIdBeforeLogging()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue);
        var oversizedId = new string('i', HttpMcpTransport.MaxRequestLogFieldCharacters + 100);
        var body = """{"jsonrpc":"2.0","id":"""
            + JsonSerializer.Serialize(oversizedId)
            + ""","method":"ping"}""";

        using var response = await harness.PostJsonAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.NotNull(record.RequestId);
        Assert.Equal(HttpMcpTransport.MaxRequestLogFieldCharacters, record.RequestId.Length);
        Assert.EndsWith(HttpMcpTransport.RequestLogTruncationMarker, record.RequestId, StringComparison.Ordinal);
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
    public async Task HttpTransport_MalformedCancellationNotification_IsQueuedForNormalHandling_Issue3711()
    {
        var listen = HttpMcpTransport.ResolveListenSpec("127.0.0.1:0");
        var outOfBandHandlerCalls = 0;
        await using var transport = new HttpMcpTransport(
            listen.Prefix,
            listen.Host,
            listen.Port,
            bearerToken: null);
        transport.OutOfBandFrameHandler = (_, _) =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return Task.FromResult<string?>(null);
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        const string body = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":""";
        var post = client.PostAsync(
            listen.Prefix,
            new StringContent(body, Encoding.UTF8, "application/json"));

        await WaitUntilAsync(
            () => transport.QueuedRequestCount == 1,
            "malformed cancellation notification to stay in the normal HTTP MCP queue");

        Assert.Equal(0, Volatile.Read(ref outOfBandHandlerCalls));
        var frame = await transport.ReadFrameAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(body, frame);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", CancellationToken.None);
        using var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        transport.OutOfBandFrameHandler = (_, _) =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return Task.FromResult<string?>(null);
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
        transport.OutOfBandFrameHandler = (_, _) =>
        {
            Interlocked.Increment(ref outOfBandHandlerCalls);
            return Task.FromResult<string?>(null);
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
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, requestLogger: records.Enqueue, maxRequestBodyBytes: 64);

        using var oversized = await harness.PostJsonAsync(new string('x', 65));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var rejectedBody = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("64 byte limit", rejectedBody, StringComparison.Ordinal);
        var rejectedRecord = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("request_body_limit_exceeded", rejectedRecord.Diagnostic);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
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

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new UnknownLengthStringContent("""{"jsonrpc":"2.0","id":3755,"method":"ping"}"""),
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

        using var oversized = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, oversized.StatusCode);
        var rejectedBody = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("response body exceeds", rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", rejectedBody, StringComparison.Ordinal);
        var rejectedRecord = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("response_body_limit_exceeded", rejectedRecord.Diagnostic);

        using var follow = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        var body = await follow.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("id").GetInt32());
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
            bearerToken: null));

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
            bearerToken: null);

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
            maxQueuedRequests: 1);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var first = client.PostAsync(
            listen.Prefix,
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        await WaitUntilAsync(() => transport.QueuedRequestCount == 1, "the first request to fill the HTTP MCP queue");

        using var second = await client.PostAsync(
            listen.Prefix,
            new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"));

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
            maxConcurrentHandlers: 1);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var events = await client.GetAsync(new Uri(new Uri(listen.Prefix), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => transport.HasEventStreams, "the first event stream to occupy the only handler slot");

        using var second = await client.PostAsync(
            listen.Prefix,
            new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"));

        AssertTooManyRequests(second, HttpMcpTransport.ConcurrentHandlerLimitRejection);
        Assert.Equal(1, transport.ConcurrentHandlerLimitRejectionCount);
    }

    [Fact]
    public async Task HttpTransport_EventsStream_DoesNotBlockPostRequests()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType!.MediaType);
        Assert.True(events.Headers.TryGetValues("X-Accel-Buffering", out var bufferingValues));
        Assert.Contains("no", bufferingValues);
        Assert.True(events.Headers.Contains("X-Cdidx-Mcp-Event-Stream-Id"));

        var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":11,"method":"ping"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(11, doc.RootElement.GetProperty("id").GetInt32());
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
            maxEventStreams: 1);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var first = await client.GetAsync(new Uri(new Uri(listen.Prefix), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await WaitUntilAsync(() => transport.EventStreamCount == 1, "the first event stream to fill the stream limit");

        using var second = await client.GetAsync(new Uri(new Uri(listen.Prefix), "events"), HttpCompletionOption.ResponseHeadersRead);

        AssertTooManyRequests(second, HttpMcpTransport.EventStreamLimitRejection);
        Assert.Equal(1, transport.EventStreamLimitRejectionCount);
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
    public async Task HttpTransport_EventsStream_OversizedKeepAliveDisconnectsStream_Issue3815()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);
        harness.SetKeepAlive(TimeSpan.FromMilliseconds(10), () => new string('x', HttpMcpTransport.MaxSseEventFrameBytes));

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.EventStreamCount == 0, "oversized keep-alive frame to close the event stream");
    }

    [Fact]
    public async Task HttpTransport_EventsStream_RemovesDisconnectedStreams()
    {
        await using var harness = await McpHttpHarness.StartAsync(_dbPath);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
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
        harness.BeforeEventStreamWriteForTests = token => Task.Delay(Timeout.InfiniteTimeSpan, token);

        using var client = new HttpClient();
        using var events = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        await WaitUntilAsync(() => harness.HasEventStreams, "the event stream to be registered");

        using var response = await harness.PostJsonAsync("""{"jsonrpc":"2.0","id":3990,"method":"initialize","params":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => !harness.HasEventStreams, "the timed-out event stream to be removed");
        var snapshot = await WaitForRequestLogRecordsAsync(records, 2);
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

            using var client = new HttpClient();
            using var firstEvents = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
            using var secondEvents = await client.GetAsync(new Uri(new Uri(harness.Endpoint), "events"), HttpCompletionOption.ResponseHeadersRead);
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

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "generic-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Unauthorized", body, StringComparison.Ordinal);
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
    public async Task HttpTransport_BearerToken_RejectsDuplicateAuthorizationHeaders_Issue3756()
    {
        const string token = "s3cret-token";
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token, requestLogger: records.Enqueue);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Bearer {token}", $"Bearer {token}" });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("unauthorized", record.AuthOutcome);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsCommaJoinedAuthorizationHeader_Issue3756()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}, Bearer {token}");
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
    public async Task HttpTransport_BearerToken_AcceptsMaxLengthHeader_Issue3798()
    {
        var token = new string('t', McpAuthenticationLimits.MaxTokenCharacters);
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
    public async Task HttpTransport_BearerToken_RejectsWhitespacePaddedHeader_Issue3505()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer  " + token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_BearerToken_RejectsOversizedHeaderBeforeHashing()
    {
        var records = new ConcurrentQueue<HttpMcpTransport.HttpRequestLogRecord>();
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: "token", requestLogger: records.Enqueue);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + new string('x', McpAuthenticationLimits.MaxTokenCharacters + 1));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var record = Assert.Single(await WaitForRequestLogRecordsAsync(records, 1));
        Assert.Equal("unauthorized", record.AuthOutcome);
    }

    [Fact]
    public async Task HttpTransport_Healthz_ReportsAuthDenialClassesWithoutChangingWireResponse_Issue3966()
    {
        const string token = "s3cret-token";
        await using var harness = await McpHttpHarness.StartAsync(_dbPath, bearerToken: token);

        using var client = new HttpClient();

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

        using var client = new HttpClient();
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

    private static async Task WaitForRequestLogDropAsync(McpHttpHarness harness)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (harness.RequestLogDroppedCount > 0)
                return;

            await Task.Delay(10);
        }

        Assert.Fail("Expected the request log queue to report at least one dropped record.");
    }

    private static void AssertTooManyRequests(HttpResponseMessage response, string rejectionReason)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.True(response.Headers.TryGetValues(HttpMcpTransport.RejectionReasonHeader, out var rejectionValues));
        Assert.Contains(rejectionReason, rejectionValues);
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

        public bool OwnedSemaphoreGatesDisposed => _transport.OwnedSemaphoreGatesDisposedForTests;

        public int EventStreamCount => _transport.EventStreamCount;

        public long EventStreamDropCount => _transport.EventStreamDropCount;

        public long RequestLogDroppedCount => _transport.RequestLogDroppedCount;

        public long RequestLogQueueFullDropCount => _transport.RequestLogQueueFullDropCount;

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
                eventStreamWriteTimeout: eventStreamWriteTimeout);
            var server = authenticator is null
                ? new McpServer(dbPath, ConsoleUi.LoadVersion())
                : new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, authenticator);
            var cts = new CancellationTokenSource();
            var loopTask = Task.Run(() => server.RunAsync(transport, cts.Token));
            // Give the listener a tick to start accepting; HttpListener.Start is synchronous but the
            // background task may not have entered GetContextAsync yet by the time the test posts.
            // listener が GetContextAsync に入る前に POST が来ないよう、ごく短い待機を挟む。
            await Task.Yield();
            var healthReadyDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (transport.HealthJsonProvider is null
                && !loopTask.IsCompleted
                && DateTimeOffset.UtcNow < healthReadyDeadline)
            {
                await Task.Delay(10);
            }
            if (loopTask.IsCompleted)
                await loopTask.ConfigureAwait(false);
            if (transport.HealthJsonProvider is null)
                throw new TimeoutException("Timed out waiting for the MCP HTTP health provider to become ready.");
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
