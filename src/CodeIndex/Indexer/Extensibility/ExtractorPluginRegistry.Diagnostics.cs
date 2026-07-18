using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static void ReportPatternConfigRejected(PatternWorkspaceState state, string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordPatternDiagnostic(
            state,
            "pattern",
            path,
            typeName: null,
            severity: "error",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true,
            category: "invalid_pattern_config");
    }

    private static void ReportPatternConfigSkipped(PatternWorkspaceState state, string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordPatternDiagnostic(
            state,
            "pattern",
            path,
            typeName: null,
            severity: "skipped",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true,
            category: "pattern_config_incomplete");
    }

    private static void ReportPatternDirectoryRejected(PatternWorkspaceState state, string path, string reason)
        => ReportPatternDirectoryRejected(state, path, reason, "pattern_directory_rejected");

    private static void ReportPatternDirectoryRejected(
        PatternWorkspaceState state,
        string path,
        string reason,
        string category)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern directory '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordPatternDiagnostic(
            state,
            "pattern_directory",
            path,
            typeName: null,
            severity: "error",
            $"Pattern directory skipped: {reason}",
            countsAsSkippedFile: false,
            category: category);
    }

    private static void ReportPatternDirectorySkipped(PatternWorkspaceState state, string path, string reason)
    {
        CommandErrorWriter.WriteStderr($"[cdidx] Skipped pattern directory '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordPatternDiagnostic(
            state,
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

    private static void ReportPatternExtractorTimeout(
        PatternWorkspaceState state,
        string path,
        string language,
        string kind)
    {
        RecordPatternDiagnostic(
            state,
            "pattern",
            path,
            typeName: null,
            severity: "warning",
            $"Pattern extractor timeout: language '{DiagnosticSanitizer.ForMessage(language)}' kind '{DiagnosticSanitizer.ForMessage(kind)}'.",
            countsAsSkippedFile: false,
            category: RegexTimeoutPolicy.ConfiguredPatternRegexTimeoutCategory);
    }

    private static void RecordPatternDiagnostic(
        PatternWorkspaceState state,
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile,
        string category = "unspecified")
    {
        lock (state.Gate)
        {
            state.DiagnosticTotalCount++;
            if (countsAsSkippedFile)
                state.SkippedFileCount++;
            var diagnostic = CreateDiagnostic(kind, path, typeName, severity, message, category);
            AddBoundedDiagnostic(state.Diagnostics, diagnostic);
            state.PublishSnapshot();
        }
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
            var diagnostic = CreateDiagnostic(kind, path, typeName, severity, message, category);
            AddBoundedDiagnostic(Diagnostics, diagnostic);
        }
    }

    private static ExtractorRegistryDiagnostic CreateDiagnostic(
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        string category)
        => new(
            DiagnosticSanitizer.ForMessage(kind),
            DiagnosticSanitizer.ForPath(path),
            DiagnosticSanitizer.ForOptionalLabel(typeName),
            DiagnosticSanitizer.ForMessage(severity),
            DiagnosticSanitizer.ForMessage(category),
            DiagnosticSanitizer.ForMessage(message));

    private static void AddBoundedDiagnostic(
        List<ExtractorRegistryDiagnostic> diagnostics,
        ExtractorRegistryDiagnostic diagnostic)
    {
        if (diagnostics.Count < DiagnosticLimit)
        {
            diagnostics.Add(diagnostic);
        }
        else if (diagnostic.Category.EndsWith("_candidate_limit_exceeded", StringComparison.Ordinal)
                 && !diagnostics.Any(item => item.Category == diagnostic.Category && item.Path == diagnostic.Path))
        {
            diagnostics[^1] = diagnostic;
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
    [JsonPropertyName("retained_load_context_count")]
    public int RetainedLoadContextCount { get; init; }
    [JsonPropertyName("load_context_lifecycle")]
    public string LoadContextLifecycle { get; init; } = ExtractorPluginRegistry.PluginLoadContextLifecycle;
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
    [JsonPropertyName("pattern_configs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PatternConfigStatus>? PatternConfigs { get; init; }
}

public sealed record ExtractorRegistryDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type_name")] string? TypeName,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message);

public sealed record PatternConfigStatus(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("rule_count")] int RuleCount);

public sealed record ExtensionTrustOverride(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("environment_variable")] string EnvironmentVariable,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path,
    [property: JsonPropertyName("message")] string Message);
