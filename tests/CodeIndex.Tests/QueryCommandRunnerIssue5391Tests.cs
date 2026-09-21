using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData("python", "py", "def main():\n    pass\n\nmain()\n")]
    [InlineData("javascript", "js", "function main() { return 1; }\nmain();\n")]
    [InlineData("java", "java", "class OWNER {\n    void main() { main(); }\n}\n")]
    [InlineData("csharp", "cs", "class OWNER {\n    void main() { main(); }\n}\n")]
    public void RunDeps_CyclesDoNotJoinIndependentEntrypoints_Issue5391(string language, string extension, string source)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_cycle_local_identity_5391");
        foreach (var owner in new[] { "Left", "Right" })
            File.WriteAllText(Path.Combine(project.Root, owner + "." + extension), source.Replace("OWNER", owner, StringComparison.Ordinal));
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);
        using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM symbol_references r JOIN symbols s ON s.id = r.target_symbol_id
                WHERE r.symbol_name = 'main' AND r.resolution_state = 'resolved' AND r.file_id = s.file_id
                """;
            Assert.Equal(2L, command.ExecuteScalar());
        }

        var cycles = RunIdentityCycles(dbPath, "--lang", language, "--symbol", "main");
        Assert.Equal(0, cycles.GetProperty("graph_edge_count").GetInt32());
        Assert.Equal(0, cycles.GetProperty("candidate_edge_count").GetInt32());
        Assert.Empty(cycles.GetProperty("cycles").EnumerateArray());
        Assert.True(cycles.GetProperty("analysis_complete").GetBoolean());
        Assert.True(cycles.GetProperty("total_cycle_count_authoritative").GetBoolean());
        if (language is "python" or "csharp")
        {
            var ordinary = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--lang", language, "--symbol", "main"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, ordinary.Result);
            using var document = ParseJsonOutput(ordinary.Stdout);
            Assert.Empty(document.RootElement.GetProperty("edges").EnumerateArray());
        }

        using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
        var mcp = RunIdentityCycleMcp(server, new JsonObject { ["cycles"] = true, ["lang"] = language });
        Assert.Equal(0, mcp["graph_edge_count"]!.GetValue<int>());
        Assert.True(mcp["analysis_complete"]!.GetValue<bool>());
        Assert.True(mcp["total_cycle_count_authoritative"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunDeps_PythonIdentityCyclesPreserveFiltersBudgetsAndCursors_Issue5391(bool aliases)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_cycle_python_identity_5391");
        foreach (var (file, target) in new[] { ("alpha", "beta"), ("beta", "alpha"), ("gamma", "delta"), ("delta", "gamma") })
            File.WriteAllText(Path.Combine(project.Root, file + ".py"), $"from {target} import {target}{(aliases ? " as run_target" : "")}\ndef {file}():\n    pass\n{(aliases ? "run_target" : target)}()\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);

        var all = RunIdentityCycles(dbPath, "--lang", "python");
        Assert.Equal(4, all.GetProperty("graph_edge_count").GetInt32());
        Assert.Equal(2, all.GetProperty("total_cycle_count").GetInt32());
        Assert.True(all.GetProperty("analysis_complete").GetBoolean());
        Assert.True(all.GetProperty("total_cycle_count_authoritative").GetBoolean());
        foreach (var extra in new[]
        {
            Array.Empty<string>(),
            new[] { "--path", "alpha.py", "--path", "beta.py" },
            new[] { "--reverse", "--path", "alpha.py", "--path", "beta.py" },
            new[] { "--exclude-path", "gamma.py", "--exclude-path", "delta.py" },
            new[] { "--symbol", "alpha", "--symbol", "beta" },
            new[] { "--reference-kind", "call" },
        })
        {
            var filtered = RunIdentityCycles(dbPath, ["--lang", "python", .. extra]);
            var ordinary = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--lang", "python", .. extra], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, ordinary.Result);
            using var document = ParseJsonOutput(ordinary.Stdout);
            Assert.Equal(document.RootElement.GetProperty("edges").GetArrayLength(), filtered.GetProperty("graph_edge_count").GetInt32());
        }
        Assert.Equal(0, RunIdentityCycles(dbPath, "--resolution-state", "resolved").GetProperty("graph_edge_count").GetInt32());
        var imported = RunIdentityCycles(dbPath, "--resolution-state", "unavailable");
        Assert.Equal(4, imported.GetProperty("graph_edge_count").GetInt32());
        Assert.All(imported.GetProperty("cycles").EnumerateArray(), cycle =>
            Assert.Equal("unavailable", Assert.Single(cycle.GetProperty("retained_evidence").GetProperty("by_resolution_state").EnumerateArray()).GetProperty("resolution_state").GetString()));
        var bounded = RunIdentityCycles(dbPath, "--graph-budget", "1");
        Assert.False(bounded.GetProperty("analysis_complete").GetBoolean());
        Assert.False(bounded.GetProperty("total_cycle_count_authoritative").GetBoolean());
        var page = RunIdentityCycles(dbPath, "--limit", "1");
        var cursor = page.GetProperty("next_cursor").GetString()!;
        var next = RunIdentityCycles(dbPath, "--limit", "1", "--cursor", cursor);
        Assert.Equal(all.GetProperty("cycles")[1].GetRawText(), Assert.Single(next.GetProperty("cycles").EnumerateArray()).GetRawText());

        using (var server = new McpServer(dbPath, "test", dbPathExplicit: true))
        {
            var mcp = RunIdentityCycleMcp(server, new JsonObject { ["cycles"] = true });
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(all.GetProperty("cycles").GetRawText()), mcp["cycles"]));
            Assert.True(mcp["total_cycle_count_authoritative"]!.GetValue<bool>());
            Assert.False(RunIdentityCycleMcp(server, new JsonObject { ["cycles"] = true, ["graphBudget"] = 1 })["analysis_complete"]!.GetValue<bool>());
        }

        using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
        using (var reader = new DbReader(db))
        {
            Assert.Throws<OperationCanceledException>(() => reader.GetFileDependencyCycleCandidates(
                10, out _, cancellationToken: new CancellationToken(true)));
        }
        var mismatch = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--limit", "1", "--cursor", cursor, "--reference-kind", "call"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, mismatch.Result);
        Assert.Contains("cursor does not match", mismatch.Stderr);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                UPDATE symbol_references SET target_symbol_id = (
                    SELECT id FROM symbols WHERE file_id = symbol_references.file_id AND kind = 'function')
                WHERE file_id = (SELECT id FROM files WHERE path = 'alpha.py')
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var stale = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--limit", "1", "--cursor", cursor], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, stale.Result);
        Assert.Contains("cursor does not match", stale.Stderr);
    }

    [Theory]
    [InlineData("java")]
    [InlineData("javascript")]
    public void RunDeps_CycleIdentityCandidatesAndLegacyEvidenceRemainDistinct_Issue5391(string language)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_cycle_candidates_5391");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "Caller.cs", ["Caller"], ["Target"]);
        InsertFileWithSymbolsAndReferences(dbPath, "First.cs", ["Target"], ["Caller"]);
        InsertFileWithSymbolsAndReferences(dbPath, "Second.cs", ["Target"], ["Caller"]);
        InsertFileWithSymbolsAndReferences(dbPath, "Decoy.cs", ["Target"], ["Caller"]);
        SetDependencyLanguage(dbPath, language);
        SetCycleReferenceResolution(dbPath, "First.cs", "Caller.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "Second.cs", "Caller.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "Decoy.cs", "Caller.cs", "resolved");
        MarkDependencyGraphReady(dbPath);
        SetReferenceIdentityContractVersion(dbPath, DbContext.ReferenceIdentityContractVersion.ToString());

        SetCycleReferenceResolution(dbPath, "Caller.cs", "First.cs", "resolved");
        var resolved = RunIdentityCycles(dbPath);
        Assert.Equal(new[] { "Caller.cs", "First.cs" }, resolved.GetProperty("cycles")[0].GetProperty("nodes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(4, resolved.GetProperty("graph_edge_count").GetInt32());

        SetCycleReferenceResolvedGroupCandidates(dbPath, "Caller.cs", ["First.cs", "Second.cs"]);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = "UPDATE symbol_references SET resolution_state = 'ambiguous' WHERE file_id = (SELECT id FROM files WHERE path = 'Caller.cs')";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var ambiguous = RunIdentityCycles(dbPath);
        var cycle = Assert.Single(ambiguous.GetProperty("cycles").EnumerateArray());
        Assert.Equal(new[] { "Caller.cs", "First.cs", "Second.cs" }, cycle.GetProperty("nodes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(5, ambiguous.GetProperty("graph_edge_count").GetInt32());
        var evidence = cycle.GetProperty("retained_evidence").GetProperty("by_resolution_state").EnumerateArray()
            .ToDictionary(e => e.GetProperty("resolution_state").GetString()!, e => e.GetProperty("reference_count").GetInt32());
        Assert.Equal(2, evidence["ambiguous"]);
        Assert.Equal(2, evidence["resolved"]);
        Assert.Empty(RunIdentityCycles(dbPath, "--resolution-state", "resolved").GetProperty("cycles").EnumerateArray());

        // A malformed resolved row cannot inherit stale candidate IDs or a name match.
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = "UPDATE symbol_references SET resolution_state = 'resolved', target_symbol_id = NULL WHERE file_id = (SELECT id FROM files WHERE path = 'Caller.cs')";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Equal(3, RunIdentityCycles(dbPath).GetProperty("graph_edge_count").GetInt32());
        Assert.Empty(RunIdentityCycles(dbPath).GetProperty("cycles").EnumerateArray());

        // Missing/stale contract stamps must ignore even populated resolved IDs/states.
        foreach (var version in new string?[] { null, "0" })
        {
            SetReferenceIdentityContractVersion(dbPath, version);
            var legacy = RunIdentityCycles(dbPath, "--resolution-state", "unavailable");
            Assert.Equal(6, legacy.GetProperty("graph_edge_count").GetInt32());
            var state = Assert.Single(legacy.GetProperty("cycles")[0].GetProperty("retained_evidence").GetProperty("by_resolution_state").EnumerateArray());
            Assert.Equal("unavailable", state.GetProperty("resolution_state").GetString());
            Assert.Equal(0, RunIdentityCycles(dbPath, "--resolution-state", "resolved").GetProperty("graph_edge_count").GetInt32());
        }
    }

    private JsonElement RunIdentityCycles(string dbPath, params string[] extra)
    {
        var result = CaptureConsole(() => QueryCommandRunner.RunDeps(["--db", dbPath, "--json", "--cycles", .. extra], _jsonOptions));
        Assert.True(result.Result == CommandExitCodes.Success, result.Stdout + result.Stderr);
        using var document = ParseJsonOutput(result.Stdout);
        return document.RootElement.Clone();
    }

    private static JsonNode RunIdentityCycleMcp(McpServer server, JsonObject arguments)
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
}
