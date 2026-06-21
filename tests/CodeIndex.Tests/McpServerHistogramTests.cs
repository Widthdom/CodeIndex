using System.Reflection;
using System.Text.Json.Nodes;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class McpServerHistogramTests
{
    [Fact]
    public void BuildTopFileHistogram_OrdersCountsDeterministically_Issue3782()
    {
        using var server = new McpServer(
            Path.Combine(Path.GetTempPath(), $"cdidx_mcp_histogram_{Guid.NewGuid():N}.db"),
            "test");
        var method = typeof(McpServer).GetMethod("BuildTopFileHistogram", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var generic = method!.MakeGenericMethod(typeof(SearchResult));
        var results = new[]
        {
            new SearchResult { Path = "src/b.cs" },
            new SearchResult { Path = "src/a.cs" },
            new SearchResult { Path = "src/b.cs" },
            new SearchResult { Path = "src/c.cs" },
            new SearchResult { Path = "src/c.cs" },
        };

        var histogram = Assert.IsType<JsonArray>(generic.Invoke(server, [results, new Func<SearchResult, string?>(result => result.Path)]));

        Assert.Equal("src/b.cs", histogram[0]!["path"]!.GetValue<string>());
        Assert.Equal(2, histogram[0]!["count"]!.GetValue<int>());
        Assert.Equal("src/c.cs", histogram[1]!["path"]!.GetValue<string>());
        Assert.Equal(2, histogram[1]!["count"]!.GetValue<int>());
        Assert.Equal("src/a.cs", histogram[2]!["path"]!.GetValue<string>());
        Assert.Equal(1, histogram[2]!["count"]!.GetValue<int>());
    }
}
