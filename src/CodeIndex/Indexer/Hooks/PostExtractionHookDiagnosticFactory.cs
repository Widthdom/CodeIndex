using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookDiagnosticFactory
{
    internal static PostExtractionHookDiagnostic Create(
        string assemblyPath,
        string? typeName,
        string message,
        string? callback = null,
        long? durationMs = null,
        string category = "unspecified")
        => new(
            DiagnosticSanitizer.ForPath(assemblyPath),
            DiagnosticSanitizer.ForOptionalLabel(typeName),
            DiagnosticSanitizer.ForMessage(message),
            DiagnosticSanitizer.ForOptionalLabel(callback),
            durationMs,
            DiagnosticSanitizer.ForMessage(category));
}
