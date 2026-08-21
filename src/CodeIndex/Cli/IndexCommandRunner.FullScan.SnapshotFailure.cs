using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanSnapshotFailureContext
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
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal required IReadOnlyDictionary<string, int> LanguageCounts { get; init; }
        internal required IReadOnlyList<string> UnknownExtensionFiles { get; init; }
        internal int FilesCount { get; init; }
        internal int Skipped { get; init; }
        internal int DanglingSymlinkCount { get; init; }
        internal int Warnings { get; init; }
        internal int Errors { get; set; }
        internal int SymbolsDroppedByKindFilter { get; init; }
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
    }

    private static int WriteFullScanSnapshotFailure(
        string changedPath,
        FullScanSnapshotFailureContext failure)
    {
        var formattedPath = FormatCSharpWorkspaceSnapshotPath(failure.ProjectRoot, changedPath);
        var exception = new IOException(
            "Directory entries or scan configuration changed after source discovery; rerun indexing from a stable workspace snapshot.");
        failure.Errors++;
        failure.ErrorList.Add(new CliJsonMessage(formattedPath, FormatIndexFileException(exception)));
        if (failure.FileErrorList.Count < PartialIndexFileErrorLimit)
            failure.FileErrorList.Add(BuildIndexFileError(formattedPath, "csharp_workspace_validation", exception));

        failure.Stopwatch.Stop();
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = failure.Writer.GetCounts();
        var graphTableAvailable = (failure.PriorReadiness & DbContext.GraphReadyFlag) != 0;
        var issuesTableAvailable = (failure.PriorReadiness & DbContext.IssuesReadyFlag) != 0;
        var referenceExtractionCapHits = failure.Writer.GetReferenceExtractionCapHits(issuesTableAvailable);
        // The connection is writable, but failure diagnostics must not trigger the
        // DbReader constructor's interrupted-FTS recovery before the write barrier.
        using var signalReader = new DbReader(failure.Writer.Connection, isReadOnly: true);
        var discoveredCSharpFiles = failure.LanguageCounts.ContainsKey("csharp");
        var discoveredSqlFiles = failure.LanguageCounts.ContainsKey("sql");
        var persistedCSharpFiles = failure.Writer.HasAnyFilesWithLanguage("csharp");
        var persistedSqlFiles = failure.Writer.HasAnyFilesWithLanguage("sql");
        var hasCSharpFiles = discoveredCSharpFiles || persistedCSharpFiles;
        var hasSqlFiles = discoveredSqlFiles || persistedSqlFiles;
        var sqlGraphContractSignal = signalReader.GetSqlGraphContractSignal(lang: null);
        if (!hasSqlFiles)
        {
            sqlGraphContractSignal = new SqlGraphContractSignal(
                Ready: true,
                Relevant: false,
                DegradedReason: null);
        }
        else if (!sqlGraphContractSignal.Relevant)
        {
            // The write barrier can fail after discovery but before the first target is
            // persisted. Keep positive language evidence in the immediate response.
            // write 前 barrier failure でも発見済み language の degraded signal を保持する。
            sqlGraphContractSignal = new SqlGraphContractSignal(
                Ready: false,
                Relevant: true,
                DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason());
        }
        var hotspotFamilySignal = signalReader.GetHotspotFamilySignal(lang: null);
        var csharpSymbolNameReady = !hasCSharpFiles
            || (persistedCSharpFiles && failure.CSharpSymbolNameContractMatchesCurrent);
        var csharpMetadataTargetReady = !hasCSharpFiles
            || (persistedCSharpFiles && failure.PriorMetadataTargetCsharpMatchesCurrent);
        var foldReady = (failure.PriorReadiness & DbContext.FoldReadyFlag) != 0;
        var memoryTimeline = BuildMemoryTimeline(failure.MemorySamples);
        var unknownExtensionClassification = UnknownExtensionClassifier.Classify(
            failure.UnknownExtensionFiles);
        var unknownExtensionGroups = unknownExtensionClassification.Groups
            .Take(UnknownExtensionClassifier.MaxCompletionGroups)
            .ToList();
        var unknownExtensionGroupOmittedCount = Math.Max(
            0,
            unknownExtensionClassification.GroupCount - unknownExtensionGroups.Count);
        var warningCount = failure.Warnings;
        if (unknownExtensionClassification.ActionableFileCount > 0)
        {
            var warning = $"{unknownExtensionClassification.ActionableFileCount} file(s) were excluded because no language mapping or extractor was available. {UnknownExtensionClassifier.GetGuidance(unknownExtensionClassification)}";
            failure.WarningList.Add(new CliJsonMessage("<unknown_extensions>", warning));
            warningCount++;
        }

        if (failure.Options.Json)
        {
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
            {
                Status = "partial",
                Mode = failure.Options.Rebuild ? "rebuild" : "incremental",
                UnknownExtensionFileCount = failure.UnknownExtensionFiles.Count,
                UnknownExtensionGroups = unknownExtensionGroups.Count > 0 ? unknownExtensionGroups : null,
                UnknownExtensionGroupCount = unknownExtensionClassification.GroupCount,
                UnknownExtensionGroupsTruncated = unknownExtensionGroupOmittedCount > 0,
                UnknownExtensionGroupLimit = UnknownExtensionClassifier.MaxCompletionGroups,
                UnknownExtensionGroupOmittedCount = unknownExtensionGroupOmittedCount,
                UnknownExtensionDiagnosticsScope = "workspace",
                UnknownExtensionFileCountLowerBound = true,
                UnknownExtensionGuidance = failure.UnknownExtensionFiles.Count > 0
                    ? UnknownExtensionClassifier.GetGuidance(unknownExtensionClassification)
                    : null,
                Summary = new IndexFullScanSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    FilesScanned = failure.FilesCount,
                    FilesSkipped = failure.Skipped,
                    FilesPurged = 0,
                    DanglingSymlinksSkipped = failure.DanglingSymlinkCount,
                    Warnings = warningCount,
                    Errors = failure.Errors,
                    SymbolsDroppedByKindFilter = failure.SymbolsDroppedByKindFilter,
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
                    failure.PriorFoldVersion == NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    failure.PriorFoldFingerprint == NameFold.Fingerprint()),
                Errors = failure.ErrorList,
                FileErrors = failure.FileErrorList,
                Warnings = failure.WarningList.Count > 0 ? failure.WarningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = failure.Stopwatch.ElapsedMilliseconds,
            }, failure.JsonContext.IndexFullScanJsonResult));
        }
        else if (!failure.Options.Quiet)
        {
            ConsoleUi.TryWriteErrorLine(
                $"Indexing stopped before index-data mutation because the scan snapshot changed: {formattedPath}");
        }

        return failure.Options.AllowPartial
            ? CommandExitCodes.Success
            : CommandExitCodes.PartialResult;
    }
}
