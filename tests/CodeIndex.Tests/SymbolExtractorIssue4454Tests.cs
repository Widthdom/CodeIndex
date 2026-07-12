using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_RawStringContentsDoNotCreateFunctions_Issue4454()
    {
        const string content = """"
            public class Queries
            {
                private const string Sql = """
                    SELECT *
                    FROM symbols
                    WHERE name IN ('A', 'B')
                    """;
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "IN");
        Assert.DoesNotContain(symbols, s => s.Name is "SELECT" or "FROM" or "WHERE");
    }
}
