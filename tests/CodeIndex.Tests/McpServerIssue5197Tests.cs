using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_DepsLargeCycleSupportsBoundedSummaryAndExplicitExpansion_Issue5197()
    {
        const int nodeCount = QueryCommandRunner.DefaultDependencyCycleNodeLimit + 5;
        var writer = new DbWriter(_db.Connection);
        var fileIds = Enumerable.Range(0, nodeCount)
            .Select(index => InsertDependencyFile(writer, $"src/LargeCycle{index:D2}.cs"))
            .ToArray();

        for (var index = 0; index < nodeCount; index++)
        {
            InsertDependencySymbols(writer, fileIds[index], [$"LargeCycle{index:D2}"]);
            InsertDependencyReferences(writer, fileIds[index], [$"LargeCycle{(index + 1) % nodeCount:D2}"]);
        }

        var summaryRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"summaryOnly":true,"limit":1,"lang":"csharp"}}}""")!;
        var summaryResponse = _server.HandleMessage(summaryRequest)!;
        var summary = summaryResponse["result"]!["structuredContent"]!;
        var cycleSummary = Assert.Single(summary["cycle_summaries"]!.AsArray())!;
        var nextStepFlags = summary["next_step_flags"]!.AsArray()
            .Select(flag => flag!.GetValue<string>())
            .ToArray();

        Assert.Null(summary["cycles"]);
        Assert.Equal(nodeCount, cycleSummary["node_count"]!.GetValue<int>());
        Assert.Equal(QueryCommandRunner.DefaultDependencyCycleNodeLimit, cycleSummary["nodes_returned"]!.GetValue<int>());
        Assert.Equal(5, cycleSummary["nodes_omitted_count"]!.GetValue<int>());
        Assert.True(summary["analysis_complete"]!.GetValue<bool>());
        Assert.True(summary["display_truncated"]!.GetValue<bool>());
        Assert.Contains("includeAllCycleNodes=true", nextStepFlags);

        var expandedRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"includeAllCycleNodes":true,"limit":1,"lang":"csharp"}}}""")!;
        var expandedResponse = _server.HandleMessage(expandedRequest)!;
        var expanded = expandedResponse["result"]!["structuredContent"]!;
        var expandedCycle = Assert.Single(expanded["cycles"]!.AsArray())!;

        Assert.Equal(nodeCount, expandedCycle["node_count"]!.GetValue<int>());
        Assert.Equal(nodeCount, expandedCycle["nodes_returned"]!.GetValue<int>());
        Assert.False(expanded["display_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_DepsNoiseSuppressionPreservesResolvedCSharpCalls_Issue5197()
    {
        var writer = new DbWriter(_db.Connection);
        var fallbackAId = InsertDependencyFile(writer, "src/McpFallbackA.cs");
        var fallbackBId = InsertDependencyFile(writer, "src/McpFallbackB.cs");
        var resolvedAId = InsertDependencyFile(writer, "src/McpResolvedA.cs");
        var resolvedBId = InsertDependencyFile(writer, "src/McpResolvedB.cs");
        var resolvedBDecoyId = InsertDependencyFile(writer, "src/McpResolvedBDecoy.cs");
        InsertDependencySymbols(writer, fallbackAId, ["McpFallbackA"]);
        InsertDependencyReferences(writer, fallbackAId, ["McpFallbackB"]);
        InsertDependencySymbols(writer, fallbackBId, ["McpFallbackB"]);
        InsertDependencyReferences(writer, fallbackBId, ["McpFallbackA"]);
        InsertDependencySymbols(writer, resolvedAId, ["McpResolvedA"]);
        InsertDependencyReferences(writer, resolvedAId, ["McpResolvedB"]);
        InsertDependencySymbols(writer, resolvedBId, ["McpResolvedB"]);
        InsertDependencyReferences(writer, resolvedBId, ["McpResolvedA"]);
        InsertDependencySymbols(writer, resolvedBDecoyId, ["McpResolvedB"]);
        InsertDependencyReferences(writer, resolvedBDecoyId, ["McpResolvedA"]);
        SetMcpCycleReferenceResolution("src/McpFallbackA.cs", "src/McpFallbackB.cs", "unresolved");
        SetMcpCycleReferenceResolution("src/McpFallbackB.cs", "src/McpFallbackA.cs", "resolved");
        SetMcpCycleReferenceResolution("src/McpResolvedA.cs", "src/McpResolvedB.cs", "resolved");
        SetMcpCycleReferenceResolution("src/McpResolvedB.cs", "src/McpResolvedA.cs", "resolved");
        SetMcpCycleReferenceResolution("src/McpResolvedBDecoy.cs", "src/McpResolvedA.cs", "resolved");

        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"suppressNoise":true,"limit":10,"lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var payload = response["result"]!["structuredContent"]!;
        var cycle = Assert.Single(payload["cycles"]!.AsArray())!;
        var nodes = cycle["nodes"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        var reason = Assert.Single(payload["symbol_filter"]!["suppression_reasons"]!.AsArray())!;

        Assert.Equal(["src/McpResolvedA.cs", "src/McpResolvedB.cs"], nodes);
        Assert.Equal("csharp_non_authoritative_qualified_call", reason["reason"]!.GetValue<string>());
        Assert.Equal(2, reason["references_removed"]!.GetValue<long>());
        Assert.True(payload["analysis_complete"]!.GetValue<bool>());
    }

    private void SetMcpCycleReferenceResolution(string sourcePath, string targetPath, string resolutionState)
    {
        using var command = _db.Connection.CreateCommand();
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
