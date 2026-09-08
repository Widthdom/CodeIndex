using System.Text.Json.Nodes;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_RecipeTokenBoundaryDefaultsOverridesAndOrigins_Issue5298()
    {
        InsertIndexedFile("src/Positive.cs", "csharp", "info.@ArgumentList.Add(value);\n");
        InsertIndexedFile("src/Long.cs", "csharp", "TypeArgumentListPattern();\n");
        InsertIndexedFile("src/Mixed.cs", "csharp", "TypeArgumentListPattern(); // ArgumentList\n");
        InsertIndexedFile("src/String.cs", "csharp", "var text = \"ArgumentList\";\n");
        foreach (var (overrides, expected) in new (string, int)[]
        {
            ("{}", 1), ("{\"tokenBoundary\":true}", 1),
            ("{\"exactSubstring\":true}", 3), ("{\"tokenBoundary\":false}", 3),
        })
        {
            var arguments = JsonNode.Parse(overrides)!.AsObject();
            arguments["recipe"] = "dogfood-risk-patterns";
            arguments["limit"] = 100;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5298,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "search", ["arguments"] = arguments },
            };
            var response = _server.HandleMessage(request)!;
            var payload = response["result"]!["structuredContent"]!;
            var query = payload["queries"]!.AsArray().Single(q => q!["name"]!.GetValue<string>() == "process-argument-list")!;
            Assert.Equal(expected, query["count"]!.GetValue<int>());
            Assert.Contains(query["results"]!.AsArray(), row => row!["path"]!.GetValue<string>() == "src/Positive.cs");
            Assert.Equal(expected == 1, query["token_boundary"]!.GetValue<bool>());
            Assert.True(payload["recipe"]!["queries"]!.AsArray().Single(q => q!["name"]!.GetValue<string>() == "process-argument-list")!["token_boundary"]!.GetValue<bool>());
        }
    }
}
