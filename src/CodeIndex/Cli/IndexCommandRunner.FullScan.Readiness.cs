using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanReadinessContext
    {
        internal required DbWriter Writer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required Stopwatch Stopwatch { get; init; }
        internal required DateTime RunStartedAtUtc { get; init; }
        internal required string ProjectRoot { get; init; }
        internal string? CurrentHeadCommit { get; init; }
        internal List<string>? IndexRunDiagnostics { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required int Errors { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required int Processed { get; init; }
        internal required int FileCount { get; init; }
        internal required int Skipped { get; init; }
        internal required int Purged { get; init; }
        internal required bool ScanHadErrors { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool HasCSharpFilesAfter { get; init; }
        internal required bool CSharpSourceEvidenceComplete { get; init; }
        internal required bool CSharpSourceEvidenceForStamp { get; init; }
        internal required bool PreservePriorPositiveCSharpSourceNoOp { get; init; }
        internal required bool CSharpMetadataTargetsNeedRefresh { get; init; }
        internal required bool TypeScriptAugmentationNeedsRefresh { get; init; }
        internal DbWriter.TypeScriptAugmentationDirtyNameScope? TypeScriptAugmentationDirtyNames { get; init; }
        internal required bool UseScopedTypeScriptAugmentationRefresh { get; init; }
        internal required IReadOnlyDictionary<string, int> LanguageCounts { get; init; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get; init; }
        internal required IReadOnlyDictionary<string, string?> PriorHotspotFamilyVersions { get; init; }
        internal required IReadOnlyDictionary<string, string?> PriorHotspotFamilyMarkerFingerprints { get; init; }
        internal required IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> CurrentHotspotFamilyMarkerFingerprints { get; init; }
        internal required IReadOnlyCollection<string> IndexedSymbolExtractorLanguages { get; init; }
        internal HashSet<string>? SkippedSymbolExtractorLanguages { get; init; }
        internal string? PriorFoldVersion { get; init; }
        internal string? PriorFoldFingerprint { get; init; }
        internal required FileIndexer.ScanFilesResult ScanResult { get; init; }
        internal required ReadableFileByteTracker ReadableFileBytes { get; init; }
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal required long FreshCountReferences { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
    }

    private sealed record FullScanReadinessResult(
        bool GraphTableAvailable,
        bool IssuesTableAvailable,
        bool CSharpSymbolNameReady,
        bool CSharpMetadataTargetReady,
        bool FoldReady,
        string? FoldReadyReason,
        long FreshCountReferences);

    private static FullScanReadinessResult FinalizeFullScanReadiness(
        FullScanReadinessContext context)
    {
        var writer = context.Writer;
        var options = context.Options;
        var cancellationToken = context.CancellationToken;
        var graphTableAvailableAfter = false;
        var issuesTableAvailableAfter = false;
        var csharpSymbolNameReadyAfter = !context.HasCSharpFilesAfter;
        var csharpMetadataTargetReadyAfter = !context.HasCSharpFilesAfter;
        var foldReadyAfter = false;
        string? foldReadyReasonAfter = null;
        var freshCountReferences = context.FreshCountReferences;

        if (context.Errors > 0)
        {
            if (!options.SymbolsOnly)
            {
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            writer.MarkIndexIncomplete(["file_index_error"]);
            writer.SetMetaValues(
                (DbContext.LastFailedIndexRunStatusMetaKey, "partial"),
                (DbContext.LastFailedIndexRunModeMetaKey, options.Rebuild ? "rebuild" : "incremental"),
                (DbContext.LastFailedIndexRunStartedAtMetaKey, context.RunStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunDurationMsMetaKey, context.Stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesProcessedMetaKey, context.Processed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesTotalMetaKey, context.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunErrorCodeMetaKey, CommandErrorCodes.IndexPartial),
                (DbContext.LastFailedIndexRunReasonMetaKey, "file_index_error"),
                (DbContext.LastFailedIndexRunProgressPersistedMetaKey, true.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunRecoveryHintMetaKey, "Fix the reported file/extractor error, then rerun the same index command. Successful files and graph edges remain persisted; a rebuild is not required."),
                (DbContext.LastFailedIndexRunFileErrorsMetaKey, JsonSerializer.Serialize(context.FileErrorList, StatusMetadataJsonContext.Default.ListStatusIndexFileError)));
        }

        if (context.Errors == 0)
        {
            writer.MarkIssuesReady();
            if (!options.SymbolsOnly)
            {
                writer.MarkGraphReady();
                writer.MarkHdlGraphContractReady();
            }
            writer.MarkIndexReaderContractsReady(options.SymbolsOnly);
            if (!options.SymbolsOnly
                && !context.ScanHadErrors
                && context.CSharpSourceEvidenceComplete
                && !context.PreservePriorPositiveCSharpSourceNoOp)
            {
                writer.SetCSharpStaticInterfaceSourceEvidence(
                    context.CSharpSourceEvidenceForStamp);
            }
            if (context.HasCSharpFilesAfter)
            {
                if (context.CSharpMetadataTargetsNeedRefresh)
                {
                    FullScanCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets(cancellationToken);
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            graphTableAvailableAfter = !options.SymbolsOnly;
            issuesTableAvailableAfter = true;
            csharpSymbolNameReadyAfter = true;

            if (!options.SymbolsOnly
                && (context.TypeScriptAugmentationNeedsRefresh
                    || context.TypeScriptAugmentationDirtyNames?.RequiresRefresh == true))
            {
                if (context.StartedWithNoIndexedFiles
                    && !context.LanguageCounts.ContainsKey("typescript"))
                {
                    writer.MarkTypeScriptAugmentationReady();
                }
                else
                {
                    FullScanTypeScriptAugmentationRebuildForTesting?.Invoke();
                    var augmentationReferences = writer.RebuildTypeScriptAugmentationReferences(
                        context.ProjectRoot,
                        context.UseScopedTypeScriptAugmentationRefresh
                            ? context.TypeScriptAugmentationDirtyNames?.DirtyNames
                            : null,
                        cancellationToken);
                    if (context.StartedWithNoIndexedFiles)
                        freshCountReferences += augmentationReferences;
                }
            }
            RestampHotspotFamilyTrustForFullScan(
                writer,
                context.ReusedHotspotFamilyLanguages,
                context.PriorHotspotFamilyVersions,
                context.PriorHotspotFamilyMarkerFingerprints,
                context.CurrentHotspotFamilyMarkerFingerprints);
            if (!options.SymbolsOnly)
            {
                if (writer.CSharpFamilyTrustAllowsReferenceIdentityReady())
                    writer.MarkReferenceIdentityContractReady();
                else
                    writer.ClearReferenceIdentityContractReady();
            }
            writer.StampSymbolExtractorVersions(context.IndexedSymbolExtractorLanguages);
            writer.StampDynamicReferenceGraphContracts(context.IndexedSymbolExtractorLanguages);

            IReadOnlyCollection<string> skippedSymbolExtractorLanguageSet =
                context.SkippedSymbolExtractorLanguages is null
                    ? Array.Empty<string>()
                    : context.SkippedSymbolExtractorLanguages;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = context.PriorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = context.PriorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent
                && foldFingerprintMatchesCurrent
                && writer.SymbolExtractorVersionsMatchCurrent(skippedSymbolExtractorLanguageSet);
            if (context.Skipped == 0 || canRestampExistingFoldTrust)
            {
                var foldStampResult = writer.MarkFoldReadyWithResult(
                    stampCurrentSymbolExtractorVersions: context.Skipped == 0,
                    symbolExtractorLanguagesToStamp:
                        context.Skipped == 0 ? context.IndexedSymbolExtractorLanguages : null);
                foldReadyAfter = foldStampResult == FoldReadyStampResult.Ready;
                if (foldStampResult == FoldReadyStampResult.MissingBackfill)
                {
                    foldReadyReasonAfter = GetFoldReadyReason(
                        false,
                        foldVersionMatchesCurrent,
                        foldFingerprintMatchesCurrent);
                }
                else if (foldStampResult == FoldReadyStampResult.NonCurrentFoldValues)
                {
                    foldReadyReasonAfter = DegradationReasonCodes.FoldRowsNotRestamped;
                }
            }
            else
            {
                var backfillReady =
                    writer.AllFoldedColumnsBackfilled(skippedSymbolExtractorLanguageSet);
                foldReadyReasonAfter = GetFoldReadyReason(
                    backfillReady,
                    foldVersionMatchesCurrent,
                    foldFingerprintMatchesCurrent);
            }

            StampWriterVersionAndSymbolKindFilter(
                writer,
                ConsoleUi.LoadVersion(),
                options.SymbolKindFilter.Signature);
            context.WriteProjectRootOnce();
            writer.WriteUnknownExtensionFileMetadata(context.ScanResult.UnknownExtensionFiles);
            var currentHeadBranch =
                GitHelper.TryGetHeadBranch(context.ProjectRoot, cancellationToken);
            var lastFullScanElapsedMs = context.Stopwatch.ElapsedMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            writer.SetMetaValues(
                (DbContext.IndexedHeadCommitMetaKey, context.CurrentHeadCommit),
                (DbContext.IndexedHeadCommitBranchMetaKey, currentHeadBranch),
                (DbContext.LastFullScanElapsedMsMetaKey, lastFullScanElapsedMs));
            TryStampIndexedHeadMetadata(
                writer,
                context.CurrentHeadCommit,
                currentHeadBranch,
                context.IndexRunDiagnostics);
            StampWorkspacePathCaseSensitivity(
                writer,
                context.ProjectRoot,
                context.IndexRunDiagnostics,
                cancellationToken);
            StampIndexedSymlinkPolicy(
                writer,
                options.SymlinkPolicy,
                context.IndexRunDiagnostics);
            if (options.MemoryTrace)
                context.MemorySamples.Add(CaptureMemorySample("finalize", context.Stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(context.MemorySamples);
            var bytesRead = context.ReadableFileBytes.MeasureRemaining();
            StampLastIndexRunMetadata(
                writer,
                options.Rebuild ? "rebuild" : "incremental",
                context.RunStartedAtUtc,
                context.Stopwatch.ElapsedMilliseconds,
                context.FileCount,
                context.Skipped,
                context.Errors,
                bytesRead.BytesRead,
                bytesRead.SkippedFileCount,
                context.Processed,
                context.Purged,
                memoryTimelineForStamp,
                context.IndexRunDiagnostics,
                writer.GetReferenceExtractionCapHits(issuesTableAvailableAfter),
                writer.GetPersistedIndexOmissionReasons());
        }

        return new FullScanReadinessResult(
            graphTableAvailableAfter,
            issuesTableAvailableAfter,
            csharpSymbolNameReadyAfter,
            csharpMetadataTargetReadyAfter,
            foldReadyAfter,
            foldReadyReasonAfter,
            freshCountReferences);
    }
}
