using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_RepeatedDeclaredContainerNamesKeepQualifiedOwners()
    {
        const string content = """
            namespace First
            {
                class Host
                {
                    class Repeated
                    {
                        void Run() { }
                    }
                }
            }

            namespace Second
            {
                class Host
                {
                    class Repeated
                    {
                        void Run() { }
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var runs = symbols
            .Where(symbol => symbol.Kind == "function" && symbol.Name == "Run")
            .OrderBy(symbol => symbol.StartLine)
            .ToArray();

        Assert.Equal(2, runs.Length);
        Assert.Equal("First.Host.Repeated", runs[0].ContainerQualifiedName);
        Assert.Equal("Second.Host.Repeated", runs[1].ContainerQualifiedName);
    }

    [Fact]
    public void Extract_Solidity_RepeatedDeclaredContainerNamesUseContainingRange()
    {
        const string content = """
            contract Vault {
                function first() external { }
            }

            contract Vault {
                function second() external { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "solidity", content);
        var first = Assert.Single(symbols.Where(symbol => symbol.Name == "first"));
        var second = Assert.Single(symbols.Where(symbol => symbol.Name == "second"));

        Assert.Equal("Vault", first.ContainerQualifiedName);
        Assert.Equal("Vault", second.ContainerQualifiedName);
        Assert.True(first.StartLine < second.StartLine);
    }
}
