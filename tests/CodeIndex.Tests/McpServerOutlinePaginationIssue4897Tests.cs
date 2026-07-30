using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class McpServerOutlinePaginationIssue4897Tests
{
    private const string LargeOutlinePath = "src/LargeOutline.cs";

    [Fact]
    public void Outline_PagesLargeDeepTreesProjectsFieldsAndHonorsByteBoundaries_Issue4897()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_mcp_outline_paging_4897");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, LargeOutlinePath, "csharp", BuildLargeOutlineSource());
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Empty.cs", "csharp", Environment.NewLine);
        using var server = new McpServer(dbPath, "1.0.0-test", dbPathExplicit: true);

        var identities = new List<(string Path, int Line)>();
        string? cursor = null;
        var pageCount = 0;
        do
        {
            var arguments = new JsonObject
            {
                ["path"] = LargeOutlinePath,
                ["limit"] = 37,
            };
            if (cursor != null)
                arguments["cursor"] = cursor;

            var structured = CallOutline(server, arguments);
            pageCount++;
            Assert.Equal(175, structured["total_symbol_count"]!.GetValue<int>());
            Assert.InRange(structured["returned_symbol_count"]!.GetValue<int>(), 1, 37);
            Assert.Equal(identities.Count, structured["cursor_offset"]!.GetValue<int>());
            var firstSymbol = structured["symbols"]![0]!;
            var excerptArgs = structured["next_step_suggestion"]!["args"]!;
            Assert.Equal(firstSymbol["startLine"]!.GetValue<int>(), excerptArgs["startLine"]!.GetValue<int>());
            Assert.Equal(firstSymbol["endLine"]!.GetValue<int>(), excerptArgs["endLine"]!.GetValue<int>());

            foreach (var symbol in structured["symbols"]!.AsArray())
            {
                identities.Add((
                    symbol!["path"]!.GetValue<string>(),
                    symbol["line"]!.GetValue<int>()));
                if (symbol["name"]!.GetValue<string>().StartsWith("Method", StringComparison.Ordinal))
                {
                    Assert.True(symbol["depth"]!.GetValue<int>() >= 3);
                    Assert.Equal("Branch", symbol["containerName"]!.GetValue<string>());
                }
            }

            cursor = structured["next_cursor"]?.GetValue<string>();
            if (cursor != null)
                Assert.StartsWith("page:v1:", cursor, StringComparison.Ordinal);
        }
        while (cursor != null);

        Assert.True(pageCount > 1);
        Assert.Equal(175, identities.Count);
        Assert.Equal(175, identities.Distinct().Count());
        Assert.True(identities.Select(identity => identity.Line).SequenceEqual(
            identities.Select(identity => identity.Line).Order()));
        Assert.Equal(
            Enumerable.Range(0, 172).Select(index => $"Method{index:D3}"),
            identities.Select(identity => identity.Path.Split('.').Last())
                .Where(name => name.StartsWith("Method", StringComparison.Ordinal)));

        var projected = CallOutline(
            server,
            new JsonObject
            {
                ["path"] = LargeOutlinePath,
                ["fields"] = new JsonArray { "name", "depth", "container" },
                ["sort"] = "name",
                ["limit"] = 5,
            });
        Assert.Equal(
            new[] { "name", "depth", "container_kind", "container_name" },
            projected["selected_fields"]!.AsArray().Select(field => field!.GetValue<string>()));
        foreach (var symbol in projected["symbols"]!.AsArray())
        {
            Assert.Equal(
                new[] { "name", "depth", "container_kind", "container_name" },
                symbol!.AsObject().Select(property => property.Key));
        }

        var empty = CallOutline(
            server,
            new JsonObject { ["path"] = "src/Empty.cs", ["limit"] = 10 });
        Assert.Equal(0, empty["total_symbol_count"]!.GetValue<int>());
        Assert.Equal(0, empty["returned_symbol_count"]!.GetValue<int>());
        Assert.Empty(empty["symbols"]!.AsArray());
        Assert.False(empty["has_more"]!.GetValue<bool>());
        Assert.Null(empty["next_cursor"]);
        Assert.Null(empty["next_step_suggestion"]);

        var byteArguments = new JsonObject
        {
            ["path"] = LargeOutlinePath,
            ["fields"] = new JsonArray { "name", "depth", "container" },
            ["limit"] = 200,
            ["maxBytes"] = 2_500,
        };
        var bounded = CallOutline(server, byteArguments);
        var boundedBytes = Encoding.UTF8.GetByteCount(bounded.ToJsonString());
        var boundedCount = bounded["returned_symbol_count"]!.GetValue<int>();
        Assert.InRange(boundedCount, 1, 174);
        Assert.True(boundedBytes <= 2_500);
        Assert.True(bounded["has_more"]!.GetValue<bool>());

        byteArguments["maxBytes"] = boundedBytes;
        var exact = CallOutline(server, byteArguments);
        Assert.Equal(boundedCount, exact["returned_symbol_count"]!.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(exact.ToJsonString()) <= boundedBytes);

        byteArguments["maxBytes"] = boundedBytes - 1;
        var belowExact = CallOutline(server, byteArguments);
        Assert.True(belowExact["returned_symbol_count"]!.GetValue<int>() < boundedCount);
        Assert.True(Encoding.UTF8.GetByteCount(belowExact.ToJsonString()) <= boundedBytes - 1);

        var tooSmallResponse = CallOutlineResponse(
            server,
            new JsonObject
            {
                ["path"] = LargeOutlinePath,
                ["fields"] = "name",
                ["maxBytes"] = 1,
            });
        Assert.True(tooSmallResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains(
            "one complete symbol row",
            tooSmallResponse["result"]!["content"]![0]!["text"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_RejectsCursorAfterIndexGenerationChanges_Issue4897()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_mcp_outline_stale_4897");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/App.cs",
            "csharp",
            "public sealed class App { public void A() { } public void B() { } }");

        string cursor;
        using (var firstServer = new McpServer(dbPath, "1.0.0-test", dbPathExplicit: true))
        {
            var firstPage = CallOutline(
                firstServer,
                new JsonObject { ["path"] = "src/App.cs", ["limit"] = 1 });
            cursor = firstPage["next_cursor"]!.GetValue<string>();
        }

        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/GenerationChange.cs",
            "csharp",
            "public sealed class GenerationChange { }");

        using var secondServer = new McpServer(dbPath, "1.0.0-test", dbPathExplicit: true);
        var response = CallOutlineResponse(
            secondServer,
            new JsonObject
            {
                ["path"] = "src/App.cs",
                ["limit"] = 1,
                ["cursor"] = cursor,
            });

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains(
            "stale",
            response["result"]!["content"]![0]!["text"]!.GetValue<string>(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "restart",
            response["result"]!["content"]![0]!["text"]!.GetValue<string>(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode CallOutline(McpServer server, JsonObject arguments)
        => CallOutlineResponse(server, arguments)["result"]!["structuredContent"]!;

    private static JsonNode CallOutlineResponse(McpServer server, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 4897,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "outline",
                ["arguments"] = arguments.DeepClone(),
            },
        };
        return server.HandleMessage(request)!;
    }

    private static string BuildLargeOutlineSource()
    {
        var methods = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 172).Select(index => $"        public void Method{index:D3}() {{ }}"));
        return $$"""
                 namespace Demo;
                 public sealed class Root
                 {
                     public sealed class Branch
                     {
                 {{methods}}
                     }
                 }
                 """;
    }
}
