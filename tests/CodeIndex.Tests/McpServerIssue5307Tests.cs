using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_MultilineSearchOrigins_Issue5307()
    {
        InsertIndexedFile("src/Comment.cs", "csharp", "/*\ninfo.ArgumentList.Add(value);\n*/\n");
        InsertIndexedFile("src/String.cs", "csharp", "var text = @\"\ninfo.ArgumentList.Add(value);\n\";\n");
        InsertIndexedFile("src/Code.cs", "csharp", "var text = $\"{Call(\"x\")}\";\ninfo.ArgumentList.Add(value);\n");
        InsertIndexedFile("src/Unknown.cs", "csharp", "var text = $\"{Call(}\";\ninfo.ArgumentList.Add(value);\n");
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
            Assert.Equal(recipe ? 1 : 4, payload["count"]!.GetValue<int>());
            if (!recipe)
            {
                var rows = payload["results"]!.AsArray();
                foreach (var (path, origin) in new[] { ("src/Comment.cs", "comment"), ("src/String.cs", "string_literal"), ("src/Code.cs", "code"), ("src/Unknown.cs", "unknown") })
                {
                    var row = rows.Single(r => r!["path"]!.GetValue<string>() == path)!;
                    Assert.True(row["matchFacets"] is not null, row.ToJsonString());
                    Assert.Equal(origin, row["matchFacets"]![0]!["origin"]!.GetValue<string>());
                    if (origin == "unknown")
                        Assert.Equal("unbalanced_interpolation", row["matchFacets"]![0]!["originUnavailable"]!["reason"]!.GetValue<string>());
                }
            }
        }
        var writer = new DbWriter(_db.Connection);
        var id = writer.UpsertFile(new FileRecord
        {
            Path = "src/CrossChunk.cs",
            Lang = "csharp",
            Lines = 2,
            Size = 100,
            Modified = DateTime.UtcNow,
            Checksum = "fixture",
        });
        writer.InsertChunks([
            new ChunkRecord { FileId = id, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "var s = $@\"{CrossChunkNeedle()}" },
            new ChunkRecord { FileId = id, ChunkIndex = 1, StartLine = 2, EndLine = 2, Content = "\"; CrossChunkNeedle();" },
        ]);
        foreach (var limit in new[] { 1, 10 })
        {
            var response = _server.HandleMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5321,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "search",
                    ["arguments"] = new JsonObject { ["query"] = "CrossChunkNeedle", ["exactSubstring"] = true, ["limit"] = limit },
                },
            })!;
            var rows = response["result"]!["structuredContent"]!["results"]!.AsArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.All(row!["matchFacets"]!.AsArray(),
                facet => Assert.Equal("code", facet!["origin"]!.GetValue<string>())));
        }
    }
}
