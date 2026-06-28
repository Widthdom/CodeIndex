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

    [Fact]
    public void Initialize_ReturnsProtocolVersion()
    {
        // Issue #1554: negotiation echoes back the client's requested protocolVersion when
        // it is in the server's supported set, instead of hardcoding the server's preferred
        // version. The legacy `2024-11-05` client is still supported, so the response should
        // mirror what the client asked for.
        // Issue #1554: 交渉ロジックはサーバー対応集合にあるクライアント要求バージョンを
        // そのまま返すようにした。レガシー `2024-11-05` クライアントは引き続きサポートする
        // ため、レスポンスは要求された値と一致する。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"test"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal("2024-11-05", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(ConsoleUi.LoadVersion(), response["result"]!["serverInfo"]!["version"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_AdvertisesResourcesAndPrompts()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var capabilities = response["result"]!["capabilities"]!;
        Assert.False(capabilities["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["prompts"]!["listChanged"]!.GetValue<bool>());
        Assert.NotNull(capabilities["logging"]);
        Assert.True(capabilities["roots"]!["listChanged"]!.GetValue<bool>());
        Assert.NotNull(capabilities["sampling"]);
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
    public void Initialize_RequestedCurrentProtocolVersion_EchoesBack()
    {
        // Issue #1554: when the client pins the current preferred version, the server
        // must echo it (the client and server agree, no fallback needed).
        // Issue #1554: クライアントが現行の優先バージョンを指定した場合、サーバーは
        // そのまま返す（合意済みなので fallback 不要）。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
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

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
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
        Assert.Contains("2025-03-26", error["message"]!.GetValue<string>());

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

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
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
        Assert.Equal("2025-03-26", root.GetProperty("result").GetProperty("protocolVersion").GetString());
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
        Assert.Contains("resources/read", instructions);
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
    public void Initialize_NamedCallerIsSticky_RejectsReIdentifySwap()
    {
        // Once a named caller has been captured, re-initialize() under a *different* name
        // is ignored so a networked MCP session cannot reset its rate-limit bucket simply
        // by re-initializing under a fresh identity. The retained name continues to key
        // all subsequent (tool, caller) buckets (#1560 DoS vector).
        // 名前付き caller の取得後は、別名での再 initialize() を無視し、レート制限バケットを
        // リセットする経路を塞ぐ（#1560 DoS）。
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"first-client"}}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);

        // Re-init under a different name is ignored / 別名は無視
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"second-client"}}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);

        // Re-init with empty clientInfo (resolves to "unknown") also cannot downgrade /
        // 空の clientInfo（"unknown" に解決）でも降格しない。
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"initialize","params":{}}""")!);
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
}
