using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private static bool TryReadValueOption(string[] args, ref int index, string optionName, string arg, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (arg == optionName)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                error = $"{optionName} requires a non-empty value.";
                return true;
            }
            value = args[++index];
            return true;
        }

        var prefix = optionName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
                error = $"{optionName} requires a non-empty value.";
            return true;
        }

        return false;
    }

    private static int WriteImportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode = CommandExitCodes.UsageError,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics = null,
        string? rootCause = null)
        => WriteStructuredError(json, jsonOptions, ImportCommandName, phase, errorCode, message, hint, usage, exitCode, diagnostics, rootCause);

    private static int WriteExportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode = CommandExitCodes.UsageError,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics = null)
        => WriteStructuredError(json, jsonOptions, ExportCommandName, phase, errorCode, message, hint, usage, exitCode, diagnostics);

    private static int WriteStructuredError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string command,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics,
        string? rootCause = null)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ExportImportErrorResult("1", "error", command, phase, errorCode, message, hint, usage, rootCause, diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).ExportImportErrorResult));
            return exitCode;
        }

        return CommandErrorWriter.Write(message, exitCode, hint, usage);
    }

    private static void AddImportValidationPhase(
        List<ImportValidationPhaseResult> validationPhases,
        string phase,
        string status = "success",
        string? message = null)
        => validationPhases.Add(new ImportValidationPhaseResult(phase, status, message));

    private static string ClassifyImportFailureRootCause(string phase, Exception exception)
        => exception switch
        {
            InvalidDataException when phase == PhaseOpenArchive => "invalid_archive",
            UnauthorizedAccessException => "permission_denied",
            SqliteException => "sqlite_error",
            IOException => "io_error",
            InvalidDataException => "invalid_data",
            _ => "unknown",
        };

    internal sealed record ExportManifest(
        [property: JsonPropertyName("format_version")]
        string FormatVersion,
        [property: JsonPropertyName("cdidx_version")]
        string CdidxVersion,
        [property: JsonPropertyName("user_version")]
        int UserVersion,
        [property: JsonPropertyName("project_root")]
        string? ProjectRoot,
        [property: JsonPropertyName("indexed_head_sha")]
        string? IndexedHeadSha,
        [property: JsonPropertyName("database_sha256")]
        string DatabaseSha256,
        [property: JsonPropertyName("file_count")]
        long? FileCount = null,
        [property: JsonPropertyName("chunk_count")]
        long? ChunkCount = null,
        [property: JsonPropertyName("symbol_count")]
        long? SymbolCount = null,
        [property: JsonPropertyName("reference_count")]
        long? ReferenceCount = null,
        [property: JsonPropertyName("graph_ready")]
        bool? GraphReady = null,
        [property: JsonPropertyName("issues_ready")]
        bool? IssuesReady = null,
        [property: JsonPropertyName("fold_ready")]
        bool? FoldReady = null,
        [property: JsonPropertyName("index_writer_version")]
        string? IndexWriterVersion = null,
        [property: JsonPropertyName("indexed_head_branch")]
        string? IndexedHeadBranch = null,
        [property: JsonPropertyName("indexed_head_timestamp")]
        string? IndexedHeadTimestamp = null,
        [property: JsonPropertyName("codeindex_meta_schema_version")]
        int? CodeIndexMetaSchemaVersion = null,
        [property: JsonPropertyName("csharp_symbol_name_contract_version")]
        int? CSharpSymbolNameContractVersion = null,
        [property: JsonPropertyName("sql_graph_contract_version")]
        int? SqlGraphContractVersion = null,
        [property: JsonPropertyName("hotspot_family_version")]
        int? HotspotFamilyVersion = null,
        [property: JsonPropertyName("unknown_extension_file_count")]
        long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")]
        string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")]
        bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")]
        int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")]
        int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")]
        int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")]
        bool? UnknownExtensionFileSampleTruncated = null,
        [property: JsonPropertyName("scope")]
        ArchiveExportScopeResult? Scope = null);
    internal sealed record ExportImportErrorResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("hint")] string Hint,
        [property: JsonPropertyName("usage")] string Usage,
        [property: JsonPropertyName("root_cause")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RootCause = null,
        [property: JsonPropertyName("diagnostics")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExportImportDiagnosticResult>? Diagnostics = null);
    internal sealed record ExportImportDiagnosticResult(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("path")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Path = null);
    internal sealed record ImportValidationPhaseResult(
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string? Message);
    internal sealed record ImportDryRunResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("archive_path")] string ArchivePath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("dry_run")] bool DryRun,
        [property: JsonPropertyName("pruned_paths")] bool PrunedPaths,
        [property: JsonPropertyName("pruned_project_root")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PrunedProjectRoot,
        [property: JsonPropertyName("replacement_would_be_allowed")] bool ReplacementWouldBeAllowed,
        [property: JsonPropertyName("validation_phases")] IReadOnlyList<ImportValidationPhaseResult> ValidationPhases,
        [property: JsonPropertyName("destination_delta")]
        ImportDestinationDeltaResult? DestinationDelta = null,
        [property: JsonPropertyName("unknown_extension_file_count")] long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")] string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")] bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")] int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")] int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")] int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")] bool? UnknownExtensionFileSampleTruncated = null);
    internal sealed record ImportDestinationDeltaResult(
        [property: JsonPropertyName("destination_exists")] bool DestinationExists,
        [property: JsonPropertyName("comparable")] bool Comparable,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("comparison")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DiffJsonResult? Comparison,
        [property: JsonPropertyName("message")] string Message);
    internal sealed record ExportArchiveResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("archive_path")] string ArchivePath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("scope")] ArchiveExportScopeResult Scope);
    private sealed record ArchiveExportOptions(
        string? Lang,
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePathPatterns,
        IReadOnlyList<string> Projects,
        string? Solution,
        bool ExcludeTests)
    {
        internal bool IsScoped =>
            !string.IsNullOrWhiteSpace(Lang) ||
            PathPatterns.Count > 0 ||
            ExcludePathPatterns.Count > 0 ||
            Projects.Count > 0 ||
            !string.IsNullOrWhiteSpace(Solution) ||
            ExcludeTests;
    }
    internal sealed record ArchiveExportScopeResult(
        [property: JsonPropertyName("scoped")] bool Scoped,
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("path")] IReadOnlyList<string> PathPatterns,
        [property: JsonPropertyName("exclude_path")] IReadOnlyList<string> ExcludePathPatterns,
        [property: JsonPropertyName("project")] IReadOnlyList<string> Projects,
        [property: JsonPropertyName("solution")] string? Solution,
        [property: JsonPropertyName("exclude_tests")] bool ExcludeTests,
        [property: JsonPropertyName("resolved_project_path")] IReadOnlyList<string> ResolvedProjectPathPatterns,
        [property: JsonPropertyName("source_file_count")] long SourceFileCount,
        [property: JsonPropertyName("exported_file_count")] long ExportedFileCount);
    private sealed record CtagsExportOptions(
        string? Lang,
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePathPatterns,
        bool ExcludeTests,
        bool IncludeGenerated,
        bool GeneratedFileFilterAvailable);
    internal sealed record CtagsExportFilterResult(
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("path")] IReadOnlyList<string> PathPatterns,
        [property: JsonPropertyName("exclude_path")] IReadOnlyList<string> ExcludePathPatterns,
        [property: JsonPropertyName("exclude_tests")] bool ExcludeTests,
        [property: JsonPropertyName("include_generated")] bool IncludeGenerated,
        [property: JsonPropertyName("generated_code_policy")] string GeneratedCodePolicy,
        [property: JsonPropertyName("generated_file_filter_available")] bool GeneratedFileFilterAvailable);
    internal sealed record CtagsExportResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("output_path")] string OutputPath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("tag_count")] long TagCount,
        [property: JsonPropertyName("emitted_count")] long EmittedCount,
        [property: JsonPropertyName("skipped_count")] long SkippedCount,
        [property: JsonPropertyName("skip_reason_counts")] IReadOnlyDictionary<string, long> SkipReasonCounts,
        [property: JsonPropertyName("filters")] CtagsExportFilterResult Filters,
        [property: JsonPropertyName("metadata_fields")] IReadOnlyList<string> MetadataFields);
    internal sealed record ImportResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("archive_path")] string ArchivePath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("dry_run")] bool DryRun,
        [property: JsonPropertyName("pruned_paths")] bool PrunedPaths,
        [property: JsonPropertyName("pruned_project_root")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PrunedProjectRoot,
        [property: JsonPropertyName("validation_phases")] IReadOnlyList<ImportValidationPhaseResult> ValidationPhases,
        [property: JsonPropertyName("unknown_extension_file_count")] long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")] string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")] bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")] int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")] int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")] int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")] bool? UnknownExtensionFileSampleTruncated = null);

    private sealed class ImportReplacementException : IOException
    {
        internal ImportReplacementException(string message, Exception innerException, IReadOnlyList<ExportImportDiagnosticResult> diagnostics)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        internal IReadOnlyList<ExportImportDiagnosticResult> Diagnostics { get; }
    }
}
