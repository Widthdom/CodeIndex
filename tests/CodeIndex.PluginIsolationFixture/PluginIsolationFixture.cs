using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

[assembly: CdidxPlugin(ExtractorPluginRegistry.CurrentApiVersion, ExtractorPluginRegistry.CurrentApiVersion)]

namespace CodeIndex.PluginIsolationFixture;

public static class PluginIsolationFixtureEnvironment
{
    public const string ThrowingConstructor = "CDIDX_TEST_THROWING_PLUGIN_CTOR";
    public const string SlowConstructor = "CDIDX_TEST_SLOW_PLUGIN_CTOR";
    public const string CrashingConstructor = "CDIDX_TEST_CRASHING_PLUGIN_CTOR";
}

public sealed class CollectiblePluginSymbolExtractor : ISymbolExtractor
{
    public string Language => "collectibledsl";

    public IReadOnlyCollection<string> FileExtensions => [".collectible"];

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
        => [new SymbolRecord { FileId = fileId, Kind = "class", Name = "worker-symbol", Line = 1 }];
}

public sealed class SlowPluginSymbolExtractor : ISymbolExtractor
{
    public SlowPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(PluginIsolationFixtureEnvironment.SlowConstructor) == "1")
            Thread.Sleep(TimeSpan.FromSeconds(30));
    }

    public string Language => "slowplugindsl";

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context) => [];
}

public sealed class CrashingPluginSymbolExtractor : ISymbolExtractor
{
    public CrashingPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(PluginIsolationFixtureEnvironment.CrashingConstructor) == "1")
            Environment.FailFast("plugin worker crash fixture");
    }

    public string Language => "crashingplugindsl";

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context) => [];
}

public sealed class CollectiblePluginReferenceExtractor : IReferenceExtractor
{
    public string Language => "collectibledsl";

    public IReadOnlyCollection<string> FileExtensions => [".collectible"];

    public IReadOnlyList<ReferenceRecord> Extract(long fileId, string source, ExtractionContext context)
        =>
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "WorkspacePluginTarget",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = source,
            },
        ];
}

public sealed class ThrowingPluginSymbolExtractor : ISymbolExtractor
{
    public ThrowingPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(PluginIsolationFixtureEnvironment.ThrowingConstructor) == "1")
            throw new InvalidOperationException("plugin ctor boom");
    }

    public string Language => "throwingplugindsl";

    public IReadOnlyCollection<string> FileExtensions => [".throwingplugin"];

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context) => [];
}

public sealed class DualRolePluginExtractor : ISymbolExtractor, IReferenceExtractor
{
    public DualRolePluginExtractor()
    {
        ConstructorCount++;
    }

    public static int ConstructorCount { get; private set; }

    public string Language => "dualroleplugindsl";

    public IReadOnlyCollection<string> FileExtensions => [".dualroleplugin"];

    IReadOnlyList<SymbolRecord> ISymbolExtractor.Extract(long fileId, string source, ExtractionContext context) => [];

    IReadOnlyList<ReferenceRecord> IReferenceExtractor.Extract(long fileId, string source, ExtractionContext context) => [];
}
