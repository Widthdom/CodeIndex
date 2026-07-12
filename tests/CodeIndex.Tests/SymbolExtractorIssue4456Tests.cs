using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_MultilineTupleReturnUsesMethodName_Issue4456()
    {
        const string content = """
            public class Builder
            {
                private static (
                    Dictionary<string, int> Counts,
                    HashSet<string> Names)
                    BuildSchemaSummary()
                {
                    return (new(), new());
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BuildSchemaSummary");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "static");
    }
}
