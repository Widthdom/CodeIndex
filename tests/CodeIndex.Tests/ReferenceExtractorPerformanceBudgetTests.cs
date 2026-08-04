using System.Diagnostics;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

[CollectionDefinition(ReferenceExtractorPerformanceBudgetCollection.Name, DisableParallelization = true)]
public sealed class ReferenceExtractorPerformanceBudgetCollection
{
    public const string Name = "Reference extractor performance budget";
}

[Collection(ReferenceExtractorPerformanceBudgetCollection.Name)]
public sealed class ReferenceExtractorPerformanceBudgetTests
{
#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_CSharpLargePlainCallFile_CompletesWithinPracticalBudget()
    {
        ReferenceExtractorWarmup.EnsurePerformanceWarmup();

        const int callerCount = 500;
        var builder = new StringBuilder();
        builder.AppendLine("class App {");
        builder.AppendLine("    void Target() { }");
        for (var index = 0; index < callerCount; index++)
            builder.Append("    void Caller").Append(index).AppendLine("() { Target(); }");
        builder.AppendLine("}");
        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var stopwatch = Stopwatch.StartNew();
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        stopwatch.Stop();

        Assert.Contains(references, reference => reference.SymbolName == "Target" && reference.ContainerName == "Caller0");
        Assert.Contains(references, reference => reference.SymbolName == "Target" && reference.ContainerName == $"Caller{callerCount - 1}");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large C# plain call reference extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_CSharpLargeMethodWithManyLocals_CompletesWithinPracticalBudget()
    {
        ReferenceExtractorWarmup.EnsurePerformanceWarmup();

        const int localCount = 1_000;
        var builder = new StringBuilder();
        builder.AppendLine("class Demo");
        builder.AppendLine("{");
        builder.AppendLine("    int Run(int input)");
        builder.AppendLine("    {");
        builder.AppendLine("        var result = input;");
        for (var i = 0; i < localCount; i++)
        {
            builder.Append("        var value").Append(i).Append(" = result + ").Append(i).AppendLine(";");
            builder.Append("        result += value").Append(i).AppendLine(";");
        }
        builder.AppendLine("        return Helper(result);");
        builder.AppendLine("    }");
        builder.AppendLine("    int Helper(int value) => value;");
        builder.AppendLine("}");
        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var stopwatch = Stopwatch.StartNew();
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        stopwatch.Stop();

        Assert.Contains(references, reference => reference.SymbolName == "Helper" && reference.ReferenceKind == "call");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large C# method reference extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }
}
