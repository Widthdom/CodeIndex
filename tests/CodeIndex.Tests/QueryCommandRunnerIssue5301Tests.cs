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
    public void RunDeps_TypedFamiliesPreserveMixedFileCyclesMappingsAndMcpParity_Issue5301()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_families_5301");
        for (var i = 0; i < 21; i++)
            File.WriteAllText(Path.Combine(project.Root, $"Part{i:D2}.cs"),
                $"namespace Demo;\npublic partial class Family {{ public static void Step{i}() {{ Step{(i + 1) % 21}(); }} }}\n");
        File.WriteAllText(Path.Combine(project.Root, "Mixed.cs"), """
            namespace Demo;
            public partial class Alpha {
                public static void A() {
                    void Hop() { Beta.B(); }
                    Hop();
                }
                public class Beta { public static void B() { Alpha.A(); } }
            }
            file class Gamma {
                public static void G() {
                    void Hop() { Delta.D(); }
                    Hop();
                }
            }
            public class Delta { public static void D() { Gamma.G(); } }
            """);
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stderr);

        JsonElement Run(params string[] extra)
        {
            var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--cycles", "--suppress-noise", .. extra], _jsonOptions));
            Assert.True(result.Result == 0, result.Stderr);
            using var json = ParseJsonOutput(result.Stdout);
            return json.RootElement.Clone();
        }

        var raw = Run();
        Assert.Equal("file", raw.GetProperty("cycle_grouping_mode").GetString());
        Assert.Equal(21, Assert.Single(raw.GetProperty("cycles").EnumerateArray()).GetProperty("node_count").GetInt32());
        var grouped = Run("--group-partial-types");
        Assert.True(grouped.GetProperty("analysis_complete").GetBoolean());
        Assert.True(grouped.GetProperty("cycle_grouping_applied").GetBoolean());
        Assert.Equal(2, grouped.GetProperty("cycles").GetArrayLength());
        Assert.All(grouped.GetProperty("cycles").EnumerateArray(), cycle => Assert.Equal(2, cycle.GetProperty("node_count").GetInt32()));
        var grouping = grouped.GetProperty("cycle_grouping");
        var family = Assert.Single(grouping.GetProperty("node_mappings").EnumerateArray(),
            mapping => mapping.GetProperty("file_count").GetInt32() == 21);
        Assert.Equal(20, family.GetProperty("files").GetArrayLength());
        Assert.True(family.GetProperty("files_truncated").GetBoolean());
        Assert.Equal(1, family.GetProperty("files_omitted_count").GetInt32());
        Assert.True(grouping.GetProperty("intra_type_reference_count").GetInt64() >= 21);
        Assert.All(grouping.GetProperty("node_mappings").EnumerateArray().Where(mapping => mapping.GetProperty("file_count").GetInt32() == 1),
            mapping => Assert.Equal("Mixed.cs", mapping.GetProperty("files")[0].GetString()));

        var page = Run("--group-partial-types", "--limit", "1");
        var cursor = page.GetProperty("next_cursor").GetString()!;
        Assert.False(string.IsNullOrEmpty(cursor));
        var next = Run("--group-partial-types", "--limit", "1", "--cursor", cursor);
        Assert.Equal(1, next.GetProperty("page_offset").GetInt32());
        Assert.NotEqual(page.GetProperty("cycles")[0].GetProperty("nodes").GetRawText(), next.GetProperty("cycles")[0].GetProperty("nodes").GetRawText());
        Assert.False(Run("--group-partial-types", "--graph-budget", "1").GetProperty("analysis_complete").GetBoolean());
        var changedMode = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--cursor", cursor], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, changedMode.Result);

        using (var server = new McpServer(dbPath, "test", dbPathExplicit: true))
        {
            var response = server.HandleMessage(JsonNode.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"groupPartialTypes":true,"suppressNoise":true}}}
                """)!)!;
            var content = response["result"]!["structuredContent"]!;
            Assert.True(content["cycle_grouping_applied"]!.GetValue<bool>(), response.ToJsonString());
            Assert.Equal(2, content["cycles"]!.AsArray().Count);
            Assert.Equal(grouping.GetProperty("intra_type_reference_count").GetInt64(), content["cycle_grouping"]!["intra_type_reference_count"]!.GetValue<long>());
        }

        SetReferenceIdentityContractVersion(dbPath, null);
        var fallback = Run("--group-partial-types");
        Assert.False(fallback.GetProperty("cycle_grouping_applied").GetBoolean());
        Assert.Equal("raw_file_fallback_metadata_unavailable", fallback.GetProperty("cycle_grouping_reason").GetString());
        Assert.Equal(raw.GetProperty("cycles")[0].GetProperty("nodes").GetRawText(), fallback.GetProperty("cycles")[0].GetProperty("nodes").GetRawText());
        var staleCursor = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--suppress-noise", "--cursor", cursor], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, staleCursor.Result);
    }

    [Fact]
    public void RunDeps_TypedGroupingSeparatesGenericNestedNamespaceAndFileScope_Issue5301()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_identity_5301");
        for (var part = 0; part < 2; part++)
        {
            File.WriteAllText(Path.Combine(project.Root, $"Kinds{part}.cs"), $$"""
                namespace First {
                    public partial class Same { public static void Plain{{part}}() { Plain{{1 - part}}(); } }
                    public partial class Same<T> { public static void Generic{{part}}() { Generic{{1 - part}}(); } }
                    public partial class Outer<T> {
                        public partial class Same { public static void Nested{{part}}() { Nested{{1 - part}}(); } }
                    }
                }
                namespace Second {
                    public partial class Same { public static void Other{{part}}() { Other{{1 - part}}(); } }
                }
                """);
        }
        File.WriteAllText(Path.Combine(project.Root, "a.py"), "def left():\n    right()\n");
        File.WriteAllText(Path.Combine(project.Root, "b.py"), "def right():\n    left()\n");
        File.WriteAllText(Path.Combine(project.Root, "self1.py"), "def self1():\n    self1()\n");
        File.WriteAllText(Path.Combine(project.Root, "self2.py"), "def self2():\n    self2()\n");
        File.WriteAllText(Path.Combine(project.Root, "left.sql"), "CREATE VIEW LeftView AS SELECT * FROM RightView;\n");
        File.WriteAllText(Path.Combine(project.Root, "right.sql"), "CREATE VIEW RightView AS SELECT * FROM LeftView;\n");
        File.WriteAllText(Path.Combine(project.Root, "TopLevel.cs"), "TopTarget.Run();\npublic class TopTarget { public static void Run() { } }\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        Assert.Equal(0, CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions)).Result);
        DowngradeSqlGraphContractVersion(dbPath);
        var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--suppress-noise"], _jsonOptions));
        Assert.True(result.Result == 0, result.Stderr);
        using var document = ParseJsonOutput(result.Stdout);
        var grouping = document.RootElement.GetProperty("cycle_grouping");
        var mappings = grouping.GetProperty("node_mappings").EnumerateArray().ToArray();
        var types = mappings.Where(mapping => mapping.GetProperty("kind").GetString() == "csharp_type").ToArray();
        Assert.Equal(4, types.Length);
        Assert.Equal(4, types.Select(mapping => mapping.GetProperty("family_identity").GetString()).Distinct().Count());
        Assert.All(types, mapping => Assert.Equal(2, mapping.GetProperty("file_count").GetInt32()));
        Assert.False(document.RootElement.GetProperty("sql_graph_contract_ready").GetBoolean());
        var python = Assert.Single(document.RootElement.GetProperty("cycles").EnumerateArray(),
            cycle => cycle.GetProperty("nodes").EnumerateArray().Any(node => node.GetString() == "file:a.py"));
        Assert.Equal(new[] { "file:a.py", "file:b.py" }, python.GetProperty("nodes").EnumerateArray().Select(node => node.GetString()));
        Assert.Contains(document.RootElement.GetProperty("cycles").EnumerateArray(),
            cycle => cycle.GetProperty("nodes").EnumerateArray().Any(node => node.GetString() == "file:left.sql"));
        using (var server = new McpServer(dbPath, "test", dbPathExplicit: true))
        {
            var response = server.HandleMessage(JsonNode.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"groupPartialTypes":true}}}
                """)!)!;
            Assert.False(response["result"]!["structuredContent"]!["sql_graph_contract_ready"]!.GetValue<bool>(), response.ToJsonString());
        }
        var topLevel = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--path", "TopLevel.cs", "--resolution-state", "resolved"], _jsonOptions));
        Assert.True(topLevel.Result == 0, topLevel.Stderr);
        using var topDocument = ParseJsonOutput(topLevel.Stdout);
        Assert.True(topDocument.RootElement.GetProperty("cycle_grouping").GetProperty("inter_node_reference_count").GetInt64() > 0);
        var emptyBudget = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--group-partial-types", "--path", "self*", "--graph-budget", "1"], _jsonOptions));
        Assert.True(emptyBudget.Result == 0, emptyBudget.Stderr);
        using var emptyDocument = ParseJsonOutput(emptyBudget.Stdout);
        Assert.False(emptyDocument.RootElement.GetProperty("analysis_complete").GetBoolean());
        Assert.Equal(0, emptyDocument.RootElement.GetProperty("cycles").GetArrayLength());
    }

    [Fact]
    public void RunDeps_StaleFamilyFallbackKeepsRawSuppressionBudgetSemantics_Issue5301()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_fallback_5301");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "Source.cs", ["Source"], ["Target"]);
        InsertFileWithSymbolsAndReferences(dbPath, "Target1.cs", ["Target"], []);
        InsertFileWithSymbolsAndReferences(dbPath, "Target2.cs", ["Target"], []);
        SetCycleReferenceResolution(dbPath, "Source.cs", "Target1.cs", "unresolved");
        MarkDependencyGraphReady(dbPath);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            var writer = new DbWriter(db.Connection);
            writer.MarkReferenceIdentityContractReady();
            writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), "0");
        }
        foreach (var grouped in new[] { false, true })
        {
            var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--graph-budget", "1", .. grouped ? new[] { "--group-partial-types" } : []], _jsonOptions));
            Assert.True(result.Result == 0, result.Stderr);
            using var document = ParseJsonOutput(result.Stdout);
            Assert.True(document.RootElement.GetProperty("analysis_complete").GetBoolean());
            Assert.False(document.RootElement.GetProperty("cycle_grouping_applied").GetBoolean());
            Assert.Equal(0, document.RootElement.GetProperty("graph_edge_count").GetInt32());
        }
        using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"groupPartialTypes":true,"suppressNoise":true,"graphBudget":1}}}
            """)!)!;
        var content = response["result"]!["structuredContent"]!;
        Assert.True(content["analysis_complete"]!.GetValue<bool>(), response.ToJsonString());
        Assert.False(content["cycle_grouping_applied"]!.GetValue<bool>());
    }
}
