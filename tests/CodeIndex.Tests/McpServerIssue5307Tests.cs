using System.Text.Json.Nodes;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_MultilineSearchOrigins_Issue5307()
    {
        InsertIndexedFile("src/Comment.cs", "csharp", "/*\ninfo.ArgumentList.Add(value);\n*/\n");
        InsertIndexedFile("src/String.cs", "csharp", "var text = @\"\ninfo.ArgumentList.Add(value);\n\";\n");
        InsertIndexedFile("src/Code.cs", "csharp", "info.ArgumentList.Add(value);\n");
        foreach (var recipe in new[] { false, true })
        {
            var arguments = recipe
                ? new JsonObject { ["recipe"] = "dogfood-risk-patterns", ["limit"] = 100 }
                : new JsonObject { ["query"] = "ArgumentList", ["exactSubstring"] = true };
            var response = _server.HandleMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5307,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "search", ["arguments"] = arguments },
            })!;
            Assert.True(response["result"]?["structuredContent"] is not null, response.ToJsonString());
            var payload = response["result"]!["structuredContent"]!;
            if (recipe)
                payload = payload["queries"]!.AsArray().Single(q => q!["name"]!.GetValue<string>() == "process-argument-list")!;
            Assert.True(payload["count"] is not null, payload.ToJsonString());
            Assert.Equal(recipe ? 1 : 3, payload["count"]!.GetValue<int>());
            if (!recipe)
            {
                var rows = payload["results"]!.AsArray();
                foreach (var (path, origin) in new[] { ("src/Comment.cs", "comment"), ("src/String.cs", "string_literal"), ("src/Code.cs", "code") })
                {
                    var row = rows.Single(r => r!["path"]!.GetValue<string>() == path)!;
                    Assert.True(row["matchFacets"] is not null, row.ToJsonString());
                    Assert.Equal(origin, row["matchFacets"]![0]!["origin"]!.GetValue<string>());
                }
            }
        }
    }
}
