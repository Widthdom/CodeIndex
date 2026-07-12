using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class SymbolExtractorIssue4415Tests
{
    [Fact]
    public void Extract_JsonArrays_PreserveContainerKindsIndicesAndScalarElements_Issue4415()
    {
        const string content = """
            {
              "items": [
                { "name": "first" },
                { "name": "second", "tags": ["red", "blue"] }
              ]
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "json", content);

        AssertSymbol(symbols, "array", "items", null);
        AssertSymbol(symbols, "object", "items[0]", "items");
        AssertSymbol(symbols, "property", "items[0].name", "items[0]");
        AssertSymbol(symbols, "object", "items[1]", "items");
        AssertSymbol(symbols, "property", "items[1].name", "items[1]");
        AssertSymbol(symbols, "array", "items[1].tags", "items[1]");
        AssertSymbol(symbols, "value", "items[1].tags[0]", "items[1].tags");
        AssertSymbol(symbols, "value", "items[1].tags[1]", "items[1].tags");
        Assert.Equal(3, Assert.Single(symbols, symbol => symbol.Name == "items[0]").Line);
        Assert.Equal(4, Assert.Single(symbols, symbol => symbol.Name == "items[1]").Line);
        Assert.Equal(4, Assert.Single(symbols, symbol => symbol.Name == "items[1].tags[0]").Line);
    }

    [Fact]
    public void Extract_JsonArrays_RepeatedEarlierValuesDoNotStealElementLines_Issue4415()
    {
        const string content = """
            {
              "prior": 1,
              "priorObject": { "same": true },
              "items": [
                1,
                { "same": true }
              ]
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "json", content);

        Assert.Equal(5, Assert.Single(symbols, symbol => symbol.Name == "items[0]").Line);
        Assert.Equal(6, Assert.Single(symbols, symbol => symbol.Name == "items[1]").Line);
    }

    private static void AssertSymbol(
        IReadOnlyList<CodeIndex.Models.SymbolRecord> symbols,
        string kind,
        string name,
        string? containerName)
    {
        var symbol = Assert.Single(symbols.Where(candidate => candidate.Kind == kind && candidate.Name == name));
        Assert.Equal(containerName, symbol.ContainerName);
        Assert.Equal(containerName, symbol.ContainerQualifiedName);
    }
}
