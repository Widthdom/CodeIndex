using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateSnapshotFailureContext
    {
        internal required DbWriter Writer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required Stopwatch Stopwatch { get; init; }
        internal required CliJsonSerializerContext JsonContext { get; init; }
        internal required string ProjectRoot { get; init; }
        internal int PriorReadiness { get; init; }
        internal bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal bool PriorMetadataTargetCsharpMatchesCurrent { get; init; }
        internal string? PriorFoldVersion { get; init; }
        internal string? PriorFoldFingerprint { get; init; }
        internal required string CurrentFoldVersion { get; init; }
        internal required string CurrentFoldFingerprint { get; init; }
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal int Skipped { get; init; }
        internal int Warnings { get; init; }
        internal int SymbolsDroppedByKindFilter { get; init; }
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
        internal required Action<string, string, string> RecordCSharpWorkspaceDrift { get; init; }
        internal required Func<int> GetErrorCount { get; init; }
    }

    private static int WriteUpdateSnapshotFailure(
        string changedPath,
        UpdateSnapshotFailureContext failure)
    {
        var formattedPath = FormatCSharpWorkspaceSnapshotPath(failure.ProjectRoot, changedPath);
        failure.RecordCSharpWorkspaceDrift(
            formattedPath,
            "Directory entries or scan configuration changed after expanded C# discovery.",
            "csharp_workspace_validation");
        var errors = failure.GetErrorCount();

        failure.Stopwatch.Stop();
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = failure.Writer.GetCounts();
        var graphTableAvailable = (failure.PriorReadiness & DbContext.GraphReadyFlag) != 0;
        var issuesTableAvailable = (failure.PriorReadiness & DbContext.IssuesReadyFlag) != 0;
        var referenceExtractionCapHits = failure.Writer.GetReferenceExtractionCapHits(issuesTableAvailable);
        // Keep early failure rendering observational: a default DbReader would recover
        // interrupted FTS state on this writable connection before the write barrier.
        using var signalReader = new DbReader(failure.Writer.Connection, isReadOnly: true);
        var sqlGraphContractSignal = signalReader.GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignal = signalReader.GetHotspotFamilySignal(lang: null);
        var hasCSharpFiles = failure.Writer.HasAnyFilesWithLanguage("csharp");
        var csharpSymbolNameReady = !hasCSharpFiles || failure.CSharpSymbolNameContractMatchesCurrent;
        var csharpMetadataTargetReady = !hasCSharpFiles || failure.PriorMetadataTargetCsharpMatchesCurrent;
        var foldReady = (failure.PriorReadiness & DbContext.FoldReadyFlag) != 0;
        var memoryTimeline = BuildMemoryTimeline(failure.MemorySamples);

        if (failure.Options.Json)
        {
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexUpdateJsonResult
            {
                Status = "partial",
                Mode = "update",
                Summary = new IndexUpdateSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    Updated = 0,
                    Removed = 0,
                    Skipped = failure.Skipped,
                    Warnings = failure.Warnings,
                    Errors = errors,
                    SymbolsDroppedByKindFilter = failure.SymbolsDroppedByKindFilter,
                    FtsOptimizeRan = false,
                    FtsMergeRan = false,
                },
                SymbolKindFilter = failure.Options.SymbolKindFilter.ToJsonResult(),
                GraphTableAvailable = graphTableAvailable,
                GraphDataCurrent = false,
                IndexComplete = false,
                ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
                ReferenceGraphComplete = signalReader.IsReferenceGraphComplete(
                    referenceExtractionCapHits),
                ReferenceExtractionCapHits = referenceExtractionCapHits,
                ErrorCode = CommandErrorCodes.IndexPartial,
                IssuesTableAvailable = issuesTableAvailable,
                SqlGraphContractReady = sqlGraphContractSignal.Ready,
                SqlGraphContractDegradedReason = sqlGraphContractSignal.DegradedReason,
                HotspotFamilyReady = hotspotFamilySignal.Ready,
                HotspotFamilyDegradedReason = hotspotFamilySignal.DegradedReason,
                CSharpSymbolNameReady = csharpSymbolNameReady,
                CSharpMetadataTargetReady = csharpMetadataTargetReady,
                FoldReady = foldReady,
                FoldReadyReason = foldReady ? null : GetFoldReadyReason(
                    backfillReady: false,
                    failure.PriorFoldVersion == failure.CurrentFoldVersion,
                    failure.PriorFoldFingerprint == failure.CurrentFoldFingerprint),
                Errors = failure.ErrorList,
                FileErrors = failure.FileErrorList,
                Warnings = failure.WarningList.Count > 0 ? failure.WarningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = failure.Stopwatch.ElapsedMilliseconds,
            }, failure.JsonContext.IndexUpdateJsonResult));
        }
        else if (!failure.Options.Quiet)
        {
            ConsoleUi.TryWriteErrorLine(
                $"Update stopped before index-data mutation because the scan snapshot changed: {formattedPath}");
        }

        return failure.Options.AllowPartial
            ? CommandExitCodes.Success
            : CommandExitCodes.PartialResult;
    }
}
