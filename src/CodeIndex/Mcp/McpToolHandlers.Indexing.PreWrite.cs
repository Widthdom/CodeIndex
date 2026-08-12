using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private readonly record struct McpIndexBeforeWriteRequest(
        string ProjectPath,
        string CheckedRootIdentity,
        bool Rebuild,
        long? MaxFileBytes,
        JsonObject OptionsPayload,
        JsonArray UnsupportedModes,
        SymbolKindFilter SymbolKindFilter);

    private readonly record struct McpIndexBeforeWriteRun(
        JsonNode? ProgressToken,
        Stopwatch Stopwatch,
        DateTime StartedAtUtc,
        JsonArray? MemorySamples);

    private readonly record struct McpIndexBeforeWriteReadiness(
        IndexDatabaseSnapshot IndexSnapshot,
        bool SqlGraphContractMatchesCurrent,
        bool CSharpSymbolNameContractMatchesCurrent,
        string CurrentMetadataTargetVersion);

    private readonly record struct McpIndexBeforeWriteFailures(
        List<IndexFileFailure> Failures,
        List<McpIndexDiagnostic> Diagnostics,
        int SymbolsDroppedByKindFilter);

    private readonly record struct McpIndexBeforeWriteContext(
        JsonNode? Id,
        McpIndexBeforeWriteRequest Request,
        McpIndexBeforeWriteRun Run,
        FileIndexer Indexer,
        FileIndexer.ScanInputSnapshot ScanInputSnapshot,
        FileIndexer.ScanFilesResult ScanResult,
        McpIndexBeforeWriteReadiness Readiness,
        McpIndexBeforeWriteFailures FailureState);

    private readonly record struct McpIndexBeforeWriteResult(
        JsonNode? Response,
        FilePurgePlan PurgePlan,
        int Purged,
        bool HadCSharpStaticInterfaceContractsBeforePurge,
        bool UseFtsBulkLoad);

    private async Task<McpIndexBeforeWriteResult> ValidateMcpIndexBeforeWriteAsync(
        McpIndexBeforeWriteContext context,
        DbWriter writer,
        McpIndexCSharpWorkspaceState csharpState,
        FilePurgePlan purgePlan,
        int purged,
        bool hadCSharpContractsBeforePurge,
        bool useFtsBulkLoad,
        CancellationToken cancellationToken)
    {
        McpIndexInputSnapshotBarrierForTesting?.Invoke("before_write");
        if (!context.Indexer.TryValidateScanInputSnapshot(
                context.ScanInputSnapshot,
                out var changedScanInputPath,
                cancellationToken))
        {
            csharpState.RecordScanInputDrift(changedScanInputPath);
            var response = await ReturnMcpIndexBeforeWriteSnapshotFailureAsync(
                    context,
                    writer)
                .ConfigureAwait(false);
            return new(response, purgePlan, purged, hadCSharpContractsBeforePurge, useFtsBulkLoad);
        }

        if (!csharpState.DeferMutations
            && !csharpState.TryValidateFileSnapshots(out var changedCSharpPath))
        {
            csharpState.DeferForBeforeWriteStatDrift(changedCSharpPath);
            purgePlan = FilePurgePlan.Empty;
            purged = 0;
            hadCSharpContractsBeforePurge = false;
            useFtsBulkLoad = false;
        }

        return new(null, purgePlan, purged, hadCSharpContractsBeforePurge, useFtsBulkLoad);
    }

    private async Task<JsonNode> ReturnMcpIndexBeforeWriteSnapshotFailureAsync(
        McpIndexBeforeWriteContext context,
        DbWriter writer)
    {
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        await EmitProgressNotificationAsync(
                context.Run.ProgressToken,
                0,
                context.ScanResult.Files.Count,
                "Indexing stopped before index-data mutation because scan inputs changed.")
            .ConfigureAwait(false);
        if (context.Run.MemorySamples != null)
        {
            context.Run.MemorySamples.Add(
                CaptureMcpIndexMemorySample("finalize", context.Run.Stopwatch));
        }

        var structured = BuildMcpIndexBeforeWriteFailurePayload(
            context,
            writer,
            totalFiles,
            totalChunks,
            totalSymbols,
            totalReferences);
        AddMcpIndexBeforeWriteFailureSignals(context, writer, structured);
        return CreateToolResult(
            context.Id,
            "Indexing stopped before index-data mutation because the scan snapshot changed.",
            structured);
    }

    private JsonObject BuildMcpIndexBeforeWriteFailurePayload(
        McpIndexBeforeWriteContext context,
        DbWriter writer,
        long totalFiles,
        long totalChunks,
        long totalSymbols,
        long totalReferences)
    {
        var request = context.Request;
        var failureState = context.FailureState;
        var readiness = context.Readiness;
        var languageCounts = context.ScanResult.LanguageCounts;
        var persistedCSharpFiles = writer.HasAnyFilesWithLanguage("csharp");
        var persistedSqlFiles = writer.HasAnyFilesWithLanguage("sql");
        var hasCSharpFiles = languageCounts.ContainsKey("csharp") || persistedCSharpFiles;
        var hasSqlFiles = languageCounts.ContainsKey("sql") || persistedSqlFiles;
        var structured = new JsonObject
        {
            ["path"] = request.ProjectPath,
            ["checked_root_identity"] = request.CheckedRootIdentity,
            ["rebuild"] = request.Rebuild,
            ["dry_run"] = false,
            ["max_file_bytes"] = request.MaxFileBytes,
            ["index_options"] = request.OptionsPayload,
            ["unsupported_modes"] = request.UnsupportedModes,
            ["summary"] = new JsonObject
            {
                ["files"] = totalFiles,
                ["chunks"] = totalChunks,
                ["symbols"] = totalSymbols,
                ["references"] = totalReferences,
                ["scanned"] = context.ScanResult.Files.Count,
                ["skipped"] = 0,
                ["purged"] = 0,
                ["unknown_extension_file_count"] = context.ScanResult.UnknownExtensionFiles.Count,
                ["errors"] = failureState.Failures.Count,
                ["failed_count"] = failureState.Failures.Count,
                ["symbols_dropped_by_kind_filter"] = failureState.SymbolsDroppedByKindFilter,
            },
            ["symbol_kind_filter"] = new JsonObject
            {
                ["include"] = ToJsonStringArray(request.SymbolKindFilter.Include),
                ["exclude"] = ToJsonStringArray(request.SymbolKindFilter.Exclude),
                ["active"] = request.SymbolKindFilter.IsActive,
            },
            ["duration_ms"] = context.Run.Stopwatch.ElapsedMilliseconds,
            ["started_at"] = context.Run.StartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["sql_graph_contract_ready"] = !hasSqlFiles
                || (persistedSqlFiles && readiness.SqlGraphContractMatchesCurrent),
            ["csharp_symbol_name_ready"] = !hasCSharpFiles
                || (persistedCSharpFiles && readiness.CSharpSymbolNameContractMatchesCurrent),
            ["csharp_metadata_target_ready"] = !hasCSharpFiles
                || (persistedCSharpFiles
                    && readiness.IndexSnapshot.MetadataTargetCSharp
                        == readiness.CurrentMetadataTargetVersion),
            ["fold_ready"] =
                (readiness.IndexSnapshot.Readiness & DbContext.FoldReadyFlag) != 0,
            ["fold_ready_reason"] =
                (readiness.IndexSnapshot.Readiness & DbContext.FoldReadyFlag) != 0
                    ? null
                    : DegradationReasonCodes.MissingFoldBackfill,
        };
        if (context.Run.MemorySamples != null)
            structured["memory_trace"] = context.Run.MemorySamples;
        return structured;
    }

    private void AddMcpIndexBeforeWriteFailureSignals(
        McpIndexBeforeWriteContext context,
        DbWriter writer,
        JsonObject structured)
    {
        var failures = context.FailureState.Failures;
        var failureArray = new JsonArray();
        foreach (var failure in failures.Take(50))
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
        structured["failed_count"] = failures.Count;
        structured["failures"] = failureArray;
        if (failures.Count > 50)
            structured["failures_truncated"] = failures.Count - 50;
        AddMcpIndexDiagnostics(structured, failures, context.FailureState.Diagnostics);

        var snapshot = context.Readiness.IndexSnapshot;
        var capHits = writer.GetReferenceExtractionCapHits(
            issuesStateAvailable: (snapshot.Readiness & DbContext.IssuesReadyFlag) != 0);
        using var reader = new DbReader(writer.Connection, isReadOnly: true);
        var persistedReadiness = reader.GetPersistedIndexGenerationReadiness(capHits);
        AddIndexGenerationReadinessSignal(structured, persistedReadiness);
        AddReferenceGraphCompletenessSignal(structured, persistedReadiness);
        if (structured["sql_graph_contract_ready"]?.GetValue<bool>() == false)
        {
            AddSqlGraphContractSignal(
                structured,
                new SqlGraphContractSignal(
                    Ready: false,
                    Relevant: true,
                    DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason()));
        }
    }
}
