using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.HookIsolationFixture;

public static class HookIsolationFixtureEnvironment
{
    public const string ModuleInitializerPidPath = "CDIDX_TEST_HOOK_MODULE_INITIALIZER_PID_PATH";
    public const string ModuleInitializerDelayMilliseconds = "CDIDX_TEST_HOOK_MODULE_INITIALIZER_DELAY_MS";
    public const string PersistentDiscoveryWorkerPidPath = "CDIDX_TEST_HOOK_DISCOVERY_PERSISTENT_PID_PATH";
    public const string PersistentDiscoveryDescendantPidPath = "CDIDX_TEST_HOOK_DISCOVERY_DESCENDANT_PID_PATH";
    public const string SelectiveSlowHookAssembly = "CDIDX_TEST_SELECTIVE_SLOW_HOOK_ASSEMBLY";
    public const string MutateCSharpPartialFamily = "CDIDX_TEST_MUTATE_CSHARP_PARTIAL_FAMILY";
    public const string RemoveCSharpStaticInterfaceMemberMarkerFileName =
        ".cdidx-test-remove-csharp-static-interface-member";

    [ModuleInitializer]
    public static void Initialize()
    {
        var delay = Environment.GetEnvironmentVariable(ModuleInitializerDelayMilliseconds);
        if (int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var delayMilliseconds)
            && delayMilliseconds > 0)
        {
            Thread.Sleep(delayMilliseconds);
        }

        var pidPath = Environment.GetEnvironmentVariable(ModuleInitializerPidPath);
        if (!string.IsNullOrWhiteSpace(pidPath))
            File.WriteAllText(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var persistentWorkerPidPath = Environment.GetEnvironmentVariable(PersistentDiscoveryWorkerPidPath);
        if (!string.IsNullOrWhiteSpace(persistentWorkerPidPath))
        {
            File.WriteAllText(
                persistentWorkerPidPath,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var persistentThread = new Thread(static () => Thread.Sleep(Timeout.Infinite))
            {
                IsBackground = false,
                Name = "cdidx-hook-discovery-persistent-fixture",
            };
            persistentThread.Start();
        }

        var descendantPidPath = Environment.GetEnvironmentVariable(PersistentDiscoveryDescendantPidPath);
        if (!string.IsNullOrWhiteSpace(descendantPidPath))
            StartPersistentDescendant(descendantPidPath);
    }

    private static Process? persistentDiscoveryDescendant;

    private static void StartPersistentDescendant(string descendantPidPath)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("The current process path is unavailable.");
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(IPostExtractionHook).Assembly.Location);
        }
        startInfo.ArgumentList.Add("__cdidx-symbol-extraction");

        persistentDiscoveryDescendant = Process.Start(startInfo)
                                        ?? throw new InvalidOperationException(
                                            "Failed to start the hook discovery descendant fixture.");
        File.WriteAllText(
            descendantPidPath,
            persistentDiscoveryDescendant.Id.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed class CSharpPartialFamilyMutationPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (Environment.GetEnvironmentVariable(
                HookIsolationFixtureEnvironment.MutateCSharpPartialFamily) != "1"
            || !string.Equals(context.Language, "csharp", StringComparison.Ordinal))
        {
            return;
        }

        var container = symbols.FirstOrDefault(symbol => symbol.Name == "HookContainer");
        if (container != null)
        {
            container.Name = "HookContainerRenamed";
            container.Signature = "file partial class HookContainerRenamed<T>";
        }

        var existing = symbols.FirstOrDefault(symbol => symbol.Name == "HookPartial");
        if (existing != null)
        {
            existing.Name = "HookOrdinary";
            existing.Signature = "void HookOrdinary();";
            existing.ContainerName = "HookContainerRenamed";
            existing.ContainerQualifiedName = "HookContainerRenamed";
        }

        symbols.Add(new SymbolRecord
        {
            FileId = existing?.FileId ?? 0,
            Kind = "function",
            Name = "HookAddedPartial",
            Signature = "[Obsolete] partial void HookAddedPartial();",
            ContainerKind = "class",
            ContainerName = "HookContainerRenamed",
            ContainerQualifiedName = "HookContainerRenamed",
            Line = 3,
            StartLine = 3,
            EndLine = 3,
        });
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class PathSelectivePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (File.Exists(Path.Combine(
                context.ProjectRoot,
                HookIsolationFixtureEnvironment.RemoveCSharpStaticInterfaceMemberMarkerFileName)))
        {
            if (string.Equals(context.Language, "csharp", StringComparison.Ordinal))
            {
                for (var index = symbols.Count - 1; index >= 0; index--)
                {
                    var symbol = symbols[index];
                    if (symbol.ContainerKind == "interface"
                        && symbol.Kind is "function" or "operator" or "property"
                        && IsStaticInterfaceContractSignature(symbol.Signature))
                    {
                        symbols.RemoveAt(index);
                    }
                }
            }

            return;
        }

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

    private static bool IsStaticInterfaceContractSignature(string? signature)
        => !string.IsNullOrWhiteSpace(signature)
           && signature.Contains("static", StringComparison.Ordinal)
           && (signature.Contains("abstract", StringComparison.Ordinal)
               || signature.Contains("virtual", StringComparison.Ordinal));
}
