using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_SemanticFiltersMatchCliRowsAndCounts_Issue5349()
    {
        InsertIndexedFile("audit5349/src/a.cs", "csharp", "// Needle5349\nvar s = \"Needle5349\"; Needle5349(); Needle5349();\n");
        InsertIndexedFile("audit5349/src/b.cs", "csharp", "Needle5349();\n");
        InsertIndexedFile("audit5349/tests/Fixture.cs", "csharp", "var fixture = \"Needle5349\";\n");
        InsertIndexedFile("audit5349/unknown.txt", "text", "Needle5349\n");
        InsertIndexedFile("audit5349/unknown.cs", "csharp", new string('\n', 4096) + "Needle5349();\n");
        foreach (var (json, cliFilters) in new (string, string[])[]
        {
            ("{\"origin\":\"code\"}", ["--origin", "code"]),
            ("{\"origin\":\"comment\"}", ["--origin", "comment"]),
            ("{\"origin\":\"string_literal\"}", ["--origin", "string_literal"]),
            ("{\"origin\":\"unknown\"}", ["--origin", "unknown"]),
            ("{\"origin\":[\"code\",\"comment\"]}", ["--origin", "code,comment"]),
            ("{\"excludeOrigin\":\"code,comment\"}", ["--exclude-origin", "code,comment"]),
            ("{\"resultKind\":\"identifier\"}", ["--result-kind", "identifier"]),
            ("{\"excludeComments\":true,\"excludeStrings\":true}", ["--exclude-comments", "--exclude-strings"]),
            ("{\"excludeFixtures\":true}", ["--exclude-fixtures"]),
        })
        {
            foreach (var tool in new[] { "search", "find_in_file", "find" })
            {
                var args = JsonNode.Parse(json)!.AsObject();
                args["query"] = "Needle5349";
                args["path"] = "audit5349/";
                args["limit"] = 100;
                var command = tool == "search" ? "search" : "find";
                if (command == "find") args["regex"] = true;
                var payload = Payload5349(Call5349(tool, args));
                string[] cliArgs = [command, "Needle5349", "--path", "audit5349/", "--db", _dbPath,
                    "--json", "--limit", "100", .. command == "find" ? new[] { "--regex" } : Array.Empty<string>(), .. cliFilters];
                var (_, output, _) = CaptureConsole(() => ProgramRunner.Run(cliArgs, JsonOptions, "test"));
                var cliRows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonNode.Parse(line)!).Where(row => row["path"] is JsonValue).ToList();
                var mcpRows = payload["results"]!.AsArray();
                Assert.Equal(cliRows.Select(row => Location5349(row, command)).Order(),
                    mcpRows.Select(row => Location5349(row!, command)).Order());
                args["countOnly"] = true;
                var counted = Payload5349(Call5349(tool, args));
                var (_, countOutput, _) = CaptureConsole(() => ProgramRunner.Run([.. cliArgs, "--count"], JsonOptions, "test"));
                var cliCount = JsonNode.Parse(countOutput)!;
                Assert.Equal(cliCount["count"]!.GetValue<int>(), counted["count"]!.GetValue<int>());
            }
        }
        var unknown = Payload5349(Call5349("find", new JsonObject
            { ["query"] = "Needle5349", ["all"] = true, ["regex"] = true, ["origin"] = "code" }));
        Assert.Equal(3, unknown["count"]!.GetValue<int>());
        Assert.Equal(2, unknown["unknown_origin_matches"]!.GetValue<int>());
        Assert.False(unknown["authoritative_rows"]!.GetValue<bool>());
        Assert.True(unknown["partial_result"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_SemanticGuardCountsAndQueryErrorsMatchSharedContracts_Issue5349()
    {
        InsertIndexedFile("audit5349/guard-a.cs", "csharp", "Guard5349();\nNeedle5349();\nNeedle5349();\n");
        InsertIndexedFile("audit5349/guard-b.cs", "csharp", "Guard5349();\nNeedle5349();\n");
        foreach (var tokenBoundary in new[] { false, true })
        {
            var args = new JsonObject { ["query"] = "Needle5349", ["path"] = "audit5349/",
                ["origin"] = "code", [tokenBoundary ? "tokenBoundary" : "exact"] = true, ["requireBefore"] = "Guard5349",
                ["guardWindow"] = 8, ["countOnly"] = true };
            var counted = Payload5349(Call5349("search", args));
            string[] cliArgs = ["search", "Needle5349", "--path", "audit5349/", "--origin", "code",
                tokenBoundary ? "--token-boundary" : "--exact", "--require-before", "Guard5349", "--guard-window", "8",
                "--count", "--json", "--db", _dbPath];
            var (_, output, _) = CaptureConsole(() => ProgramRunner.Run(cliArgs, JsonOptions, "test"));
            var expected = JsonNode.Parse(output)!["count"]!.GetValue<int>();
            Assert.Equal(expected, counted["count"]!.GetValue<int>());
            Assert.Equal(expected, counted["top_files"]!.AsArray().Sum(file => file!["count"]!.GetValue<int>()));
            if (!tokenBoundary) Assert.Equal(2, expected);
        }

        foreach (var countOnly in new[] { false, true })
        {
            var args = new JsonObject { ["query"] = string.Join(' ', Enumerable.Repeat("a", 129)),
                ["countOnly"] = countOnly };
            var expected = Call5349("search", args)["result"]!;
            args["origin"] = "code";
            var actual = Call5349("search", args)["result"]!;
            Assert.True(actual["isError"]!.GetValue<bool>());
            Assert.Equal("invalid_argument", actual["structuredContent"]!["category"]!.GetValue<string>());
            Assert.Equal(expected["content"]!.ToJsonString(), actual["content"]!.ToJsonString());
        }
    }

    [Fact]
    public void ToolsCall_SemanticRecipeCapsAndUnknownsStayPartialThroughBatch_Issue5349()
    {
        const string cappedPath = "audit5349/capped-auth.cs";
        const string content = "// Authorization\n";
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord { Path = cappedPath, Lang = "csharp",
            Size = content.Length, Lines = 1, Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime });
        writer.InsertChunks(Enumerable.Range(0, DbReader.MaxContextRankingCandidates + 1).Select(index => new ChunkRecord
        {
            FileId = fileId, ChunkIndex = index, StartLine = 1, EndLine = 1, Content = content,
        }).ToList());
        var capped = Payload5349(Call5349("search", new JsonObject
            { ["recipe"] = "auth-token-audit", ["path"] = cappedPath, ["origin"] = "code", ["limit"] = 20 }));
        var authorization = capped["queries"]!.AsArray().Single(child => child!["name"]!.GetValue<string>() == "authorization-header")!;
        Assert.Equal(0, authorization["count"]!.GetValue<int>());
        Assert.False(authorization["candidate_scan_complete"]!.GetValue<bool>());
        AssertPartial5349(authorization);
        AssertPartial5349(capped);

        foreach (var recipeMode in new[] { false, true })
        {
            var guardedArgs = new JsonObject { ["path"] = cappedPath, ["limit"] = 1,
                ["requireBefore"] = "AbsentGuard5349" };
            guardedArgs[recipeMode ? "recipe" : "query"] = recipeMode ? "auth-token-audit" : "Authorization";
            var expectedError = Call5349("search", guardedArgs)["result"]!;
            guardedArgs["origin"] = "code";
            var actualError = Call5349("search", guardedArgs)["result"]!;
            Assert.True(actualError["isError"]!.GetValue<bool>());
            Assert.Equal("invalid_argument", actualError["structuredContent"]!["category"]!.GetValue<string>());
            Assert.Contains("candidate", expectedError["content"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("candidate", actualError["content"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            if (recipeMode)
                Assert.Contains("auth-token-audit", actualError["content"]!.ToJsonString(), StringComparison.Ordinal);
        }

        InsertIndexedFile("audit5349/unknown-recipe.cs", "csharp", new string('\n', 4096) + "info.ArgumentList.Add(value);\n");
        var args = new JsonObject { ["recipe"] = "dogfood-risk-patterns", ["path"] = "audit5349/unknown-recipe.cs",
            ["origin"] = "code", ["limit"] = 20 };
        var unknown = Payload5349(Call5349("search", args));
        var child = unknown["queries"]!.AsArray().Single(query => query!["name"]!.GetValue<string>() == "process-argument-list")!;
        Assert.Equal(0, child["count"]!.GetValue<int>());
        Assert.True(child["candidate_scan_complete"]!.GetValue<bool>());
        Assert.False(child["origin_classification_complete"]!.GetValue<bool>());
        Assert.False(child["truncated"]!.GetValue<bool>());
        AssertPartial5349(child);
        AssertPartial5349(unknown);
        var batch = Payload5349(Call5349("batch_query", new JsonObject { ["queries"] = new JsonArray
            { new JsonObject { ["tool"] = "search", ["arguments"] = args.DeepClone() } } }));
        var batchRecipe = batch["results"]![0]!["result"]!;
        AssertPartial5349(batchRecipe);
        AssertPartial5349(batchRecipe["queries"]!.AsArray().Single(query => query!["name"]!.GetValue<string>() == "process-argument-list")!);
    }

    private static void AssertPartial5349(JsonNode payload)
    {
        Assert.True(payload["partial_result"]!.GetValue<bool>());
        Assert.True(payload["degraded"]!.GetValue<bool>());
        Assert.False(payload["total_count_authoritative"]!.GetValue<bool>());
        Assert.NotNull(payload["recovery_guidance"]);
    }

    [Fact]
    public void ToolsCall_SemanticSearchTerminalCoverageIgnoresPageFullness_Issue5349()
    {
        const string path = "audit5349/complete-pages.cs";
        const int count = 240;
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord { Path = path, Lang = "csharp",
            Size = count * 24, Lines = count, Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime });
        writer.InsertChunks(Enumerable.Range(0, count / 10).Select(index => new ChunkRecord
        {
            FileId = fileId, ChunkIndex = index, StartLine = index * 10 + 1, EndLine = (index + 1) * 10,
            Content = string.Join('\n', Enumerable.Repeat("using System;", 10)),
        }).ToList());
        foreach (var limit in new[] { 1, 100 })
        {
            var args = new JsonObject { ["query"] = "using System", ["path"] = path,
                ["tokenBoundary"] = true, ["origin"] = "comment", ["limit"] = limit };
            var complete = Payload5349(Call5349("search", args));
            Assert.Equal(0, complete["count"]!.GetValue<int>());
            Assert.True(complete["candidate_scan_complete"]!.GetValue<bool>());
            Assert.True(complete["origin_classification_complete"]!.GetValue<bool>());
            Assert.True(complete["total_count_authoritative"]!.GetValue<bool>());
            Assert.False(complete["partial_result"]!.GetValue<bool>());
            Assert.Null(complete["recovery_guidance"]);

            args["origin"] = "code";
            args["countOnly"] = true;
            var counted = Payload5349(Call5349("search", args));
            Assert.Equal(count, counted["count"]!.GetValue<int>());
            Assert.True(counted["authoritative_count"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void ToolsCall_FindResumesScanAndByteCapsWithoutLostZeroWidthMatches_Issue5349()
    {
        InsertIndexedFile("audit5349/a.cs", "csharp", "// Needle5349\nNeedle5349(); Needle5349();\nNeedle5349();\n");
        InsertIndexedFile("audit5349/b.cs", "csharp", "var s = \"Needle5349\"; Needle5349();\nNeedle5349();\n");
        foreach (var query in new[] { "Needle5349", "(?=Needle5349)" })
        {
            var args = new JsonObject { ["query"] = query, ["all"] = true, ["regex"] = true,
                ["origin"] = "code", ["limit"] = 1, ["lineScanLimit"] = 1 };
            var seen = new List<string>();
            string? cursor = null;
            for (var page = 0; page < 100; page++)
            {
                if (cursor is not null) args["cursor"] = cursor;
                var payload = Payload5349(Call5349("find", args));
                if (page > 0) Assert.False(payload["authoritative_rows"]!.GetValue<bool>());
                foreach (var row in payload["results"]!.AsArray())
                {
                    seen.Add(Location5349(row!, "find"));
                    Assert.Equal("code", row!["match_facets"]![0]!["origin"]!.GetValue<string>());
                    if (query[0] == '(') Assert.Equal(0, row["length"]!.GetValue<int>());
                }
                cursor = payload["next_cursor"]?.GetValue<string>();
                Assert.Equal(cursor is not null, payload["has_more"]!.GetValue<bool>());
                if (cursor is null) break;
                args["limit"] = 2;
                args["lineScanLimit"] = 2;
            }
            Assert.Null(cursor);
            Assert.Equal(5, seen.Count);
            Assert.Equal(5, seen.Distinct().Count());
        }

        InsertIndexedFile("audit5349/wide.cs", "csharp", string.Join('\n', Enumerable.Repeat("Needle5349(); " + new string('x', 400), 8)));
        var wideArgs = new JsonObject { ["query"] = "Needle5349", ["path"] = "audit5349/wide.cs", ["regex"] = true,
            ["origin"] = "code", ["limit"] = 8, ["maxBytes"] = 3000 };
        var wideRows = new List<int>();
        string? next = null;
        for (var page = 0; page < 10; page++)
        {
            if (next is not null) wideArgs["cursor"] = next;
            var payload = Payload5349(Call5349("find", wideArgs));
            Assert.True(Encoding.UTF8.GetByteCount(payload.ToJsonString()) <= 3000, payload.ToJsonString());
            wideRows.AddRange(payload["results"]!.AsArray().Select(row => row!["line"]!.GetValue<int>()));
            next = payload["next_cursor"]?.GetValue<string>();
            if (next is null) break;
        }
        Assert.Null(next);
        Assert.Equal(Enumerable.Range(1, 8), wideRows);
        wideArgs.Remove("cursor");
        wideArgs["maxBytes"] = 1;
        var tooSmall = Call5349("find", wideArgs);
        Assert.Contains(CommandErrorCodes.ResponseBudgetTooSmall, tooSmall.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("next_cursor", tooSmall.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCall_SemanticSearchAndFindRejectChangedCursors_Issue5349()
    {
        for (var i = 0; i < 4; i++)
            InsertIndexedFile($"audit5349/{i}.cs", "csharp", "Needle5349();\n");
        foreach (var tool in new[] { "search", "find", "find_in_file" })
        {
            var args = new JsonObject { ["query"] = "Needle5349", ["path"] = "audit5349/", ["origin"] = "code", ["limit"] = 1 };
            if (tool != "search") args["regex"] = true;
            var first = Payload5349(Call5349(tool, args));
            var cursor = first["next_cursor"]!.GetValue<string>();
            var seen = new List<string> { Location5349(first["results"]![0]!, tool == "search" ? "search" : "find") };
            args["cursor"] = cursor;
            var second = Payload5349(Call5349(tool, args));
            seen.Add(Location5349(second["results"]![0]!, tool == "search" ? "search" : "find"));
            Assert.Equal(2, seen.Distinct().Count());
            foreach (var (key, value) in new (string, JsonNode)[]
            {
                ("origin", JsonValue.Create("comment")), ("excludeOrigin", JsonValue.Create("string_literal")),
                ("excludeFixtures", JsonValue.Create(true)), ("path", JsonValue.Create("audit5349/0.cs")),
                ("excludeTests", JsonValue.Create(true)), ("includeGenerated", JsonValue.Create(true)),
            })
            {
                var changed = args.DeepClone().AsObject();
                changed[key] = value;
                Assert.Contains("cursor_", Call5349(tool, changed).ToJsonString(), StringComparison.Ordinal);
            }
            InsertIndexedFile($"audit5349/changed-{tool}.cs", "csharp", "Needle5349();\n");
            Assert.Contains("cursor_stale", Call5349(tool, args).ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolsCall_FindCountCapsAndSemanticRecipeBatchKeepMetadata_Issue5349()
    {
        InsertIndexedFile("audit5349/a.cs", "csharp", "info.ArgumentList.Add(value);\ninfo.ArgumentList.Add(value);\n");
        InsertIndexedFile("audit5349/b.cs", "csharp", "// ArgumentList\n");
        var args = new JsonObject { ["query"] = "ArgumentList", ["all"] = true, ["regex"] = true,
            ["origin"] = "code", ["countOnly"] = true, ["lineScanLimit"] = 1 };
        var count = 0;
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            if (cursor is not null) args["cursor"] = cursor;
            var payload = Payload5349(Call5349("find", args));
            count += payload["count"]!.GetValue<int>();
            if (page > 0) Assert.False(payload["authoritative_count"]!.GetValue<bool>());
            cursor = payload["next_cursor"]?.GetValue<string>();
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(2, count);
        var recipe = Payload5349(Call5349("search", new JsonObject
            { ["recipe"] = "dogfood-risk-patterns", ["path"] = "audit5349/", ["origin"] = "code", ["limit"] = 10 }));
        var child = recipe["queries"]!.AsArray().Single(q => q!["name"]!.GetValue<string>() == "process-argument-list")!;
        Assert.Equal(2, child["count"]!.GetValue<int>());
        Assert.True(child["origin_classification_complete"]!.GetValue<bool>());
        var batch = Call5349("batch_query", new JsonObject { ["queries"] = new JsonArray
        {
            new JsonObject { ["tool"] = "find", ["arguments"] = new JsonObject
                { ["query"] = "ArgumentList", ["all"] = true, ["regex"] = true, ["origin"] = "code", ["lineScanLimit"] = 1 } },
            new JsonObject { ["tool"] = "search", ["arguments"] = new JsonObject
                { ["query"] = "ArgumentList", ["origin"] = "code", ["path"] = "audit5349/" } },
        } });
        Assert.Contains("next_cursor", batch.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("scan_complete", batch.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unknown_argument", batch.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCall_FindDiscoveryAndInvalidArgumentsStaySynchronized_Issue5349()
    {
        var list = _server.HandleMessage(JsonNode.Parse("""{"jsonrpc":"2.0","id":5349,"method":"tools/list","params":{"format":"full","names":["search","find","find_in_file"]}}""")!)!;
        var tools = list["result"]!["tools"]!.AsArray();
        Assert.Equal(3, tools.Count);
        foreach (var tool in tools)
        {
            Assert.NotNull(tool!["inputSchema"]!["properties"]!["origin"]);
            Assert.NotNull(tool["outputSchema"]);
        }
        var scoped = tools.Single(tool => tool!["name"]!.GetValue<string>() == "find_in_file")!;
        Assert.Contains(scoped["inputSchema"]!["required"]!.AsArray(), value => value!.GetValue<string>() == "path");
        var defaults = Payload5349(Call5349("find_in_file", new JsonObject
            { ["query"] = "Read", ["path"] = "src/", ["excludeComments"] = false,
                ["excludeStrings"] = false, ["excludeFixtures"] = false, ["origin"] = new JsonArray() }));
        Assert.Null(defaults["origin_classification_complete"]);
        foreach (var (tool, json) in new[]
        {
            ("find_in_file", "{\"query\":\"Read\"}"),
            ("find", "{\"query\":\"Read\"}"),
            ("find", "{\"query\":\"Read\",\"all\":true,\"path\":\"src/\"}"),
            ("find", "{\"query\":\"Read\",\"path\":\"src/\",\"lineScanLimit\":1}"),
            ("find", "{\"query\":\"Read\",\"all\":true,\"lineScanLimit\":10000001}"),
            ("find", "{\"query\":\"Read\",\"all\":true,\"origin\":\"code\"}"),
            ("find", "{\"query\":\"Read\",\"all\":true,\"regex\":true,\"resultKind\":\"call_site\"}"),
            ("search", "{\"query\":\"Read\",\"origin\":\"invalid\"}"),
            ("search", "{\"query\":\"Read\",\"origin\":[\"code\",123]}"),
            ("search", "{\"query\":\"Read\",\"excludeFixtures\":1}"),
            ("find", "{\"query\":\"(\",\"all\":true,\"regex\":true}"),
            ("find", "{\"query\":\"Read\",\"all\":true,\"cursor\":\"bad\"}"),
        })
        {
            var response = Call5349(tool, JsonNode.Parse(json)!.AsObject());
            Assert.True(response["error"] is not null || response["result"]?["isError"]?.GetValue<bool>() == true, response.ToJsonString());
        }
    }

    [Fact]
    public async Task ToolsCall_FindTimeoutAndRequestCancellationDoNotIssueCursors_Issue5349()
    {
        InsertIndexedFile("audit5349/slow.cs", "csharp", new string('a', 100000) + "!");
        try
        {
            DbReader.FindRegexMatchTimeoutForTesting = TimeSpan.FromMilliseconds(1);
            foreach (var tool in new[] { "find", "find_in_file" })
            {
                var response = Call5349(tool, new JsonObject
                    { ["query"] = "(a+)+$", ["regex"] = true, ["path"] = "audit5349/slow.cs", ["origin"] = "code" });
                Assert.Contains(CommandErrorCodes.RegexMatchTimeout, response.ToJsonString(), StringComparison.Ordinal);
                Assert.DoesNotContain("next_cursor", response.ToJsonString(), StringComparison.Ordinal);
            }
        }
        finally { DbReader.FindRegexMatchTimeoutForTesting = null; }

        InsertIndexedFile("audit5349/cancel.cs", "csharp", "Needle5349();\nNeedle5349();\n");
        using var cancel = new CancellationTokenSource();
        var scanned = 0;
        try
        {
            DbReader.FindLineScannedForTesting = () => { scanned++; cancel.Cancel(); };
            using var server = new McpServer(_dbPath, "test", dbPathExplicit: true);
            var transport = new QueuedFrameTransport(
                """{"jsonrpc":"2.0","id":5349,"method":"tools/call","params":{"name":"find","arguments":{"query":"Needle5349","path":"audit5349/cancel.cs","regex":true,"origin":"code"}}}""");
            await server.RunAsync(transport, cancel.Token).WaitAsync(TestDeterminism.DefaultTimeout);
            Assert.True(scanned > 0);
            Assert.All(transport.WrittenFrames, frame => Assert.DoesNotContain("next_cursor", frame ?? string.Empty, StringComparison.Ordinal));
        }
        finally { DbReader.FindLineScannedForTesting = null; }
        var recovered = Payload5349(Call5349("find", new JsonObject
            { ["query"] = "Needle5349", ["path"] = "audit5349/cancel.cs", ["regex"] = true, ["origin"] = "code" }));
        Assert.Equal(2, recovered["count"]!.GetValue<int>());
    }

    private JsonNode Call5349(string tool, JsonObject args) => _server.HandleMessage(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 5349, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() },
    })!;

    private static JsonNode Payload5349(JsonNode response)
    {
        Assert.Null(response["error"]);
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        return response["result"]!["structuredContent"]!;
    }

    private static string Location5349(JsonNode row, string command)
        => row["path"]!.GetValue<string>() + ":" + (command == "search"
            ? (row["match_lines"] ?? row["matchLines"])?.ToJsonString() : row["line"] + ":" + row["column"] + ":" + row["length"]);
}
