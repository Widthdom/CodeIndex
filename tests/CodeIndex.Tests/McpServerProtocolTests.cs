using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public async Task RequestBeforeInitialize_IsRejected_Issue4468()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new QueueMcpTransport(prependInitialize: false, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");

        await server.RunAsync(transport, CancellationToken.None);

        var response = JsonNode.Parse(Assert.Single(transport.WrittenFrames))!;
        Assert.Equal(-32002, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Server not initialized", response["error"]!["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData(null)]
    public void RequestWithoutExactJsonRpc20_IsInvalidRequest_Issue4468(string? version)
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var request = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject(),
        };
        if (version is not null)
            request["jsonrpc"] = version;

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void PingHealth_UsesInjectedTimeProvider_Issue3963()
    {
        var clock = new ManualTimeProvider(ManualTimeProvider.FixtureUtcNow);
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            dbPathExplicit: false,
            serializeResponse: null,
            authenticator: null,
            toolFilter: null,
            auditLog: null,
            maxConcurrency: 1,
            timeProvider: clock);
        var requestAt = ManualTimeProvider.FixtureUtcNow.AddSeconds(7);
        clock.SetUtcNow(requestAt);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")!;

        var response = server.HandleMessage(request);

        var result = response!["result"]!.AsObject();
        Assert.Equal(7, result["uptime_s"]!.GetValue<long>());
        Assert.Equal(requestAt.ToString("O", CultureInfo.InvariantCulture), result["last_request_at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public void Initialize_ReturnsSupportedProtocolVersion(string protocolVersion)
    {
        // Issue #1554: negotiation echoes back the client's requested protocolVersion when
        // it is in the server's supported set, including the version used by current Codex
        // clients and both legacy versions.
        // Issue #1554: 交渉ロジックはサーバー対応集合にあるクライアント要求バージョンを
        // そのまま返す。現行 Codex が使うバージョンと旧バージョンを同じ fixture で検証する。
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = protocolVersion,
                ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                ["capabilities"] = new JsonObject(),
            },
        };
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal(protocolVersion, response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(ConsoleUi.LoadVersion(), response["result"]!["serverInfo"]!["version"]!.GetValue<string>());

        var capabilities = response["result"]!["capabilities"]!.AsObject();
        Assert.Equal(
            ["logging", "prompts", "resources", "tools"],
            capabilities.Select(static capability => capability.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.False(capabilities["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["prompts"]!["listChanged"]!.GetValue<bool>());
        Assert.NotNull(capabilities["logging"]);
        Assert.False(capabilities.ContainsKey("roots"));
        Assert.False(capabilities.ContainsKey("sampling"));
    }

    [Fact]
    public async Task Initialize_CodexProtocolVersion_AllowsToolsList()
    {
        // Codex 0.144.5 requests MCP 2025-06-18. Exercise the lifecycle-enforcing transport
        // path so a compatible initialize cannot regress into the reported tools/list failure.
        // Codex 0.144.5 は MCP 2025-06-18 を要求する。lifecycle を強制する transport
        // 経路で initialize から tools/list まで成功することを検証する。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new QueueMcpTransport(
            prependInitialize: false,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"codex-mcp-client","version":"0.144.5"},"capabilities":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
        );

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(2, transport.WrittenFrames.Count);
        var initializeResponse = JsonNode.Parse(transport.WrittenFrames[0])!;
        Assert.Equal("2025-06-18", initializeResponse["result"]!["protocolVersion"]!.GetValue<string>());
        var toolsResponse = JsonNode.Parse(transport.WrittenFrames[1])!;
        Assert.Null(toolsResponse["error"]);
        Assert.Contains(toolsResponse["result"]!["tools"]!.AsArray(),
            tool => tool!["name"]!.GetValue<string>() == "search");
    }

    [Fact]
    public async Task Initialize_SecondInitializeIsRejectedWithoutMutatingSession_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new QueueMcpTransport(
            prependInitialize: false,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"first-client","version":"1.0"},"capabilities":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"second-client","version":"2.0"},"capabilities":{"roots":{}}}}"""
        );

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(2, transport.WrittenFrames.Count);
        Assert.NotNull(JsonNode.Parse(transport.WrittenFrames[0])!["result"]);
        var duplicate = JsonNode.Parse(transport.WrittenFrames[1])!;
        Assert.Equal(-32600, duplicate["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_request", duplicate["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.Equal("duplicate_initialize", duplicate["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Equal("initialized", duplicate["error"]!["data"]!["session_phase"]!.GetValue<string>());
        Assert.Equal("first-client/1.0", server.CurrentCaller);
        Assert.False(server.ClientSupportsRootsForTests);
    }

    [Fact]
    public async Task Initialize_LateTimedOutWorkerReleasesClaimForRetry_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion())
        {
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestDelayForTests = _ =>
        {
            delayStarted.TrySetResult();
            return releaseDelay.Task;
        };

        var timedOutResponseTask = server.ProcessFrameAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"late-client","version":"1.0"},"capabilities":{}}}""");
        try
        {
            await delayStarted.Task.WaitAsync(TestDeterminism.DefaultTimeout);
            var timedOutResponse = JsonNode.Parse(
                await timedOutResponseTask.WaitAsync(TestDeterminism.DefaultTimeout) ?? string.Empty)!;
            Assert.Equal("timeout", timedOutResponse["error"]!["data"]!["reason"]!.GetValue<string>());
        }
        finally
        {
            server.RequestDelayForTests = null;
            releaseDelay.TrySetResult();
        }

        await TestDeterminism.WaitUntilAsync(
            () => server.BuildRequestTimeoutDiagnosticsStatus()["isolated_action_draining_count"]!.GetValue<long>() == 0,
            "the late initialize worker to drain and release its sealed-frame claim");

        var retryResponse = JsonNode.Parse(
            await server.ProcessFrameAsync(
                """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"retry-client","version":"2.0"},"capabilities":{}}}""")
            ?? string.Empty)!;
        Assert.NotNull(retryResponse["result"]);
        Assert.Equal("retry-client/2.0", server.CurrentCaller);
    }

    [Fact]
    public async Task InitializedNotification_RequestsRootsOnlyWhenClientAdvertisesSupport_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new RootsNegotiationTranscriptTransport(advertiseRoots: true);

        var runTask = server.RunAsync(transport, CancellationToken.None);
        await transport.ReadyToCompleteInput.WaitAsync(TestDeterminism.DefaultTimeout);
        await TestDeterminism.WaitUntilAsync(
            () => server.ClientRootsForTests.SequenceEqual(["file:///negotiated-workspace"], StringComparer.Ordinal),
            "the roots/list response to commit the negotiated client roots");
        transport.CompleteInput();
        await runTask;

        var rootsRequest = JsonNode.Parse(Assert.Single(transport.OutOfBandFrames))!;
        Assert.Equal("roots/list", rootsRequest["method"]!.GetValue<string>());
        Assert.Equal(["file:///negotiated-workspace"], server.ClientRootsForTests);
        Assert.False(server.ClientRootsStaleForTests);

        var transcript = transport.Transcript;
        var initializeResponseIndex = Array.FindIndex(
            transcript,
            static entry => entry.StartsWith("server:", StringComparison.Ordinal)
                && entry.Contains("\"protocolVersion\":\"2025-06-18\"", StringComparison.Ordinal));
        var initializedNotificationIndex = Array.FindIndex(
            transcript,
            static entry => entry.StartsWith("client:", StringComparison.Ordinal)
                && entry.Contains("\"method\":\"notifications/initialized\"", StringComparison.Ordinal));
        var rootsRequestIndex = Array.FindIndex(
            transcript,
            static entry => entry.StartsWith("server-oob:", StringComparison.Ordinal)
                && entry.Contains("\"method\":\"roots/list\"", StringComparison.Ordinal));
        Assert.True(initializeResponseIndex >= 0);
        Assert.True(initializedNotificationIndex > initializeResponseIndex);
        Assert.True(rootsRequestIndex > initializedNotificationIndex);
    }

    [Fact]
    public async Task InitializedNotification_DoesNotRequestRootsWithoutClientCapability_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new RootsNegotiationTranscriptTransport(advertiseRoots: false);

        var runTask = server.RunAsync(transport, CancellationToken.None);
        await transport.ReadyToCompleteInput.WaitAsync(TestDeterminism.DefaultTimeout);
        Assert.Empty(transport.OutOfBandFrames);
        transport.CompleteInput();
        await runTask;

        Assert.Empty(transport.OutOfBandFrames);
        Assert.Empty(server.ClientRootsForTests);
    }

    [Fact]
    public async Task InitializedNotification_CoalescesAndDrainsRootsRefresh_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        Assert.NotNull(server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"roots":{}}}}""")!)?["result"]);

        var rootsRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRootsResponse = new ManualResetEventSlim(false);
        var rootsRequestCount = 0;
        string? requestedMethod = null;
        server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethod = method;
            Interlocked.Increment(ref rootsRequestCount);
            rootsRequestStarted.TrySetResult();
            if (!releaseRootsResponse.Wait(TestDeterminism.DefaultTimeout))
                throw new TimeoutException("Timed out waiting to release the coalesced roots response.");
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "file:///coalesced-workspace" }),
            };
        };

        Task? drainTask = null;
        try
        {
            Assert.Null(server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""")!));
            await rootsRequestStarted.Task.WaitAsync(TestDeterminism.DefaultTimeout);
            for (var index = 0; index < 32; index++)
            {
                Assert.Null(server.HandleMessage(JsonNode.Parse(
                    """{"jsonrpc":"2.0","method":"notifications/initialized"}""")!));
            }

            Assert.Equal(1, Volatile.Read(ref rootsRequestCount));
            drainTask = server.DrainInFlightTasksAsync(
                [],
                TestDeterminism.DefaultTimeout,
                TimeSpan.Zero);
            Assert.False(drainTask.IsCompleted);
        }
        finally
        {
            releaseRootsResponse.Set();
        }

        Assert.NotNull(drainTask);
        await drainTask!.WaitAsync(TestDeterminism.DefaultTimeout);
        Assert.Equal("roots/list", requestedMethod);
        Assert.Equal(["file:///coalesced-workspace"], server.ClientRootsForTests);
        Assert.False(server.ClientRootsStaleForTests);
    }

    [Fact]
    public async Task InitializedNotification_LateRootsRefreshRetainsStdioResourcesAfterBoundedDrain_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion())
        {
            InFlightDrainGracePeriod = TimeSpan.Zero,
            InFlightPostCancelGracePeriod = TimeSpan.Zero,
        };
        var inputPayload = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"roots":{}}}}"""
            + "\n"
            + """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
            + "\n");
        var allowEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new GatedEofReadStream(inputPayload, allowEof.Task);
        var output = new BlockingSecondWriteStream();
        var transport = new StdioMcpTransport(input, output, bufferSize: 1024 * 1024);

        try
        {
            var runTask = server.RunAsync(transport, CancellationToken.None);
            await output.SecondWriteStarted.WaitAsync(TestDeterminism.DefaultTimeout);
            allowEof.TrySetResult();
            await runTask.WaitAsync(TestDeterminism.DefaultTimeout);

            await transport.DisposeAsync();
            Assert.False(output.IsDisposed);
        }
        finally
        {
            allowEof.TrySetResult();
            output.ReleaseSecondWrite();
            await transport.DisposeAsync();
        }

        await TestDeterminism.WaitUntilAsync(
            () => output.IsDisposed,
            "stdio output disposal after the late roots refresh released its writer",
            TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Initialize_CapturesClientCapabilitiesAndRootsForSessionStatus()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"codex","version":"5.0"},"capabilities":{"experimental":{"progress":true}},"rootUri":"file:///workspace","roots":[{"uri":"file:///workspace/src"}]}}""")!;
        _server.HandleMessage(request);

        Assert.True(_server.ClientCapabilitiesForTests!["experimental"]!["progress"]!.GetValue<bool>());
        Assert.Equal(["file:///workspace", "file:///workspace/src"], _server.ClientRootsForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["client_capabilities"]!["experimental"]!["progress"]!.GetValue<bool>());
        var capabilitiesSummary = session["client_capabilities_summary"]!;
        Assert.False(capabilitiesSummary["roots"]!.GetValue<bool>());
        Assert.False(capabilitiesSummary["sampling"]!.GetValue<bool>());
        Assert.False(capabilitiesSummary["truncated"]!.GetValue<bool>());
        Assert.Contains(capabilitiesSummary["top_level_keys"]!.AsArray(), key => key!.GetValue<string>() == "experimental");
        Assert.Contains(capabilitiesSummary["experimental_keys"]!.AsArray(), key => key!.GetValue<string>() == "progress");
        Assert.Equal("codex", session["client_info"]!["name"]!.GetValue<string>());
        Assert.Equal("5.0", session["client_info"]!["version"]!.GetValue<string>());
        Assert.Equal("file:///workspace", session["roots"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("info", session["log_level"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_CapturesClientCapabilitiesAsDetachedClone_Issue3055()
    {
        var capabilities = new JsonObject
        {
            ["experimental"] = new JsonObject
            {
                ["progress"] = true,
            },
        };
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["capabilities"] = capabilities,
            },
        };

        _server.HandleMessage(request);
        capabilities["experimental"]!["progress"] = false;
        var copy = _server.ClientCapabilitiesForTests!;
        copy["experimental"]!["progress"] = false;

        Assert.True(_server.ClientCapabilitiesForTests!["experimental"]!["progress"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_CapsClientRootsForSessionStatus_Issue3076()
    {
        var longRoot = "file:///" + new string('r', McpServer.MaxClientRootUriChars + 50);
        var roots = new JsonArray();
        for (var i = 0; i < McpServer.MaxClientRootCount + 3; i++)
        {
            roots.Add(new JsonObject
            {
                ["uri"] = i == 0 ? longRoot : $"file:///workspace/{i}",
            });
        }

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["rootUri"] = "file:///workspace",
                ["roots"] = roots,
            },
        };
        _server.HandleMessage(request);

        Assert.Equal(McpServer.MaxClientRootCount + 4, _server.ClientRootsForTests.Length);
        Assert.Contains(longRoot, _server.ClientRootsForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["roots_truncated"]!.GetValue<bool>());
        Assert.Equal(McpServer.MaxClientRootCount + 4, session["root_count"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientRootCount, session["root_limit"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientRootUriChars, session["root_uri_length_limit"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientRootCount, session["roots"]!.AsArray().Count);
        Assert.DoesNotContain(longRoot, response.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_CapsClientCapabilitiesByteSizeForSessionStatus_Issue3225()
    {
        var largeValue = new string('c', McpServer.MaxClientCapabilitiesJsonBytes + 100);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["roots"] = new JsonObject(),
                    ["sampling"] = new JsonObject(),
                    ["experimental"] = new JsonObject
                    {
                        ["large"] = largeValue,
                    },
                },
            },
        };
        _server.HandleMessage(request);

        Assert.Empty(_server.ClientCapabilitiesForTests!.AsObject());
        Assert.True(_server.ClientSupportsRootsForTests);
        Assert.True(_server.ClientSupportsSamplingForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["client_capabilities_truncated"]!.GetValue<bool>());
        Assert.Equal("byte_limit", session["client_capabilities_truncation_reason"]!.GetValue<string>());
        Assert.True(session["client_capabilities_serialized_bytes"]!.GetValue<int>() > McpServer.MaxClientCapabilitiesJsonBytes);
        Assert.Equal(McpServer.MaxClientCapabilitiesJsonBytes, session["client_capabilities_byte_limit"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientCapabilitiesDepth, session["client_capabilities_depth_limit"]!.GetValue<int>());
        Assert.Empty(session["client_capabilities"]!.AsObject());
        var capabilitiesSummary = session["client_capabilities_summary"]!;
        Assert.True(capabilitiesSummary["roots"]!.GetValue<bool>());
        Assert.True(capabilitiesSummary["sampling"]!.GetValue<bool>());
        Assert.True(capabilitiesSummary["truncated"]!.GetValue<bool>());
        Assert.Equal("byte_limit", capabilitiesSummary["truncation_reason"]!.GetValue<string>());
        Assert.DoesNotContain(largeValue, response.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_CapsClientCapabilitiesDepthForSessionStatus_Issue3225()
    {
        var capabilities = new JsonObject();
        capabilities["roots"] = new JsonObject();
        capabilities["sampling"] = new JsonObject();
        var current = capabilities;
        for (var i = 0; i < McpServer.MaxClientCapabilitiesDepth + 4; i++)
        {
            var next = new JsonObject();
            current[$"level{i}"] = next;
            current = next;
        }

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["capabilities"] = capabilities,
            },
        };
        _server.HandleMessage(request);

        Assert.Empty(_server.ClientCapabilitiesForTests!.AsObject());
        Assert.True(_server.ClientSupportsRootsForTests);
        Assert.True(_server.ClientSupportsSamplingForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["client_capabilities_truncated"]!.GetValue<bool>());
        Assert.Equal("depth_limit", session["client_capabilities_truncation_reason"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxClientCapabilitiesJsonBytes, session["client_capabilities_byte_limit"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientCapabilitiesDepth, session["client_capabilities_depth_limit"]!.GetValue<int>());
        Assert.Empty(session["client_capabilities"]!.AsObject());
        var capabilitiesSummary = session["client_capabilities_summary"]!;
        Assert.True(capabilitiesSummary["roots"]!.GetValue<bool>());
        Assert.True(capabilitiesSummary["sampling"]!.GetValue<bool>());
        Assert.True(capabilitiesSummary["truncated"]!.GetValue<bool>());
        Assert.Equal("depth_limit", capabilitiesSummary["truncation_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ClientInfo_TruncatesSessionStatusAndCallerIdentity_Issue3120()
    {
        var name = new string('n', McpBoundedText.MaxClientInfoChars + 25);
        var version = new string('v', McpBoundedText.MaxClientInfoChars + 25);
        var nameDisplay = McpBoundedText.ForDisplay(name, McpBoundedText.MaxClientInfoChars);
        var versionDisplay = McpBoundedText.ForDisplay(version, McpBoundedText.MaxClientInfoChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = name,
                    ["version"] = version,
                },
            },
        };

        _server.HandleMessage(request);

        Assert.Equal($"{nameDisplay.Text}/{versionDisplay.Text}", _server.CurrentCaller);
        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;

        Assert.DoesNotContain(name, response.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(version, response.ToJsonString(), StringComparison.Ordinal);
        var clientInfo = response["result"]!["structuredContent"]!["mcp_session"]!["client_info"]!;
        Assert.Equal(nameDisplay.Text, clientInfo["name"]!.GetValue<string>());
        Assert.Equal(name.Length, clientInfo["name_length"]!.GetValue<int>());
        Assert.True(clientInfo["name_truncated"]!.GetValue<bool>());
        Assert.Equal(versionDisplay.Text, clientInfo["version"]!.GetValue<string>());
        Assert.Equal(version.Length, clientInfo["version_length"]!.GetValue<int>());
        Assert.True(clientInfo["version_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void LoggingSetLevel_UpdatesSessionLogLevel()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"emergency"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Equal("emergency", _server.McpLogLevelForTests);
    }

    [Fact]
    public void LoggingSetLevel_InvalidLevel_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"trace"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_NoProtocolVersion_UsesPreferred()
    {
        // Issue #1554: empty params still works — the negotiation falls back to the server's
        // preferred (newest) version so existing clients that never sent the field keep
        // working unchanged.
        // Issue #1554: params が空でも動作する — 既定の優先バージョン（最新）に fallback
        // することで、protocolVersion を送らない既存クライアントの互換を保つ。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-06-18", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_UnsupportedProtocolVersion_ReturnsInvalidParamsError()
    {
        // Issue #1554: an unsupported requested version must NOT silently downgrade to the
        // server's preferred version — that hid mismatches and made future spec bumps
        // observably break clients. Instead the handshake returns -32602 with structured
        // data so clients can branch on the failure and report a precise diagnostic.
        // Issue #1554: 未対応の要求バージョンを黙ってダウングレードしてはならない
        // （ミスマッチを覆い隠してしまうため）。-32602 と構造化データを返し、クライアントが
        // 失敗判定して正確な診断を出せるようにする。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("2099-01-01", error["message"]!.GetValue<string>());
        Assert.Contains("2025-06-18", error["message"]!.GetValue<string>());

        var data = error["data"]!;
        Assert.Equal("2099-01-01", data["requestedVersion"]!.GetValue<string>());
        var supported = data["supportedVersions"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        Assert.Equal(McpServer.SupportedProtocolVersions, supported);

        // #1581: the version-negotiation error path must also carry the canonical envelope
        // (`category` / `suggestion` / `retry_safe`) on top of the #1554 version fields so
        // clients can branch on category instead of parsing the message string.
        // #1581: バージョン交渉エラーも canonical envelope を必ず併載し、クライアントは
        // category で分岐できる。
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["suggestion"]!.GetValue<string>()));
        Assert.False(data["retry_safe"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Initialize_FailedNegotiationLeavesSessionStateUnchangedThenSuccessCommits_Issue4540()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var sessionId = server.CurrentSessionId;
        var failedInitialize = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
          "protocolVersion":"2099-01-01",
          "clientInfo":{"name":"poison-client","version":"0.0"},
          "capabilities":{"roots":{},"sampling":{},"experimental":{"poison":true}},
          "rootUri":"file:///poison",
          "roots":[{"uri":"file:///poison/src"}]
        }}
        """)!;
        var transport = new QueueMcpTransport(
            prependInitialize: false,
            failedInitialize.ToJsonString());

        await server.RunAsync(transport, CancellationToken.None);

        var failedResponse = JsonNode.Parse(Assert.Single(transport.WrittenFrames))!;
        Assert.Equal(-32602, failedResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("unknown", server.CurrentCaller);
        Assert.Null(server.ClientCapabilitiesForTests);
        Assert.Empty(server.ClientRootsForTests);
        Assert.False(server.ClientSupportsRootsForTests);
        Assert.False(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);

        var beforeSuccess = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""")!)!;
        Assert.Equal(-32002, beforeSuccess["error"]!["code"]!.GetValue<int>());

        var successfulInitialize = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":3,"method":"initialize","params":{
          "protocolVersion":"2025-03-26",
          "clientInfo":{"name":"valid-client","version":"2.0"},
          "capabilities":{"roots":{},"sampling":{},"experimental":{"valid":true}},
          "rootUri":"file:///valid",
          "roots":[{"uri":"file:///valid/src"}]
        }}
        """)!;
        var successfulResponse = server.HandleMessage(successfulInitialize)!;

        Assert.NotNull(successfulResponse["result"]);
        Assert.Equal("valid-client/2.0", server.CurrentCaller);
        Assert.True(server.ClientCapabilitiesForTests!["experimental"]!["valid"]!.GetValue<bool>());
        Assert.Equal(["file:///valid", "file:///valid/src"], server.ClientRootsForTests);
        Assert.True(server.ClientSupportsRootsForTests);
        Assert.True(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);

        var statusResponse = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!)!;
        var statusJson = statusResponse.ToJsonString();
        var session = statusResponse["result"]!["structuredContent"]!["mcp_session"]!;
        Assert.Equal("valid-client", session["client_info"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("poison-client", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("file:///poison", statusJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_SerializationFailureLeavesSessionStateUnchangedThenSuccessCommits_Issue4540()
    {
        var serializerCalls = 0;
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            false,
            response => Interlocked.Increment(ref serializerCalls) == 1
                ? throw new JsonException("initialize serializer failed")
                : response.ToJsonString());
        var sessionId = server.CurrentSessionId;
        var failedTransport = new QueueMcpTransport(
            prependInitialize: false,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"serialization-poison","version":"0.0"},
              "capabilities":{"roots":{},"sampling":{},"experimental":{"poison":true}},
              "rootUri":"file:///serialization-poison",
              "roots":[{"uri":"file:///serialization-poison/src"}]
            }}
            """);

        await server.RunAsync(failedTransport, CancellationToken.None);

        var failedResponse = JsonNode.Parse(Assert.Single(failedTransport.WrittenFrames))!;
        Assert.Equal(-32603, failedResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("unknown", server.CurrentCaller);
        Assert.Null(server.ClientCapabilitiesForTests);
        Assert.Empty(server.ClientRootsForTests);
        Assert.False(server.ClientSupportsRootsForTests);
        Assert.False(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);

        var beforeSuccess = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""")!)!;
        Assert.Equal(-32002, beforeSuccess["error"]!["code"]!.GetValue<int>());

        var successfulTransport = new QueueMcpTransport(
            prependInitialize: false,
            """
            {"jsonrpc":"2.0","id":3,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"serialization-valid","version":"2.0"},
              "capabilities":{"roots":{},"sampling":{},"experimental":{"valid":true}},
              "rootUri":"file:///serialization-valid",
              "roots":[{"uri":"file:///serialization-valid/src"}]
            }}
            """);

        await server.RunAsync(successfulTransport, CancellationToken.None);

        var successfulResponse = JsonNode.Parse(Assert.Single(successfulTransport.WrittenFrames))!;
        Assert.NotNull(successfulResponse["result"]);
        Assert.Equal("serialization-valid/2.0", server.CurrentCaller);
        Assert.True(server.ClientCapabilitiesForTests!["experimental"]!["valid"]!.GetValue<bool>());
        Assert.Equal(
            ["file:///serialization-valid", "file:///serialization-valid/src"],
            server.ClientRootsForTests);
        Assert.True(server.ClientSupportsRootsForTests);
        Assert.True(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);

        var statusResponse = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!)!;
        var statusJson = statusResponse.ToJsonString();
        Assert.DoesNotContain("serialization-poison", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("file:///serialization-poison", statusJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_BatchProvisionalStateDoesNotPublishWhenSerializationFails_Issue4540()
    {
        var serializerCalls = 0;
        JsonNode? attemptedBatchResponse = null;
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            false,
            response =>
            {
                if (Interlocked.Increment(ref serializerCalls) != 1)
                    return response.ToJsonString();

                attemptedBatchResponse = JsonNode.Parse(response.ToJsonString());
                throw new JsonException("batch initialize serializer failed");
            });
        var transport = new QueueMcpTransport(
            prependInitialize: false,
            """
            [
              {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                "clientInfo":{"name":"batch-provisional","version":"1.0"},
                "capabilities":{"roots":{},"experimental":{"generation":"provisional"}},
                "roots":[{"uri":"file:///batch-provisional"}]
              }},
              {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}
            ]
            """);

        await server.RunAsync(transport, CancellationToken.None);

        var failedResponse = JsonNode.Parse(Assert.Single(transport.WrittenFrames))!;
        Assert.Equal(-32603, failedResponse["error"]!["code"]!.GetValue<int>());
        var attemptedResponses = Assert.IsType<JsonArray>(attemptedBatchResponse);
        var provisionalSession = attemptedResponses[1]!["result"]!["structuredContent"]!["mcp_session"]!;
        Assert.Equal("batch-provisional", provisionalSession["client_info"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "provisional",
            provisionalSession["client_capabilities"]!["experimental"]!["generation"]!.GetValue<string>());
        Assert.Equal("file:///batch-provisional", Assert.Single(provisionalSession["roots"]!.AsArray())!.GetValue<string>());

        Assert.Equal("unknown", server.CurrentCaller);
        Assert.Null(server.ClientCapabilitiesForTests);
        Assert.Empty(server.ClientRootsForTests);
        Assert.False(server.ClientSupportsRootsForTests);
    }

    [Fact]
    public async Task Initialize_ResponseByteLimitFailureLeavesStateUnchangedThenSuccessCommits_Issue4540()
    {
        using var responseLimit = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "1024");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var sessionId = server.CurrentSessionId;
        var failedTransport = new QueueMcpTransport(
            prependInitialize: false,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"response-limit-poison","version":"0.0"},
              "capabilities":{"roots":{},"sampling":{},"experimental":{"poison":true}},
              "roots":[{"uri":"file:///response-limit-poison"}]
            }}
            """);

        await server.RunAsync(failedTransport, CancellationToken.None);

        var failedResponse = JsonNode.Parse(Assert.Single(failedTransport.WrittenFrames))!;
        Assert.Equal(-32603, failedResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("response_too_large", failedResponse["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Equal("unknown", server.CurrentCaller);
        Assert.Null(server.ClientCapabilitiesForTests);
        Assert.Empty(server.ClientRootsForTests);
        Assert.False(server.ClientSupportsRootsForTests);
        Assert.False(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);

        var beforeSuccess = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""")!)!;
        Assert.Equal(-32002, beforeSuccess["error"]!["code"]!.GetValue<int>());

        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "10485760");
        var successfulTransport = new QueueMcpTransport(
            prependInitialize: false,
            """
            {"jsonrpc":"2.0","id":3,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"response-limit-valid","version":"2.0"},
              "capabilities":{"roots":{},"sampling":{},"experimental":{"valid":true}},
              "roots":[{"uri":"file:///response-limit-valid"}]
            }}
            """);

        await server.RunAsync(successfulTransport, CancellationToken.None);

        var successfulResponse = JsonNode.Parse(Assert.Single(successfulTransport.WrittenFrames))!;
        Assert.NotNull(successfulResponse["result"]);
        Assert.Equal("response-limit-valid/2.0", server.CurrentCaller);
        Assert.True(server.ClientCapabilitiesForTests!["experimental"]!["valid"]!.GetValue<bool>());
        Assert.Equal(["file:///response-limit-valid"], server.ClientRootsForTests);
        Assert.True(server.ClientSupportsRootsForTests);
        Assert.True(server.ClientSupportsSamplingForTests);
        Assert.Equal(sessionId, server.CurrentSessionId);
    }

    [Fact]
    public async Task Initialize_DuplicateDoesNotReplaceConcurrentSessionSnapshot_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var initialResponse = server.HandleMessage(JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
          "protocolVersion":"2025-03-26",
          "clientInfo":{"name":"snapshot-old","version":"1.0"},
          "capabilities":{"roots":{},"experimental":{"old":true}},
          "rootUri":"file:///snapshot-old"
        }}
        """)!)!;
        Assert.NotNull(initialResponse["result"]);

        using var snapshotCaptured = new ManualResetEventSlim(false);
        using var releaseSnapshotReader = new ManualResetEventSlim(false);
        server.McpSessionSnapshotCapturedForTests = () =>
        {
            snapshotCaptured.Set();
            if (!releaseSnapshotReader.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the captured MCP session snapshot.");
        };

        var statusTask = Task.Run(() => server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!));
        JsonNode duplicateInitializeResponse;
        try
        {
            Assert.True(snapshotCaptured.Wait(TimeSpan.FromSeconds(10)), "Status did not capture its MCP session snapshot.");
            duplicateInitializeResponse = server.HandleMessage(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":3,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"snapshot-new","version":"2.0"},
              "capabilities":{"sampling":{},"experimental":{"new":true}},
              "rootUri":"file:///snapshot-new"
            }}
            """)!)!;
        }
        finally
        {
            server.McpSessionSnapshotCapturedForTests = null;
            releaseSnapshotReader.Set();
        }

        Assert.Equal(-32600, duplicateInitializeResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal(
            "duplicate_initialize",
            duplicateInitializeResponse["error"]!["data"]!["reason"]!.GetValue<string>());
        var concurrentStatus = await statusTask.WaitAsync(TimeSpan.FromSeconds(10));
        var oldSession = concurrentStatus!["result"]!["structuredContent"]!["mcp_session"]!;
        Assert.Equal("snapshot-old", oldSession["client_info"]!["name"]!.GetValue<string>());
        Assert.Equal("1.0", oldSession["client_info"]!["version"]!.GetValue<string>());
        Assert.Equal("file:///snapshot-old", Assert.Single(oldSession["roots"]!.AsArray())!.GetValue<string>());
        Assert.True(oldSession["client_capabilities"]!["experimental"]!["old"]!.GetValue<bool>());
        Assert.DoesNotContain("snapshot-new", concurrentStatus.ToJsonString(), StringComparison.Ordinal);

        var laterStatus = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!)!;
        var retainedSession = laterStatus["result"]!["structuredContent"]!["mcp_session"]!;
        Assert.Equal("snapshot-old", retainedSession["client_info"]!["name"]!.GetValue<string>());
        Assert.Equal("1.0", retainedSession["client_info"]!["version"]!.GetValue<string>());
        Assert.Equal("file:///snapshot-old", Assert.Single(retainedSession["roots"]!.AsArray())!.GetValue<string>());
        Assert.True(retainedSession["client_capabilities"]!["experimental"]!["old"]!.GetValue<bool>());
        Assert.DoesNotContain("snapshot-new", laterStatus.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_DuplicateDoesNotReplaceInFlightRootNegotiation_Issue4848()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        server.HandleMessage(JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
          "protocolVersion":"2025-03-26",
          "clientInfo":{"name":"root-refresh-old","version":"1.0"},
          "capabilities":{"roots":{}},
          "rootUri":"file:///root-refresh-initial"
        }}
        """)!);

        using var rootsRequestStarted = new ManualResetEventSlim(false);
        using var releaseRootsResponse = new ManualResetEventSlim(false);
        server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("roots/list", method);
            rootsRequestStarted.Set();
            if (!releaseRootsResponse.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the roots/list response.");
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "file:///stale-root-refresh" }),
            };
        };

        var rootsNegotiationTask = Task.Run(() => server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""")!));
        try
        {
            Assert.True(rootsRequestStarted.Wait(TimeSpan.FromSeconds(10)), "The roots/list request did not start.");
            var duplicateInitializeResponse = server.HandleMessage(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":3,"method":"initialize","params":{
              "protocolVersion":"2025-03-26",
              "clientInfo":{"name":"root-refresh-new","version":"2.0"},
              "capabilities":{"roots":{}},
              "rootUri":"file:///root-refresh-new"
            }}
            """)!)!;
            Assert.Equal(-32600, duplicateInitializeResponse["error"]!["code"]!.GetValue<int>());
            Assert.Equal(
                "duplicate_initialize",
                duplicateInitializeResponse["error"]!["data"]!["reason"]!.GetValue<string>());
        }
        finally
        {
            releaseRootsResponse.Set();
        }

        Assert.Null(await rootsNegotiationTask.WaitAsync(TimeSpan.FromSeconds(10)));
        await TestDeterminism.WaitUntilAsync(
            () => server.ClientRootsForTests.SequenceEqual(["file:///stale-root-refresh"], StringComparer.Ordinal),
            "the accepted handshake roots response to update the retained session");
        Assert.Equal(["file:///stale-root-refresh"], server.ClientRootsForTests);
        Assert.Equal("root-refresh-old/1.0", server.CurrentCaller);
    }

    [Fact]
    public async Task RootsChangedNotification_InvalidatesInFlightRootRefreshBeforeAuthorization_Issue4540()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var workspaceRootUri = new Uri(Path.GetFullPath(".") + Path.DirectorySeparatorChar).AbsoluteUri;
        var initializeRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject { ["roots"] = new JsonObject() },
                ["rootUri"] = workspaceRootUri,
            },
        };
        Assert.NotNull(server.HandleMessage(initializeRequest)?["result"]);

        using var rootsRequestStarted = new ManualResetEventSlim(false);
        using var releaseRootsResponse = new ManualResetEventSlim(false);
        server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("roots/list", method);
            rootsRequestStarted.Set();
            if (!releaseRootsResponse.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the roots/list response.");
            return new JsonObject { ["roots"] = new JsonArray() };
        };

        var indexTask = Task.Run(() => server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!));
        try
        {
            Assert.True(rootsRequestStarted.Wait(TimeSpan.FromSeconds(10)), "The roots/list request did not start.");
            Assert.Null(server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""")!));
        }
        finally
        {
            releaseRootsResponse.Set();
        }

        var indexResponse = await indexTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(indexResponse!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", indexResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal([workspaceRootUri], server.ClientRootsForTests);
        Assert.True(server.ClientRootsStaleForTests);
    }

    [Fact]
    public void Initialize_MalformedProtocolVersion_FallsBackToPreferred()
    {
        // Non-string `protocolVersion` (e.g. number, null, object) is treated the same as
        // "field absent": fall back to the preferred version. Erroring would break tolerant
        // clients that send `null` when no preference exists, while a silent fallback only
        // kicks in for genuinely malformed inputs and not for the strict-mismatch path.
        // 非文字列の `protocolVersion`（数値・null・オブジェクト）は「未指定」と同じ扱いで
        // 既定の優先バージョンに fallback する。null を許容するクライアントとの互換を残しつつ、
        // 厳格な不一致ケース（文字列だが対応外）には引き続き -32602 を返せる。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":42}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-06-18", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void CancelRequest_ForActiveRequest_ReturnsRequestCancelledError()
    {
        // Issue #1418: MCP `$/cancelRequest` must target the matching JSON-RPC id and
        // cancel the per-request token before the tool does DB work.
        // Issue #1418: MCP `$/cancelRequest` は対応する JSON-RPC id の per-request token を
        // cancel し、ツールが DB 作業に入る前に中断できる必要がある。
        _server.RequestRegisteredForTests = id =>
        {
            var cancel = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1418}}""")!;
            Assert.Null(_server.HandleMessage(cancel));
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, error["code"]!.GetValue<int>());
        Assert.Equal("request_cancelled", error["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void CancelRequest_UnknownOrMalformedId_IsNotificationOnly()
    {
        var unknown = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"missing"}}""")!;
        var missing = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{}}""")!;

        Assert.Null(_server.HandleMessage(unknown));
        Assert.Null(_server.HandleMessage(missing));
    }

    [Fact]
    public void BatchQuery_SlotsIncludeChildCorrelationIds()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping","arguments":{}},{"tool":"languages","arguments":{}}]}}}""")!;

        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        var first = results[0]!["correlation_id"]!.GetValue<string>();
        var second = results[1]!["correlation_id"]!.GetValue<string>();
        Assert.EndsWith(".1", first, StringComparison.Ordinal);
        Assert.EndsWith(".2", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Initialize_UnsupportedProtocolVersion_TruncatesDiagnostics_Issue3119()
    {
        var requested = new string('p', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = requested,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(requested, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(display.Text, response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["requestedVersion"]!.GetValue<string>());
        Assert.Equal(requested.Length, data["requestedVersion_length"]!.GetValue<int>());
        Assert.True(data["requestedVersion_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_NullId_PreservesNullResponseId()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":null,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        using var document = JsonDocument.Parse(response.ToJsonString());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("2025-06-18", root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Initialize_NoId_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void Initialize_BooleanId_ReturnsInvalidRequestWithNullId()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":true,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        using var document = JsonDocument.Parse(response.ToJsonString());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("id must be string, number, or null", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Initialize_ReturnsToolsCapability()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
        Assert.False(response["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_ReturnsInstructions()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var instructions = response["result"]!["instructions"]?.GetValue<string>();
        Assert.NotNull(instructions);
        Assert.Contains("map", instructions!);
        Assert.Contains("analyze_symbol", instructions);
        Assert.Contains("search", instructions);
        Assert.Contains("CodeIndex MCP tools", instructions);
        Assert.Contains("grep/find/cat", instructions);
        Assert.Contains("resources/templates/list", instructions);
        Assert.Contains("cdidx://file-path/{path}", instructions);
        Assert.Contains("resources/list", instructions);
        Assert.Contains("optional path, lang, includeGenerated, and maxBytes", instructions);
        Assert.Contains("result.nextCursor", instructions);
        Assert.Contains("unchanged filters", instructions);
        Assert.Contains("resources/read", instructions);
        Assert.Contains("startLine/endLine", instructions);
        Assert.Contains("maxBytes", instructions);
        Assert.Contains("result._meta.nextCursor", instructions);
        Assert.Contains("whole-file reads", instructions);
        Assert.Contains("definition", instructions);
        Assert.Contains("references", instructions);
        Assert.Contains("callers/callees", instructions);
        Assert.Contains("excerpt", instructions);
        // Verify index-first bootstrap guidance / インデックス未作成時の案内を検証
        Assert.Contains("index", instructions);
        Assert.Contains("backfill_fold", instructions);
        Assert.Contains("impact_mode", instructions);
        Assert.Contains("file_impacts", instructions);
        Assert.Contains("heuristic file-level dependency hints", instructions);
        // Verify language list comes from ReferenceExtractor / 言語リストがReferenceExtractorから来ることを検証
        foreach (var lang in ReferenceExtractor.GetSupportedLanguages())
        {
            Assert.Contains(lang, instructions);
        }
    }

    [Fact]
    public void Initialize_InstructionsOmitsDisabledTools()
    {
        // BuildInstructions feeds tool-selection guidance to AI clients via `initialize`.
        // Once an operator disables a tool through the gate, the instructions must stop
        // advertising it; otherwise the client follows the guidance and hits a `-32601`
        // every time (#1561).
        // BuildInstructions は initialize 経由で AI クライアントに tool 選択ガイダンスを渡す。
        // gate で無効化された tool を案内し続けると、クライアントが従って毎回 -32601 を踏むので、
        // 無効化されたツールについての文章は出力しない (#1561)。
        var allow = McpToolFilter.Parse("search,definition", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!;
        var response = server.HandleMessage(request)!;
        var instructions = response["result"]!["instructions"]!.GetValue<string>();

        Assert.Contains("'search'", instructions);
        Assert.Contains("'definition'", instructions);
        // Disabled tools must not be mentioned by name. Use single-quote-anchored names so
        // that prose like "graph-supported languages" does not false-positive on "graph".
        // 無効化された tool 名はガイダンスに含めない。"graph-supported languages" のような
        // 一般語で誤検出しないよう、'name' 形式で照合する。
        Assert.DoesNotContain("'index'", instructions);
        Assert.DoesNotContain("'map'", instructions);
        Assert.DoesNotContain("'status'", instructions);
        Assert.DoesNotContain("'batch_query'", instructions);
        Assert.DoesNotContain("'backfill_fold'", instructions);
        Assert.DoesNotContain("'suggest_improvement'", instructions);
        Assert.DoesNotContain("'analyze_symbol'", instructions);
        Assert.DoesNotContain("'outline'", instructions);
        Assert.DoesNotContain("'find_in_file'", instructions);
        Assert.DoesNotContain("'excerpt'", instructions);
        Assert.DoesNotContain("'languages'", instructions);
        Assert.DoesNotContain("'files'", instructions);
        Assert.DoesNotContain("'deps'", instructions);
        Assert.DoesNotContain("'unused_symbols'", instructions);
        Assert.DoesNotContain("'symbol_hotspots'", instructions);
        Assert.DoesNotContain("'impact_analysis'", instructions);
        // The exactName-guidance sentence used to enumerate "symbols/definition/references/
        // callers/callees/analyze_symbol" verbatim. With only 'search' and 'definition'
        // enabled, none of those disabled names should leak into the guidance.
        // exactName 案内に旧実装はツール名を直書きしていたため、無効化されたツール名が漏れて
        // いないかを bare 名前 (single-quote 無し) でも確認する。
        Assert.DoesNotContain("symbols/", instructions);
        Assert.DoesNotContain("references/", instructions);
    }

    [Fact]
    public void Initialize_CapturesClientInfoAsCallerIdentity()
    {
        // The caller identity is read from `clientInfo.name` on `initialize` so the
        // limiter can attribute / throttle per client. Missing `clientInfo` falls back to
        // `"unknown"` so anonymous clients still get a coherent bucket (#1560).
        // `clientInfo.name` を取り込むことで、クライアント単位の計量・スロットルが効く。
        // `clientInfo` が無い場合は `"unknown"` に fallback する（#1560）。
        Assert.Equal("unknown", _server.CurrentCaller);

        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"my-client","version":"1.2.3"}}}""")!);
        Assert.Equal("my-client/1.2.3", _server.CurrentCaller);
    }

    [Fact]
    public void Initialize_UpgradesFromUnknownToNamedCaller()
    {
        // The first initialize() with a named clientInfo upgrades the caller out of the
        // anonymous `"unknown"` bucket, so subsequent calls are throttled per client
        // rather than under a shared anonymous bucket (#1560).
        // 最初の名前付き initialize で `"unknown"` から昇格し、以降は client 単位で計量される。
        Assert.Equal("unknown", _server.CurrentCaller);

        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"named-client"}}}""")!);
        Assert.Equal("named-client", _server.CurrentCaller);
    }

    [Fact]
    public void Initialize_DuplicateKeepsOriginalCallerIdentity_Issue4848()
    {
        // A second initialize is a protocol error and cannot reset the rate-limit identity.
        // 2 回目の initialize は protocol error となり、rate-limit identity を変更できない。
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"first-client"}}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);

        var duplicate = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"second-client"}}}""")!)!;

        Assert.Equal(-32600, duplicate["error"]!["code"]!.GetValue<int>());
        Assert.Equal("duplicate_initialize", duplicate["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Equal("first-client", _server.CurrentCaller);
    }

    [Fact]
    public void BatchQuery_RejectsNestedBatchQuerySlots()
    {
        // batch_query slots that themselves request `batch_query` are rejected before
        // rate-limit token consumption so the per-(tool, caller) bucket cannot be drained
        // by recursive expansion, and the error message names the constraint explicitly
        // instead of bubbling up the generic "Unknown tool" error (#1560 nesting vector).
        // 内側で batch_query を呼ぶスロットは、トークン消費の前に明示的に拒否し、再帰展開で
        // バケットを枯渇させる経路を塞ぐ。エラーメッセージもネスト禁止を明示する（#1560）。
        var request = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[
            {"tool":"batch_query","args":{"queries":[]}}
        ]}}}
        """)!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        var nested = results[0]!;
        Assert.Equal("batch_query cannot be nested inside batch_query.", nested["error"]!.GetValue<string>());
        Assert.Null(nested["error_category"]);
    }

    [Fact]
    public void BatchQuery_PerSlotRateLimited_MarksOnlyOverQuotaSlots()
    {
        // batch_query mitigation: each inner slot also consumes a token from the
        // (inner-tool, caller) bucket, so a misbehaving client cannot bypass the limiter
        // by stuffing 10 inner `search` calls into a single allowed batch (#1560 evidence).
        // Outer `batch_query` and inner `status` have independent buckets keyed by tool;
        // burst=2 lets the outer call through and the first two inner `status` slots, while
        // the third inner slot is throttled and surfaces error_category=rate_limited +
        // retry_after_ms in the per-slot result.
        // batch_query の対策: 内側スロットも (inner-tool, caller) からトークンを消費するため、
        // 内側 search を 10 個詰めて制限を迂回できない。バケットはツール毎に独立しており、
        // burst=2 なら外側 batch_query と 1〜2 個目の内側 status が通り、3 個目はスロット単位で
        // error_category=rate_limited と retry_after_ms を返す。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 0.1, BurstCapacity = 2.0 });

        var request = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[
            {"tool":"status"},
            {"tool":"status"},
            {"tool":"status"}
        ]}}}
        """)!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);

        Assert.Null(results[0]!["error"]);
        Assert.Null(results[1]!["error"]);

        var throttled = results[2]!;
        Assert.NotNull(throttled["error"]);
        Assert.Equal("rate_limited", throttled["error_category"]!.GetValue<string>());
        Assert.True(throttled["retry_after_ms"]!.GetValue<long>() >= 1);

        var metadata = structured["metadata"]!;
        Assert.Equal(2, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(1, metadata["failure_count"]!.GetValue<int>());
    }

    [Fact]
    public void BatchQuery_UniqueUnknownSlotNamesShareBoundedRateLimitBucket_Issue4547()
    {
        InstallRateLimiter(_server, new RateLimiterOptions
        {
            RefillTokensPerSecond = 1.0,
            BurstCapacity = 16.0,
            MaxBucketCount = 4,
        });
        var queries = new JsonArray();
        for (var i = 0; i < 8; i++)
        {
            queries.Add(new JsonObject
            {
                ["tool"] = $"unknown-slot-{i}",
                ["arguments"] = new JsonObject(),
            });
        }
        queries.Add(new JsonObject { ["tool"] = "status" });
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject { ["queries"] = queries },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Equal(9, results.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.False(results[i]!["ok"]!.GetValue<bool>());
            Assert.Equal(McpErrorEnvelope.CategoryToolUnknown, results[i]!["category"]!.GetValue<string>());
        }
        Assert.True(results[8]!["ok"]!.GetValue<bool>());
        Assert.Equal(4, _server.RateLimiter.BucketCount);
    }

    [Fact]
    public void HandleMessage_BatchMixedRequests_ReturnsResponseArray()
    {
        var batch = JsonNode.Parse("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"nope"}]""")!;

        var response = _server.HandleMessage(batch)!.AsArray();

        Assert.Equal(2, response.Count);
        Assert.Equal(1, response[0]!["id"]!.GetValue<int>());
        Assert.NotNull(response[0]!["result"]);
        Assert.Equal(2, response[1]!["id"]!.GetValue<int>());
        Assert.Equal(-32601, response[1]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void HandleMessage_AllNotificationBatch_ReturnsNull()
    {
        var batch = JsonNode.Parse("""[{"jsonrpc":"2.0","method":"notifications/initialized"}]""")!;

        Assert.Null(_server.HandleMessage(batch));
    }

    [Fact]
    public void HandleMessage_EmptyBatch_ReturnsInvalidRequest()
    {
        var response = _server.HandleMessage(JsonNode.Parse("[]")!)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void HandleMessage_NestedBatchItem_ReturnsInvalidRequestInBatchResponse()
    {
        var response = _server.HandleMessage(JsonNode.Parse("""[[{"jsonrpc":"2.0","id":1,"method":"ping"}]]""")!)!.AsArray();

        Assert.Single(response);
        Assert.Equal(-32600, response[0]!["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response[0]!);
    }

    [Fact]
    public void HandleMessage_ScalarBatchItem_ReturnsInvalidRequestWithNullId()
    {
        var response = _server.HandleMessage(JsonNode.Parse("""[1]""")!)!.AsArray();

        Assert.Single(response);
        Assert.Equal(-32600, response[0]!["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response[0]!);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ProcessFrame_InvalidTopLevelRequestWithoutRecoverableId_ReturnsInvalidRequestWithNullId_Issue4538(
        string frame)
    {
        var raw = _server.ProcessFrame(frame);

        Assert.NotNull(raw);
        var response = JsonNode.Parse(raw!)!;
        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_request", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
    }

    private sealed class RootsNegotiationTranscriptTransport : IMcpTransport, IOutOfBandMcpTransport
    {
        private readonly bool _advertiseRoots;
        private readonly object _transcriptGate = new();
        private readonly List<string> _transcript = [];
        private readonly TaskCompletionSource<JsonNode> _rootsRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readyToCompleteInput =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeInput =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        internal RootsNegotiationTranscriptTransport(bool advertiseRoots)
        {
            _advertiseRoots = advertiseRoots;
        }

        public string Name => "roots-transcript";
        public string Endpoint => "memory://roots-transcript";
        internal List<string> OutOfBandFrames { get; } = [];
        internal Task ReadyToCompleteInput => _readyToCompleteInput.Task;

        internal string[] Transcript
        {
            get
            {
                lock (_transcriptGate)
                    return _transcript.ToArray();
            }
        }

        public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            string? frame;
            switch (Interlocked.Increment(ref _readCount))
            {
                case 1:
                    frame = _advertiseRoots
                        ? """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"roots":{"listChanged":true}}}}"""
                        : """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""";
                    break;
                case 2:
                    frame = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
                    break;
                case 3 when _advertiseRoots:
                    var rootsRequest = await _rootsRequest.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    frame = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = rootsRequest["id"]!.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["roots"] = new JsonArray(
                                new JsonObject { ["uri"] = "file:///negotiated-workspace" }),
                        },
                    }.ToJsonString();
                    break;
                case 3:
                case 4:
                    _readyToCompleteInput.TrySetResult();
                    await _completeInput.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    frame = null;
                    break;
                default:
                    frame = null;
                    break;
            }

            if (frame is not null)
                Record("client:" + frame);
            return frame;
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            if (frame is not null)
                Record("server:" + frame);
            return Task.CompletedTask;
        }

        public Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_transcriptGate)
                OutOfBandFrames.Add(frame);
            Record("server-oob:" + frame);
            _rootsRequest.TrySetResult(JsonNode.Parse(frame)!);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void CompleteInput() => _completeInput.TrySetResult();

        private void Record(string entry)
        {
            lock (_transcriptGate)
                _transcript.Add(entry);
        }
    }

    private sealed class GatedEofReadStream(byte[] payload, Task allowEof) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead != 0)
                return bytesRead;

            await allowEof.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class BlockingSecondWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _secondWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        internal Task SecondWriteStarted => _secondWriteStarted.Task;
        internal bool IsDisposed { get; private set; }

        internal void ReleaseSecondWrite() => _releaseSecondWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 2)
            {
                _secondWriteStarted.TrySetResult();
                await _releaseSecondWrite.Task.ConfigureAwait(false);
            }

            await base.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
