using System.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_Cpp_SameLineMemberDedupeRemainsNameBasedWithinEachClassLine()
    {
        var content = """
            class First { int run(); void run(int value); int firstOnly(); };
            struct Second { int run(); int secondOnly(); };
            class First { int run(); int laterOnly(); };
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        var members = symbols
            .Where(s => s.Kind == "function" && s.ContainerName is "First" or "Second")
            .Select(s => (s.Line, s.ContainerKind, s.ContainerName, s.Name, s.Signature))
            .ToList();
        Assert.Equal(
            [
                (1, "class", "First", "run", "int run()"),
                (1, "class", "First", "firstOnly", "int firstOnly()"),
                (2, "struct", "Second", "run", "int run()"),
                (2, "struct", "Second", "secondOnly", "int secondOnly()"),
                (3, "class", "First", "run", "int run()"),
                (3, "class", "First", "laterOnly", "int laterOnly()"),
            ],
            members);
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_Cpp_ManySameLineClasses_CompletesWithinPracticalBudget()
    {
        const int classCount = 50_000;
        _ = SymbolExtractor.Extract(0, "cpp", "class Warmup { int run(); int unique(); };");
        var content = string.Join('\n', Enumerable.Range(0, classCount).Select(i =>
            $"class Item{i} {{ int shared(); int unique{i}(); }};"));

        var stopwatch = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "cpp", content);
        stopwatch.Stop();

        Assert.Equal(classCount * 2, symbols.Count(s => s.Kind == "function" && s.ContainerName?.StartsWith("Item", StringComparison.Ordinal) == true));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "shared" && s.ContainerName == "Item0");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == $"unique{classCount - 1}" && s.ContainerName == $"Item{classCount - 1}");
        var runawayBudget = TimeSpan.FromSeconds(10);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Dense C++ same-line class extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }
}
