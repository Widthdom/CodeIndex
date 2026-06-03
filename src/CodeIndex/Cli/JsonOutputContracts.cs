using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal sealed record CliJsonMessage(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("message")] string Message);

internal sealed record BackfillFoldJsonResult(
    [property: JsonPropertyName("symbols")] int Symbols,
    [property: JsonPropertyName("symbol_references")] int SymbolReferences,
    [property: JsonPropertyName("rewrite_all")] bool RewriteAll,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("was_already_complete")] bool WasAlreadyComplete,
    [property: JsonPropertyName("fold_ready_before")] bool FoldReadyBefore,
    [property: JsonPropertyName("fold_ready_after")] bool FoldReadyAfter,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("user_version_before")] int UserVersionBefore,
    [property: JsonPropertyName("user_version_after")] int UserVersionAfter,
    [property: JsonPropertyName("fold_ready")] bool FoldReady);

internal sealed record OptimizeFtsJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("writes_since_optimize_before")] int WritesSinceOptimizeBefore,
    [property: JsonPropertyName("writes_since_optimize_after")] int WritesSinceOptimizeAfter,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);

internal sealed record CommandErrorJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hint")] string? Hint,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("category")] string? Category = null);

internal sealed record DbIntegrityCheckJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("issues")] List<string> Issues);

internal sealed record DbCheckpointJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("files")] List<string> Files);

internal sealed record DbCheckpointListJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("checkpoints")] List<DbCheckpointListEntryJsonResult> Checkpoints);

internal sealed record DbCheckpointListEntryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
    [property: JsonPropertyName("bytes")] long Bytes);

internal sealed record DbRestoreJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("backup_path")] string BackupPath);

internal sealed record DbSchemaEntryJsonResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("table_name")] string? TableName,
    [property: JsonPropertyName("sql")] string? Sql);

internal sealed record DbSchemaJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("user_version")] int UserVersion,
    [property: JsonPropertyName("entries")] List<DbSchemaEntryJsonResult> Entries);

internal sealed record DbPruneJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("orphan_symbol_references")] int OrphanSymbolReferences,
    [property: JsonPropertyName("orphan_reference_lines")] int OrphanReferenceLines,
    [property: JsonPropertyName("orphan_symbols")] int OrphanSymbols,
    [property: JsonPropertyName("total")] int Total);

internal sealed record DiffSummaryJsonResult(
    [property: JsonPropertyName("left_file_count")] long LeftFileCount,
    [property: JsonPropertyName("right_file_count")] long RightFileCount,
    [property: JsonPropertyName("file_count_delta")] long FileCountDelta,
    [property: JsonPropertyName("left_symbol_count")] long LeftSymbolCount,
    [property: JsonPropertyName("right_symbol_count")] long RightSymbolCount,
    [property: JsonPropertyName("symbol_count_delta")] long SymbolCountDelta,
    [property: JsonPropertyName("left_reference_count")] long LeftReferenceCount,
    [property: JsonPropertyName("right_reference_count")] long RightReferenceCount,
    [property: JsonPropertyName("reference_count_delta")] long ReferenceCountDelta,
    [property: JsonPropertyName("left_schema_version")] long LeftSchemaVersion,
    [property: JsonPropertyName("right_schema_version")] long RightSchemaVersion,
    [property: JsonPropertyName("schema_versions_equal")] bool SchemaVersionsEqual);

internal sealed record DiffJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("identical")] bool Identical,
    [property: JsonPropertyName("left_db")] string LeftDb,
    [property: JsonPropertyName("right_db")] string RightDb,
    [property: JsonPropertyName("summary")] DiffSummaryJsonResult Summary,
    [property: JsonPropertyName("files_only_in_left")] List<string> FilesOnlyInLeft,
    [property: JsonPropertyName("files_only_in_right")] List<string> FilesOnlyInRight,
    [property: JsonPropertyName("symbols_only_in_left")] List<string>? SymbolsOnlyInLeft,
    [property: JsonPropertyName("symbols_only_in_right")] List<string>? SymbolsOnlyInRight,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("detailed")] bool Detailed);

internal sealed record DiffSummaryOnlyJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("identical")] bool Identical,
    [property: JsonPropertyName("left_db")] string LeftDb,
    [property: JsonPropertyName("right_db")] string RightDb,
    [property: JsonPropertyName("summary")] DiffSummaryJsonResult Summary);

internal sealed record ReportBundleSummary(
    [property: JsonPropertyName("output_path")] string OutputPath,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("schema_tables")] int SchemaTables,
    [property: JsonPropertyName("log_lines_included")] int LogLinesIncluded,
    [property: JsonPropertyName("log_included")] bool LogIncluded,
    [property: JsonPropertyName("db_included")] bool DbIncluded,
    [property: JsonPropertyName("db_path")] string? DbPath);

internal sealed record QueryCountJsonResult(
    [property: JsonPropertyName("count")] int Count);

internal sealed record QueryCountFilesJsonResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("query")] string Query);

internal sealed record QueryFindCountJsonResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: Obsolete("Use the 'files' JSON field. The 'file_count' field is a deprecated compatibility alias for find --count --json.")]
    [property: JsonPropertyName("file_count")] int FileCount);

internal sealed record QueryPathErrorJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("error")] string Error);

internal sealed record JsonStreamDoneResult(
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("interrupted")] bool Interrupted);

internal sealed record LanguageEntryJsonResult(
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("extensions")] List<string> Extensions,
    [property: JsonPropertyName("aliases")] List<string> Aliases,
    [property: JsonPropertyName("symbol_extraction")] bool SymbolExtraction,
    [property: JsonPropertyName("graph_queries")] bool GraphQueries);

internal sealed record LanguagesJsonResult(
    [property: JsonPropertyName("languages")] List<LanguageEntryJsonResult> Languages);

internal sealed class IndexDryRunJsonResult
{
    public string Status { get; init; } = string.Empty;
    public int FilesTotal { get; init; }
    public Dictionary<string, int> Languages { get; init; } = new();
    public List<CliJsonMessage>? Errors { get; init; }
}

internal sealed class IndexWatchEventJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string? ProjectRoot { get; init; }
    public string? Db { get; init; }
    public int? DebounceMs { get; init; }
    public int? BatchSize { get; init; }
    public long? ElapsedMs { get; init; }
    public int? ExitCode { get; init; }
    public string? Reason { get; init; }
}

internal sealed class IndexUpdateSummaryJsonResult
{
    public long FilesTotal { get; init; }
    public long ChunksTotal { get; init; }
    public long SymbolsTotal { get; init; }
    public long ReferencesTotal { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }
    public int Skipped { get; init; }
    public int Warnings { get; init; }
    public int Errors { get; init; }
    public int SymbolsDroppedByKindFilter { get; init; }
    [JsonPropertyName("fts_optimize_ran")]
    public bool FtsOptimizeRan { get; init; }
}

internal sealed class IndexFullScanSummaryJsonResult
{
    public long FilesTotal { get; init; }
    public long ChunksTotal { get; init; }
    public long SymbolsTotal { get; init; }
    public long ReferencesTotal { get; init; }
    public int FilesScanned { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesPurged { get; init; }
    [JsonPropertyName("dangling_symlinks_skipped")]
    public int DanglingSymlinksSkipped { get; init; }
    public int Warnings { get; init; }
    public int Errors { get; init; }
    public int SymbolsDroppedByKindFilter { get; init; }
}

internal sealed class IndexMemorySampleJsonResult
{
    public string Phase { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public long HeapBytes { get; init; }
    public long TotalAllocatedBytes { get; init; }
    public long GcHeapSizeBytes { get; init; }
    public long FragmentedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

internal sealed class IndexMemoryTimelineJsonResult
{
    public List<IndexMemorySampleJsonResult> Samples { get; init; } = [];
    public long PeakWorkingSetBytes { get; init; }
    public long PeakHeapBytes { get; init; }
}

public sealed class IndexSymbolKindFilterJsonResult
{
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

internal sealed class IndexUpdateJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public IndexUpdateSummaryJsonResult Summary { get; init; } = new();
    public IndexSymbolKindFilterJsonResult SymbolKindFilter { get; init; } = new();
    public bool GraphTableAvailable { get; init; }
    public bool IssuesTableAvailable { get; init; }
    public bool SqlGraphContractReady { get; init; }
    public string? SqlGraphContractDegradedReason { get; init; }
    public bool HotspotFamilyReady { get; init; }
    public string? HotspotFamilyDegradedReason { get; init; }
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; init; }
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; init; }
    public bool FoldReady { get; init; }
    public string? FoldReadyReason { get; init; }
    public string? DegradedReason { get; init; }
    public string? RecommendedAction { get; init; }
    public string? AlternativeAction { get; init; }
    public bool CwdDriftDetected { get; init; }
    public string? CwdAtStart { get; init; }
    public string? CwdAtFinalize { get; init; }
    public string? CwdDriftNotice { get; init; }
    public List<CliJsonMessage>? Errors { get; init; }
    public List<CliJsonMessage>? Warnings { get; init; }
    public IndexMemoryTimelineJsonResult? MemoryTimeline { get; init; }
    public long ElapsedMs { get; init; }
}

internal sealed class IndexFullScanJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public IndexFullScanSummaryJsonResult Summary { get; init; } = new();
    public IndexSymbolKindFilterJsonResult SymbolKindFilter { get; init; } = new();
    public bool GraphTableAvailable { get; init; }
    public bool IssuesTableAvailable { get; init; }
    public bool SqlGraphContractReady { get; init; }
    public string? SqlGraphContractDegradedReason { get; init; }
    public bool HotspotFamilyReady { get; init; }
    public string? HotspotFamilyDegradedReason { get; init; }
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; init; }
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; init; }
    public bool FoldReady { get; init; }
    public string? FoldReadyReason { get; init; }
    public string? DegradedReason { get; init; }
    public string? RecommendedAction { get; init; }
    public string? AlternativeAction { get; init; }
    public bool HeadChanged { get; init; }
    public string? PriorIndexedHeadCommit { get; init; }
    public string? CurrentHeadCommit { get; init; }
    public string? HeadChangeNotice { get; init; }
    public bool CwdDriftDetected { get; init; }
    public string? CwdAtStart { get; init; }
    public string? CwdAtFinalize { get; init; }
    public string? CwdDriftNotice { get; init; }
    public List<CliJsonMessage>? Errors { get; init; }
    public List<CliJsonMessage>? Warnings { get; init; }
    public IndexMemoryTimelineJsonResult? MemoryTimeline { get; init; }
    public long ElapsedMs { get; init; }
}

internal sealed record SymbolHotspotJsonResult(
    string Name,
    string Kind,
    string Path,
    int Line,
    int ReferenceCount,
    double ReferenceScore,
    string? Visibility,
    string? Container);

internal sealed record GroupedSymbolHotspotJsonResult(
    string Name,
    string Kind,
    string Path,
    int Line,
    int ReferenceCount,
    double ReferenceScore,
    string? Visibility,
    string? Container,
    int DefinitionSites,
    List<string> Paths);

internal sealed record VersionInfoJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("build_date")] string BuildDate,
    [property: JsonPropertyName("dirty")] string Dirty);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackfillFoldJsonResult))]
[JsonSerializable(typeof(ActiveWorkspaceJsonResult))]
[JsonSerializable(typeof(ActiveWorkspaceState))]
[JsonSerializable(typeof(CalleeResult))]
[JsonSerializable(typeof(CallerResult))]
[JsonSerializable(typeof(CliJsonMessage))]
[JsonSerializable(typeof(CompactSearchResult))]
[JsonSerializable(typeof(CompactSearchResult[]))]
[JsonSerializable(typeof(CommandErrorJsonResult))]
[JsonSerializable(typeof(ConfigShowJsonResult))]
[JsonSerializable(typeof(DbCheckpointJsonResult))]
[JsonSerializable(typeof(DbCheckpointListEntryJsonResult))]
[JsonSerializable(typeof(DbCheckpointListJsonResult))]
[JsonSerializable(typeof(DbIntegrityCheckJsonResult))]
[JsonSerializable(typeof(DbPruneJsonResult))]
[JsonSerializable(typeof(DbRestoreJsonResult))]
[JsonSerializable(typeof(DbSchemaEntryJsonResult))]
[JsonSerializable(typeof(DbSchemaJsonResult))]
[JsonSerializable(typeof(DefinitionResult))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(DiffJsonResult))]
[JsonSerializable(typeof(DiffSummaryOnlyJsonResult))]
[JsonSerializable(typeof(DiffSummaryJsonResult))]
[JsonSerializable(typeof(ExactZeroHintResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ExportArchiveResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ExportManifest))]
[JsonSerializable(typeof(ExcerptSemanticToken))]
[JsonSerializable(typeof(FileDependencyResult))]
[JsonSerializable(typeof(FileExcerptResult))]
[JsonSerializable(typeof(FileFindResult))]
[JsonSerializable(typeof(FileIssue))]
[JsonSerializable(typeof(FileResult))]
[JsonSerializable(typeof(FreshnessHintResult))]
[JsonSerializable(typeof(FtsQueryDiagnostics))]
[JsonSerializable(typeof(GroupedHotspotResult))]
[JsonSerializable(typeof(GroupedSymbolHotspotJsonResult))]
[JsonSerializable(typeof(ImpactAnalysisResult))]
[JsonSerializable(typeof(ImpactCycleResult))]
[JsonSerializable(typeof(ImpactResult))]
[JsonSerializable(typeof(IndexDryRunJsonResult))]
[JsonSerializable(typeof(IndexFreshnessCheckResult))]
[JsonSerializable(typeof(IndexFullScanJsonResult))]
[JsonSerializable(typeof(IndexFullScanSummaryJsonResult))]
[JsonSerializable(typeof(IndexMemorySampleJsonResult))]
[JsonSerializable(typeof(IndexMemoryTimelineJsonResult))]
[JsonSerializable(typeof(IndexUpdateJsonResult))]
[JsonSerializable(typeof(IndexUpdateSummaryJsonResult))]
[JsonSerializable(typeof(IndexWatchEventJsonResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ImportResult))]
[JsonSerializable(typeof(HookCommandJsonResult))]
[JsonSerializable(typeof(JsonStreamDoneResult))]
[JsonSerializable(typeof(LanguageEntryJsonResult))]
[JsonSerializable(typeof(LanguagesJsonResult))]
[JsonSerializable(typeof(LspLocation))]
[JsonSerializable(typeof(LspPosition))]
[JsonSerializable(typeof(LspRange))]
[JsonSerializable(typeof(List<CalleeResult>))]
[JsonSerializable(typeof(List<CallerResult>))]
[JsonSerializable(typeof(List<CliJsonMessage>))]
[JsonSerializable(typeof(List<DefinitionResult>))]
[JsonSerializable(typeof(List<FileDependencyResult>))]
[JsonSerializable(typeof(List<FileIssue>))]
[JsonSerializable(typeof(List<GroupedSymbolHotspotJsonResult>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<ImpactCycleResult>))]
[JsonSerializable(typeof(List<ImpactResult>))]
[JsonSerializable(typeof(List<LspLocation>))]
[JsonSerializable(typeof(QueryCountJsonResult))]
[JsonSerializable(typeof(QueryCountFilesJsonResult))]
[JsonSerializable(typeof(QueryFindCountJsonResult))]
[JsonSerializable(typeof(QueryPathErrorJsonResult))]
[JsonSerializable(typeof(List<ReferenceResult>))]
[JsonSerializable(typeof(List<List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<SymbolHotspotJsonResult>))]
[JsonSerializable(typeof(List<SymbolResult>))]
[JsonSerializable(typeof(List<UnusedSymbolResult>))]
[JsonSerializable(typeof(OutlineResult))]
[JsonSerializable(typeof(OutlineSymbol))]
[JsonSerializable(typeof(OptimizeFtsJsonResult))]
[JsonSerializable(typeof(QueryCountResult))]
[JsonSerializable(typeof(ReferenceResult))]
[JsonSerializable(typeof(RepoEntrypointResult))]
[JsonSerializable(typeof(RepoFileSummaryResult))]
[JsonSerializable(typeof(RepoLanguageResult))]
[JsonSerializable(typeof(RepoMapResult))]
[JsonSerializable(typeof(RepoModuleResult))]
[JsonSerializable(typeof(ReportBundleSummary))]
[JsonSerializable(typeof(SearchHighlight))]
[JsonSerializable(typeof(SearchGuardEvidence))]
[JsonSerializable(typeof(List<SearchGuardEvidence>))]
[JsonSerializable(typeof(SearchQueryHint))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(SearchTermOccurrence))]
[JsonSerializable(typeof(SearchTruncationContext))]
[JsonSerializable(typeof(ExtractorRegistryDiagnostic))]
[JsonSerializable(typeof(ExtractorRegistryStatus))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(StatusReadinessDegradation))]
[JsonSerializable(typeof(StatusDbPragmaSettings))]
[JsonSerializable(typeof(StatusLastIndexRun))]
[JsonSerializable(typeof(StatusProcessMetrics))]
[JsonSerializable(typeof(SuggestionDetailJsonResult))]
[JsonSerializable(typeof(SuggestionExportJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftDuplicateMatchJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftDuplicatePreflightJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftExportJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftPreflightSummaryJsonResult))]
[JsonSerializable(typeof(SuggestionIssueDraftSourceJsonResult))]
[JsonSerializable(typeof(SuggestionListItemJsonResult))]
[JsonSerializable(typeof(SymbolAnalysisResult))]
[JsonSerializable(typeof(SymbolHotspotJsonResult))]
[JsonSerializable(typeof(SymbolResult))]
[JsonSerializable(typeof(UnusedSymbolResult))]
[JsonSerializable(typeof(CodeIndex.Models.UpdateCheckResult))]
[JsonSerializable(typeof(VacuumResult))]
[JsonSerializable(typeof(VersionInfoJsonResult))]
[JsonSerializable(typeof(WorkspaceListJsonResult))]
[JsonSerializable(typeof(WorkspaceManifest))]
[JsonSerializable(typeof(WorkspaceMember))]
internal partial class CliJsonSerializerContext : JsonSerializerContext;

internal static class CliJsonSerializerContextFactory
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, CliJsonSerializerContext> s_contexts = new();

    public static CliJsonSerializerContext Create(JsonSerializerOptions jsonOptions) =>
        s_contexts.GetValue(jsonOptions, CreateContext);

    private static CliJsonSerializerContext CreateContext(JsonSerializerOptions jsonOptions)
    {
        var contextOptions = new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = null,
        };
        return new CliJsonSerializerContext(contextOptions);
    }
}
