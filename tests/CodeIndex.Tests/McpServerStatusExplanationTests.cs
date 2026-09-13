using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_StatusFieldExplanation_MatchesCliWithoutDatabase_Issue5352()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), "cdidx_5352_" + Guid.NewGuid().ToString("N"), "missing.db");
        using var server = new McpServer(missingDb, "1.0.0-test", dbPathExplicit: true);
        var options = ProgramRunner.CreateDefaultJsonOptions();
        var list = server.HandleMessage(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full","names":"status"}}""")!)!;
        var tool = Assert.Single(list["result"]!["tools"]!.AsArray())!;
        Assert.Equal("string", tool["inputSchema"]!["properties"]!["explainField"]!["type"]!.GetValue<string>());
        Assert.Equal(1, tool["inputSchema"]!["properties"]!["maxBytes"]!["minimum"]!.GetValue<int>());
        Assert.Null(tool["inputSchema"]!["properties"]!["explainField"]!["enum"]);
        Assert.Equal(new[] { "freshness", "readiness", "all" }, tool["inputSchema"]!["properties"]!["explain"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        var schema = tool["outputSchema"]!.AsObject();
        foreach (var field in new[] { "index_complete", "db_pragma_settings.busy_timeout_ms", " INDEX_COMPLETE ", "Index generation completeness", "DB_PRAGMA_SETTINGS.BUSY_TIMEOUT_MS" })
        foreach (var mode in new[] { "full", "compact", "bounded" })
        {
            var args = new JsonObject { ["explainField"] = field, ["format"] = mode == "compact" ? "compact" : "full" };
            var cliArgs = new List<string> { "status", "--explain", field, "--json" };
            if (mode == "compact") cliArgs.Add("--compact");
            if (mode == "bounded")
            {
                args["maxBytes"] = 8192;
                cliArgs.AddRange(["--max-json-bytes", "8192"]);
            }
            var response = CallStatusExplanation(server, args);
            Assert.Null(response["error"]);
            Assert.Null(response["result"]!["isError"]);
            var payload = response["result"]!["structuredContent"]!;
            Assert.True(MatchesSchema(payload, schema, schema), payload.ToJsonString());
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(cliArgs.ToArray(), options, "1.0.0-test"));
            Assert.True(exitCode == 0, $"{field}/{mode}: {stdout}{stderr}");
            Assert.Empty(stderr);
            var cli = JsonNode.Parse(stdout)!;
            var row = mode == "full" ? payload : Assert.Single(payload["results"]!.AsArray())!;
            var cliRow = mode == "full" ? cli : Assert.Single(cli["results"]!.AsArray())!;
            foreach (var property in cliRow.AsObject())
                Assert.True(JsonNode.DeepEquals(property.Value, row[property.Key]), $"{field}/{mode}: {property.Key}");
            if (mode != "full")
            {
                Assert.Equal(ProjectionFieldRegistry.GetStatusExplainCompactFields(), row.AsObject().Select(p => p.Key));
                foreach (var property in payload["metadata"]!.AsObject())
                    Assert.True(JsonNode.DeepEquals(property.Value, cli["metadata"]![property.Key]), property.Key);
            }
            if (mode == "bounded") Assert.True(Encoding.UTF8.GetByteCount(payload.ToJsonString()) + 1 <= 8192);
            foreach (var key in new[] { "db_path", "project_root", "elapsed_ms", "indexed_at_head_sha", "result_stable_at", "mcp_session", "sqlite_diagnostics", "version" })
            {
                Assert.Null(payload[key]);
                Assert.Null(payload["metadata"]?[key]);
            }
            Assert.DoesNotContain(missingDb, response.ToJsonString(), StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingDb)));
    }

    [Fact]
    public void ToolsCall_StatusFieldExplanation_UsesSerializerRegistryAndBoundedUnknownErrors_Issue5352()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), "cdidx_5352_" + Guid.NewGuid().ToString("N"), "missing.db");
        using var server = new McpServer(missingDb, "1.0.0-test");
        var options = ProgramRunner.CreateDefaultJsonOptions();
        foreach (var field in QueryCommandRunner.GetStatusSerializableFieldNames(options))
        {
            var response = CallStatusExplanation(server, new JsonObject { ["explainField"] = field });
            Assert.Null(response["result"]!["isError"]);
            Assert.Equal(field, response["result"]!["structuredContent"]!["field"]!.GetValue<string>());
        }
        foreach (var field in new[] { "indexed_follow_symlinks_policy", "db_pragma_settings.nope", "db_pragma_settings..busy_timeout_ms", "a.b.c.d.e", "", new string('x', 241), "/Users/example/.ssh/private\n\u001b[31m" + new string('x', 400) })
        {
            var response = CallStatusExplanation(server, new JsonObject { ["explainField"] = field });
            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var payload = response["result"]!["structuredContent"]!;
            Assert.Equal(CommandErrorCodes.UsageError, payload["error_code"]!.GetValue<string>());
            if (field.Length > 0)
            {
                var (_, stdout, _) = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(["--explain", field, "--json"], options));
                var cli = JsonNode.Parse(stdout)!;
                Assert.Equal(cli["message"]!.GetValue<string>(), payload["message"]!.GetValue<string>());
                Assert.Equal(cli["hint"]!.GetValue<string>(), payload["hint"]!.GetValue<string>());
            }
            Assert.DoesNotContain("/Users/example", response.ToJsonString(), StringComparison.Ordinal);
            Assert.True(payload["message"]!.GetValue<string>().Length < 300);
            Assert.True(payload["hint"]!.GetValue<string>().Length < 6000);
        }
        foreach (var option in new[] { "check", "scopes", "staleAfterSeconds", "explain", "config", "logPath", "updateCheck", "fields" })
        {
            JsonNode value = option switch
            {
                "check" or "config" or "logPath" or "updateCheck" => JsonValue.Create(false)!,
                "staleAfterSeconds" => JsonValue.Create(60)!,
                "scopes" => JsonValue.Create("workspace")!,
                "explain" => JsonValue.Create("all")!,
                _ => JsonValue.Create("field")!,
            };
            var response = CallStatusExplanation(server, new JsonObject { ["explainField"] = "index_complete", [option] = value });
            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        }
        foreach (var args in new[]
        {
            new JsonObject { ["explainField"] = null },
            new JsonObject { ["explainField"] = 42 },
            new JsonObject { ["explainField"] = "index_complete", ["maxBytes"] = 0 },
            new JsonObject { ["explainField"] = "index_complete", ["maxBytes"] = "100" },
            new JsonObject { ["explainField"] = "index_complete", ["format"] = "csv" },
            new JsonObject { ["maxBytes"] = 8192 },
        })
        {
            var response = CallStatusExplanation(server, args);
            if (response["error"] is JsonNode protocolError)
                Assert.Equal(-32602, protocolError["code"]!.GetValue<int>());
            else
                Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingDb)));
    }

    [Fact]
    public void ToolsCall_StatusFieldExplanation_ReportsMeasuredBudgetAndRetry_Issue5352()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", "10485760");
        var missingDb = Path.Combine(Path.GetTempPath(), "cdidx_5352_" + Guid.NewGuid().ToString("N"), "missing.db");
        using var server = new McpServer(missingDb, "1.0.0-test");
        var args = new JsonObject { ["explainField"] = "index_complete", ["maxBytes"] = 100 };
        var response = CallStatusExplanation(server, args);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var error = response["result"]!["structuredContent"]!;
        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error["error_code"]!.GetValue<string>());
        Assert.Equal(100, error["requested_bytes"]!.GetValue<int>());
        Assert.True(error["minimum_required_bytes_known"]!.GetValue<bool>());
        var minimum = error["minimum_required_bytes"]!.GetValue<long>();
        Assert.True(minimum > 100);
        Assert.Equal("increase_max_bytes", error["retry"]!["action"]!.GetValue<string>());
        args["maxBytes"] = (int)minimum;
        var retry = CallStatusExplanation(server, args);
        Assert.Null(retry["result"]!["isError"]);
        Assert.Single(retry["result"]!["structuredContent"]!["results"]!.AsArray());
        Assert.Equal(minimum, Encoding.UTF8.GetByteCount(retry["result"]!["structuredContent"]!.ToJsonString()) + 1);
        args["maxBytes"] = (int)minimum - 1;
        Assert.True(CallStatusExplanation(server, args)["result"]!["isError"]!.GetValue<bool>());
        var frameBytes = Encoding.UTF8.GetByteCount(retry.ToJsonString());
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", frameBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var fullArgs = new JsonObject { ["explainField"] = "index_complete" };
        var reduced = CallStatusExplanation(server, fullArgs);
        Assert.Null(reduced["result"]!["isError"]);
        Assert.Single(reduced["result"]!["structuredContent"]!["results"]!.AsArray());
        Assert.Equal(frameBytes, Encoding.UTF8.GetByteCount(reduced.ToJsonString()));
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", (frameBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var frameError = CallStatusExplanation(server, fullArgs)["result"]!["structuredContent"]!;
        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, frameError["error_code"]!.GetValue<string>());
        Assert.Equal("json_rpc_response", frameError["budget_scope"]!.GetValue<string>());
        Assert.Equal(frameBytes, frameError["minimum_required_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_StatusFieldExplanation_PreservesMeasuredRetry_Issue5352()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), "cdidx_5352_" + Guid.NewGuid().ToString("N"), "missing.db");
        using var server = new McpServer(missingDb, "1.0.0-test");
        var args = new JsonObject { ["explainField"] = "index_complete", ["maxBytes"] = 1 };
        var directError = CallStatusExplanation(server, args)["result"]!["structuredContent"]!;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 5352, ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray(new JsonObject { ["tool"] = "status", ["arguments"] = args }),
                },
            },
        };
        var batchPayload = server.HandleMessage(request)!["result"]!["structuredContent"]!;
        Assert.Equal(1, batchPayload["failure_count"]!.GetValue<int>());
        var error = Assert.Single(batchPayload["results"]!.AsArray())!;
        Assert.False(error["ok"]!.GetValue<bool>());
        foreach (var key in new[] { "error_code", "minimum_required_bytes", "minimum_required_bytes_known", "minimum_required_bytes_uncertain", "minimum_structured_content_bytes", "budget_scope", "retry" })
            Assert.True(JsonNode.DeepEquals(directError[key], error[key]), key);
        Assert.True(error["minimum_response_bytes"]!.GetValue<int>() > 0);
        args["maxBytes"] = (int)error["retry"]!["recommended_bytes"]!.GetValue<long>();
        var retry = server.HandleMessage(request)!["result"]!["structuredContent"]!;
        Assert.Equal(1, retry["success_count"]!.GetValue<int>());
        var result = Assert.Single(retry["results"]!.AsArray())!;
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Single(result["result"]!["results"]!.AsArray());
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingDb)));
    }

    [Fact]
    public void JsonRpcBatch_StatusFieldExplanation_OffersSuccessfulIndividualRetry_Issue5352()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", "8192");
        var missingDb = Path.Combine(Path.GetTempPath(), "cdidx_5352_" + Guid.NewGuid().ToString("N"), "missing.db");
        using var server = new McpServer(missingDb, "1.0.0-test");
        var requests = new JsonArray();
        for (var index = 0; index < 6; index++)
            requests.Add(CreateStatusExplanationRequest(new JsonObject { ["explainField"] = "index_complete" }, index + 1));
        var responses = server.HandleMessage(requests)!.AsArray();
        Assert.Equal(requests.Count, responses.Count);
        Assert.True(Encoding.UTF8.GetByteCount(responses.ToJsonString()) <= 8192);
        foreach (var response in responses)
        {
            Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
            var error = response["result"]!["structuredContent"]!;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error["error_code"]!.GetValue<string>());
            Assert.Equal("json_rpc_batch_item", error["budget_scope"]!.GetValue<string>());
            Assert.Equal("split_batch", error["retry"]!["action"]!.GetValue<string>());
            Assert.Equal("individual", error["retry"]!["request_mode"]!.GetValue<string>());
            Assert.Null(error["retry"]!["recommended_bytes"]);
            Assert.True(error["minimum_required_bytes"]!.GetValue<long>() > error["effective_bytes"]!.GetValue<int>());
            var requiredLimit = error["retry"]!["minimum_response_budget_bytes"]!.GetValue<int>();
            Assert.InRange(requiredLimit, 1, 8192);
            var request = requests.Single(item => JsonNode.DeepEquals(item!["id"], response["id"]))!;
            var retry = server.HandleMessage(request)!;
            Assert.Null(retry["result"]!["isError"]);
            Assert.Equal("index_complete", retry["result"]!["structuredContent"]!["field"]!.GetValue<string>());
            Assert.True(Encoding.UTF8.GetByteCount(retry.ToJsonString()) <= 8192);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingDb)));
    }

    private static JsonNode CallStatusExplanation(McpServer server, JsonObject args)
        => server.HandleMessage(CreateStatusExplanationRequest(args))!;

    private static JsonObject CreateStatusExplanationRequest(JsonObject args, int id = 5352)
        => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "status", ["arguments"] = args.DeepClone() },
        };
}
