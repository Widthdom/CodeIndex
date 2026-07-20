using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

internal static class ReferenceExtractorWarmup
{
    private static Process? persistentDiscoveryDescendant;

    [ModuleInitializer]
    internal static void WarmUp()
    {
        var moduleInitializerDelay = Environment.GetEnvironmentVariable(
            PostExtractionHookTests.ModuleInitializerDelayEnvironmentVariable);
        if (int.TryParse(moduleInitializerDelay, out var delayMilliseconds) && delayMilliseconds > 0)
            Thread.Sleep(delayMilliseconds);

        var persistentWorkerPidPath = Environment.GetEnvironmentVariable(
            PostExtractionHookTests.PersistentDiscoveryWorkerPidPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(persistentWorkerPidPath))
        {
            File.WriteAllText(
                persistentWorkerPidPath,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var persistentThread = new Thread(static () => Thread.Sleep(Timeout.Infinite))
            {
                IsBackground = false,
                Name = "cdidx-hook-discovery-persistent-fixture",
            };
            persistentThread.Start();
        }

        var persistentDescendantPidPath = Environment.GetEnvironmentVariable(
            PostExtractionHookTests.PersistentDiscoveryDescendantPidPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(persistentDescendantPidPath))
        {
            if (!SymbolExtractionWorker.TryCreateStartInfo(out var startInfo, out var error))
                throw new InvalidOperationException(error);

            persistentDiscoveryDescendant = Process.Start(startInfo)
                                            ?? throw new InvalidOperationException("Failed to start the hook discovery descendant fixture.");
            File.WriteAllText(
                persistentDescendantPidPath,
                persistentDiscoveryDescendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

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
