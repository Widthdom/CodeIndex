using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunDeps_SqlQualifiedCyclesPreserveSchemasEvidenceFiltersAndCursors_Issue5312(bool qualified)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_sql_cycles_5312");
        File.WriteAllText(Path.Combine(project.Root, "dbo-left.sql"), $"CREATE VIEW dbo.LeftView AS SELECT * FROM {(qualified ? "dbo." : "")}RightView;\n");
        File.WriteAllText(Path.Combine(project.Root, "dbo-right.sql"), $"CREATE VIEW dbo.RightView AS SELECT * FROM {(qualified ? "dbo." : "")}LeftView;\n");
        File.WriteAllText(Path.Combine(project.Root, "sales-left.sql"), "CREATE VIEW [sales].[LeftView] AS SELECT * FROM [sales].[RightView];\n");
        File.WriteAllText(Path.Combine(project.Root, "sales-right.sql"), "CREATE VIEW [sales].[RightView] AS SELECT * FROM [sales].[LeftView];\n");
        File.WriteAllText(Path.Combine(project.Root, "decoy.sql"), "CREATE VIEW audit.LeftView AS SELECT 1;\nCREATE VIEW audit.RightView AS SELECT 2;\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stderr);

        JsonElement Run(params string[] extra)
        {
            var result = CaptureConsole(() => QueryCommandRunner.RunDeps(["--db", dbPath, "--json", .. extra], _jsonOptions));
            Assert.True(result.Result == 0, result.Stderr + result.Stdout);
            using var document = ParseJsonOutput(result.Stdout);
            return document.RootElement.Clone();
        }

        var ordinary = Run("--path", "*.sql");
        // Unqualified view references retain ordinary deps' cross-schema leaf fallback.
        var expectedEdgeCount = qualified ? 4 : 8;
        Assert.Equal(expectedEdgeCount, ordinary.GetProperty("edges").GetArrayLength());
        Assert.All(ordinary.GetProperty("edges").EnumerateArray(), edge => Assert.Equal(1, edge.GetProperty("reference_count").GetInt32()));
        var all = Run("--cycles", "--path", "*.sql");
        Assert.True(all.GetProperty("analysis_complete").GetBoolean());
        Assert.True(all.GetProperty("total_cycle_count_authoritative").GetBoolean());
        Assert.Equal(expectedEdgeCount, all.GetProperty("graph_edge_count").GetInt32());
        Assert.Equal(2, all.GetProperty("total_cycle_count").GetInt32());
        Assert.Equal("file", all.GetProperty("cycle_grouping_mode").GetString());
        var cycles = all.GetProperty("cycles").EnumerateArray().ToArray();
        Assert.Equal(new[] { "dbo-left.sql", "dbo-right.sql" }, cycles[0].GetProperty("nodes").EnumerateArray().Select(node => node.GetString()));
        Assert.Equal(new[] { "sales-left.sql", "sales-right.sql" }, cycles[1].GetProperty("nodes").EnumerateArray().Select(node => node.GetString()));
        Assert.All(cycles, cycle =>
        {
            Assert.Equal(2, cycle.GetProperty("internal_edge_count").GetInt32());
            Assert.Equal(2, cycle.GetProperty("reference_count").GetInt32());
            var evidence = cycle.GetProperty("retained_evidence");
            Assert.True(evidence.GetProperty("classification_complete").GetBoolean());
            Assert.Equal("sql", Assert.Single(evidence.GetProperty("by_source_language").EnumerateArray()).GetProperty("source_language").GetString());
            Assert.Equal(2, evidence.GetProperty("retained_reference_count").GetInt32());
        });

        foreach (var extra in new[]
        {
            new[] { "--path", "dbo-*" },
            new[] { "--reverse", "--path", "dbo-*" },
            new[] { "--exclude-path", "sales-*" },
            new[] { "--symbol", "dbo.LeftView", "--symbol", "dbo.RightView" },
        })
        {
            if (!qualified && extra[0] == "--symbol")
                continue;
            var filtered = Run(["--cycles", .. extra]);
            Assert.Equal(cycles[0].GetProperty("nodes").GetRawText(), Assert.Single(filtered.GetProperty("cycles").EnumerateArray()).GetProperty("nodes").GetRawText());
            Assert.Equal(Run(extra).GetProperty("edges").GetArrayLength(), filtered.GetProperty("graph_edge_count").GetInt32());
        }
        Assert.Equal(0, Run("--cycles", "--resolution-state", "resolved").GetProperty("graph_edge_count").GetInt32());
        Assert.Equal(expectedEdgeCount, Run("--cycles", "--suppress-noise", "--lang", "sql").GetProperty("graph_edge_count").GetInt32());
        var bounded = Run("--cycles", "--graph-budget", "1");
        Assert.False(bounded.GetProperty("analysis_complete").GetBoolean());
        Assert.False(bounded.GetProperty("total_cycle_count_authoritative").GetBoolean());
        var page = Run("--cycles", "--limit", "1");
        var cursor = page.GetProperty("next_cursor").GetString()!;
        var next = Run("--cycles", "--limit", "1", "--cursor", cursor);
        Assert.Equal(cycles[1].GetProperty("nodes").GetRawText(), Assert.Single(next.GetProperty("cycles").EnumerateArray()).GetProperty("nodes").GetRawText());
        Assert.False(next.GetProperty("has_more").GetBoolean());

        using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
        JsonNode Mcp(JsonObject arguments)
        {
            var response = server.HandleMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "deps", ["arguments"] = arguments }
            })!;
            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            return response["result"]!["structuredContent"]!;
        }
        var mcp = Mcp(new JsonObject { ["cycles"] = true, ["path"] = "*.sql" });
        Assert.Equal(expectedEdgeCount, mcp["graph_edge_count"]!.GetValue<int>());
        Assert.Equal(2, mcp["cycles"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(all.GetProperty("cycles").GetRawText()), mcp["cycles"]));
        var mcpFiltered = Mcp(new JsonObject { ["cycles"] = true, ["path"] = "dbo-*", ["reverse"] = true });
        Assert.True(JsonNode.DeepEquals(mcp["cycles"]![0], mcpFiltered["cycles"]![0]));
        Assert.Equal(2, mcpFiltered["graph_edge_count"]!.GetValue<int>());
        Assert.Equal(0, Mcp(new JsonObject { ["cycles"] = true, ["resolutionStates"] = new JsonArray("resolved") })["graph_edge_count"]!.GetValue<int>());
        Assert.False(Mcp(new JsonObject { ["cycles"] = true, ["graphBudget"] = 1 })["analysis_complete"]!.GetValue<bool>());
        var mcpPage = Mcp(new JsonObject { ["cycles"] = true, ["limit"] = 1 });
        var mcpNext = Mcp(new JsonObject { ["cycles"] = true, ["limit"] = 1, ["cursor"] = mcpPage["next_cursor"]!.GetValue<string>() });
        Assert.True(JsonNode.DeepEquals(mcp["cycles"]![1], mcpNext["cycles"]![0]));
    }
}
