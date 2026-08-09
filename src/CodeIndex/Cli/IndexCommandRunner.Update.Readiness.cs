using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateReadinessContext
    {
        internal required DbWriter Writer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required Stopwatch Stopwatch { get; init; }
        internal required DateTime RunStartedAtUtc { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required int PriorReadiness { get; init; }
        internal string? PriorFoldVersion { get; init; }
        internal string? PriorFoldFingerprint { get; init; }
        internal required string CurrentFoldVersion { get; init; }
        internal required string CurrentFoldFingerprint { get; init; }
        internal required bool PriorSymbolExtractorVersionsMatchCurrent { get; init; }
        internal required bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal required bool PriorMetadataTargetCsharpMatchesCurrent { get; init; }
        internal required bool SqlGraphContractMatchesCurrent { get; init; }
        internal required bool HdlGraphContractMatchesCurrent { get; init; }
        internal required IReadOnlyDictionary<string, string?> PriorHotspotFamilyVersions { get; init; }
        internal required IReadOnlyDictionary<string, string?> PriorHotspotFamilyMarkerFingerprints { get; init; }
        internal required IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> CurrentHotspotFamilyMarkerFingerprints { get; init; }
        internal required bool ReadinessDemoted { get; init; }
        internal required bool MutualRecursionRefreshNeeded { get; init; }
        internal required bool ReferenceIdentityContractMatchedBeforeMutation { get; init; }
        internal required bool CSharpMetadataTargetsNeedRefresh { get; init; }
        internal required bool TypeScriptAugmentationNeedsRefresh { get; init; }
        internal DbWriter.TypeScriptAugmentationDirtyNameScope? TypeScriptAugmentationDirtyNames { get; init; }
        internal required bool UseScopedTypeScriptAugmentationRefresh { get; init; }
        internal required int Updated { get; init; }
        internal required int Removed { get; init; }
        internal required int Skipped { get; init; }
        internal required int TargetCount { get; init; }
        internal required int Errors { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<IndexMemorySampleJsonResult> MemorySamples { get; init; }
        internal required bool TypeScriptAugmentationOwnsDeferredReferenceGraphRefresh { get; init; }
        internal required IReadOnlyList<string> FullyRefreshedDynamicGraphLanguages { get; init; }
    }

    private sealed record UpdateReadinessResult(
        bool GraphTableAvailable,
        bool IssuesTableAvailable,
        bool CSharpSymbolNameReady,
        bool CSharpMetadataTargetReady,
        bool FoldReady,
        string? FoldReadyReason);

    private static UpdateReadinessResult FinalizeUpdateReadiness(UpdateReadinessContext context)
    {
        var writer = context.Writer;
        var options = context.Options;
        var cancellationToken = context.CancellationToken;
        var hasCSharpFilesAfter = writer.HasAnyFilesWithLanguage("csharp");
        var hasSqlFilesAfter = writer.HasAnyFilesWithLanguage("sql");
        var graphTableAvailableAfter = !context.ReadinessDemoted
            ? (context.PriorReadiness & DbContext.GraphReadyFlag) != 0
            : false;
        var issuesTableAvailableAfter = !context.ReadinessDemoted
            ? (context.PriorReadiness & DbContext.IssuesReadyFlag) != 0
            : false;
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter
            || (!context.ReadinessDemoted && context.CSharpSymbolNameContractMatchesCurrent);
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter
            || (!context.ReadinessDemoted && context.PriorMetadataTargetCsharpMatchesCurrent);
        var foldReadyAfter = !context.ReadinessDemoted
            && (context.PriorReadiness & DbContext.FoldReadyFlag) != 0
            && context.PriorFoldVersion == context.CurrentFoldVersion
            && context.PriorFoldFingerprint == context.CurrentFoldFingerprint
            && context.PriorSymbolExtractorVersionsMatchCurrent;
        string? foldReadyReasonAfter = foldReadyAfter
            ? null
            : GetFoldReadyReason(
                (context.PriorReadiness & DbContext.FoldReadyFlag) != 0,
                context.PriorFoldVersion == context.CurrentFoldVersion,
                context.PriorFoldFingerprint == context.CurrentFoldFingerprint);

        if (context.Errors > 0)
        {
            if (!options.SymbolsOnly && (context.PriorReadiness & DbContext.GraphReadyFlag) != 0)
            {
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            writer.MarkIndexIncomplete(["file_index_error"]);
            writer.SetMetaValues(
                (DbContext.LastFailedIndexRunStatusMetaKey, "partial"),
                (DbContext.LastFailedIndexRunModeMetaKey, "update"),
                (DbContext.LastFailedIndexRunStartedAtMetaKey, context.RunStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunDurationMsMetaKey, context.Stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesProcessedMetaKey, (context.Updated + context.Removed + context.Skipped).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesTotalMetaKey, context.TargetCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunErrorCodeMetaKey, CommandErrorCodes.IndexPartial),
                (DbContext.LastFailedIndexRunReasonMetaKey, "file_index_error"),
                (DbContext.LastFailedIndexRunProgressPersistedMetaKey, true.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunRecoveryHintMetaKey, "Fix the reported file/extractor error, then rerun the same index command. Successful files and graph edges remain persisted; a rebuild is not required."),
                (DbContext.LastFailedIndexRunFileErrorsMetaKey, JsonSerializer.Serialize(context.FileErrorList, StatusMetadataJsonContext.Default.ListStatusIndexFileError)));
        }

        if (context.ReadinessDemoted && context.Errors == 0)
        {
            writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(cancellationToken, "update readiness restamp");
            if ((context.PriorReadiness & DbContext.GraphReadyFlag) != 0)
            {
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            writer.StampSymbolExtractorVersions(context.FullyRefreshedDynamicGraphLanguages);
            writer.StampDynamicReferenceGraphContracts(context.FullyRefreshedDynamicGraphLanguages);
            if ((context.PriorReadiness & DbContext.IssuesReadyFlag) != 0)
            {
                writer.MarkIssuesReady();
                issuesTableAvailableAfter = true;
            }
            if (context.SqlGraphContractMatchesCurrent || !hasSqlFilesAfter)
                writer.MarkSqlGraphContractReady();
            var hasHdlFilesAfter = writer.HasAnyFilesWithLanguage("verilog")
                || writer.HasAnyFilesWithLanguage("systemverilog")
                || writer.HasAnyFilesWithLanguage("vhdl");
            if (context.HdlGraphContractMatchesCurrent || !hasHdlFilesAfter)
                writer.MarkHdlGraphContractReady();
            if (context.CSharpSymbolNameContractMatchesCurrent || !hasCSharpFilesAfter)
            {
                writer.MarkCSharpSymbolNameContractReady();
                csharpSymbolNameReadyAfter = true;
            }
            if (hasCSharpFilesAfter)
            {
                if (context.CSharpMetadataTargetsNeedRefresh)
                {
                    UpdateCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets(cancellationToken);
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }

            using (var hotspotFamilyTxn = writer.BeginTransaction(cancellationToken, "update hotspot-family restamp"))
            {
                if (TypeScriptAugmentationRefreshPolicy.IsRefreshRequired(
                        options.SymbolsOnly,
                        context.TypeScriptAugmentationNeedsRefresh,
                        context.TypeScriptAugmentationDirtyNames?.RequiresRefresh == true))
                {
                    UpdateTypeScriptAugmentationRebuildForTesting?.Invoke();
                    writer.RebuildTypeScriptAugmentationReferences(
                        context.ProjectRoot,
                        context.UseScopedTypeScriptAugmentationRefresh
                            ? context.TypeScriptAugmentationDirtyNames?.DirtyNames
                            : null,
                        context.TypeScriptAugmentationOwnsDeferredReferenceGraphRefresh,
                        cancellationToken);
                }
                if (options.MemoryTrace
                    && context.TypeScriptAugmentationOwnsDeferredReferenceGraphRefresh)
                {
                    context.MemorySamples.Add(CaptureMemorySample("reference_graph", context.Stopwatch));
                }
                RestampHotspotFamilyTrustForUpdate(
                    writer,
                    context.PriorHotspotFamilyVersions,
                    context.PriorHotspotFamilyMarkerFingerprints,
                    context.CurrentHotspotFamilyMarkerFingerprints);
                HotspotFamilyUpdateRestampReadyForCommitForTesting?.Invoke();
                hotspotFamilyTxn.Commit();
            }
            if (!options.SymbolsOnly)
            {
                if (writer.CSharpFamilyTrustAllowsReferenceIdentityReady(hasCSharpFilesAfter))
                    writer.MarkReferenceIdentityContractReady();
                else
                    writer.ClearReferenceIdentityContractReady();
            }
            if ((context.PriorReadiness & DbContext.FoldReadyFlag) != 0
                && context.PriorFoldVersion == context.CurrentFoldVersion
                && context.PriorFoldFingerprint == context.CurrentFoldFingerprint
                && context.PriorSymbolExtractorVersionsMatchCurrent)
            {
                foldReadyAfter = writer.MarkFoldReady();
            }
            StampWriterVersionAndSymbolKindFilter(writer, ConsoleUi.LoadVersion(), options.SymbolKindFilter.Signature);
            writer.ClearBatchInProgress();
            readinessTxn.Commit();
        }

        return new UpdateReadinessResult(
            graphTableAvailableAfter,
            issuesTableAvailableAfter,
            csharpSymbolNameReadyAfter,
            csharpMetadataTargetReadyAfter,
            foldReadyAfter,
            foldReadyReasonAfter);
    }
}
