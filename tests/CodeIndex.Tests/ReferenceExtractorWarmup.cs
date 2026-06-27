using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

internal static class ReferenceExtractorWarmup
{
    [ModuleInitializer]
    internal static void WarmUp()
    {
        if (!IsContinuousIntegration() || !IsNet8TestAssembly())
            return;

        // Practical budget tests measure steady-state extractor work; keep C# regex/JIT/tiered startup outside the guard.
        var builder = new StringBuilder();
        builder.AppendLine("class Warmup {");
        builder.AppendLine("    void Target() { }");
        for (var index = 0; index < 1_024; index++)
            builder.Append("    void Caller").Append(index).AppendLine("() { Target(); }");
        builder.AppendLine("}");

        var content = builder.ToString();
        for (var pass = 0; pass < 2; pass++)
        {
            var symbols = SymbolExtractor.Extract(1, "csharp", content);
            _ = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool IsContinuousIntegration()
        => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsNet8TestAssembly()
    {
        var targetFramework = (TargetFrameworkAttribute?)Attribute.GetCustomAttribute(
            typeof(ReferenceExtractorWarmup).Assembly,
            typeof(TargetFrameworkAttribute));
        return targetFramework?.FrameworkName.Contains("Version=v8.0", StringComparison.OrdinalIgnoreCase) == true;
    }
}
