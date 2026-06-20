using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static void ReportPatternConfigRejected(string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern",
            path,
            typeName: null,
            severity: "error",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true,
            category: "invalid_pattern_config");
    }

    private static void ReportPatternConfigSkipped(string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern",
            path,
            typeName: null,
            severity: "skipped",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true,
            category: "pattern_config_incomplete");
    }

    private static void ReportPatternDirectoryRejected(string path, string reason)
        => ReportPatternDirectoryRejected(path, reason, "pattern_directory_rejected");

    private static void ReportPatternDirectoryRejected(string path, string reason, string category)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern directory '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern_directory",
            path,
            typeName: null,
            severity: "error",
            $"Pattern directory skipped: {reason}",
            countsAsSkippedFile: false,
            category: category);
    }

    private static void ReportPatternDirectorySkipped(string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern directory '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern_directory",
            path,
            typeName: null,
            severity: "skipped",
            $"Pattern directory skipped: {reason}.",
            countsAsSkippedFile: false,
            category: "pattern_candidate_limit_exceeded");
    }

    private static void ReportPluginDirectorySkipped(string path, string reason, string category)
    {
        RecordDiagnostic(
            "plugin_directory",
            path,
            typeName: null,
            severity: "skipped",
            $"Plugin directory skipped: {reason}.",
            countsAsSkippedFile: false,
            category: category);
    }

    private static void RecordDiagnostic(
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile,
        string category = "unspecified")
    {
        lock (Gate)
        {
            diagnosticTotalCount++;
            if (countsAsSkippedFile)
                skippedFileCount++;
            if (Diagnostics.Count < DiagnosticLimit)
                Diagnostics.Add(new ExtractorRegistryDiagnostic(
                    DiagnosticSanitizer.ForMessage(kind),
                    DiagnosticSanitizer.ForPath(path),
                    DiagnosticSanitizer.ForOptionalLabel(typeName),
                    DiagnosticSanitizer.ForMessage(severity),
                    DiagnosticSanitizer.ForMessage(category),
                    DiagnosticSanitizer.ForMessage(message)));
        }
    }
}

public sealed class ExtractorRegistryStatus
{
    [JsonPropertyName("plugin_assembly_count")]
    public int PluginAssemblyCount { get; init; }
    [JsonPropertyName("pattern_config_count")]
    public int PatternConfigCount { get; init; }
    [JsonPropertyName("symbol_extractor_count")]
    public int SymbolExtractorCount { get; init; }
    [JsonPropertyName("reference_extractor_count")]
    public int ReferenceExtractorCount { get; init; }
    [JsonPropertyName("skipped_file_count")]
    public int SkippedFileCount { get; init; }
    [JsonPropertyName("diagnostic_count")]
    public int DiagnosticCount { get; init; }
    [JsonPropertyName("diagnostic_limit")]
    public int DiagnosticLimit { get; init; }
    [JsonPropertyName("diagnostics_truncated")]
    public bool DiagnosticsTruncated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtractorRegistryDiagnostic>? Diagnostics { get; init; }
}

public sealed record ExtractorRegistryDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type_name")] string? TypeName,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message);

public sealed record ExtensionTrustOverride(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("environment_variable")] string EnvironmentVariable,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path,
    [property: JsonPropertyName("message")] string Message);
