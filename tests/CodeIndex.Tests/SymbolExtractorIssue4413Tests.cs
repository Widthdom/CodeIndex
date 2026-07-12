using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_MultilineOutParameterDoesNotCreateOutFunction_Issue4413()
    {
        const string content = """
            public class Parser
            {
                public bool TryParse(string text, out
                    int value)
                {
                    value = 0;
                    return true;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "out");
    }
}

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_CSharp_OutModifierDoesNotCreateTypeReference_Issue4413()
    {
        const string content = """
            public class Parser
            {
                public bool TryParse(string text, out
                    int value) => true;
            }
            """;
        var (_, references) = ExtractSymbolsAndReferences("csharp", content);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "type_reference" && r.SymbolName == "out");
    }
}
