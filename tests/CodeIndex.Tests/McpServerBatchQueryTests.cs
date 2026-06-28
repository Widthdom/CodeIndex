using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_BatchQueryNearResponseLimit_TruncatesDeterministically_Issue3792()
    {
        var queries = new JsonArray();
        for (var i = 0; i < McpServer.MaxBatchQuerySize; i++)
        {
            queries.Add(new JsonObject
            {
                ["slotId"] = $"slot-{i.ToString(CultureInfo.InvariantCulture)}",
                ["tool"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["limit"] = 1,
                    ["format"] = "compact",
                },
            });
        }
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3792,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["maxResponseBytes"] = 5000,
                    ["queries"] = queries,
                },
            },
        };

        var stopwatch = Stopwatch.StartNew();
        var response = _server.HandleMessage(request)!;
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.True(structured["metadata"]!["estimated_response_bytes"]!.GetValue<int>() <= 5000);
        Assert.True(structured["results"]!.AsArray().Count > 0);
        var truncatedQueries = structured["truncated_queries"]!.AsArray();
        Assert.NotEmpty(truncatedQueries);
        var firstTruncatedIndex = truncatedQueries[0]!["request_index"]!.GetValue<int>();
        Assert.Equal(firstTruncatedIndex, structured["cascade_started_at_index"]!.GetValue<int>());
        Assert.True(firstTruncatedIndex > 0);
        Assert.Equal("slot-" + firstTruncatedIndex.ToString(CultureInfo.InvariantCulture), truncatedQueries[0]!["slot_id"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ProjectFilterResolverFailure_ReturnsSlotError_Issue3160()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"search","arguments":{"query":"App","project":"DefinitelyMissingProject3160"}}]}}}""")!;

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["metadata"]!["errors"]!.GetValue<int>());
        var slot = Assert.Single(structured["results"]!.AsArray());
        Assert.False(slot!["ok"]!.GetValue<bool>());
        Assert.Contains("Project filter could not be resolved", slot["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, slot["category"]!.GetValue<string>());
        Assert.Equal("project", slot["parameter"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_DisabledInnerTool_ReturnsSlotError()
    {
        // batch_query stays enabled, but the slot for a denied inner tool must surface a
        // per-slot error instead of executing it. Otherwise CDIDX_MCP_TOOLS_DENY could be
        // bypassed by smuggling the disabled name into a batch slot.
        var deny = McpToolFilter.Parse(null, "symbols");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"symbols","arguments":{"query":"App"}}]}}}""")!;
        var response = server.HandleMessage(request)!;
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();

        Assert.Single(results);
        var slot = results[0]!.AsObject();
        var slotError = slot["error"]!.GetValue<string>();
        Assert.Contains("Tool not enabled", slotError);
        // Carry the JSON-RPC error code on the slot so AI clients can branch on a code
        // instead of substring-matching prose (#1561).
        // AI クライアントが prose を部分一致せず code で分岐できるよう、slot にコードを乗せる (#1561)。
        Assert.Equal(-32601, slot["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_DisabledWriteTool_PrefersGateCodeOverWriteGuard()
    {
        // When a write tool is excluded by the gate AND smuggled into a batch slot, both
        // guards could match. The gate runs first so the slot carries the structured
        // `code: -32601` shape — "this tool is not on offer for this deployment" — instead
        // of the generic write-in-batch prose. Otherwise scoped clients see different
        // error shapes depending on whether a tool happened to be a write tool (#1561).
        // 書き込みツールが gate でも除外され、かつ batch slot に紛れ込んだケース。両 guard が
        // 該当するが、gate を先に走らせて構造化 `code: -32601` を出すことで、scoped クライアントが
        // 「このデプロイでは無効」という意図を一貫した shape で受け取れる (#1561)。
        var allow = McpToolFilter.Parse("batch_query,search", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"index","arguments":{"path":"/tmp/x"}}]}}}""")!;
        var response = server.HandleMessage(request)!;
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();

        Assert.Single(results);
        var slot = results[0]!.AsObject();
        Assert.Equal(-32601, slot["code"]!.GetValue<int>());
        Assert.Contains("Tool not enabled", slot["error"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ExecutesMultipleQueries()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status"},{"tool":"files","arguments":{}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 2 of 2 queries", text);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0]!["request_index"]!.GetValue<int>());
        Assert.True(results[0]!["ok"]!.GetValue<bool>());
        Assert.Equal("status", results[0]!["tool"]!.GetValue<string>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.True(results[1]!["ok"]!.GetValue<bool>());
        Assert.Equal("files", results[1]!["tool"]!.GetValue<string>());
        var metadata = response["result"]!["structuredContent"]!["metadata"]!;
        Assert.Equal(2, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(2, metadata["executed"]!.GetValue<int>());
        Assert.Equal(0, metadata["errors"]!.GetValue<int>());
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["total_count"]!.GetValue<int>());
        Assert.Equal(2, structured["success_count"]!.GetValue<int>());
        Assert.Equal(0, structured["failure_count"]!.GetValue<int>());
        Assert.False(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("none", structured["failure_scope"]!.GetValue<string>());
    }
}
