using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class SymbolExtractorIssue4458Tests
{
    [Theory]
    [InlineData("events.jsonl")]
    [InlineData("events.ndjson")]
    public void DetectLanguage_JsonLinesExtensionsUseDedicatedLanguage_Issue4458(string path)
    {
        Assert.Equal("jsonl", FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void Extract_JsonLines_PreservesRecordHierarchyAndPhysicalLines_Issue4458()
    {
        const string content = """
            {"result":{"line":3,"highlight":{"line":3}}}
            {"done":true,"line":9}
            """;

        var symbols = SymbolExtractor.Extract(1, "jsonl", content);

        AssertSymbol(symbols, "record", "[0]", 1, null);
        AssertSymbol(symbols, "object", "[0].result", 1, "[0]");
        AssertSymbol(symbols, "property", "[0].result.line", 1, "[0].result");
        AssertSymbol(symbols, "object", "[0].result.highlight", 1, "[0].result");
        AssertSymbol(symbols, "property", "[0].result.highlight.line", 1, "[0].result.highlight");
        AssertSymbol(symbols, "record", "[1]", 2, null);
        AssertSymbol(symbols, "property", "[1].done", 2, "[1]");
        AssertSymbol(symbols, "property", "[1].line", 2, "[1]");
    }

    [Fact]
    public void Extract_JsonLines_MalformedRecordDoesNotFlattenFollowingRecord_Issue4458()
    {
        const string content = """
            {"broken":
            {"ok":{"value":1}}
            """;

        var symbols = SymbolExtractor.Extract(1, "jsonl", content);

        Assert.DoesNotContain(symbols, symbol => symbol.Name.Contains("broken", StringComparison.Ordinal));
        AssertSymbol(symbols, "record", "[1]", 2, null);
        AssertSymbol(symbols, "property", "[1].ok.value", 2, "[1].ok");
    }

    private static void AssertSymbol(
        IReadOnlyList<CodeIndex.Models.SymbolRecord> symbols,
        string kind,
        string name,
        int line,
        string? containerName)
    {
        var symbol = Assert.Single(symbols.Where(candidate => candidate.Kind == kind && candidate.Name == name));
        Assert.Equal(line, symbol.Line);
        Assert.Equal(containerName, symbol.ContainerName);
    }
}
