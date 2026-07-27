using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed record IndexCompletionDetails(
        string ProjectPath,
        string CheckedRootIdentity,
        bool Rebuild,
        long? MaxFileBytes,
        JsonObject IndexOptions,
        JsonArray UnsupportedModes,
        long TotalFiles,
        long TotalChunks,
        long TotalSymbols,
        long TotalReferences,
        int Scanned,
        int Skipped,
        int Purged,
        int UnknownExtensionFileCount,
        int Errors,
        int SymbolsDroppedByKindFilter,
        SymbolKindFilter SymbolKindFilter,
        long DurationMilliseconds,
        DateTime StartedAtUtc,
        DateTime CompletedAtUtc,
        bool SqlGraphContractReady,
        bool CSharpSymbolNameReady,
        bool CSharpMetadataTargetReady,
        bool FoldReady,
        string? FoldReadyReason,
        JsonArray? MemoryTrace,
        IReadOnlyList<IndexFileFailure> Failures,
        IReadOnlyList<McpIndexDiagnostic> Diagnostics,
        DbWriter Writer);

    private JsonNode BuildIndexDryRunResult(
        JsonNode? id,
        McpIndexRequestOptions indexOptions,
        string projectPath,
        string cwd,
        McpPathBoundary.IndexRootAuthorization authorizedRoot,
        JsonArray unsupportedModes,
        DateTime runStartedAtUtc,
        Stopwatch runStopwatch,
        JsonArray? memorySamples)
    {
        var requestToken = _currentRequestToken.Value;
        var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, requestToken);
        var repositoryRoot = GitHelper.TryGetRepositoryRoot(projectPath, requestToken);
        var ignoreRuleRoot = repositoryRoot != null && IsIndexPathAuthorized(cwd, repositoryRoot)
            ? repositoryRoot
            : projectPath;
        var indexer = new FileIndexer(
            projectPath,
            ignoreCase,
            ignoreRuleRoot,
            indexOptions.MaxFileBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: indexOptions.SymlinkPolicy,
            generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment(),
            pathAccessValidator: authorizedRoot.EnsureAuthorizedEntry,
            openReadForIndexContent: authorizedRoot.OpenAuthorizedRead,
            enumerateFileSystemEntries: authorizedRoot.EnumerateAuthorizedFileSystemEntries,
            bindConfigurationReadsToFileSystemIdentity: true,
            internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(_dbPath));
        var scan = indexer.ScanFilesDetailed(cancellationToken: requestToken);
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
        var fatalScanErrors = scan.Errors.Count(error => error.IsFatal);
        var payload = new JsonObject
        {
            ["path"] = projectPath,
            ["checked_root_identity"] = authorizedRoot.CheckedRootIdentity,
            ["dry_run"] = true,
            ["would_rebuild"] = indexOptions.Rebuild,
            ["max_file_bytes"] = indexOptions.MaxFileBytes,
            ["index_options"] = indexOptions.OptionsPayload,
            ["unsupported_modes"] = unsupportedModes,
            ["summary"] = new JsonObject
            {
                ["files_scanned"] = scan.Files.Count,
                ["scan_errors"] = scan.Errors.Count,
                ["fatal_scan_errors"] = fatalScanErrors,
                ["unknown_extension_file_count"] = scan.UnknownExtensionFiles.Count,
                ["would_mutate_database"] = false,
            },
            ["duration_ms"] = runStopwatch.ElapsedMilliseconds,
            ["started_at"] = runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (memorySamples != null)
        {
            memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));
            payload["memory_trace"] = memorySamples;
        }
        return CreateToolResult(id, "Index dry run complete.", payload);
    }

    private JsonNode BuildIndexCompletionResult(JsonNode? id, IndexCompletionDetails details)
    {
        var structured = new JsonObject
        {
            ["path"] = details.ProjectPath,
            ["checked_root_identity"] = details.CheckedRootIdentity,
            ["rebuild"] = details.Rebuild,
            ["dry_run"] = false,
            ["max_file_bytes"] = details.MaxFileBytes,
            ["index_options"] = details.IndexOptions,
            ["unsupported_modes"] = details.UnsupportedModes,
            ["summary"] = new JsonObject
            {
                ["files"] = details.TotalFiles,
                ["chunks"] = details.TotalChunks,
                ["symbols"] = details.TotalSymbols,
                ["references"] = details.TotalReferences,
                ["scanned"] = details.Scanned,
                ["skipped"] = details.Skipped,
                ["purged"] = details.Purged,
                ["unknown_extension_file_count"] = details.UnknownExtensionFileCount,
                ["errors"] = details.Errors,
                ["failed_count"] = details.Failures.Count,
                ["symbols_dropped_by_kind_filter"] = details.SymbolsDroppedByKindFilter
            },
            ["symbol_kind_filter"] = new JsonObject
            {
                ["include"] = ToJsonStringArray(details.SymbolKindFilter.Include),
                ["exclude"] = ToJsonStringArray(details.SymbolKindFilter.Exclude),
                ["active"] = details.SymbolKindFilter.IsActive,
            },
            ["duration_ms"] = details.DurationMilliseconds,
            ["started_at"] = details.StartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["completed_at"] = details.CompletedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["sql_graph_contract_ready"] = details.SqlGraphContractReady,
            ["csharp_symbol_name_ready"] = details.CSharpSymbolNameReady,
            ["csharp_metadata_target_ready"] = details.CSharpMetadataTargetReady,
            // #86 codex review: AI clients use this to tell whether --exact will use the
            // Unicode fold path or silently fall back to ASCII NOCASE. If false after a clean
            ["fold_ready"] = details.FoldReady,
            ["fold_ready_reason"] = details.FoldReadyReason
        };
        if (details.MemoryTrace != null)
            structured["memory_trace"] = details.MemoryTrace;
        if (details.Failures.Count > 0)
        {
            var failureArray = new JsonArray();
            foreach (var failure in details.Failures.Take(50))
            {
                failureArray.Add(new JsonObject
                {
                    ["path"] = failure.Path,
                    ["stage"] = failure.Stage,
                    ["exception_type"] = failure.ExceptionType,
                    ["message"] = failure.Message,
                    ["message_truncated"] = failure.MessageTruncated,
                });
            }
            structured["failed_count"] = details.Failures.Count;
            structured["failures"] = failureArray;
            if (details.Failures.Count > 50)
                structured["failures_truncated"] = details.Failures.Count - 50;
            GlobalToolLog.Error(
                $"mcp_index_file_failures count={details.Failures.Count} first_path={QuoteMcpIndexFailureLogValue(details.Failures[0].Path)} first_error={QuoteMcpIndexFailureLogValue($"{details.Failures[0].ExceptionType}: {details.Failures[0].Message}")}");
        }
        AddMcpIndexDiagnostics(structured, details.Failures, details.Diagnostics);
        using var signalReader = new DbReader(details.Writer.Connection);
        var persistedReadiness = signalReader.GetPersistedIndexGenerationReadiness();
        AddIndexGenerationReadinessSignal(structured, persistedReadiness);
        AddReferenceGraphCompletenessSignal(structured, persistedReadiness);
        if (!details.SqlGraphContractReady)
        {
            var sqlGraphContractSignal = signalReader.GetSqlGraphContractSignal();
            AddSqlGraphContractSignal(
                structured,
                sqlGraphContractSignal.Relevant && !sqlGraphContractSignal.Ready
                    ? sqlGraphContractSignal
                    : new SqlGraphContractSignal(
                        Ready: false,
                        Relevant: true,
                        DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason()));
        }

        return CreateToolResult(
            id,
            details.Errors == 0 && !persistedReadiness.IndexComplete
                ? $"Indexing finished with persisted omissions: {string.Join(", ", persistedReadiness.IndexIncompleteReasons)}."
                : details.Errors == 0 && !details.FoldReady
                ? details.FoldReadyReason switch
                {
                    "stale_fold_key_version" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry an older fold-key version. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "stale_fold_key_fingerprint" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry folded keys generated under an older runtime fingerprint. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "missing_fold_backfill" => "Indexing complete. Note: --exact Unicode fold path not active because legacy rows without name_folded remain. Run backfill_fold to upgrade without reparsing files, or do a full rebuild.",
                    _ => "Indexing complete. Note: --exact Unicode fold path not active."
                }
                : "Indexing complete.",
            structured);
    }
}
