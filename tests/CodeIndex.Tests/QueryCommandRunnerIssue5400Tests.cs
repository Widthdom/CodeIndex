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
    [InlineData("java")]
    [InlineData("javascript")]
    public void RunDeps_SelectedIdentitiesAndFallbackEvidence_Issue5400(string language)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_dependency_identity_5400");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "Caller.cs", ["Caller"], ["Target"]);
        foreach (var file in new[] { "First.cs", "Second.cs", "Decoy.cs" })
            InsertFileWithSymbolsAndReferences(dbPath, file, ["Target"], []);
        SetDependencyLanguage(dbPath, language);
        MarkDependencyGraphReady(dbPath);
        SetReferenceIdentityContractVersion(dbPath, DbContext.ReferenceIdentityContractVersion.ToString());

        // Persist candidates that disagree with the resolved target. Only the
        // target ID is authoritative for resolved evidence, even without its candidate row.
        SetCycleReferenceResolvedGroupCandidates(dbPath, "Caller.cs", ["Second.cs", "Decoy.cs"]);
        SetCycleReferenceResolution(dbPath, "Caller.cs", "First.cs", "resolved");
        AssertTargets("resolved", "First.cs");
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        using (var reader = new DbReader(db))
        {
            var writer = new DbWriter(db.Connection);
            Assert.Single(reader.GetFileDependencies(10, lang: language));
            foreach (var contract in new string?[] { null, "0" })
            {
                SetReferenceIdentityContractVersion(dbPath, contract);
                var fallback = reader.GetFileDependencies(10, lang: language);
                Assert.Equal(3, fallback.Count);
                Assert.All(fallback, edge => Assert.Equal("unavailable", Assert.Single(edge.Evidence!).ResolutionState));
                writer.SetMeta(DbContext.ReferenceIdentityContractVersionMetaKey, DbContext.ReferenceIdentityContractVersion.ToString());
                Assert.Equal("First.cs", Assert.Single(reader.GetFileDependencies(10, lang: language)).TargetPath);
            }
        }
        Assert.Empty(RunOrdinaryIdentityDeps(dbPath, "--reverse", "--path", "Decoy.cs").GetProperty("edges").EnumerateArray());
        Assert.Empty(RunOrdinaryIdentityDeps(dbPath, "--exclude-path", "Caller.cs").GetProperty("edges").EnumerateArray());
        Assert.Empty(RunOrdinaryIdentityDeps(dbPath, "--symbol", "Missing").GetProperty("edges").EnumerateArray());
        Assert.Single(RunOrdinaryIdentityDeps(dbPath, "--symbol", "Target", "--reference-kind", "call", "--limit", "1").GetProperty("edges").EnumerateArray());

        SetCycleReferenceResolvedGroupCandidates(dbPath, "Caller.cs", ["First.cs", "Second.cs"]);
        AssertTargets("resolved_group", "First.cs", "Second.cs");
        SetState("ambiguous");
        AssertTargets("ambiguous", "First.cs", "Second.cs");
        SetState("resolved"); // NULL confirmed ID must not use stale candidates or names.
        Assert.Empty(RunOrdinaryIdentityDeps(dbPath).GetProperty("edges").EnumerateArray());
        SetState("unresolved");
        AssertTargets("unresolved", "Decoy.cs", "First.cs", "Second.cs");
        SetState("future_state");
        AssertTargets("unavailable", "Decoy.cs", "First.cs", "Second.cs");
        SetState(null);
        AssertTargets("unavailable", "Decoy.cs", "First.cs", "Second.cs");
        foreach (var contract in new string?[] { null, "0" })
        {
            SetReferenceIdentityContractVersion(dbPath, contract);
            SetCycleReferenceResolution(dbPath, "Caller.cs", "First.cs", "resolved");
            AssertTargets("unavailable", "Decoy.cs", "First.cs", "Second.cs");
        }

        void SetState(string? state)
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText = "UPDATE symbol_references SET resolution_state = $state, target_symbol_id = NULL";
            command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        void AssertTargets(string state, params string[] targets)
        {
            var all = RunOrdinaryIdentityDeps(dbPath);
            var selected = RunOrdinaryIdentityDeps(dbPath, "--resolution-state", state);
            Assert.Equal(all.GetProperty("edges").GetRawText(), selected.GetProperty("edges").GetRawText());
            var edges = selected.GetProperty("edges").EnumerateArray().ToArray();
            Assert.Equal(targets.Order(StringComparer.Ordinal), edges.Select(e => e.GetProperty("target_path").GetString()).Order(StringComparer.Ordinal));
            Assert.All(edges, edge =>
            {
                Assert.Equal(1, edge.GetProperty("reference_count").GetInt32());
                Assert.Equal(state, Assert.Single(edge.GetProperty("evidence").EnumerateArray()).GetProperty("resolution_state").GetString());
            });
            Assert.Empty(RunOrdinaryIdentityDeps(dbPath, "--resolution-state", state == "resolved" ? "unavailable" : "resolved").GetProperty("edges").EnumerateArray());
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var mcp = RunIdentityCycleMcp(server, new JsonObject { ["resolutionStates"] = new JsonArray(state) });
            AssertOrdinaryDepsMcpParity(selected, mcp);
        }
    }

    [Theory]
    [InlineData("from beta import beta", "beta()", "unavailable")]
    [InlineData("from beta import beta as run_target", "run_target()", "unavailable")]
    [InlineData("import beta as module", "module.beta()", "unresolved")]
    public void RunDeps_PythonBindingEvidenceIsNotDefinitionResolution_Issue5400(string import, string call, string initialState)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_dependency_python_5400");
        File.WriteAllText(Path.Combine(project.Root, "caller.py"), import + "\n" + call + "\n" + call + "\n");
        File.WriteAllText(Path.Combine(project.Root, "beta.py"), "def beta():\n    pass\n");
        File.WriteAllText(Path.Combine(project.Root, "decoy.py"), "def beta():\n    pass\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);

        AssertEvidence(initialState);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            // The same binding may instead carry a confirmed definition identity.
            command.CommandText = """
                UPDATE symbol_references SET resolution_state = 'resolved', target_symbol_id = (
                    SELECT s.id FROM symbols s JOIN files f ON f.id = s.file_id
                    WHERE f.path = 'beta.py' AND s.name = 'beta')
                """;
            Assert.Equal(2, command.ExecuteNonQuery());
        }
        AssertEvidence("resolved");

        void AssertEvidence(string state)
        {
            var all = RunOrdinaryIdentityDeps(dbPath, "--lang", "python");
            var edge = Assert.Single(all.GetProperty("edges").EnumerateArray());
            Assert.Equal("caller.py", edge.GetProperty("source_path").GetString());
            Assert.Equal("beta.py", edge.GetProperty("target_path").GetString());
            Assert.Equal(2, edge.GetProperty("reference_count").GetInt32());
            Assert.Equal(state, Assert.Single(edge.GetProperty("evidence").EnumerateArray()).GetProperty("resolution_state").GetString());
            foreach (var extra in new[]
            {
                new[] { "--resolution-state", state },
                new[] { "--symbol", "beta", "--reference-kind", "call", "--limit", "1" },
                new[] { "--path", "caller.py" },
                new[] { "--reverse", "--path", "beta.py" },
                new[] { "--exclude-path", "decoy.py" }
            })
                Assert.Equal(all.GetProperty("edges").GetRawText(), RunOrdinaryIdentityDeps(dbPath, extra).GetProperty("edges").GetRawText());
            var rejected = state == "resolved" ? "unavailable" : "resolved";
            Assert.Empty(RunOrdinaryIdentityDeps(dbPath, "--resolution-state", rejected).GetProperty("edges").EnumerateArray());
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var mcp = RunIdentityCycleMcp(server, new JsonObject { ["resolutionStates"] = new JsonArray(state) });
            AssertOrdinaryDepsMcpParity(all, mcp);
            Assert.Empty(RunIdentityCycleMcp(server, new JsonObject { ["resolutionStates"] = new JsonArray(rejected) })["edges"]!.AsArray());
        }
    }

    [Fact]
    public void RunDeps_CountsMixedKindCandidatesOncePerReference_Issue5400()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_dependency_candidate_counts_5400");
        File.WriteAllText(Path.Combine(project.Root, "stat.cpp"), "struct stat { int mode; };\nint stat(const char* path, struct stat* result);\n");
        File.WriteAllText(Path.Combine(project.Root, "caller.cpp"), "int main() {\n    stat(\"a\", nullptr);\n}\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);
        using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(DISTINCT target.kind) FROM symbol_references r
                JOIN files source ON source.id = r.file_id
                JOIN symbol_reference_candidates candidate ON candidate.reference_id = r.id
                JOIN symbols target ON target.id = candidate.symbol_id
                WHERE source.path = 'caller.cpp' AND r.symbol_name = 'stat' AND r.resolution_state = 'resolved_group'
                """;
            Assert.Equal(2L, command.ExecuteScalar());
        }
        foreach (var state in new[] { "resolved_group", "ambiguous" })
        {
            if (state == "ambiguous")
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                using var command = db.Connection.CreateCommand();
                command.CommandText = "UPDATE symbol_references SET resolution_state = 'ambiguous' WHERE file_id = (SELECT id FROM files WHERE path = 'caller.cpp') AND symbol_name = 'stat'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var result = RunOrdinaryIdentityDeps(dbPath, "--resolution-state", state, "--limit", "1");
            var edge = Assert.Single(result.GetProperty("edges").EnumerateArray());
            Assert.Equal("stat.cpp", edge.GetProperty("target_path").GetString());
            Assert.Equal(1, edge.GetProperty("reference_count").GetInt32());
            Assert.Equal(1, edge.GetProperty("ranking_score").GetDouble());
            var evidence = Assert.Single(edge.GetProperty("evidence").EnumerateArray());
            Assert.Equal(1, evidence.GetProperty("reference_count").GetInt32());
            Assert.Equal("symbol", evidence.GetProperty("target_kind").GetString());
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            AssertOrdinaryDepsMcpParity(result, RunIdentityCycleMcp(server,
                new JsonObject { ["resolutionStates"] = new JsonArray(state), ["limit"] = 1 }));
        }
    }

    private void AssertOrdinaryDepsMcpParity(JsonElement cli, JsonNode mcp)
    {
        // MCP serializes the same model with camelCase rather than CLI snake_case.
        var edges = mcp["edges"]!.Deserialize<FileDependencyResult[]>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(cli.GetProperty("edges").GetRawText()),
            JsonSerializer.SerializeToNode(edges, _jsonOptions)), mcp.ToJsonString());
    }

    private JsonElement RunOrdinaryIdentityDeps(string dbPath, params string[] extra)
    {
        var result = CaptureConsole(() => QueryCommandRunner.RunDeps(["--db", dbPath, "--json", .. extra], _jsonOptions));
        Assert.True(result.Result == CommandExitCodes.Success, result.Stdout + result.Stderr);
        using var document = ParseJsonOutput(result.Stdout);
        return document.RootElement.Clone();
    }
}
