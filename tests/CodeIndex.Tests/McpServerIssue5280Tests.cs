using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_DepsEvidenceFiltersMatchCliCycleSemantics_Issue5280()
    {
        var writer = new DbWriter(_db.Connection);
        var firstId = InsertDependencyFile(writer, "src/McpFilterA.cs");
        var secondId = InsertDependencyFile(writer, "src/McpFilterB.cs");
        var decoyId = InsertDependencyFile(writer, "src/McpFilterDecoy.cs");
        var decoyTargetId = InsertDependencyFile(writer, "src/McpFilterDecoyTarget.cs");
        InsertDependencySymbols(writer, firstId, ["McpFilterA"]);
        InsertDependencySymbols(writer, secondId, ["McpFilterB"]);
        InsertDependencySymbols(writer, decoyId, ["McpFilterDecoy"]);
        InsertDependencySymbols(writer, decoyTargetId, ["McpFilterDecoyTarget"]);
        InsertDependencyReferences(writer, firstId, ["McpFilterB"]);
        InsertDependencyReferences(writer, secondId, ["McpFilterA"]);
        InsertDependencyReferences(writer, decoyId, ["McpFilterDecoyTarget"]);
        SetMcpDependencyEvidence("src/McpFilterA.cs", "type_reference", "unresolved");
        SetMcpDependencyEvidence("src/McpFilterB.cs", "type_reference", "unresolved");
        SetMcpDependencyEvidence("src/McpFilterDecoy.cs", "call", "resolved");
        writer.SetMeta(
            DbContext.ReferenceIdentityContractVersionMetaKey,
            DbContext.ReferenceIdentityContractVersion.ToString());

        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":5280,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"graphBudget":2,"limit":10,"lang":"csharp","resolutionStates":["unresolved"],"referenceKinds":["type_reference"]}}}""")!;
        var response = _server.HandleMessage(request)!;
        Assert.NotNull(response["result"]!["structuredContent"]);
        var payload = response["result"]!["structuredContent"]!;
        var cycle = Assert.Single(payload["cycles"]!.AsArray())!;
        var filter = payload["query_context"]!["dependency_evidence_filter"]!;

        Assert.Equal(2, cycle["reference_count"]!.GetValue<long>());
        Assert.Equal("unresolved", filter["resolution_states"]![0]!.GetValue<string>());
        Assert.Equal("type_reference", filter["reference_kinds"]![0]!.GetValue<string>());
        Assert.Equal("aggregation_ranking_and_graph_budget", filter["applied_before"]!.GetValue<string>());
        Assert.False(filter["whole_program_completeness_implied"]!.GetValue<bool>());

        var toolsListResponse = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":5281,"method":"tools/list","params":{"format":"full","names":"deps"}}""")!)!;
        var depsTool = Assert.Single(toolsListResponse["result"]!["tools"]!.AsArray())!;
        var properties = depsTool["inputSchema"]!["properties"]!;
        Assert.Equal(64, properties["resolutionStates"]!["maxItems"]!.GetValue<int>());
        Assert.Contains(properties["referenceKinds"]!["items"]!["enum"]!.AsArray(),
            value => value!.GetValue<string>() == "subscribe");
        Assert.True(MatchesSchema(payload, depsTool["outputSchema"]!.AsObject(), depsTool["outputSchema"]!.AsObject()));
    }

    [Fact]
    public void ToolsCall_DepsRejectsUnknownEvidenceFilterValues_Issue5280()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":5282,"method":"tools/call","params":{"name":"deps","arguments":{"resolutionStates":["missing"]}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Unsupported dependency resolution state",
            response["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private void SetMcpDependencyEvidence(string sourcePath, string kind, string resolution)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            UPDATE symbol_references
            SET reference_kind = $kind,
                resolution_state = $resolution,
                resolution_candidate_count = CASE WHEN $resolution = 'unresolved' THEN 0 ELSE 1 END
            WHERE file_id = (SELECT id FROM files WHERE path = $sourcePath)
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$resolution", resolution);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
