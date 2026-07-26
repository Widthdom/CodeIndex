using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanFinalOutputContext
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
        internal bool StartedWithNoIndexedFiles { get; init; }
        internal bool ScanHadErrors { get; init; }
        internal long FreshCountFiles { get; init; }
        internal long FreshCountChunks { get; init; }
        internal long FreshCountSymbols { get; init; }
        internal long FreshCountReferences { get; init; }
        internal bool HasSqlFilesAfter { get; init; }
        internal bool GraphTableAvailableAfter { get; init; }
        internal bool IssuesTableAvailableAfter { get; init; }
        internal bool CSharpSymbolNameReadyAfter { get; init; }
        internal bool CSharpMetadataTargetReadyAfter { get; init; }
        internal bool FoldReadyAfter { get; init; }
        internal string? FoldReadyReasonAfter { get; init; }
        internal long ExtractedFiles { get; init; }
        internal long PersistedFiles { get; init; }
        internal long ExtractedChunks { get; init; }
        internal long PersistedChunks { get; init; }
        internal long ExtractedSymbols { get; init; }
        internal long PersistedSymbols { get; init; }
        internal long ExtractedReferences { get; init; }
        internal long PersistedReferences { get; init; }
        internal int FilesCount { get; init; }
        internal int Skipped { get; init; }
        internal int Purged { get; init; }
        internal required FileIndexer.ScanFilesResult ScanResult { get; init; }
        internal required IReadOnlyDictionary<string, int> LanguageCounts { get; init; }
        internal bool HeadChangeDetected { get; init; }
        internal string? PriorIndexedHeadCommit { get; init; }
        internal string? CurrentHeadCommit { get; init; }
        internal string? HeadChangeNotice { get; init; }
        internal bool ShowNextSteps { get; init; }
    }

    private static int WriteFullScanFinalOutput(FullScanFinalOutputContext output)
    {
        if (output.Options.MemoryTrace)
            output.MemorySamples.Add(CaptureMemorySample("commit", output.Stopwatch));
        output.Stopwatch.Stop();
        var memoryTimeline = BuildMemoryTimeline(output.MemorySamples);
        WarnIfMemoryThresholdExceeded(memoryTimeline);
        // Detect cwd drift between option-parsing and finalize. See RunUpdateMode for the
        // rationale; the warning is informational because we already absolutized paths.
        // Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(output.InitialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            output.WarningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            output.Warnings++;
        }
        output.Warnings += AddPostExtractionHookWarnings(output.PostExtractionHooks, output.WarningList);
        var (totalFiles, totalChunks, totalSymbols, totalReferences) =
            output.StartedWithNoIndexedFiles && !output.ScanHadErrors && output.Errors == 0
                ? (output.FreshCountFiles, output.FreshCountChunks, output.FreshCountSymbols, output.FreshCountReferences)
                : output.Writer.GetCounts();
        var signalReader = new DbReader(output.Writer.Connection);
        var referenceExtractionCapHitsAfter = signalReader.GetReferenceExtractionCapHits();
        var persistedReadinessAfter = signalReader.GetPersistedIndexGenerationReadiness(
            referenceExtractionCapHitsAfter);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        if (!output.HasSqlFilesAfter)
        {
            sqlGraphContractSignalAfter = new SqlGraphContractSignal(
                Ready: true,
                Relevant: false,
                DegradedReason: null);
        }
        else if (!sqlGraphContractSignalAfter.Relevant)
        {
            // A failed first SQL target leaves no persisted row for DbReader to classify.
            // Preserve the discovered-language contract in this immediate index response.
            // 最初の SQL target failure で row が無くても index response は degraded を返す。
            sqlGraphContractSignalAfter = new SqlGraphContractSignal(
                Ready: false,
                Relevant: true,
                DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason());
        }
        var hotspotFamilySignalAfter = signalReader.GetHotspotFamilySignal(lang: null);
        var sqlGraphContractReadyAfter = sqlGraphContractSignalAfter.Ready;
        var sqlGraphContractDegradedReasonAfter = sqlGraphContractSignalAfter.DegradedReason;
        var hotspotFamilyReadyAfter = hotspotFamilySignalAfter.Ready;
        var hotspotFamilyDegradedReasonAfter = hotspotFamilySignalAfter.DegradedReason;

        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            persistedReadinessAfter.GraphTableAvailable,
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
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
            {
                Status = output.Errors > 0 ? "partial" : "success",
                Mode = output.Options.Rebuild ? "rebuild" : "incremental",
                Summary = new IndexFullScanSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    FilesExtracted = output.ExtractedFiles,
                    FilesPersisted = output.PersistedFiles,
                    ChunksExtracted = output.ExtractedChunks,
                    ChunksPersisted = output.PersistedChunks,
                    SymbolsExtracted = output.ExtractedSymbols,
                    SymbolsPersisted = output.PersistedSymbols,
                    ReferencesExtracted = output.ExtractedReferences,
                    ReferencesPersisted = output.PersistedReferences,
                    FilesScanned = output.FilesCount,
                    FilesSkipped = output.Skipped,
                    FilesPurged = output.Purged,
                    DanglingSymlinksSkipped = output.ScanResult.DanglingSymlinks.Count,
                    Warnings = output.Warnings,
                    Errors = output.Errors,
                    SymbolsDroppedByKindFilter = output.SymbolsDroppedByKindFilter,
                },
                SymbolKindFilter = output.Options.SymbolKindFilter.ToJsonResult(),
                GraphTableAvailable = persistedReadinessAfter.GraphTableAvailable,
                GraphDataCurrent = persistedReadinessAfter.GraphDataCurrent,
                IndexComplete = persistedReadinessAfter.IndexComplete,
                IndexIncompleteReasons = persistedReadinessAfter.IndexComplete
                    ? null
                    : persistedReadinessAfter.IndexIncompleteReasons,
                ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
                ReferenceGraphComplete = persistedReadinessAfter.ReferenceGraphComplete,
                ReferenceGraphIncompleteReasons = persistedReadinessAfter.ReferenceGraphComplete
                    ? null
                    : persistedReadinessAfter.ReferenceGraphIncompleteReasons,
                ReferenceExtractionCapHits = referenceExtractionCapHitsAfter,
                ErrorCode = output.Errors > 0 ? CommandErrorCodes.IndexPartial : null,
                IssuesTableAvailable = output.IssuesTableAvailableAfter,
                SqlGraphContractReady = sqlGraphContractReadyAfter,
                SqlGraphContractDegradedReason = sqlGraphContractDegradedReasonAfter,
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
                HeadChanged = output.HeadChangeDetected,
                PriorIndexedHeadCommit = output.PriorIndexedHeadCommit,
                CurrentHeadCommit = output.CurrentHeadCommit,
                HeadChangeNotice = output.HeadChangeNotice,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = output.InitialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = output.ErrorList.Count > 0 ? output.ErrorList : null,
                FileErrors = output.FileErrorList.Count > 0 ? output.FileErrorList : null,
                Warnings = output.WarningList.Count > 0 ? output.WarningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = output.Stopwatch.ElapsedMilliseconds,
            }, output.JsonContext.IndexFullScanJsonResult));
        }
        else if (!output.Options.Quiet)
        {
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine("Done.");
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Files", ConsoleUi.FormatNumber(totalFiles), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", ConsoleUi.FormatNumber(totalChunks), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", ConsoleUi.FormatNumber(totalSymbols), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Refs", ConsoleUi.FormatNumber(totalReferences), indent: "  "));
            if (output.Skipped > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", $"{ConsoleUi.FormatNumber(output.Skipped)} (unchanged)", indent: "  "));
            if (output.ScanResult.DanglingSymlinks.Count > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Dangling symlinks", $"{ConsoleUi.FormatNumber(output.ScanResult.DanglingSymlinks.Count)} output.Skipped", indent: "  "));
            if (output.Options.Verbose && output.ScanResult.UnknownExtensionFiles.Count > 0)
            {
                CommandOutputWriter.WriteLine($"  Unknown extension files: {ConsoleUi.FormatNumber(output.ScanResult.UnknownExtensionFiles.Count)}");
                foreach (var relPath in output.ScanResult.UnknownExtensionFiles.Take(5))
                    CommandOutputWriter.WriteLine($"    {relPath}");
                if (output.ScanResult.UnknownExtensionFiles.Count > 5)
                    CommandOutputWriter.WriteLine($"    ... {ConsoleUi.FormatNumber(output.ScanResult.UnknownExtensionFiles.Count - 5)} more");
            }
            if (output.Warnings > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", ConsoleUi.FormatNumber(output.Warnings), indent: "  "));
            if (output.Errors > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Errors", ConsoleUi.FormatNumber(output.Errors), indent: "  "));
            if (output.SymbolsDroppedByKindFilter > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Filtered symbols", ConsoleUi.FormatNumber(output.SymbolsDroppedByKindFilter), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine(
                "Index",
                persistedReadinessAfter.IndexComplete ? "complete" : "incomplete",
                indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine(
                "Graph",
                persistedReadinessAfter.ReferenceGraphComplete ? "ready" : "degraded",
                indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Issues", output.IssuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# names", output.CSharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", output.CSharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Fold", output.FoldReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(output.Stopwatch.Elapsed, output.Options.DurationFormat), indent: "  "));
            CommandOutputWriter.WriteLine();
            if (output.Errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to index. Fix the reported files or permissions, then rerun `cdidx index \"{output.ProjectRoot}\"` to restore a fully ready index.");
            if (!persistedReadinessAfter.IndexComplete)
                ConsoleUi.PrintWarning($"Index generation is incomplete: {string.Join(", ", persistedReadinessAfter.IndexIncompleteReasons)}.");
            if (!persistedReadinessAfter.ReferenceGraphComplete)
                ConsoleUi.PrintWarning($"Reference graph is incomplete: {string.Join(", ", persistedReadinessAfter.ReferenceGraphIncompleteReasons)}.");
            if (!persistedReadinessAfter.GraphTableAvailable || !output.IssuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !output.CSharpSymbolNameReadyAfter || !output.CSharpMetadataTargetReadyAfter || !output.FoldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(persistedReadinessAfter.GraphTableAvailable, output.IssuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, output.CSharpSymbolNameReadyAfter, output.CSharpMetadataTargetReadyAfter, output.FoldReadyAfter, output.FoldReadyReasonAfter, output.ProjectRoot, output.ResolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
            if (output.Errors == 0
                && persistedReadinessAfter.IndexComplete
                && output.ShowNextSteps)
                ConsoleUi.PrintIndexCompleteSummary(output.ProjectRoot, output.ResolvedDbPath, incremental: !output.Options.Rebuild, output.FilesCount, output.LanguageCounts);
        }

        if (!output.Options.Json && !output.Options.Quiet && output.Stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            ConsoleUi.EmitCompletionNotification(
                output.Options.NotifyMode,
                persistedReadinessAfter.IndexComplete
                    ? $"cdidx index complete ({ConsoleUi.Counted(output.FilesCount, "file", format: "N0")})"
                    : $"cdidx index finished with omissions ({ConsoleUi.Counted(output.FilesCount, "file", format: "N0")})");

        return output.Errors > 0 && !output.Options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }
}
