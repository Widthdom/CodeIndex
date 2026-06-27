using System.Runtime.CompilerServices;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

internal static class ReferenceExtractorWarmup
{
    [ModuleInitializer]
    internal static void WarmUp()
    {
        // Practical budget tests measure steady-state extractor work; keep C# regex/JIT startup outside the guard.
        var builder = new StringBuilder();
        builder.AppendLine("class Warmup {");
        builder.AppendLine("    void Target() { }");
        for (var index = 0; index < 128; index++)
            builder.Append("    void Caller").Append(index).AppendLine("() { Target(); }");
        builder.AppendLine("}");

        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        _ = ReferenceExtractor.Extract(1, "csharp", content, symbols);
    }
}
