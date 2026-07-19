using CodeIndex.Models;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class IsolatedPluginExtractorProxy : ISymbolExtractor, IReferenceExtractor
{
    private readonly ExtractorPluginWorkerManifestEntry manifest;
    private readonly ExtractorPluginWorkerClient worker;
    private readonly Action<string, string, string> reportFailure;

    internal IsolatedPluginExtractorProxy(
        ExtractorPluginWorkerManifestEntry manifest,
        ExtractorPluginWorkerClient worker,
        Action<string, string, string> reportFailure)
    {
        this.manifest = manifest;
        this.worker = worker;
        this.reportFailure = reportFailure;
    }

    string ISymbolExtractor.Language
        => manifest.SymbolLanguage
           ?? throw new InvalidOperationException("Plugin manifest does not expose symbol extraction.");

    IReadOnlyCollection<string> ISymbolExtractor.FileExtensions
        => manifest.SymbolFileExtensions ?? [];

    string IReferenceExtractor.Language
        => manifest.ReferenceLanguage
           ?? throw new InvalidOperationException("Plugin manifest does not expose reference extraction.");

    IReadOnlyCollection<string> IReferenceExtractor.FileExtensions
        => manifest.ReferenceFileExtensions ?? [];

    IReadOnlyList<SymbolRecord> ISymbolExtractor.Extract(
        long fileId,
        string source,
        ExtractionContext context)
    {
        var result = worker.ExtractSymbols(manifest.TypeName, fileId, source, context);
        if (result.Success && result.Response?.Symbols != null)
            return result.Response.Symbols;

        Report(result);
        return [];
    }

    IReadOnlyList<ReferenceRecord> IReferenceExtractor.Extract(
        long fileId,
        string source,
        ExtractionContext context)
    {
        var result = worker.ExtractReferences(manifest.TypeName, fileId, source, context);
        if (result.Success && result.Response?.References != null)
            return result.Response.References;

        Report(result);
        return [];
    }

    private void Report(ExtractorPluginWorkerResult result)
        => reportFailure(
            manifest.TypeName,
            result.ErrorCategory ?? "plugin_worker_response_invalid",
            result.Error ?? "Plugin worker response omitted extractor results.");
}
