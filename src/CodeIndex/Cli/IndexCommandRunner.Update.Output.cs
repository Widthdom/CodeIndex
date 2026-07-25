using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateFinalOutputContext
    {
        internal required DbWriter Writer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required Stopwatch Stopwatch { get; init; }
        internal required CliJsonSerializerContext JsonContext { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required string ResolvedDbPath { get; init; }
        internal string? InitialCwd { get; init; }
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal PostExtractionHookRunner? PostExtractionHooks { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal int Warnings { get; set; }
        internal int Errors { get; init; }
        internal int SymbolsDroppedByKindFilter { get; init; }
        internal bool GraphTableAvailableAfter { get; init; }
        internal bool IssuesTableAvailableAfter { get; init; }
        internal bool CSharpSymbolNameReadyAfter { get; init; }
        internal bool CSharpMetadataTargetReadyAfter { get; init; }
        internal bool FoldReadyAfter { get; init; }
        internal string? FoldReadyReasonAfter { get; init; }
        internal int Updated { get; init; }
        internal int Removed { get; init; }
        internal int Skipped { get; init; }
        internal bool FtsMergeRan { get; init; }
    }

    private static int WriteUpdateFinalOutput(UpdateFinalOutputContext output)
    {
        output.Stopwatch.Stop();
        var memoryTimeline = BuildMemoryTimeline(output.MemorySamples);
        WarnIfMemoryThresholdExceeded(memoryTimeline);
        // Detect cwd drift between option-parsing and finalize. Paths used in this run are
        // already absolute, but a drifted cwd is a strong signal that an embedded host or
        // signal handler mutated process state -- surface it so the operator can correct
        // their hosting code. Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(output.InitialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            output.WarningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            output.Warnings++;
        }
        output.Warnings += AddPostExtractionHookWarnings(output.PostExtractionHooks, output.WarningList);
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = output.Writer.GetCounts();
        var signalReader = new DbReader(output.Writer.Connection);
        var referenceExtractionCapHitsAfter = signalReader.GetReferenceExtractionCapHits();
        var referenceGraphCompleteAfter = signalReader.IsReferenceGraphComplete(
            referenceExtractionCapHitsAfter);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        var hdlGraphContractSignalAfter = signalReader.GetHdlGraphContractSignal(lang: null);
        var hotspotFamilySignalAfter = signalReader.GetHotspotFamilySignal(lang: null);
        var sqlGraphContractReadyAfter = sqlGraphContractSignalAfter.Ready;
        var sqlGraphContractDegradedReasonAfter = sqlGraphContractSignalAfter.DegradedReason;
        var hotspotFamilyReadyAfter = hotspotFamilySignalAfter.Ready;
        var hotspotFamilyDegradedReasonAfter = hotspotFamilySignalAfter.DegradedReason;

        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            output.GraphTableAvailableAfter,
            output.IssuesTableAvailableAfter,
            sqlGraphContractReadyAfter,
            hotspotFamilyReadyAfter,
            output.CSharpSymbolNameReadyAfter,
            output.CSharpMetadataTargetReadyAfter,
            output.FoldReadyAfter,
            output.FoldReadyReasonAfter,
            output.ProjectRoot,
            output.ResolvedDbPath);

        if (output.Options.Json)
        {
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexUpdateJsonResult
            {
                Status = output.Errors > 0 ? "partial" : "success",
                Mode = "update",
                Summary = new IndexUpdateSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    Updated = output.Updated,
                    Removed = output.Removed,
                    Skipped = output.Skipped,
                    Warnings = output.Warnings,
                    Errors = output.Errors,
                    SymbolsDroppedByKindFilter = output.SymbolsDroppedByKindFilter,
                    FtsOptimizeRan = false,
                    FtsMergeRan = output.FtsMergeRan,
                },
                SymbolKindFilter = output.Options.SymbolKindFilter.ToJsonResult(),
                GraphTableAvailable = output.GraphTableAvailableAfter,
                GraphDataCurrent = output.Errors == 0
                    && output.GraphTableAvailableAfter
                    && referenceGraphCompleteAfter
                    && hdlGraphContractSignalAfter.Ready,
                IndexComplete = output.Errors == 0,
                ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
                ReferenceGraphComplete = referenceGraphCompleteAfter,
                ReferenceExtractionCapHits = referenceExtractionCapHitsAfter,
                ErrorCode = output.Errors > 0 ? CommandErrorCodes.IndexPartial : null,
                IssuesTableAvailable = output.IssuesTableAvailableAfter,
                SqlGraphContractReady = sqlGraphContractReadyAfter,
                SqlGraphContractDegradedReason = sqlGraphContractDegradedReasonAfter,
                HdlGraphContractReady = hdlGraphContractSignalAfter.Ready,
                HdlGraphContractDegradedReason = hdlGraphContractSignalAfter.DegradedReason,
                HotspotFamilyReady = hotspotFamilyReadyAfter,
                HotspotFamilyDegradedReason = hotspotFamilyDegradedReasonAfter,
                CSharpSymbolNameReady = output.CSharpSymbolNameReadyAfter,
                CSharpMetadataTargetReady = output.CSharpMetadataTargetReadyAfter,
                // #86 codex review: expose fold-readiness so AI clients can decide whether
                // `--exact` will use the Unicode fold path or fall back to ASCII NOCASE.
                // #86 codex: AI クライアントが --exact の経路を判断できるよう fold_ready を返す。
                FoldReady = output.FoldReadyAfter,
                FoldReadyReason = output.FoldReadyAfter ? null : output.FoldReadyReasonAfter,
                DegradedReason = foldOnlyRemediation?.DegradedReason,
                RecommendedAction = foldOnlyRemediation?.RecommendedAction,
                AlternativeAction = foldOnlyRemediation?.AlternativeAction,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = output.InitialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = output.ErrorList.Count > 0 ? output.ErrorList : null,
                FileErrors = output.FileErrorList.Count > 0 ? output.FileErrorList : null,
                Warnings = output.WarningList.Count > 0 ? output.WarningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = output.Stopwatch.ElapsedMilliseconds,
            }, output.JsonContext.IndexUpdateJsonResult));
        }
        else
        {
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine("Done.");
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{ConsoleUi.FormatNumber(totalFiles)} (total in DB)", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", ConsoleUi.FormatNumber(totalChunks), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", ConsoleUi.FormatNumber(totalSymbols), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Refs", ConsoleUi.FormatNumber(totalReferences), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Updated", ConsoleUi.FormatNumber(output.Updated), indent: "  "));
            if (output.Removed > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Removed", ConsoleUi.FormatNumber(output.Removed), indent: "  "));
            if (output.Skipped > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", ConsoleUi.FormatNumber(output.Skipped), indent: "  "));
            if (output.Warnings > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", ConsoleUi.FormatNumber(output.Warnings), indent: "  "));
            if (output.Errors > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Errors", ConsoleUi.FormatNumber(output.Errors), indent: "  "));
            if (output.SymbolsDroppedByKindFilter > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Filtered symbols", ConsoleUi.FormatNumber(output.SymbolsDroppedByKindFilter), indent: "  "));
            if (output.FtsMergeRan) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("FTS merge", "completed", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Graph", output.GraphTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Issues", output.IssuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# names", output.CSharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", output.CSharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Fold", output.FoldReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(output.Stopwatch.Elapsed, output.Options.DurationFormat), indent: "  "));
            CommandOutputWriter.WriteLine();
            if (output.Errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to update. Fix the reported files or permissions, then rerun `cdidx index \"{output.ProjectRoot}\"` to restore a fully ready index.");
            if (!output.GraphTableAvailableAfter || !output.IssuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !output.CSharpSymbolNameReadyAfter || !output.CSharpMetadataTargetReadyAfter || !output.FoldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(output.GraphTableAvailableAfter, output.IssuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, output.CSharpSymbolNameReadyAfter, output.CSharpMetadataTargetReadyAfter, output.FoldReadyAfter, output.FoldReadyReasonAfter, output.ProjectRoot, output.ResolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
        }

        if (!output.Options.Json && !output.Options.Quiet && output.Stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            ConsoleUi.EmitCompletionNotification(
                output.Options.NotifyMode,
                $"cdidx index update complete ({ConsoleUi.Counted(output.Updated + output.Removed + output.Skipped, "file", format: "N0")})");

        return output.Errors > 0 && !output.Options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }
}
