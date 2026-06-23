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

internal sealed record DoctorJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("kernel")] string Kernel,
    [property: JsonPropertyName("dotnet")] string Dotnet,
    [property: JsonPropertyName("process")] string Process,
    [property: JsonPropertyName("base_dir")] string BaseDir,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("terminal")] DoctorTerminalJsonResult Terminal,
    [property: JsonPropertyName("display")] DoctorDisplayJsonResult Display,
    [property: JsonPropertyName("paths")] DoctorPathsJsonResult Paths,
    [property: JsonPropertyName("config")] DoctorConfigJsonResult Config,
    [property: JsonPropertyName("cdidx_env")] IReadOnlyList<DoctorEnvironmentVariableJsonResult> CdidxEnv,
    [property: JsonPropertyName("environment_inventory")] IReadOnlyList<EnvironmentVariableInventoryItem> EnvironmentInventory,
    [property: JsonPropertyName("redaction")] DoctorRedactionJsonResult Redaction);

internal sealed record DoctorTerminalJsonResult(
    [property: JsonPropertyName("stdout_tty")] bool StdoutTty,
    [property: JsonPropertyName("stderr_tty")] bool StderrTty,
    [property: JsonPropertyName("columns")] string Columns,
    [property: JsonPropertyName("no_color")] string NoColor,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("ui_locale")] string UiLocale);

internal sealed record DoctorDisplayJsonResult(
    [property: JsonPropertyName("color")] DoctorDisplayDecisionJsonResult Color,
    [property: JsonPropertyName("progress")] DoctorDisplayDecisionJsonResult Progress,
    [property: JsonPropertyName("terminal_hint")] DoctorDisplayTerminalHintJsonResult TerminalHint,
    [property: JsonPropertyName("max_line_width")] DoctorDisplayMaxLineWidthJsonResult MaxLineWidth,
    [property: JsonPropertyName("ambiguous_width")] DoctorDisplayAmbiguousWidthJsonResult AmbiguousWidth,
    [property: JsonPropertyName("truncation")] DoctorDisplayTruncationJsonResult Truncation);

internal sealed record DoctorDisplayDecisionJsonResult(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record DoctorDisplayTerminalHintJsonResult(
    [property: JsonPropertyName("has_hint")] bool HasHint,
    [property: JsonPropertyName("disabled")] bool Disabled,
    [property: JsonPropertyName("stdout_redirected")] bool StdoutRedirected,
    [property: JsonPropertyName("string_writer_capture")] bool StringWriterCapture,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("term_program")] string TermProgram,
    [property: JsonPropertyName("ci")] string Ci,
    [property: JsonPropertyName("windows_terminal")] string WindowsTerminal);

internal sealed record DoctorDisplayMaxLineWidthJsonResult(
    [property: JsonPropertyName("value")] int Value,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("used_fallback")] bool UsedFallback,
    [property: JsonPropertyName("fallback")] int Fallback,
    [property: JsonPropertyName("minimum")] int Minimum,
    [property: JsonPropertyName("maximum")] int Maximum,
    [property: JsonPropertyName("environment_variable")] string EnvironmentVariable,
    [property: JsonPropertyName("raw_value")] string RawValue);

internal sealed record DoctorDisplayAmbiguousWidthJsonResult(
    [property: JsonPropertyName("wide")] bool Wide,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("locale")] string Locale);

internal sealed record DoctorDisplayTruncationJsonResult(
    [property: JsonPropertyName("default_max_line_width")] int DefaultMaxLineWidth,
    [property: JsonPropertyName("max_allowed_line_width")] int MaxAllowedLineWidth,
    [property: JsonPropertyName("diagnostic_value_char_limit")] int DiagnosticValueCharLimit,
    [property: JsonPropertyName("marker_format")] string MarkerFormat);

internal sealed record DoctorPathsJsonResult(
    [property: JsonPropertyName("db")] string Db,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("data_source")] string DataSource,
    [property: JsonPropertyName("log_dir")] string LogDir);

internal sealed record DoctorConfigJsonResult(
    [property: JsonPropertyName("dot_cdidxrc_json")] string DotCdidxrcJson,
    [property: JsonPropertyName("disable_config_file")] string DisableConfigFile);

internal sealed record DoctorEnvironmentVariableJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("sensitive")] bool Sensitive,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("original_length")] int OriginalLength);

internal sealed record DoctorRedactionJsonResult(
    [property: JsonPropertyName("paths_redacted")] bool PathsRedacted,
    [property: JsonPropertyName("secrets_redacted")] bool SecretsRedacted);

internal sealed record UpgradeJsonResult(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("from_cache")] bool FromCache,
    [property: JsonPropertyName("selected_version")] string? SelectedVersion,
    [property: JsonPropertyName("selected_channel")] string SelectedChannel,
    [property: JsonPropertyName("selection_source")] string SelectionSource,
    [property: JsonPropertyName("include_prerelease")] bool IncludePrerelease,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_category")] string? ErrorCategory,
    [property: JsonPropertyName("error_hint")] string? ErrorHint,
    [property: JsonPropertyName("install_attempted")] bool InstallAttempted,
    [property: JsonPropertyName("install_exit_code")] int? InstallExitCode,
    [property: JsonPropertyName("install_succeeded")] bool? InstallSucceeded,
    [property: JsonPropertyName("handoff_command")] string? HandoffCommand,
    [property: JsonPropertyName("handoff_url")] string? HandoffUrl,
    [property: JsonPropertyName("handoff_asset")] string? HandoffAsset,
    [property: JsonPropertyName("handoff_asset_url")] string? HandoffAssetUrl,
    [property: JsonPropertyName("installer_verification")] string? InstallerVerification,
    [property: JsonPropertyName("installer_trust_boundary")] string? InstallerTrustBoundary,
    [property: JsonPropertyName("installer_stdout_tail")] string? InstallerStdoutTail,
    [property: JsonPropertyName("installer_stderr_tail")] string? InstallerStderrTail,
    [property: JsonPropertyName("installer_output_truncated")] bool? InstallerOutputTruncated,
    [property: JsonPropertyName("install_directory_error")] string? InstallDirectoryError);

internal sealed record DbIntegrityCheckJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("diagnostic_code")] string DiagnosticCode,
    [property: JsonPropertyName("issues")] List<string> Issues,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("rows_truncated")] bool RowsTruncated = false,
    [property: JsonPropertyName("text_truncated")] bool TextTruncated = false,
    [property: JsonPropertyName("row_limit")] int RowLimit = 0,
    [property: JsonPropertyName("text_limit")] int TextLimit = 0);

internal sealed record DbCheckpointJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("files")] List<string> Files,
    [property: JsonPropertyName("files_truncated")] bool FilesTruncated = false,
    [property: JsonPropertyName("file_limit")] int FileLimit = 0,
    [property: JsonPropertyName("diagnostics")] List<DbDiagnosticJsonResult>? Diagnostics = null,
    [property: JsonPropertyName("dry_run")] bool DryRun = false,
    [property: JsonPropertyName("bytes")] long Bytes = 0);

internal sealed record DbCheckpointListJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("checkpoints")] List<DbCheckpointListEntryJsonResult> Checkpoints,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("checkpoint_limit")] int CheckpointLimit = 0,
    [property: JsonPropertyName("file_limit")] int FileLimit = 0,
    [property: JsonPropertyName("diagnostics")] List<DbDiagnosticJsonResult>? Diagnostics = null);

internal sealed record DbCheckpointListEntryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("files_truncated")] bool FilesTruncated = false);

internal sealed record DbRestoreJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("checkpoint_path")] string CheckpointPath,
    [property: JsonPropertyName("backup_path")] string BackupPath,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("hint")] string? Hint = null,
    [property: JsonPropertyName("rollback_failed")] bool RollbackFailed = false,
    [property: JsonPropertyName("rollback_failure")] DbDiagnosticJsonResult? RollbackFailure = null);

internal sealed record DbRestoreBackupEntryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("backup_path")] string BackupPath,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("files_truncated")] bool FilesTruncated = false);

internal sealed record DbRestoreBackupListJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("backups")] List<DbRestoreBackupEntryJsonResult> Backups,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("backup_limit")] int BackupLimit = 0,
    [property: JsonPropertyName("file_limit")] int FileLimit = 0,
    [property: JsonPropertyName("diagnostics")] List<DbDiagnosticJsonResult>? Diagnostics = null);

internal sealed record DbRestoreBackupPruneJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("keep")] int Keep,
    [property: JsonPropertyName("deleted")] int Deleted,
    [property: JsonPropertyName("retained")] int Retained,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("backup_limit")] int BackupLimit = 0,
    [property: JsonPropertyName("diagnostics")] List<DbDiagnosticJsonResult>? Diagnostics = null);

internal sealed record DbSchemaEntryJsonResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("table_name")] string? TableName,
    [property: JsonPropertyName("sql")] string? Sql);

internal sealed record DbSchemaJsonResult(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("user_version")] int UserVersion,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("diagnostic_code")] string DiagnosticCode,
    [property: JsonPropertyName("object_type_counts")] Dictionary<string, int> ObjectTypeCounts,
    [property: JsonPropertyName("object_type_omitted_counts")] Dictionary<string, int> ObjectTypeOmittedCounts,
    [property: JsonPropertyName("entries")] List<DbSchemaEntryJsonResult> Entries,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("entries_truncated")] bool EntriesTruncated = false,
    [property: JsonPropertyName("sql_truncated")] bool SqlTruncated = false,
    [property: JsonPropertyName("entry_limit")] int EntryLimit = 0,
    [property: JsonPropertyName("sql_text_limit")] int SqlTextLimit = 0,
    [property: JsonPropertyName("summary_only")] bool SummaryOnly = false,
    [property: JsonPropertyName("type_filter")] string? TypeFilter = null,
    [property: JsonPropertyName("name_filter")] string? NameFilter = null,
    [property: JsonPropertyName("include_internal")] bool IncludeInternal = true);

internal sealed record DbPruneJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("orphan_symbol_references")] int OrphanSymbolReferences,
    [property: JsonPropertyName("orphan_reference_lines")] int OrphanReferenceLines,
    [property: JsonPropertyName("orphan_symbols")] int OrphanSymbols,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("warnings")] List<DbDiagnosticJsonResult>? Warnings = null);

internal sealed record DbDiagnosticJsonResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path = null);

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
    [property: JsonPropertyName("detailed")] bool Detailed,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("diagnostics")] List<DiffDiagnosticJsonResult>? Diagnostics = null);

internal sealed record DiffDiagnosticJsonResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

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

internal sealed record SearchGroupedCountJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("groups")] List<SearchGroupedCountItemJsonResult> Groups);

internal sealed record SearchGroupedCountItemJsonResult(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("symbol_name")] string? SymbolName,
    [property: JsonPropertyName("symbol_kind")] string? SymbolKind,
    [property: JsonPropertyName("symbol_start_line")] int? SymbolStartLine,
    [property: JsonPropertyName("symbol_end_line")] int? SymbolEndLine,
    [property: JsonPropertyName("container_name")] string? ContainerName);

internal sealed record SearchAggregationJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("unique")] bool Unique,
    [property: JsonPropertyName("groups")] List<SearchGroupedCountItemJsonResult> Groups);

internal sealed record SearchFileGroupedJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("total_matches")] int TotalMatches,
    [property: JsonPropertyName("returned_groups")] int ReturnedGroups,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("per_file_limit")] int PerFileLimit,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("groups")] List<SearchFileGroupJsonResult> Groups);

internal sealed record SearchFileGroupJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("omitted_count")] int OmittedCount);

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
    [property: JsonPropertyName("interrupted")] bool Interrupted,
    [property: JsonPropertyName("read_only_fallback")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnlyFallback = null,
    [property: JsonPropertyName("wal_checkpoint_attempted")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? WalCheckpointAttempted = null,
    [property: JsonPropertyName("wal_checkpoint_succeeded")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? WalCheckpointSucceeded = null,
    [property: JsonPropertyName("read_only_immutable_fallback")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnlyImmutableFallback = null,
    [property: JsonPropertyName("wal_checkpoint_skipped_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WalCheckpointSkippedReason = null,
    [property: JsonPropertyName("wal_checkpoint_failure_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WalCheckpointFailureReason = null,
    [property: JsonPropertyName("wal_stale_snapshot_risk")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? WalStaleSnapshotRisk = null,
    [property: JsonPropertyName("wal_stale_snapshot_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WalStaleSnapshotReason = null);

internal sealed record LanguageEntryJsonResult(
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("extensions")] List<string> Extensions,
    [property: JsonPropertyName("aliases")] List<string> Aliases,
    [property: JsonPropertyName("symbol_extraction")] bool SymbolExtraction,
    [property: JsonPropertyName("reference_extraction")] bool ReferenceExtraction,
    [property: JsonPropertyName("graph_queries")] bool GraphQueries,
    [property: JsonPropertyName("capability_gaps")] List<string> CapabilityGaps,
    [property: JsonPropertyName("indexed_file_count")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? IndexedFileCount = null);

internal sealed record LanguagesJsonResult(
    [property: JsonPropertyName("languages")] List<LanguageEntryJsonResult> Languages);

internal sealed class IndexDryRunJsonResult
{
    public string Status { get; init; } = string.Empty;
    public int FilesTotal { get; init; }
    public bool Estimates { get; init; }
    public int ProjectedFileUpdates { get; init; }
    public int ProjectedFileDeletes { get; init; }
    public int ProjectedFilePurges { get; init; }
    public int UnsupportedTotal { get; init; }
    public int UnknownExtensionTotal { get; init; }
    public int CandidatePathLimit { get; init; }
    public int CandidatePathsProcessed { get; init; }
    public bool CandidatePathsTruncated { get; init; }
    public bool TotalsLowerBound { get; init; }
    public Dictionary<string, long> EstimatedTableMutations { get; init; } = new();
    public List<string>? FileSamples { get; init; }
    public bool FileSamplesTruncated { get; init; }
    public int FileSampleLimit { get; init; }
    public Dictionary<string, int> Languages { get; init; } = new();
    public int ErrorsTotal { get; init; }
    public List<CliJsonMessage>? Errors { get; init; }
    public bool ErrorsTruncated { get; init; }
    public int ErrorLimit { get; init; }
}

internal sealed class IndexWatchEventJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string? Phase { get; init; }
    public string? ProjectRoot { get; init; }
    public string? Db { get; init; }
    public int? DebounceMs { get; init; }
    public int? WatchPendingPathLimit { get; init; }
    public int? BatchSize { get; init; }
    public List<string>? BatchPathSamples { get; init; }
    public int? BatchPathSampleLimit { get; init; }
    public bool? BatchPathSamplesTruncated { get; init; }
    public long? ElapsedMs { get; init; }
    public int? ExitCode { get; init; }
    public int? Updated { get; init; }
    public int? Removed { get; init; }
    public int? Errors { get; init; }
    public string? SubRunParseStatus { get; init; }
    public string? SubRunParseReason { get; init; }
    public string? OverflowReason { get; init; }
    public IndexWatchRecoveryCommandJsonResult? RecoveryCommand { get; init; }
    public string? Reason { get; init; }
}

internal sealed class IndexWatchRecoveryCommandJsonResult
{
    public string Command { get; init; } = string.Empty;
    public List<string> Args { get; init; } = [];
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
    double RankingScore,
    double GenericNamePenalty,
    string? Visibility,
    string? Container);

internal sealed record GroupedSymbolHotspotSiteJsonResult(
    string Path,
    string? Lang,
    int Line,
    string? Visibility,
    string? Container,
    [property: JsonPropertyName("logical_target_key")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalTargetKey);

internal sealed record GroupedSymbolHotspotJsonResult(
    string Name,
    string Kind,
    string Path,
    int Line,
    int ReferenceCount,
    double ReferenceScore,
    double RankingScore,
    double GenericNamePenalty,
    string? Visibility,
    string? Container,
    int DefinitionSites,
    List<string> Paths,
    bool PathsTruncated,
    [property: JsonPropertyName("representative")] GroupedSymbolHotspotSiteJsonResult Representative,
    [property: JsonPropertyName("definition_site_details")] List<GroupedSymbolHotspotSiteJsonResult> DefinitionSiteDetails);

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
[JsonSerializable(typeof(ActiveWorkspaceStatusJsonResult))]
[JsonSerializable(typeof(ConfigShowJsonResult))]
[JsonSerializable(typeof(ConfigFileStatusJsonResult))]
[JsonSerializable(typeof(ConfigEffectiveValueJsonResult))]
[JsonSerializable(typeof(DbCheckpointJsonResult))]
[JsonSerializable(typeof(DbCheckpointListEntryJsonResult))]
[JsonSerializable(typeof(DbCheckpointListJsonResult))]
[JsonSerializable(typeof(DbDiagnosticJsonResult))]
[JsonSerializable(typeof(DbIntegrityCheckJsonResult))]
[JsonSerializable(typeof(DbPruneJsonResult))]
[JsonSerializable(typeof(DbRestoreBackupEntryJsonResult))]
[JsonSerializable(typeof(DbRestoreBackupListJsonResult))]
[JsonSerializable(typeof(DbRestoreBackupPruneJsonResult))]
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
[JsonSerializable(typeof(ExportImportCommandRunner.CtagsExportFilterResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.CtagsExportResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ExportArchiveResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ExportImportErrorResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ExportManifest))]
[JsonSerializable(typeof(ExportImportCommandRunner.ImportDryRunResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ImportValidationPhaseResult))]
[JsonSerializable(typeof(ExcerptContentLineSpan))]
[JsonSerializable(typeof(ExcerptRecoveryHint))]
[JsonSerializable(typeof(ExcerptSemanticToken))]
[JsonSerializable(typeof(FileDependencyResult))]
[JsonSerializable(typeof(FileExcerptResult))]
[JsonSerializable(typeof(FileFindResult))]
[JsonSerializable(typeof(FileFindSnippetTruncationContext))]
[JsonSerializable(typeof(FileIssue))]
[JsonSerializable(typeof(FileResult))]
[JsonSerializable(typeof(FreshnessHintResult))]
[JsonSerializable(typeof(FtsQueryDiagnostics))]
[JsonSerializable(typeof(GroupedHotspotResult))]
[JsonSerializable(typeof(GroupedSymbolHotspotSiteJsonResult))]
[JsonSerializable(typeof(GroupedSymbolHotspotJsonResult))]
[JsonSerializable(typeof(ImpactAnalysisResult))]
[JsonSerializable(typeof(ImpactCycleResult))]
[JsonSerializable(typeof(ImpactPathNode))]
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
[JsonSerializable(typeof(IndexWatchRecoveryCommandJsonResult))]
[JsonSerializable(typeof(ExportImportCommandRunner.ImportResult))]
[JsonSerializable(typeof(HookCommandJsonResult))]
[JsonSerializable(typeof(HookCommandWarningJsonResult))]
[JsonSerializable(typeof(List<HookCommandWarningJsonResult>))]
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
[JsonSerializable(typeof(List<FileResult>))]
[JsonSerializable(typeof(List<GroupedSymbolHotspotJsonResult>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<ImpactCycleResult>))]
[JsonSerializable(typeof(List<ImpactResult>))]
[JsonSerializable(typeof(List<LspLocation>))]
[JsonSerializable(typeof(QueryCountJsonResult))]
[JsonSerializable(typeof(QueryCountFilesJsonResult))]
[JsonSerializable(typeof(QueryFindCountJsonResult))]
[JsonSerializable(typeof(QueryPathErrorJsonResult))]
[JsonSerializable(typeof(SearchGroupedCountJsonResult))]
[JsonSerializable(typeof(SearchGroupedCountItemJsonResult))]
[JsonSerializable(typeof(SearchAggregationJsonResult))]
[JsonSerializable(typeof(SearchFileGroupedJsonResult))]
[JsonSerializable(typeof(SearchFileGroupJsonResult))]
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
[JsonSerializable(typeof(SearchCommandHint))]
[JsonSerializable(typeof(SearchGuardCheck))]
[JsonSerializable(typeof(List<SearchGuardCheck>))]
[JsonSerializable(typeof(SearchGuardEvidence))]
[JsonSerializable(typeof(List<SearchGuardEvidence>))]
[JsonSerializable(typeof(SearchGuardSpan))]
[JsonSerializable(typeof(SearchMatchFacet))]
[JsonSerializable(typeof(List<SearchMatchFacet>))]
[JsonSerializable(typeof(SearchQueryHint))]
[JsonSerializable(typeof(SearchNamedBatchQueryResultJsonResult))]
[JsonSerializable(typeof(SearchNamedBatchRunJsonResult))]
[JsonSerializable(typeof(SearchRecipeListItemJsonResult))]
[JsonSerializable(typeof(SearchRecipeListJsonResult))]
[JsonSerializable(typeof(SearchRecipeBroadCatchTaxonomyJsonResult))]
[JsonSerializable(typeof(SearchRecipeBroadCatchBoundaryJsonResult))]
[JsonSerializable(typeof(SearchRecipeBroadCatchDiagnosticBehaviorJsonResult))]
[JsonSerializable(typeof(SearchRecipeFilterSupportJsonResult))]
[JsonSerializable(typeof(SearchRecipeLimitSemanticsJsonResult))]
[JsonSerializable(typeof(SearchRecipeCompactRunJsonResult))]
[JsonSerializable(typeof(SearchRecipeCompactQueryResultJsonResult))]
[JsonSerializable(typeof(SearchRecipeCompactResultJsonResult))]
[JsonSerializable(typeof(SearchRecipeTopFileJsonResult))]
[JsonSerializable(typeof(SearchRecipeQueryListItemJsonResult))]
[JsonSerializable(typeof(SearchRecipeQueryResultJsonResult))]
[JsonSerializable(typeof(SearchRecipeRunJsonResult))]
[JsonSerializable(typeof(SearchRecipeScopeJsonResult))]
[JsonSerializable(typeof(SearchRecipeExcludedDiagnosticJsonResult))]
[JsonSerializable(typeof(SearchIssueDraftExportJsonResult))]
[JsonSerializable(typeof(SearchIssueDraftJsonResult))]
[JsonSerializable(typeof(SearchIssueDraftSourceJsonResult))]
[JsonSerializable(typeof(IssueDraftTriageMetadataJsonResult))]
[JsonSerializable(typeof(SearchNextMatchHint))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(SearchTermOccurrence))]
[JsonSerializable(typeof(SearchTruncationContext))]
[JsonSerializable(typeof(MacProfileDiagnostic))]
[JsonSerializable(typeof(ExtractorRegistryDiagnostic))]
[JsonSerializable(typeof(ExtractorRegistryStatus))]
[JsonSerializable(typeof(DoctorConfigJsonResult))]
[JsonSerializable(typeof(DoctorDisplayAmbiguousWidthJsonResult))]
[JsonSerializable(typeof(DoctorDisplayDecisionJsonResult))]
[JsonSerializable(typeof(DoctorDisplayJsonResult))]
[JsonSerializable(typeof(DoctorDisplayMaxLineWidthJsonResult))]
[JsonSerializable(typeof(DoctorDisplayTerminalHintJsonResult))]
[JsonSerializable(typeof(DoctorDisplayTruncationJsonResult))]
[JsonSerializable(typeof(DoctorEnvironmentVariableJsonResult))]
[JsonSerializable(typeof(DoctorJsonResult))]
[JsonSerializable(typeof(DoctorPathsJsonResult))]
[JsonSerializable(typeof(DoctorRedactionJsonResult))]
[JsonSerializable(typeof(DoctorTerminalJsonResult))]
[JsonSerializable(typeof(EnvironmentVariableInventoryItem))]
[JsonSerializable(typeof(EnvironmentVariableInventoryLocation))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(StatusSqliteConnectionPolicy))]
[JsonSerializable(typeof(StatusFailedOrPartialIndexRun))]
[JsonSerializable(typeof(StatusReadinessDegradation))]
[JsonSerializable(typeof(StatusDbPragmaSettings))]
[JsonSerializable(typeof(StatusLastIndexRun))]
[JsonSerializable(typeof(StatusMaintenanceGuidance))]
[JsonSerializable(typeof(StatusProcessMetrics))]
[JsonSerializable(typeof(StatusRepairCommand))]
[JsonSerializable(typeof(StatusUnknownExtensionGroup))]
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
[JsonSerializable(typeof(UpgradeJsonResult))]
[JsonSerializable(typeof(VacuumResult))]
[JsonSerializable(typeof(VersionInfoJsonResult))]
[JsonSerializable(typeof(WorkspaceListJsonResult))]
[JsonSerializable(typeof(WorkspaceManifest))]
[JsonSerializable(typeof(WorkspaceManifestStatusJsonResult))]
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
