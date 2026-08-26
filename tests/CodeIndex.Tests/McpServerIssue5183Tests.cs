using System.Text.Json.Nodes;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ExactCallersAndImpactAnalysis_KeepUnresolvedNamesOutOfConfirmedCounts_Issue5183()
    {
        InsertIndexedFile(
            "src/issue5183/McpCaller.cs",
            "csharp",
            """
            namespace Issue5183;
            public class McpCaller5183
            {
                public void CallMissing5183() => ExternalApi5183.MissingLeaf5183();
            }
            """);

        var callers = CallIssue5183Tool(
            "callers",
            new JsonObject
            {
                ["query"] = "MissingLeaf5183",
                ["lang"] = "csharp",
                ["exact"] = true,
                ["countOnly"] = true,
            });
        Assert.Equal(0, callers["count"]!.GetValue<int>());
        Assert.False(callers["identity_root_available"]!.GetValue<bool>());
        Assert.Equal("no_identity_backed_root", callers["identity_root_unavailable_reason"]!.GetValue<string>());
        Assert.Equal("no_identity_root", callers["graph_evidence_confidence"]!.GetValue<string>());
        Assert.True(callers["degraded"]!.GetValue<bool>());
        Assert.False(callers["authoritative_count"]!.GetValue<bool>());

        var broadCallers = CallIssue5183Tool(
            "callers",
            new JsonObject
            {
                ["query"] = "MissingLeaf5183",
                ["lang"] = "csharp",
                ["countOnly"] = true,
            });
        Assert.Equal(1, broadCallers["count"]!.GetValue<int>());
        Assert.Equal("name_discovery", broadCallers["graph_evidence_confidence"]!.GetValue<string>());
        Assert.Null(broadCallers["identity_root_available"]);

        var impact = CallIssue5183Tool(
            "impact_analysis",
            new JsonObject
            {
                ["query"] = "MissingLeaf5183",
                ["lang"] = "csharp",
                ["countOnly"] = true,
            });
        Assert.Equal(0, impact["confirmed_count"]!.GetValue<int>());
        Assert.True(impact["heuristic"]!.GetValue<bool>());
        Assert.False(impact["identity_root_available"]!.GetValue<bool>());
        Assert.Equal("no_identity_backed_root", impact["identity_root_unavailable_reason"]!.GetValue<string>());
        Assert.True(impact["degraded"]!.GetValue<bool>());
        Assert.False(impact["authoritative_count"]!.GetValue<bool>());
        Assert.Null(impact["total"]);
    }

    private JsonObject CallIssue5183Tool(string name, JsonObject arguments)
    {
        var response = _server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 5183,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments,
            },
        })!;

        Assert.Null(response["error"]);
        return response["result"]!["structuredContent"]!.AsObject();
    }
}
