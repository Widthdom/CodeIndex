using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunDeps_DeclarationNavigationPagesBothDimensionsAndRejectsStaleTokens_Issue5326()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_navigation_5326");
        for (var i = 0; i < 21; i++)
            File.WriteAllText(Path.Combine(project.Root, $"宣言{i:D2}.cs"),
                $"namespace Demo;\npublic partial class Family {{ public static void Step{i}() {{ Step{(i + 1) % 21}(); }} }}\n");
        const int ringCount = QueryCommandRunner.DefaultDependencyCycleNodeLimit + 1;
        File.WriteAllText(Path.Combine(project.Root, "Ring.cs"), "namespace Demo;\n" +
            string.Join("\n", Enumerable.Range(0, ringCount).Select(i =>
                $"public class Ring{i} {{ public static void Run{i}() {{ Ring{(i + 1) % ringCount}.Run{(i + 1) % ringCount}(); }} }}")));
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        Assert.Equal(0, CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions)).Result);

        (JsonElement Json, string Text) Mapping(int expectedExit = 0, params string[] extra)
        {
            var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--node-mappings", .. extra], _jsonOptions));
            Assert.True(result.Result == expectedExit, result.Stdout + result.Stderr);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout), result.Stderr);
            using var json = JsonDocument.Parse(result.Stdout);
            return (json.RootElement.Clone(), result.Stdout);
        }

        var cyclesResult = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--suppress-noise"], _jsonOptions));
        Assert.Equal(0, cyclesResult.Result);
        using var cyclesDoc = ParseJsonOutput(cyclesResult.Stdout);
        var cycles = cyclesDoc.RootElement;
        var group = cycles.GetProperty("cycle_grouping");
        Assert.Equal(40, group.GetProperty("node_mappings").GetArrayLength());
        Assert.True(group.GetProperty("mapping_nodes_truncated").GetBoolean());
        Assert.DoesNotContain("every file path", cycles.GetProperty("largest_component").GetProperty("node_expansion").GetString());
        Assert.Contains("--node-mappings", cycles.GetProperty("largest_component").GetProperty("node_expansion").GetString());

        var first = Mapping();
        var generation = first.Json.GetProperty("node_generation").GetString()!;
        Assert.Equal(group.GetProperty("node_generation").GetString(), generation);
        Assert.Equal(40, first.Json.GetProperty("node_mappings").GetArrayLength());
        Assert.False(first.Json.TryGetProperty("analysis_complete", out _));
        var all = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var page = first.Json;
        for (var iteration = 0; ; iteration++)
        {
            Assert.True(iteration < 10);
            foreach (var mapping in page.GetProperty("node_mappings").EnumerateArray())
                Assert.True(all.TryAdd(mapping.GetProperty("id").GetString()!, mapping.Clone()));
            if (!page.GetProperty("has_more").GetBoolean()) break;
            page = Mapping(0, "--mapping-cursor", page.GetProperty("next_mapping_cursor").GetString()!).Json;
        }
        Assert.Equal(first.Json.GetProperty("total_count").GetInt64(), all.Count);
        Assert.Equal(22 + ringCount + 1, all.Count); // Files, ordinary types, and one partial family.
        Assert.Equal(22, all.Values.Count(value => value.GetProperty("kind").GetString() == "file_scope"));
        var boundedIds = new HashSet<string>(StringComparer.Ordinal);
        string? nodeCursor = null;
        for (var iteration = 0; ; iteration++)
        {
            Assert.True(iteration < 100);
            var smallPage = Mapping(0, ["--max-json-bytes", "2500",
                .. nodeCursor == null ? Array.Empty<string>() : new[] { "--mapping-cursor", nodeCursor }]);
            Assert.True(Encoding.UTF8.GetByteCount(smallPage.Text) <= 2500);
            foreach (var mapping in smallPage.Json.GetProperty("node_mappings").EnumerateArray())
                Assert.True(boundedIds.Add(mapping.GetProperty("id").GetString()!));
            if (!smallPage.Json.GetProperty("has_more").GetBoolean()) break;
            nodeCursor = smallPage.Json.GetProperty("next_mapping_cursor").GetString()!;
        }
        Assert.True(boundedIds.SetEquals(all.Keys));
        var family = Assert.Single(all.Values, value => value.GetProperty("file_count").GetInt32() == 21);
        var node = family.GetProperty("id").GetString()!;
        var fileCursor = family.GetProperty("next_file_cursor").GetString()!;
        var next = Mapping(0, "--cycle-node", node, "--node-generation", generation, "--mapping-cursor", fileCursor).Json;
        Assert.Equal(20, next.GetProperty("page_offset").GetInt32());
        Assert.False(next.GetProperty("has_more").GetBoolean());
        var fileNames = family.GetProperty("files").EnumerateArray().Select(value => value.GetString())
            .Concat(next.GetProperty("node_mappings")[0].GetProperty("files").EnumerateArray().Select(value => value.GetString())).ToArray();
        Assert.Equal(Enumerable.Range(0, 21).Select(i => $"宣言{i:D2}.cs"), fileNames);
        foreach (var mapping in all.Values)
        {
            var resolved = Mapping(0, "--cycle-node", mapping.GetProperty("id").GetString()!, "--node-generation", generation).Json;
            Assert.Equal(mapping.GetProperty("files").GetRawText(), resolved.GetProperty("node_mappings")[0].GetProperty("files").GetRawText());
        }

        var resolver = Mapping(0, "--cycle-node", node, "--node-generation", generation);
        var exactBudget = Encoding.UTF8.GetByteCount(resolver.Text);
        Assert.Equal(resolver.Text, Mapping(0, "--cycle-node", node, "--node-generation", generation,
            "--max-json-bytes", exactBudget.ToString()).Text);
        var bounded = Mapping(0, "--cycle-node", node, "--node-generation", generation,
            "--max-json-bytes", (exactBudget - 1).ToString());
        Assert.True(Encoding.UTF8.GetByteCount(bounded.Text) < exactBudget);
        Assert.True(bounded.Json.GetProperty("byte_limited").GetBoolean());
        var boundedFiles = bounded.Json.GetProperty("node_mappings")[0].GetProperty("files").GetArrayLength();
        Assert.InRange(boundedFiles, 1, 19);
        var boundedNext = Mapping(0, "--cycle-node", node, "--node-generation", generation,
            "--mapping-cursor", bounded.Json.GetProperty("next_mapping_cursor").GetString()!).Json;
        Assert.Equal(boundedFiles, boundedNext.GetProperty("page_offset").GetInt32());
        Assert.Equal(21 - boundedFiles, boundedNext.GetProperty("returned_count").GetInt32());
        var tooSmall = Mapping(CommandExitCodes.UsageError, "--max-json-bytes", "1");
        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, tooSmall.Json.GetProperty("error_code").GetString());
        Assert.False(tooSmall.Json.TryGetProperty("next_mapping_cursor", out _));
        foreach (var invalid in new[]
        {
            new[] { "--cycle-node", node },
            new[] { "--cycle-node", "file:absent.cs", "--node-generation", generation },
            new[] { "--cycle-node", "csharp-type:symbol:999999", "--node-generation", generation },
            new[] { "--cycle-node", "file:Ring.cs", "--node-generation", generation, "--mapping-cursor", fileCursor },
            new[] { "--mapping-cursor", fileCursor },
            new[] { "--mapping-cursor", first.Json.GetProperty("next_mapping_cursor").GetString()! + "x" },
            new[] { "--mapping-cursor", new string('x', 256) },
            new[] { "--node-generation", "wrong" },
        })
            Mapping(CommandExitCodes.UsageError, invalid);
        foreach (var incompatible in new[] { new[] { "--path", "Ring.cs" }, new[] { "--format", "json-graph" }, new[] { "--cursor", fileCursor }, new[] { "--limit", "0" }, new[] { "--json=array" }, new[] { "--json=ndjson" } })
        {
            var rejected = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--node-mappings", .. incompatible], _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, rejected.Result);
        }

        using (var server = new McpServer(dbPath, "test", dbPathExplicit: true))
        {
            JsonNode Call(JsonObject args)
                => server.HandleMessage(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = "deps", ["arguments"] = args }
                })!;
            JsonObject Arguments() => new() { ["cycles"] = true, ["groupPartialTypes"] = true, ["nodeMappings"] = true };
            var response = Call(Arguments());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first.Json.GetRawText()), response["result"]!["structuredContent"]));
            var args = Arguments();
            args["cycleNode"] = node;
            args["nodeGeneration"] = generation;
            args["mappingCursor"] = fileCursor;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(next.GetRawText()), Call(args)["result"]!["structuredContent"]));
            args = Arguments();
            args["cycleNode"] = node;
            args["nodeGeneration"] = generation;
            args["maxBytes"] = exactBudget - 1;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bounded.Json.GetRawText()), Call(args)["result"]!["structuredContent"]));
            args = Arguments();
            args["maxBytes"] = 1;
            Assert.True(Call(args)["result"]!["isError"]!.GetValue<bool>());
            args = Arguments();
            args["mappingCursor"] = "bad";
            Assert.True(Call(args)["result"]!["isError"]!.GetValue<bool>());
            args = Arguments();
            args["nodeMappings"] = false;
            args["cycleNode"] = node;
            Assert.True(Call(args)["result"]!["isError"]!.GetValue<bool>());
        }

        File.AppendAllText(Path.Combine(project.Root, "Ring.cs"), "\n// new generation\n");
        Assert.Equal(0, CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions)).Result);
        Mapping(CommandExitCodes.UsageError, "--cycle-node", node, "--node-generation", generation);
        Mapping(CommandExitCodes.UsageError, "--mapping-cursor", first.Json.GetProperty("next_mapping_cursor").GetString()!);
        SetReferenceIdentityContractVersion(dbPath, null);
        var unavailable = Mapping(CommandExitCodes.UsageError);
        Assert.Contains("metadata", unavailable.Json.GetProperty("message").GetString());
    }
}
