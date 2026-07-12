using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_StaticLambdaDoesNotCreateStaticFunction_Issue4453()
    {
        const string content = """
            public class NameFold
            {
                public string Fold(string value) => string.Create(value.Length, value, static (span, state) => state.AsSpan().CopyTo(span));
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "static");
    }
}
