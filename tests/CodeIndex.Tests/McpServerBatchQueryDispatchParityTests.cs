using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_BatchQuery_SynchronousReadToolsMatchDirectDispatch()
    {
        var calls = BuildSynchronousReadToolCalls();
        Assert.Equal(
            [
                "search:{\"query\":\"Run\",\"lang\":\"csharp\",\"limit\":5}",
                "definition:{\"query\":\"App\",\"exactName\":true}",
                "references:{\"query\":\"Run\",\"kind\":\"call\"}",
                "callers:{\"query\":\"Run\",\"rankBy\":\"weighted\"}",
                "callees:{\"query\":\"App.Run\"}",
                "symbols:{\"query\":\"App\",\"kind\":\"class\"}",
                "files:{\"query\":\"app.cs\",\"lang\":\"csharp\"}",
                "find_in_file:{\"path\":\"src/app.cs\",\"query\":\"Run\",\"before\":1,\"after\":1}",
                "excerpt:{\"path\":\"src/app.cs\",\"startLine\":1,\"endLine\":5}",
                "read_resource:{\"uri\":\"cdidx://file/src/app.cs\",\"startLine\":1,\"endLine\":5}",
                "map:{\"limit\":5,\"excludeTests\":true}",
                "analyze_symbol:{\"query\":\"Run\",\"includeBody\":true}",
                "status:{\"fields\":[\"files\"]}",
                "outline:{\"path\":\"src/app.cs\"}",
                "deps:{\"path\":\"src/\",\"reverse\":false,\"limit\":10}",
                "impact_analysis:{\"query\":\"Run\",\"maxHops\":2,\"withPaths\":true}",
                "languages:{}",
                "validate:{\"kind\":\"line_too_long\"}",
                "unused_symbols:{\"lang\":\"csharp\",\"limit\":10}",
                "symbol_hotspots:{\"lang\":\"csharp\",\"limit\":10}",
                "ping:{}",
            ],
            calls.Select(call => $"{call.Tool}:{call.Arguments.ToJsonString()}"));
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { ["fields"] = new JsonArray { "files" } },
            calls.Single(call => call.Tool == "status").Arguments));

        var clock = new ManualTimeProvider(new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero));
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            dbPathExplicit: false,
            serializeResponse: null,
            authenticator: null,
            toolFilter: null,
            auditLog: null,
            maxConcurrency: McpServer.DefaultMaxConcurrency,
            timeProvider: clock);
        server.OverrideRateLimiterForTests(new RateLimiter(RateLimiterOptions.Disabled, clock.GetUtcNow));

        for (var index = 0; index < calls.Length; index++)
        {
            var (tool, arguments) = calls[index];
            var direct = server.HandleMessage(BuildToolCall(index * 2, tool, arguments.DeepClone()))!;
            var directResult = direct["result"]!;
            Assert.False(directResult["isError"]?.GetValue<bool>() ?? false);

            var batch = server.HandleMessage(BuildSingleSlotBatchCall(index * 2 + 1, tool, arguments.DeepClone()))!;
            var slot = Assert.Single(batch["result"]!["structuredContent"]!["results"]!.AsArray())!;
            Assert.True(slot["ok"]!.GetValue<bool>());
            Assert.Equal(0, slot["request_index"]!.GetValue<int>());
            Assert.Equal(tool, slot["tool"]!.GetValue<string>());
            Assert.Equal(
                directResult["content"]![0]!["text"]!.GetValue<string>(),
                slot["summary"]!.GetValue<string>());
            var directStructured = CloneWithoutInvocationIdentity(directResult["structuredContent"]);
            var batchStructured = CloneWithoutInvocationIdentity(slot["result"]);
            Assert.True(
                JsonNode.DeepEquals(directStructured, batchStructured),
                $"Structured result differed for synchronous tool '{tool}'. Direct: {directResult["structuredContent"]?.ToJsonString()} Batch: {slot["result"]?.ToJsonString()}");
        }
    }

    [Fact]
    public void ToolsCall_BatchQuery_OperationCanceledSlotIsIsolatedAndOrderIsPreserved()
    {
        var previous = McpServer.StatusUpdateCheckForTesting;
        McpServer.StatusUpdateCheckForTesting = (_, _) => throw new OperationCanceledException("characterization cancellation");
        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "batch_query",
                    ["arguments"] = new JsonObject
                    {
                        ["queries"] = new JsonArray
                        {
                            new JsonObject { ["tool"] = "ping" },
                            new JsonObject
                            {
                                ["tool"] = "status",
                                ["arguments"] = new JsonObject { ["updateCheck"] = true },
                            },
                            new JsonObject { ["tool"] = "ping" },
                        },
                    },
                },
            };

            var response = _server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(3, structured["count"]!.GetValue<int>());
            Assert.Equal(3, structured["total_count"]!.GetValue<int>());
            Assert.Equal(2, structured["success_count"]!.GetValue<int>());
            Assert.Equal(1, structured["failure_count"]!.GetValue<int>());
            Assert.True(structured["partial_failure"]!.GetValue<bool>());
            Assert.Equal("isolated", structured["failure_scope"]!.GetValue<string>());
            Assert.Null(structured["cascade_started_at_index"]);

            var metadata = structured["metadata"]!;
            Assert.Equal(3, metadata["submitted"]!.GetValue<int>());
            Assert.Equal(3, metadata["executed"]!.GetValue<int>());
            Assert.Equal(1, metadata["errors"]!.GetValue<int>());

            var slots = structured["results"]!.AsArray();
            Assert.Equal([0, 1, 2], slots.Select(slot => slot!["request_index"]!.GetValue<int>()));
            Assert.Equal(["ping", "status", "ping"], slots.Select(slot => slot!["tool"]!.GetValue<string>()));
            Assert.Equal([true, false, true], slots.Select(slot => slot!["ok"]!.GetValue<bool>()));
            Assert.Contains("Tool 'status' failed", slots[1]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(McpErrorEnvelope.CategoryRequestCancelled, slots[1]!["category"]!.GetValue<string>());
            Assert.True(slots[1]!["retry_safe"]!.GetValue<bool>());
            Assert.NotNull(slots[0]!["result"]!["version"]);
            Assert.NotNull(slots[2]!["result"]!["version"]);
        }
        finally
        {
            McpServer.StatusUpdateCheckForTesting = previous;
        }
    }

    private static (string Tool, JsonObject Arguments)[] BuildSynchronousReadToolCalls()
        =>
        [
            ("search", new JsonObject { ["query"] = "Run", ["lang"] = "csharp", ["limit"] = 5 }),
            ("definition", new JsonObject { ["query"] = "App", ["exactName"] = true }),
            ("references", new JsonObject { ["query"] = "Run", ["kind"] = "call" }),
            ("callers", new JsonObject { ["query"] = "Run", ["rankBy"] = "weighted" }),
            ("callees", new JsonObject { ["query"] = "App.Run" }),
            ("symbols", new JsonObject { ["query"] = "App", ["kind"] = "class" }),
            ("files", new JsonObject { ["query"] = "app.cs", ["lang"] = "csharp" }),
            ("find_in_file", new JsonObject { ["path"] = "src/app.cs", ["query"] = "Run", ["before"] = 1, ["after"] = 1 }),
            ("excerpt", new JsonObject { ["path"] = "src/app.cs", ["startLine"] = 1, ["endLine"] = 5 }),
            ("read_resource", new JsonObject { ["uri"] = "cdidx://file/src/app.cs", ["startLine"] = 1, ["endLine"] = 5 }),
            ("map", new JsonObject { ["limit"] = 5, ["excludeTests"] = true }),
            ("analyze_symbol", new JsonObject { ["query"] = "Run", ["includeBody"] = true }),
            ("status", new JsonObject { ["fields"] = new JsonArray { "files" } }),
            ("outline", new JsonObject { ["path"] = "src/app.cs" }),
            ("deps", new JsonObject { ["path"] = "src/", ["reverse"] = false, ["limit"] = 10 }),
            ("impact_analysis", new JsonObject { ["query"] = "Run", ["maxHops"] = 2, ["withPaths"] = true }),
            ("languages", new JsonObject()),
            ("validate", new JsonObject { ["kind"] = "line_too_long" }),
            ("unused_symbols", new JsonObject { ["lang"] = "csharp", ["limit"] = 10 }),
            ("symbol_hotspots", new JsonObject { ["lang"] = "csharp", ["limit"] = 10 }),
            ("ping", new JsonObject()),
        ];

    private static JsonObject BuildToolCall(int id, string tool, JsonNode arguments)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = arguments,
            },
        };

    private static JsonObject BuildSingleSlotBatchCall(int id, string tool, JsonNode arguments)
        => BuildToolCall(
            id,
            "batch_query",
            new JsonObject
            {
                ["queries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tool"] = tool,
                        ["arguments"] = arguments,
                    },
                },
            });

    private static JsonNode? CloneWithoutInvocationIdentity(JsonNode? node)
    {
        var clone = node?.DeepClone();
        RemoveInvocationIdentity(clone);
        return clone;
    }

    private static void RemoveInvocationIdentity(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("correlation_id");
            obj.Remove("request_id");
            foreach (var child in obj.Select(pair => pair.Value).ToArray())
                RemoveInvocationIdentity(child);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RemoveInvocationIdentity(child);
        }
    }
}
