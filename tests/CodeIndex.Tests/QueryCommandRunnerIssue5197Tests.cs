using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunDeps_LargeCycleSeparatesBoundedNodeDisplayFromCompleteAnalysis_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_large_cycle_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        const int nodeCount = QueryCommandRunner.DefaultDependencyCycleNodeLimit + 5;
        for (var i = 0; i < nodeCount; i++)
        {
            InsertFileWithSymbolsAndReferences(
                dbPath,
                $"src/Node{i:D2}.cs",
                [$"Node{i:D2}"],
                [$"Node{(i + 1) % nodeCount:D2}"]);
        }
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--summary-only", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var summary = Assert.Single(json.GetProperty("cycle_summaries").EnumerateArray());
        var largest = json.GetProperty("largest_component");

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.False(json.TryGetProperty("cycles", out _));
        Assert.True(json.GetProperty("analysis_complete").GetBoolean());
        Assert.True(json.GetProperty("display_truncated").GetBoolean());
        Assert.Equal("component_node_limit", json.GetProperty("display_truncation_reason").GetString());
        Assert.Equal("bounded_sample", json.GetProperty("node_materialization_mode").GetString());
        Assert.Equal(nodeCount, summary.GetProperty("node_count").GetInt32());
        Assert.Equal(nodeCount, summary.GetProperty("internal_edge_count").GetInt32());
        Assert.Equal(nodeCount, summary.GetProperty("reference_count").GetInt64());
        Assert.Equal(QueryCommandRunner.DefaultDependencyCycleNodeLimit, summary.GetProperty("nodes_returned").GetInt32());
        Assert.Equal(5, summary.GetProperty("nodes_omitted_count").GetInt32());
        Assert.Equal(nodeCount, largest.GetProperty("node_count").GetInt32());
        Assert.Equal("file", json.GetProperty("cycle_grouping_mode").GetString());
        Assert.False(json.GetProperty("cycle_grouping_applied").GetBoolean());
        Assert.Contains(
            "--all-cycle-nodes",
            json.GetProperty("next_step_flags").EnumerateArray().Select(static value => value.GetString()));

        var (expandedExitCode, expandedStdout, expandedStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--all-cycle-nodes", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));

        using var expandedDocument = ParseJsonOutput(expandedStdout);
        var expandedJson = expandedDocument.RootElement;
        var expandedCycle = Assert.Single(expandedJson.GetProperty("cycles").EnumerateArray());
        Assert.Equal(CommandExitCodes.Success, expandedExitCode);
        Assert.Equal(string.Empty, expandedStderr);
        Assert.False(expandedJson.GetProperty("display_truncated").GetBoolean());
        Assert.True(expandedJson.GetProperty("analysis_complete").GetBoolean());
        Assert.Equal("complete", expandedJson.GetProperty("node_materialization_mode").GetString());
        Assert.Equal(nodeCount, expandedCycle.GetProperty("nodes_returned").GetInt32());
        Assert.False(expandedCycle.GetProperty("nodes_truncated").GetBoolean());
    }

    [Fact]
    public void RunDeps_CSharpNoiseSuppressionRemovesOnlyNonAuthoritativeQualifiedCalls_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_csharp_cycle_noise_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/FallbackA.cs", ["FallbackA"], ["FallbackB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/FallbackB.cs", ["FallbackB"], ["FallbackA"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedA.cs", ["ResolvedA"], ["ResolvedB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedB.cs", ["ResolvedB"], ["ResolvedA"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedBDecoy.cs", ["ResolvedB"], ["ResolvedA"]);
        SetCycleReferenceResolution(dbPath, "src/FallbackA.cs", "src/FallbackB.cs", "unresolved");
        SetCycleReferenceResolution(dbPath, "src/FallbackB.cs", "src/FallbackA.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedA.cs", "src/ResolvedB.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedB.cs", "src/ResolvedA.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedBDecoy.cs", "src/ResolvedA.cs", "resolved");
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--limit", "10", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var cycle = Assert.Single(json.GetProperty("cycles").EnumerateArray());
        var nodes = cycle.GetProperty("nodes").EnumerateArray().Select(static node => node.GetString()).ToArray();
        var resolutionBreakdown = cycle.GetProperty("retained_evidence").GetProperty("by_resolution_state");
        var resolution = Assert.Single(resolutionBreakdown.EnumerateArray());
        var reason = Assert.Single(json.GetProperty("symbol_filter").GetProperty("suppression_reasons").EnumerateArray());

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(["src/ResolvedA.cs", "src/ResolvedB.cs"], nodes);
        Assert.Equal("resolved", resolution.GetProperty("resolution_state").GetString());
        Assert.Equal(2, resolution.GetProperty("reference_count").GetInt64());
        Assert.True(cycle.GetProperty("retained_evidence").GetProperty("classification_complete").GetBoolean());
        Assert.Equal("csharp_non_authoritative_qualified_call", reason.GetProperty("reason").GetString());
        Assert.Equal(2, reason.GetProperty("edges_affected").GetInt32());
        Assert.Equal(2, reason.GetProperty("edges_removed").GetInt32());
        Assert.Equal(2, reason.GetProperty("references_removed").GetInt64());
    }

    private static void SetCycleReferenceResolution(
        string dbPath,
        string sourcePath,
        string targetPath,
        string resolutionState)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbol_references
            SET reference_kind = 'call',
                target_qualifier = 'Receiver',
                resolution_state = $resolutionState,
                resolution_candidate_count = 1,
                target_symbol_id = CASE WHEN $resolutionState = 'resolved' THEN (
                    SELECT s.id
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = $targetPath
                      AND s.name = symbol_references.symbol_name
                    ORDER BY s.id
                    LIMIT 1
                ) ELSE NULL END
            WHERE file_id = (SELECT id FROM files WHERE path = $sourcePath)
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$targetPath", targetPath);
        command.Parameters.AddWithValue("$resolutionState", resolutionState);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
