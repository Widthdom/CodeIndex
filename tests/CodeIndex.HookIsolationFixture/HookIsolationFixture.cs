using System.Globalization;
using System.Runtime.CompilerServices;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.HookIsolationFixture;

public static class HookIsolationFixtureEnvironment
{
    public const string ModuleInitializerPidPath = "CDIDX_TEST_HOOK_MODULE_INITIALIZER_PID_PATH";
    public const string SelectiveSlowHookAssembly = "CDIDX_TEST_SELECTIVE_SLOW_HOOK_ASSEMBLY";

    [ModuleInitializer]
    public static void Initialize()
    {
        var pidPath = Environment.GetEnvironmentVariable(ModuleInitializerPidPath);
        if (!string.IsNullOrWhiteSpace(pidPath))
            File.WriteAllText(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed class PathSelectivePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        var assemblyName = Path.GetFileName(GetType().Assembly.Location);
        if (string.Equals(
                assemblyName,
                Environment.GetEnvironmentVariable(HookIsolationFixtureEnvironment.SelectiveSlowHookAssembly),
                StringComparison.Ordinal))
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }

        symbols.Add(new SymbolRecord
        {
            Kind = "domain_tag",
            Name = $"Selective:{assemblyName}",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        });
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}
