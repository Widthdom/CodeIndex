using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static Action<string>? FullScanInputSnapshotBarrierForTesting { get; set; }

    private const int PartialIndexFileErrorLimit = StatusMetadataLimits.MaxFileErrors;

    internal static bool ShouldUseFreshReferenceResolutionDefaults(
        bool startedWithNoIndexedFiles,
        bool rebuild,
        bool symbolsOnly)
        => startedWithNoIndexedFiles && !rebuild && !symbolsOnly;





    private static int RunFullScan(
        DbContext db,
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        string resolvedDbPath,
        IndexCommandOptions options,
        Stopwatch stopwatch,
        DateTime runStartedAtUtc,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
        int priorReadiness,
        bool priorIndexComplete,
        bool priorSymbolsOnlyGraphOmitted,
        string? priorFoldVersion,
        string? priorFoldFingerprint,
        bool priorSymbolExtractorVersionsMatchCurrent,
        string? priorCSharpSymbolNameContractVersion,
        string? priorMetadataTargetCsharp,
        string? priorSqlGraphContractVersion,
        string? priorHdlGraphContractVersion,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyVersions,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyMarkerFingerprints,
        string? priorIndexedProjectRoot,
        string? priorIndexedHeadCommit,
        string? currentHeadCommit,
        string? priorSymbolKindFilterSignature,
        string? initialCwd,
        List<string>? indexRunDiagnostics,
        bool showNextSteps,
        CancellationToken cancellationToken,
        bool forceJavaScriptTypeScriptRefresh = false,
        bool forceExtractorRefresh = false)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        var memorySamples = options.MemoryTrace ? new List<IndexMemorySampleJsonResult> { CaptureMemorySample("start", stopwatch) } : [];
        var actualMode = options.Rebuild ? "rebuild" : "incremental";
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions, cancellationToken);
        if (unresolvedMergeExitCode != null)
            return unresolvedMergeExitCode.Value;

        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var csharpIndexedProjectRootCompatible = normalizedPriorIndexedProjectRoot == null
            || projectRootWritten;
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var priorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharp == currentMetadataTargetVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var currentHdlGraphContractVersion = DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hdlGraphContractMatchesCurrent = priorHdlGraphContractVersion == currentHdlGraphContractVersion;
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            options.SymbolKindFilter.Signature,
            StringComparison.Ordinal);
        var priorFilterRetainedCSharpContractMembers =
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(
                priorSymbolKindFilterSignature);

        // Remember HEAD divergence on the default full-scan path so a partial run can explain
        // that whole-workspace verification did not advance. A successful full scan reconciles
        // and purges the complete workspace, so the final response must not keep reporting the
        // pre-scan difference or recommend a rebuild. Issues #1508 and #5054.
        // 既定の full-scan 経路では、partial 終了時に workspace 全体の検証値が進まなかった
        // ことを説明できるよう事前 HEAD 差分を保持する。成功時は全 workspace を照合・purge
        // 済みなので、完了レスポンスで事前差分や rebuild 推奨を残さない。Issues #1508 / #5054。
        var headChangeDetected = !options.Rebuild
            && !string.IsNullOrWhiteSpace(priorIndexedHeadCommit)
            && !string.IsNullOrWhiteSpace(currentHeadCommit)
            && !string.Equals(priorIndexedHeadCommit, currentHeadCommit, StringComparison.Ordinal);

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectRoot);
                projectRootWritten = true;
            }
        }

        int? initialScanFileCapacity = options.Rebuild ? null : writer.GetIndexedFileCount();
        var discovery = DiscoverFullScanFiles(
            indexer,
            projectRoot,
            options,
            spinnerFrames,
            initialScanFileCapacity,
            cancellationToken: cancellationToken);
        var scanResult = discovery.ScanResult;
        var currentHotspotFamilyMarkerFingerprints = scanResult.ProjectMarkerFingerprints;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var scanHadErrors = scanResult.HadErrors;
        var files = discovery.Files;
        var languageCounts = scanResult.LanguageCounts;
        var csharpPrepassCapacity = languageCounts.TryGetValue("csharp", out var csharpFileCount) ? csharpFileCount : 0;
        var targetPreparation = PrepareFullScanTargets(
            indexer,
            projectRoot,
            files,
            scanResult.FileLanguages,
            options.SymbolsOnly,
            csharpPrepassCapacity);
        var fileTargets = targetPreparation.FileTargets;
        var csharpPrepassTargets = targetPreparation.CSharpPrepassTargets;
        var preWriteSelection = new FullScanPreWriteSelectionState
        {
            ReadableFileBytes = new ReadableFileByteTracker(
                files.Count,
                fileIndex => files[fileIndex],
                projectRoot,
                indexRunDiagnostics),
        };
        void ThrowIfFullScanCancelled(int filesProcessed, int? filesTotal)
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            throw new IndexInterruptedException(
                filesProcessed,
                filesTotal,
                actualMode);
        }
        var errorList = discovery.ErrorList;
        var fileErrorList = errorList
            .Take(PartialIndexFileErrorLimit)
            .Select(error => new StatusIndexFileError
            {
                File = FileIndexer.NormalizePathSeparators(error.File),
                Category = "file_read_error",
                Phase = "discovery",
                Detail = error.Message.Length <= 240
                    ? error.Message
                    : string.Concat(error.Message.AsSpan(0, 239), "\u2026"),
            })
            .ToList();
        var warningList = discovery.WarningList;
        warningList.InsertRange(0, options.OptionWarnings);
        AddProjectMarkerFingerprintWarnings(currentHotspotFamilyMarkerFingerprints, warningList, options);
        var scanCheckpointPath = discovery.ScanCheckpointPath;
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("scan", stopwatch));

        ThrowIfFullScanCancelled(0, files.Count);

        CancellationTokenSource? purgeCts = null;
        if (!options.Json && !options.Quiet)
            purgeCts = ConsoleUi.StartSpinner("Cleaning up stale entries...", spinnerFrames);
        var startedWithNoIndexedFiles = !writer.HasAnyIndexedFiles();
        var useFreshReferenceResolutionDefaults = ShouldUseFreshReferenceResolutionDefaults(
            startedWithNoIndexedFiles,
            options.Rebuild,
            options.SymbolsOnly);
        var priorCSharpStaticInterfaceSourceEvidence = options.Rebuild || startedWithNoIndexedFiles
            ? null
            : writer.GetCSharpStaticInterfaceSourceEvidence();
        var deferCSharpMutationsForIncompleteScan = !options.SymbolsOnly
            && !startedWithNoIndexedFiles
            && scanHadErrors
            && priorCSharpStaticInterfaceSourceEvidence != false;
        var requiresConservativeCSharpSourceRefresh = !options.SymbolsOnly
            && !startedWithNoIndexedFiles
            && priorCSharpStaticInterfaceSourceEvidence != false;
        // Delay source-evidence invalidation until scan, workspace preflight, and the final
        // uncached C# stat check have completed. A true positive no-op then keeps its durable
        // marker without a transient null write, while dirty runs still publish unknown/true
        // before their first indexed-row mutation.
        // source evidenceのunknown化は最終C# stat確認後まで遅延し、true no-opの往復writeを避ける。
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var useScopedTypeScriptAugmentationRefresh = !options.SymbolsOnly
            && !startedWithNoIndexedFiles
            && projectRootWritten
            && !forceJavaScriptTypeScriptRefresh
            && !forceExtractorRefresh;
        using var typeScriptAugmentationDirtyNames = !options.Rebuild
            && typeScriptAugmentationVersionMatchesCurrent
                ? writer.BeginTypeScriptAugmentationDirtyNameTracking(useScopedTypeScriptAugmentationRefresh)
                : null;
        var purgePreparation = PlanFullScanStaleFiles(
            writer,
            scanResult,
            fileTargets,
            scanHadErrors,
            startedWithNoIndexedFiles,
            options.SymbolsOnly,
            deferCSharpMutationsForIncompleteScan,
            priorCSharpStaticInterfaceSourceEvidence,
            priorFilterRetainedCSharpContractMembers,
            cancellationToken);
        var staleFilePurgePlan = purgePreparation.StaleFilePurgePlan;
        var purged = purgePreparation.Purged;
        var retainedPaths = purgePreparation.RetainedPaths;
        var indexedJavaScriptTypeScriptConfigPathsBeforePurge =
            purgePreparation.IndexedJavaScriptTypeScriptConfigPathsBeforePurge;
        var hadCSharpStaticInterfaceContractsBeforePurge =
            purgePreparation.HadCSharpStaticInterfaceContractsBeforePurge;
        ConsoleUi.StopSpinner(purgeCts);
        WriteFullScanJsonLiveness(options, purged > 0
            ? $"identified {purged:N0} stale file(s); preparing index writes..."
            : "preparing index writes...");

        ThrowIfFullScanCancelled(0, files.Count);
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
        var freshFoldProducerSnapshot =
            ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectRoot);
        var authoritativeFreshFoldRowsClaim = !options.Rebuild
            && startedWithNoIndexedFiles
            && freshFoldProducerSnapshot.UsesOnlyBuiltInProducers
            ? writer.TryClaimAuthoritativeFreshFoldRows(cancellationToken)
            : null;
        var purgedRefs = 0;

        int warnings = warningList.Count, errors = errorList.Count;
        var ftsMutated = purged > 0;
        var symbolsDroppedByKindFilter = 0;
        var mutualRecursionRefreshNeeded = !options.SymbolsOnly
            && (!writer.ReferenceIdentityContractMatchesCurrent() || purged > 0);

        FullScanProgressSession? fullScanProgressForResume = null;
        var indexProgress = new IndexProgressReporter(
            options,
            "Indexing...",
            spinnerFrames,
            ConsoleUi.TryWriteErrorLine,
            canResume: () => preWriteSelection.Processed < files.Count
                && !fullScanProgressForResume!.IndexProgressVisible,
            clearProgressLineBeforeWrite: true);
        var indexedSymbolExtractorLanguages = new HashSet<string>(languageCounts.Count, StringComparer.Ordinal);
        using var fullScanProgress = fullScanProgressForResume = new FullScanProgressSession(
            options,
            files.Count,
            indexProgress,
            preWriteSelection);
        var extractionParallelism = Math.Max(1, options.Parallelism);
        var typeScriptAugmentationNeedsRefresh = !options.SymbolsOnly
            && (options.Rebuild
                || startedWithNoIndexedFiles
                || purged > 0
                || !projectRootWritten
                || !typeScriptAugmentationVersionMatchesCurrent);
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var typeScriptAugmentationReadyClearPending = false;
        var fullScanWritePhaseStarted = false;
        var csharpMetadataTargetsNeedRefresh = options.Rebuild
            || startedWithNoIndexedFiles
            || purged > 0
            || !priorMetadataTargetCsharpMatchesCurrent;
        var reportedCSharpWorkspaceFailures = new HashSet<string>(StringComparer.Ordinal);

        void RecordCSharpWorkspaceFailure(string path, string phase, Exception exception)
        {
            path = string.IsNullOrWhiteSpace(path) ? "<csharp_workspace>" : path;
            if (!reportedCSharpWorkspaceFailures.Add($"{phase}\n{path}"))
                return;

            errors++;
            errorList.Add(new CliJsonMessage(path, FormatIndexFileException(exception)));
            if (fileErrorList.Count < PartialIndexFileErrorLimit)
                fileErrorList.Add(BuildIndexFileError(path, phase, exception));
        }

        void RequireTypeScriptAugmentationRefresh()
        {
            if (!typeScriptAugmentationReadyCleared)
            {
                typeScriptAugmentationReadyCleared = true;
                if (fullScanWritePhaseStarted)
                    writer.ClearTypeScriptAugmentationReady();
                else
                    typeScriptAugmentationReadyClearPending = true;
            }

            if (!options.SymbolsOnly)
                typeScriptAugmentationNeedsRefresh = true;
        }

        if (purged > 0)
            RequireTypeScriptAugmentationRefresh();

        var javaScriptTypeScriptRefreshRequired = forceJavaScriptTypeScriptRefresh
            || (!options.Rebuild
                && !startedWithNoIndexedFiles
                && FullScanJavaScriptTypeScriptConfigChanged());

        bool FullScanJavaScriptTypeScriptConfigChanged()
        {
            foreach (var indexedConfigPath in indexedJavaScriptTypeScriptConfigPathsBeforePurge)
            {
                if (!retainedPaths!.Contains(indexedConfigPath))
                    return true;
            }

            foreach (var target in fileTargets)
            {
                if (!IsJavaScriptTypeScriptConfigPath(target.IndexPath))
                    continue;

                var existingId = TryGetUnchangedFileIdFromChecksum(
                    writer,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    options.MaxFileSizeBytes);
                if (existingId == null)
                    return true;
            }

            return false;
        }

        var preWriteState = new FullScanPreWriteState
        {
            Scan = new FullScanPreWriteMutableScanState
            {
                HadCSharpStaticInterfaceContractsBeforePurge =
                    hadCSharpStaticInterfaceContractsBeforePurge,
                StaleFilePurgePlan = staleFilePurgePlan,
                DeferCSharpMutationsForIncompleteScan =
                    deferCSharpMutationsForIncompleteScan,
                Purged = purged,
                FtsMutated = ftsMutated,
            },
            CSharp = new FullScanPreWriteCSharpState(),
            Selection = preWriteSelection,
            Diagnostics = new FullScanPreWriteDiagnosticsState
            {
                ErrorList = errorList,
                FileErrorList = fileErrorList,
                WarningList = warningList,
                ReportedCSharpWorkspaceFailures =
                    reportedCSharpWorkspaceFailures,
                Errors = errors,
            },
        };
        var preWriteSession = new FullScanPreWriteSession(
            new FullScanPreWriteRequest(
                new FullScanPreWriteCore(
                    writer,
                    indexer,
                    options,
                    projectRoot),
                new FullScanPreWriteBaseline(
                    priorIndexComplete,
                    priorReadiness,
                    priorSymbolsOnlyGraphOmitted,
                    priorCSharpStaticInterfaceSourceEvidence,
                    startedWithNoIndexedFiles,
                    scanHadErrors,
                    projectRootWritten),
                new FullScanPreWriteContracts(
                    symbolKindFilterMatchesPrior,
                    csharpSymbolNameContractMatchesCurrent,
                    csharpIndexedProjectRootCompatible,
                    AllowReuseWithCurrentHotspotFamilyTrust(
                        "csharp",
                        hotspotFamilyTrustMatchesCurrent),
                    requiresConservativeCSharpSourceRefresh,
                    forceExtractorRefresh),
                new FullScanPreWriteRuntime(
                    fileTargets,
                    csharpPrepassTargets,
                    scanResult.FileLanguages,
                    csharpPrepassCapacity,
                    extractionParallelism,
                    files.Count,
                    actualMode,
                    cancellationToken),
                new FullScanPreWriteReusePolicy(
                    CanSkipTargetsBeforeContentLoad:
                        !forceExtractorRefresh
                        && !options.Rebuild
                        && !startedWithNoIndexedFiles
                        && !options.SymbolsOnly,
                    sqlGraphContractMatchesCurrent,
                    hdlGraphContractMatchesCurrent,
                    hotspotFamilyTrustMatchesCurrent,
                    javaScriptTypeScriptRefreshRequired)),
            preWriteState);
        preWriteSession.PrepareCSharpWorkspace();
        var csharpPreflight = preWriteState.CSharp;
        var reusableIndexedFileStats =
            csharpPreflight.ReusableIndexedFileStats;

        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("csharp_prepass", stopwatch));

        var freshCountFiles = 0L;
        var freshCountChunks = 0L;
        var freshCountSymbols = 0L;
        var freshCountReferences = 0L;
        var extractedFiles = 0L;
        var extractedChunks = 0L;
        var extractedSymbols = 0L;
        var extractedReferences = 0L;
        var persistedFiles = 0L;
        var persistedChunks = 0L;
        var persistedSymbols = 0L;
        var persistedReferences = 0L;
        var inputValidation = preWriteSession.PrepareWriteBoundary(
            discovery.InputSnapshot);
        if (!inputValidation.IsValid)
        {
            return WriteFullScanSnapshotFailure(
                inputValidation.ChangedPath,
                new FullScanSnapshotFailureContext
                {
                    Writer = writer,
                    Options = options,
                    Stopwatch = stopwatch,
                    JsonContext = jsonContext,
                    ProjectRoot = projectRoot,
                    PriorReadiness = priorReadiness,
                    CSharpSymbolNameContractMatchesCurrent = csharpSymbolNameContractMatchesCurrent,
                    PriorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharpMatchesCurrent,
                    PriorFoldVersion = priorFoldVersion,
                    PriorFoldFingerprint = priorFoldFingerprint,
                    MemorySamples = memorySamples,
                    LanguageCounts = languageCounts,
                    UnknownExtensionFiles = scanResult.UnknownExtensionFiles,
                    FilesCount = files.Count,
                    Skipped = preWriteSelection.Skipped,
                    DanglingSymlinkCount = scanResult.DanglingSymlinks.Count,
                    Warnings = warnings,
                    Errors = preWriteState.Diagnostics.Errors,
                    SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                    ErrorList = errorList,
                    FileErrorList = fileErrorList,
                    WarningList = warningList,
                });
        }

        staleFilePurgePlan = preWriteState.Scan.StaleFilePurgePlan;
        deferCSharpMutationsForIncompleteScan =
            preWriteState.Scan.DeferCSharpMutationsForIncompleteScan;
        purged = preWriteState.Scan.Purged;
        ftsMutated = preWriteState.Scan.FtsMutated;
        errors = preWriteState.Diagnostics.Errors;
        var extractionFileIndexes =
            preWriteSelection.ExtractionFileIndexes;
        var extractionWorkItemCount =
            preWriteSelection.ExtractionWorkItemCount;
        var useFtsBulkLoad = preWriteSelection.UseFtsBulkLoad;
        var csharpWorkspace = csharpPreflight.Workspace;
        var csharpWorkspaceFileSnapshots =
            csharpPreflight.WorkspaceFileSnapshots;
        var preservePriorPositiveCSharpSourceNoOp =
            csharpPreflight.PreservePriorPositiveSourceNoOp;
        var csharpSourceEvidenceForStamp =
            csharpPreflight.Evidence.ForStamp;
        var csharpSourceEvidenceComplete =
            csharpPreflight.Evidence.Complete;

        // The captured scan has now crossed its only pre-write authority barrier. Start the
        // outer write scopes immediately afterwards so no durable readiness, evidence, purge,
        // or file mutation can precede the validation above.
        // scan snapshot の write前 authority barrier 通過直後に outer write scope を開始する。
        using var mmapBulkWrite = SqliteMmapBulkWriteGuard.Start(writer, useFtsBulkLoad);
        if (options.Rebuild)
            db.RepairIncompleteBatchReadiness();
        using var referenceGraphRefresh = writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: options.Rebuild || startedWithNoIndexedFiles,
            useFreshReferenceResolutionDefaults: useFreshReferenceResolutionDefaults);
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh(
            deferSecondaryIndexes: !options.SymbolsOnly && useFtsBulkLoad);
        using var fullScanTxn = writer.BeginTransaction(cancellationToken, "full scan write phase");
        if (referenceGraphRefresh.FreshReferenceResolutionDefaultsPending
            && !writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction(cancellationToken))
        {
            // Another connection committed after the early empty-DB observation. All file,
            // symbol, and reference writes below remain in the authoritative full refresh, but
            // existing candidate-free references must be normalized by the ordinary full SQL.
            // 早期のempty-DB確認後に別connectionがcommitした。以降のfile/symbol/reference writeは
            // authoritative full refreshのまま維持し、既存candidate-free referenceは通常のfull SQLで正規化する。
            referenceGraphRefresh.DisableFreshReferenceResolutionDefaults();
        }
        var deferAuthoritativeFreshPersistenceIndexes =
            referenceGraphRefresh.FreshReferenceResolutionDefaultsPending
            && !options.SymbolsOnly
            && useFtsBulkLoad;
        var deferAuthoritativeFreshCoreIndexes =
            referenceGraphRefresh.FreshReferenceResolutionDefaultsPending
            && useFtsBulkLoad;
        fullScanWritePhaseStarted = true;
        writer.SetMeta(
            DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
            false.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.RecoverInterruptedFtsBulkLoadIfNeeded(cancellationToken);
        writer.MarkBatchInProgress();
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearSqlGraphContractReady();
        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
        if (options.SymbolsOnly)
            writer.ClearReferenceIdentityContractReady();
        writer.ClearMetadataTargetReady();
        if (typeScriptAugmentationReadyClearPending)
        {
            writer.ClearTypeScriptAugmentationReady();
            typeScriptAugmentationReadyClearPending = false;
        }
        DeleteScanCheckpoint(scanCheckpointPath, warningList, options.Json, options.Quiet);
        FullScanWritePhaseStartedForTesting?.Invoke();
        ThrowIfFullScanCancelled(0, files.Count);

        if (!options.SymbolsOnly && !preservePriorPositiveCSharpSourceNoOp)
        {
            // This is the last source-evidence write before stale/file mutations. Complete
            // positive evidence may be published early; every false or incomplete snapshot
            // stays unknown until a clean run finishes.
            // stale/file mutation直前にcomplete positiveだけを公開し、それ以外はunknownにする。
            writer.SetCSharpStaticInterfaceSourceEvidence(
                csharpSourceEvidenceComplete && csharpSourceEvidenceForStamp ? true : null);
        }

        using var referenceSecondaryIndexBulkLoad =
            ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                writer,
                enabled: !options.SymbolsOnly && useFtsBulkLoad,
                cancellationToken,
                refreshPlannerStatisticsBeforeCandidatePopulation:
                    deferAuthoritativeFreshPersistenceIndexes,
                deferAuthoritativeFreshPersistenceIndexes:
                    deferAuthoritativeFreshPersistenceIndexes);
        using var coreSecondaryIndexBulkLoad =
            CoreSecondaryIndexBulkLoadGuard.StartTransactional(
                writer,
                enabled: deferAuthoritativeFreshCoreIndexes,
                cancellationToken);
        using var ftsBulkLoad = FtsBulkLoadTriggerGuard.Start(writer, useFtsBulkLoad);

        if (staleFilePurgePlan.Count > 0)
        {
            FullScanStaleFilePurgeForTesting?.Invoke(useFtsBulkLoad);
            purged = writer.ApplyFilePurgePlan(
                staleFilePurgePlan,
                cancellationToken: cancellationToken);
            if (purged > 0)
                WriteProjectRootOnce();
        }

        // Stale-file cascades run first so unsupported/all-reference cleanup never scans rows
        // that the same run is already deleting with their owning files.
        // stale file の cascade を先に行い、同じ run で消える file の reference を
        // unsupported/all cleanup が重複走査しないようにする。
        FullScanReferencePurgeForTesting?.Invoke();
        purgedRefs = deferCSharpMutationsForIncompleteScan || startedWithNoIndexedFiles
            ? 0
            : options.SymbolsOnly
                ? writer.PurgeAllReferences()
                : writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages(projectRoot));
        if (purgedRefs > 0)
        {
            if (!options.SymbolsOnly)
                mutualRecursionRefreshNeeded = true;
            else
                RequireTypeScriptAugmentationRefresh();
            if (!options.Json && !options.Quiet)
            {
                var reason = options.SymbolsOnly ? "symbols-only mode" : "unsupported language";
                CommandOutputWriter.WriteLine($"  Purged {purgedRefs:N0} stale references ({reason})");
            }
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("purge", stopwatch));
        if (!options.Json && !options.Quiet)
        {
            if (purged > 0)
            {
                var purgeMessage = scanHadErrors
                    ? $"  Purged {purged:N0} previously indexed files that were positively observed as no longer indexable or missing from directories whose file listing completed successfully"
                    : $"  Purged {purged:N0} stale files (missing or no longer indexable)";
                CommandOutputWriter.WriteLine(purgeMessage);
            }
            if (scanHadErrors)
                ConsoleUi.PrintWarning("Skipped authoritative purge outside directories whose file listing completed successfully because some paths could not be scanned.");
        }

        fullScanProgress.ReportJsonIndexProgressIfNeeded();

        var extractionSession = new FullScanExtractionSession
        {
            Request = new FullScanExtractionRequest(
                    new FullScanExtractionCore(
                        writer,
                        indexer,
                        options,
                        projectRoot,
                        fileTargets,
                        preWriteSelection.ReadableFileBytes,
                        indexProgress,
                        fullScanProgress),
                    new FullScanExtractionWork(
                        extractionFileIndexes,
                        extractionWorkItemCount,
                        extractionParallelism,
                        files.Count,
                        forceExtractorRefresh,
                        authoritativeFreshFoldRowsClaim,
                        cancellationToken,
                        actualMode),
                    new FullScanExtractionContracts(
                        priorSymbolsOnlyGraphOmitted,
                        symbolKindFilterMatchesPrior,
                        csharpIndexedProjectRootCompatible,
                        csharpSymbolNameContractMatchesCurrent,
                        sqlGraphContractMatchesCurrent,
                        hdlGraphContractMatchesCurrent,
                        startedWithNoIndexedFiles),
                    new FullScanExtractionReuse(
                        javaScriptTypeScriptRefreshRequired,
                        hotspotFamilyTrustMatchesCurrent)),
            State = new FullScanExtractionState
            {
                PreWrite = preWriteState,
                Refresh = new FullScanExtractionRefreshState
                {
                    FtsMutated = ftsMutated,
                    MutualRecursionRefreshNeeded = mutualRecursionRefreshNeeded,
                    CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
                    SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                    ReusedHotspotFamilyLanguages = preWriteSelection.ReusedHotspotFamilyLanguages,
                    SkippedSymbolExtractorLanguages = preWriteSelection.SkippedSymbolExtractorLanguages,
                    IndexedSymbolExtractorLanguages = indexedSymbolExtractorLanguages,
                },
            },
            External = new FullScanExtractionExternalOperations(
                    RequireTypeScriptAugmentationRefresh,
                    WriteProjectRootOnce),
        };
        using var authoritativeFreshBulkInsert =
            writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: referenceGraphRefresh.FreshReferenceResolutionDefaultsPending,
                cancellationToken);
        var postExtractionHooks = RunFullScanExtractionPipeline(extractionSession);
        authoritativeFreshBulkInsert?.Complete();
        // Native INSERT statements must be finalized before CREATE INDEX invalidates the
        // connection schema. Every graph/readiness consumer below sees the restored set.
        // native INSERT statementをfinalizeした後にCREATE INDEXを行い、以降の
        // graph/readiness consumerからは復元済みschemaだけを見せる。
        coreSecondaryIndexBulkLoad?.Complete(cancellationToken);
        preWriteState.CSharp.PrepassSymbolArtifacts = null;
        deferCSharpMutationsForIncompleteScan =
            preWriteState.Scan.DeferCSharpMutationsForIncompleteScan;
        csharpWorkspaceFileSnapshots =
            preWriteState.CSharp.WorkspaceFileSnapshots;
        csharpWorkspace = preWriteState.CSharp.Workspace;
        preservePriorPositiveCSharpSourceNoOp =
            preWriteState.CSharp.PreservePriorPositiveSourceNoOp;
        csharpSourceEvidenceForStamp = preWriteState.CSharp.Evidence.ForStamp;
        csharpSourceEvidenceComplete = preWriteState.CSharp.Evidence.Complete;
        preWriteSelection.Skipped += extractionSession.State.Counts.Skipped;
        persistedFiles += extractionSession.State.PersistenceCounts.PersistedFiles;
        persistedChunks += extractionSession.State.PersistenceCounts.PersistedChunks;
        persistedSymbols += extractionSession.State.PersistenceCounts.PersistedSymbols;
        persistedReferences += extractionSession.State.PersistenceCounts.PersistedReferences;
        freshCountFiles += extractionSession.State.PersistenceCounts.FreshFiles;
        freshCountChunks += extractionSession.State.PersistenceCounts.FreshChunks;
        freshCountSymbols += extractionSession.State.PersistenceCounts.FreshSymbols;
        freshCountReferences += extractionSession.State.PersistenceCounts.FreshReferences;
        warnings += extractionSession.State.Counts.Warnings;
        errors = preWriteState.Diagnostics.Errors + extractionSession.State.Counts.Errors;
        ftsMutated = extractionSession.State.Refresh.FtsMutated;
        mutualRecursionRefreshNeeded =
            extractionSession.State.Refresh.MutualRecursionRefreshNeeded;
        csharpMetadataTargetsNeedRefresh =
            extractionSession.State.Refresh.CSharpMetadataTargetsNeedRefresh;
        symbolsDroppedByKindFilter =
            extractionSession.State.Refresh.SymbolsDroppedByKindFilter;
        extractedFiles += extractionSession.State.Counts.ExtractedFiles;
        extractedChunks += extractionSession.State.Counts.ExtractedChunks;
        extractedSymbols += extractionSession.State.Counts.ExtractedSymbols;
        extractedReferences += extractionSession.State.Counts.ExtractedReferences;
        preWriteSelection.ReusedHotspotFamilyLanguages =
            extractionSession.State.Refresh.ReusedHotspotFamilyLanguages;
        preWriteSelection.SkippedSymbolExtractorLanguages =
            extractionSession.State.Refresh.SkippedSymbolExtractorLanguages;

        indexProgress.Pause();

        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));

        ThrowIfFullScanCancelled(preWriteSelection.Processed, files.Count);
        var referenceIdentityReadyForMutualRecursionRefresh =
            !deferCSharpMutationsForIncompleteScan && mutualRecursionRefreshNeeded
                ? writer.CSharpFamilyTrustAllowsReferenceIdentityReady(
                    (options.Rebuild || startedWithNoIndexedFiles)
                    && !scanHadErrors
                    && errors == 0
                        ? languageCounts.ContainsKey("csharp")
                        : null)
                : (bool?)null;
        var canStampTypeScriptAugmentationReadyWithoutRebuild =
            (startedWithNoIndexedFiles || options.Rebuild)
            && !scanHadErrors
            && !languageCounts.ContainsKey("typescript");
        var willRebuildTypeScriptAugmentation =
            !deferCSharpMutationsForIncompleteScan
            && TypeScriptAugmentationRefreshPolicy.ShouldRebuildReferences(
                options.SymbolsOnly,
                canFinalize: errors == 0,
                typeScriptAugmentationNeedsRefresh,
                typeScriptAugmentationDirtyNames?.RequiresRefresh == true,
                canStampReadyWithoutRebuild:
                    canStampTypeScriptAugmentationReadyWithoutRebuild);
        var deferMutualRecursionRefreshToTypeScriptAugmentation =
            mutualRecursionRefreshNeeded && willRebuildTypeScriptAugmentation;
        if ((!deferCSharpMutationsForIncompleteScan
             && mutualRecursionRefreshNeeded
             && !deferMutualRecursionRefreshToTypeScriptAugmentation)
            || referenceSecondaryIndexBulkLoad != null)
        {
            var phase = deferMutualRecursionRefreshToTypeScriptAugmentation
                ? "restoring reference query indexes"
                : "finalizing reference graph";
            WriteFullScanJsonLiveness(options, $"{phase}...");
            var referenceGraphHeartbeat = StartFullScanJsonPhaseHeartbeat(options, phase);
            try
            {
                if (!deferCSharpMutationsForIncompleteScan
                    && mutualRecursionRefreshNeeded
                    && !deferMutualRecursionRefreshToTypeScriptAugmentation)
                {
                    writer.RefreshMutualRecursionFlags(
                        cancellationToken,
                        stampReferenceIdentityContractReady:
                            referenceIdentityReadyForMutualRecursionRefresh,
                        referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
                }

                if (willRebuildTypeScriptAugmentation)
                {
                    // Keep only the candidate reverse lookup deferred until the TypeScript-owned
                    // graph refresh; readiness retains every ordinary reference query path.
                    referenceSecondaryIndexBulkLoad?.PrepareForDeferredGraphRefresh(
                        cancellationToken);
                }
                else
                {
                    referenceSecondaryIndexBulkLoad?.Complete(cancellationToken);
                }
            }
            finally
            {
                StopFullScanJsonPhaseHeartbeat(referenceGraphHeartbeat);
            }
        }
        if (options.MemoryTrace && !willRebuildTypeScriptAugmentation)
            memorySamples.Add(CaptureMemorySample("reference_graph", stopwatch));
        ThrowIfFullScanCancelled(preWriteSelection.Processed, files.Count);
        if (ftsBulkLoad != null)
        {
            var phase = ftsMutated ? "rebuilding text index" : "restoring text index triggers";
            WriteFullScanJsonLiveness(options, $"{phase}...");
            var ftsHeartbeat = StartFullScanJsonPhaseHeartbeat(options, phase);
            try
            {
                ftsBulkLoad.Complete(ftsMutated, FullScanFtsOptimizeForTesting, cancellationToken);
            }
            finally
            {
                StopFullScanJsonPhaseHeartbeat(ftsHeartbeat);
            }
        }
        else if (ftsMutated)
        {
            writer.RecordFtsIncrementalWrite();
            if (writer.GetFtsIncrementalWritesSinceMerge() >= DbWriter.DefaultFtsMergeIncrementalWriteThreshold)
            {
                WriteFullScanJsonLiveness(options, "merging text index segments...");
                var mergeHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "merging text index segments");
                try
                {
                    FullScanFtsMergeForTesting?.Invoke();
                    writer.MergeFtsSegments(cancellationToken: cancellationToken);
                }
                finally
                {
                    StopFullScanJsonPhaseHeartbeat(mergeHeartbeat);
                }
            }
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("text_index", stopwatch));
        ThrowIfFullScanCancelled(preWriteSelection.Processed, files.Count);
        var readinessStableFiles = true;
        string? readinessChangedFilePath = null;
        if (discovery.InputSnapshot != null)
        {
            FullScanCSharpReadinessValidationForTesting?.Invoke();
            if (!options.SymbolsOnly
                && errors == 0
                && !deferCSharpMutationsForIncompleteScan
                && csharpWorkspaceFileSnapshots != null)
            {
                readinessStableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    csharpPrepassTargets,
                    csharpWorkspaceFileSnapshots,
                    out readinessChangedFilePath,
                    cancellationToken);
            }
            else if (!options.SymbolsOnly
                     && errors == 0
                     && !deferCSharpMutationsForIncompleteScan
                     && preservePriorPositiveCSharpSourceNoOp)
            {
                foreach (var target in csharpPrepassTargets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IndexedFileStatReuse.TryGetReusableUnchangedFile(
                            reusableIndexedFileStats!,
                            target.FilePath,
                            target.IndexPath,
                            target.Language,
                            target.GeneratedExtractionSuppressed) != null)
                    {
                        continue;
                    }

                    readinessStableFiles = false;
                    readinessChangedFilePath = target.DisplayRelativePath;
                    break;
                }
            }

            FullScanInputSnapshotBarrierForTesting?.Invoke("before_readiness");
            var readinessStableScanInputs = indexer.TryValidateScanInputSnapshot(
                discovery.InputSnapshot,
                out var readinessChangedScanInputPath,
                cancellationToken);
            if (!readinessStableFiles || !readinessStableScanInputs)
            {
                extractionSession.DeferCSharpMutationsForLoadedSnapshotDrift(
                    readinessChangedFilePath
                    ?? readinessChangedScanInputPath
                    ?? "<csharp_workspace>");
                deferCSharpMutationsForIncompleteScan =
                    preWriteState.Scan.DeferCSharpMutationsForIncompleteScan;
                preservePriorPositiveCSharpSourceNoOp =
                    preWriteState.CSharp.PreservePriorPositiveSourceNoOp;
                csharpSourceEvidenceForStamp = preWriteState.CSharp.Evidence.ForStamp;
                csharpSourceEvidenceComplete = preWriteState.CSharp.Evidence.Complete;
                csharpWorkspaceFileSnapshots =
                    preWriteState.CSharp.WorkspaceFileSnapshots;
                csharpWorkspace = preWriteState.CSharp.Workspace;
                errors = preWriteState.Diagnostics.Errors
                    + extractionSession.State.Counts.Errors;
            }
        }

        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // ClearReadyFlags() ran at the start.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        // A clean, complete fresh/rebuild run is authoritative for both presence and absence.
        // With an incremental or partial discovery, positive evidence remains authoritative
        // while absence falls back to persisted rows. This prevents readiness from depending
        // on whether a failed C#/SQL target happened to persist before the failure.
        // complete fresh/rebuild discovery は presence/absence の双方に使い、partial discovery
        // でも発見済み language は保持する。absence だけを persisted row へ fallback する。
        var freshLanguageAbsenceAuthoritative =
            (options.Rebuild || startedWithNoIndexedFiles) && !scanHadErrors && errors == 0;
        var discoveredCSharpFiles = languageCounts.ContainsKey("csharp");
        var discoveredSqlFiles = languageCounts.ContainsKey("sql");
        var hasCSharpFilesAfter = discoveredCSharpFiles
            || (!freshLanguageAbsenceAuthoritative && writer.HasAnyFilesWithLanguage("csharp"));
        var hasSqlFilesAfter = discoveredSqlFiles
            || (!freshLanguageAbsenceAuthoritative && writer.HasAnyFilesWithLanguage("sql"));
        var graphTableAvailableAfter = false;
        var issuesTableAvailableAfter = false;
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter;
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter;
        var foldReadyAfter = false;
        string? foldReadyReasonAfter = null;
        if (!options.SymbolsOnly
            && !deferCSharpMutationsForIncompleteScan
            && postExtractionHooks?.SawCSharpStaticInterfaceSourceContract == true)
        {
            if (!csharpWorkspace.HasSourceStaticInterfaceContracts)
            {
                // Extraction observed a contract absent from the immutable prepass. Its
                // current file is correct, but unchanged implementers may still carry the
                // prior graph. Leave evidence unknown so the next clean run performs the
                // complete C# repair instead of accepting another positive stat-only no-op.
                // prepassに無かったcontractはunknown化し、次回clean runで全C#を修復する。
                csharpSourceEvidenceForStamp = false;
                csharpSourceEvidenceComplete = false;
                writer.SetCSharpStaticInterfaceSourceEvidence(null);
                RecordCSharpWorkspaceFailure(
                    "<csharp_workspace>",
                    "csharp_workspace_validation",
                    new InvalidOperationException(
                        "A C# static-interface contract appeared after workspace preflight; rerun indexing to repair unchanged implementers."));
            }
            else
            {
                csharpSourceEvidenceForStamp = true;
                csharpSourceEvidenceComplete = true;
                writer.SetCSharpStaticInterfaceSourceEvidence(true);
            }
        }

        // The augmentation rebuild refreshes the whole reference graph after inserting its
        // synthetic edges. Avoid doing the same graph pass immediately beforehand. If the
        // final immutable-input validation makes augmentation ineligible, run the deferred
        // pass before readiness finalization so a partial run still persists a coherent graph.
        // augmentation が synthetic edge 挿入後に行う graph refresh を唯一の pass にする。
        // 最終 validation で実行不能になった場合だけ、readiness 確定前に遅延分を補完する。
        var deferredMutualRecursionRefreshCompletedBeforeReadiness = false;
        var willRebuildTypeScriptAugmentationAfterReadinessValidation =
            !deferCSharpMutationsForIncompleteScan
            && TypeScriptAugmentationRefreshPolicy.ShouldRebuildReferences(
                options.SymbolsOnly,
                canFinalize: errors == 0,
                typeScriptAugmentationNeedsRefresh,
                typeScriptAugmentationDirtyNames?.RequiresRefresh == true,
                canStampReadyWithoutRebuild:
                    canStampTypeScriptAugmentationReadyWithoutRebuild);
        if (willRebuildTypeScriptAugmentation
            && !willRebuildTypeScriptAugmentationAfterReadinessValidation)
        {
            if (deferMutualRecursionRefreshToTypeScriptAugmentation)
            {
                WriteFullScanJsonLiveness(options, "finalizing reference graph after readiness validation...");
                var referenceGraphHeartbeat = StartFullScanJsonPhaseHeartbeat(
                    options,
                    "finalizing reference graph after readiness validation");
                try
                {
                    writer.RefreshMutualRecursionFlags(
                        cancellationToken,
                        stampReferenceIdentityContractReady:
                            referenceIdentityReadyForMutualRecursionRefresh,
                        referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
                    deferredMutualRecursionRefreshCompletedBeforeReadiness = true;
                }
                finally
                {
                    StopFullScanJsonPhaseHeartbeat(referenceGraphHeartbeat);
                }
            }
            referenceSecondaryIndexBulkLoad?.Complete(cancellationToken);
        }
        if (options.MemoryTrace && deferredMutualRecursionRefreshCompletedBeforeReadiness)
            memorySamples.Add(CaptureMemorySample("reference_graph", stopwatch));
        var readiness = FinalizeFullScanReadiness(new FullScanReadinessContext
        {
            Writer = writer,
            Options = options,
            Stopwatch = stopwatch,
            RunStartedAtUtc = runStartedAtUtc,
            ProjectRoot = projectRoot,
            CurrentHeadCommit = currentHeadCommit,
            IndexRunDiagnostics = indexRunDiagnostics,
            CancellationToken = cancellationToken,
            Errors = errors,
            FileErrorList = fileErrorList,
            Processed = preWriteSelection.Processed,
            FileCount = files.Count,
            Skipped = preWriteSelection.Skipped,
            Purged = purged,
            ScanHadErrors = scanHadErrors,
            StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
            AuthoritativeFreshFoldRowsClaim = authoritativeFreshFoldRowsClaim,
            FreshFoldProducerSnapshot = freshFoldProducerSnapshot,
            HasCSharpFilesAfter = hasCSharpFilesAfter,
            CSharpSourceEvidenceComplete = csharpSourceEvidenceComplete,
            CSharpSourceEvidenceForStamp = csharpSourceEvidenceForStamp,
            PreservePriorPositiveCSharpSourceNoOp = preservePriorPositiveCSharpSourceNoOp,
            CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
            TypeScriptAugmentationNeedsRefresh = typeScriptAugmentationNeedsRefresh,
            TypeScriptAugmentationDirtyNames = typeScriptAugmentationDirtyNames,
            UseScopedTypeScriptAugmentationRefresh = useScopedTypeScriptAugmentationRefresh,
            LanguageCounts = languageCounts,
            ReusedHotspotFamilyLanguages = preWriteSelection.ReusedHotspotFamilyLanguages,
            PriorHotspotFamilyVersions = priorHotspotFamilyVersions,
            PriorHotspotFamilyMarkerFingerprints = priorHotspotFamilyMarkerFingerprints,
            CurrentHotspotFamilyMarkerFingerprints = currentHotspotFamilyMarkerFingerprints,
            IndexedSymbolExtractorLanguages = indexedSymbolExtractorLanguages,
            SkippedSymbolExtractorLanguages = preWriteSelection.SkippedSymbolExtractorLanguages,
            PriorFoldVersion = priorFoldVersion,
            PriorFoldFingerprint = priorFoldFingerprint,
            ScanResult = scanResult,
            ReadableFileBytes = preWriteSelection.ReadableFileBytes,
            MemorySamples = memorySamples,
            TypeScriptAugmentationOwnsDeferredReferenceGraphRefresh =
                deferMutualRecursionRefreshToTypeScriptAugmentation
                && !deferredMutualRecursionRefreshCompletedBeforeReadiness,
            TypeScriptAugmentationRebuildOwnsReferenceGraphMemorySample =
                willRebuildTypeScriptAugmentationAfterReadinessValidation,
            ReferenceSecondaryIndexBulkLoad =
                willRebuildTypeScriptAugmentationAfterReadinessValidation
                    ? referenceSecondaryIndexBulkLoad
                    : null,
            FreshCountReferences = freshCountReferences,
            WriteProjectRootOnce = WriteProjectRootOnce,
        });
        if (referenceSecondaryIndexBulkLoad != null
            && willRebuildTypeScriptAugmentationAfterReadinessValidation)
            writer.ReportReferenceSecondaryIndexBulkLoadState("readiness_completed");
        referenceSecondaryIndexBulkLoad?.Complete(cancellationToken);
        graphTableAvailableAfter = readiness.GraphTableAvailable;
        issuesTableAvailableAfter = readiness.IssuesTableAvailable;
        csharpSymbolNameReadyAfter = readiness.CSharpSymbolNameReady;
        csharpMetadataTargetReadyAfter = readiness.CSharpMetadataTargetReady;
        foldReadyAfter = readiness.FoldReady;
        foldReadyReasonAfter = readiness.FoldReadyReason;
        freshCountReferences = readiness.FreshCountReferences;
        hotspotAggregateRefresh.Complete(cancellationToken);
        writer.ClearBatchInProgress();
        fullScanTxn.Commit();
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("commit", stopwatch));
        if (referenceSecondaryIndexBulkLoad != null
            && willRebuildTypeScriptAugmentationAfterReadinessValidation)
            writer.ReportReferenceSecondaryIndexBulkLoadState("full_scan_committed");
        StatusRebuildReclaim? rebuildReclaim = null;
        if (options.Rebuild && errors == 0)
        {
            WriteFullScanJsonLiveness(options, "evaluating rebuild free-page reclaim...");
            CancellationTokenSource? reclaimCts = null;
            if (!options.Json && !options.Quiet)
                reclaimCts = ConsoleUi.StartSpinner("Reclaiming rebuild free space...", spinnerFrames);
            try
            {
                rebuildReclaim = db.RunRebuildReclaimIfRecommended(cancellationToken);
            }
            finally
            {
                ConsoleUi.StopSpinner(reclaimCts);
            }
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("rebuild_reclaim", stopwatch));
            TryStampRebuildReclaimMetadata(
                writer,
                rebuildReclaim,
                stopwatch.ElapsedMilliseconds,
                BuildMemoryTimeline(memorySamples));
        }
        return WriteFullScanFinalOutput(new FullScanFinalOutputContext
        {
            Writer = writer,
            Options = options,
            Stopwatch = stopwatch,
            JsonContext = jsonContext,
            ProjectRoot = projectRoot,
            ResolvedDbPath = resolvedDbPath,
            InitialCwd = initialCwd,
            MemorySamples = memorySamples,
            PostExtractionHooks = postExtractionHooks,
            WarningList = warningList,
            ErrorList = errorList,
            FileErrorList = fileErrorList,
            Warnings = warnings,
            Errors = errors,
            SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
            StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
            ScanHadErrors = scanHadErrors,
            FreshCountFiles = freshCountFiles,
            FreshCountChunks = freshCountChunks,
            FreshCountSymbols = freshCountSymbols,
            FreshCountReferences = freshCountReferences,
            HasSqlFilesAfter = hasSqlFilesAfter,
            GraphTableAvailableAfter = graphTableAvailableAfter,
            IssuesTableAvailableAfter = issuesTableAvailableAfter,
            CSharpSymbolNameReadyAfter = csharpSymbolNameReadyAfter,
            CSharpMetadataTargetReadyAfter = csharpMetadataTargetReadyAfter,
            FoldReadyAfter = foldReadyAfter,
            FoldReadyReasonAfter = foldReadyReasonAfter,
            ExtractedFiles = extractedFiles,
            PersistedFiles = persistedFiles,
            ExtractedChunks = extractedChunks,
            PersistedChunks = persistedChunks,
            ExtractedSymbols = extractedSymbols,
            PersistedSymbols = persistedSymbols,
            ExtractedReferences = extractedReferences,
            PersistedReferences = persistedReferences,
            FilesCount = files.Count,
            Skipped = preWriteSelection.Skipped,
            Purged = purged,
            ScanResult = scanResult,
            LanguageCounts = languageCounts,
            HeadChangeDetected = headChangeDetected,
            PriorIndexedHeadCommit = priorIndexedHeadCommit,
            CurrentHeadCommit = currentHeadCommit,
            ShowNextSteps = showNextSteps,
            RebuildReclaim = rebuildReclaim,
        });
    }
}
