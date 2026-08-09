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

    private const int PartialIndexFileErrorLimit = 50;





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

        void ThrowIfFullScanCancelled(int filesProcessed, int? filesTotal)
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            throw new IndexInterruptedException(filesProcessed, filesTotal, actualMode);
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
        var readableFileBytes = new ReadableFileByteTracker(
            files.Count,
            fileIndex => files[fileIndex],
            projectRoot,
            indexRunDiagnostics);
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
        AddProjectMarkerFingerprintWarnings(currentHotspotFamilyMarkerFingerprints, warningList, options);
        var scanCheckpointPath = discovery.ScanCheckpointPath;
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("scan", stopwatch));

        ThrowIfFullScanCancelled(0, files.Count);

        CancellationTokenSource? purgeCts = null;
        if (!options.Json && !options.Quiet)
            purgeCts = ConsoleUi.StartSpinner("Cleaning up stale entries...", spinnerFrames);
        var startedWithNoIndexedFiles = !writer.HasAnyIndexedFiles();
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
        var purgedRefs = 0;

        int processed = 0, skipped = 0, warnings = warningList.Count, errors = errorList.Count;
        var ftsMutated = purged > 0;
        var symbolsDroppedByKindFilter = 0;
        var mutualRecursionRefreshNeeded = !options.SymbolsOnly
            && (!writer.ReferenceIdentityContractMatchesCurrent() || purged > 0);

        var indexProgressVisible = false;
        var indexProgress = new IndexProgressReporter(
            options,
            "Indexing...",
            spinnerFrames,
            ConsoleUi.TryWriteErrorLine,
            canResume: () => processed < files.Count && !indexProgressVisible,
            clearProgressLineBeforeWrite: true);
        HashSet<string>? reusedHotspotFamilyLanguages = null;
        HashSet<string>? skippedSymbolExtractorLanguages = null;
        var indexedSymbolExtractorLanguages = new HashSet<string>(languageCounts.Count, StringComparer.Ordinal);
        string? currentJsonIndexFile = null;
        ActiveExtractionPhase?[] activeExtractionPhases = [];
        using var fullScanProgress = new FullScanProgressSession(
            options,
            files.Count,
            indexProgress,
            () => processed,
            () => indexProgressVisible,
            () => currentJsonIndexFile,
            () => activeExtractionPhases);
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

        void DeferCSharpMutationsForIncompleteWorkspace(
            CSharpStaticInterfaceWorkspaceSymbols workspace)
        {
            if (workspace.SourceContractEvidenceComplete)
                return;

            deferCSharpMutationsForIncompleteScan = true;
            staleFilePurgePlan = FilePurgePlan.Empty;
            purged = 0;
            ftsMutated = false;
            hadCSharpStaticInterfaceContractsBeforePurge = false;

            var incompletePaths = workspace.IncompleteSourcePaths;
            if (incompletePaths == null || incompletePaths.Count == 0)
            {
                RecordCSharpWorkspaceFailure(
                    "<csharp_workspace>",
                    "csharp_prepass",
                    new IOException("C# static-interface workspace preflight could not read a source file."));
                return;
            }

            foreach (var path in incompletePaths.Take(PartialIndexFileErrorLimit))
            {
                RecordCSharpWorkspaceFailure(
                    path,
                    "csharp_prepass",
                    new IOException("C# static-interface workspace preflight could not read this source file."));
            }
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

        bool TargetRequiresJavaScriptTypeScriptRefresh(string? language, string indexPath)
            => javaScriptTypeScriptRefreshRequired
               && (IsJavaScriptTypeScriptLanguage(language) || IsJavaScriptTypeScriptConfigPath(indexPath));

        void InsertIssuesForIndexedFile(long fileId, IReadOnlyList<FileIssue> issues)
        {
            if (startedWithNoIndexedFiles)
                writer.InsertIssuesForNewFile(fileId, issues);
            else
                writer.InsertIssues(fileId, issues);
        }

        bool IsExistingCSharpSymbolPathNowNonCSharp(string indexPath)
        {
            var currentPath = Path.Combine(
                projectRoot,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(indexPath));
            return scanResult.FileLanguages.TryGetValue(currentPath, out var currentLanguage)
                && currentLanguage != "csharp";
        }

        var csharpPreflight = PrepareFullScanCSharpWorkspace(
            new FullScanCSharpPreflightContext
            {
                Writer = writer,
                Indexer = indexer,
                Options = options,
                ProjectRoot = projectRoot,
                FileTargets = fileTargets,
                CSharpPrepassTargets = csharpPrepassTargets,
                CSharpPrepassCapacity = csharpPrepassCapacity,
                StaleFilePurgePlan = staleFilePurgePlan,
                StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
                PriorIndexComplete = priorIndexComplete,
                PriorReadiness = priorReadiness,
                ScanHadErrors = scanHadErrors,
                ForceExtractorRefresh = forceExtractorRefresh,
                PriorSymbolsOnlyGraphOmitted = priorSymbolsOnlyGraphOmitted,
                SymbolKindFilterMatchesPrior = symbolKindFilterMatchesPrior,
                CSharpSymbolNameContractMatchesCurrent =
                    csharpSymbolNameContractMatchesCurrent,
                CSharpIndexedProjectRootCompatible =
                    csharpIndexedProjectRootCompatible,
                CSharpHotspotTrustMatchesCurrent =
                    AllowReuseWithCurrentHotspotFamilyTrust(
                        "csharp",
                        hotspotFamilyTrustMatchesCurrent),
                RequiresConservativeCSharpSourceRefresh =
                    requiresConservativeCSharpSourceRefresh,
                HadCSharpStaticInterfaceContractsBeforePurge =
                    hadCSharpStaticInterfaceContractsBeforePurge,
                PriorCSharpStaticInterfaceSourceEvidence =
                    priorCSharpStaticInterfaceSourceEvidence,
                ProjectRootWritten = projectRootWritten,
                ExtractionParallelism = extractionParallelism,
                FilesCount = files.Count,
                ActualMode = actualMode,
                CancellationToken = cancellationToken,
                IsExistingCSharpSymbolPathNowNonCSharp =
                    IsExistingCSharpSymbolPathNowNonCSharp,
                GetDeferCSharpMutationsForIncompleteScan =
                    () => deferCSharpMutationsForIncompleteScan,
                GetPurged = () => purged,
                DeferCSharpMutationsForIncompleteWorkspace =
                    DeferCSharpMutationsForIncompleteWorkspace,
            });
        var reusableIndexedFileStats =
            csharpPreflight.ReusableIndexedFileStats;
        var csharpPrepassStatReuse =
            csharpPreflight.CSharpPrepassStatReuse;
        var csharpWorkspaceFileSnapshots =
            csharpPreflight.CSharpWorkspaceFileSnapshots;
        var csharpWorkspace = csharpPreflight.CSharpWorkspace;
        var forceFullCSharpRefreshFromInvalidatedNoOp =
            csharpPreflight.ForceFullCSharpRefreshFromInvalidatedNoOp;
        var preservePriorPositiveCSharpSourceNoOp =
            csharpPreflight.PreservePriorPositiveCSharpSourceNoOp;
        var csharpSourceEvidenceForStamp =
            csharpPreflight.CSharpSourceEvidenceForStamp;
        var csharpSourceEvidenceComplete =
            csharpPreflight.CSharpSourceEvidenceComplete;

        void DeferCSharpMutationsForLoadedSnapshotDrift(string path)
        {
            path = FormatCSharpWorkspaceSnapshotPath(projectRoot, path);
            deferCSharpMutationsForIncompleteScan = true;
            preservePriorPositiveCSharpSourceNoOp = false;
            csharpSourceEvidenceForStamp = false;
            csharpSourceEvidenceComplete = false;
            csharpWorkspaceFileSnapshots = null;
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: [path]);
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            RecordCSharpWorkspaceFailure(
                path,
                "csharp_workspace_validation",
                new IOException(
                    "A C# source changed after workspace preflight; rerun indexing to refresh the complete C# graph."));
        }

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
        void CountFreshInsertedRows(
            int chunkCount = 0,
            int symbolCount = 0,
            int referenceCount = 0)
        {
            persistedFiles++;
            persistedChunks += chunkCount;
            persistedSymbols += symbolCount;
            persistedReferences += referenceCount;
            if (!startedWithNoIndexedFiles)
                return;

            freshCountFiles++;
            freshCountChunks += chunkCount;
            freshCountSymbols += symbolCount;
            freshCountReferences += referenceCount;
        }

        var canSkipFullScanTargetsBeforeContentLoad = !forceExtractorRefresh
            && !options.Rebuild
            && !startedWithNoIndexedFiles
            && !options.SymbolsOnly;

        IndexedFileStatReuseResult? GetFullScanTargetStatMatch(
            int fileIndex,
            bool allowCSharpPrepassCache)
        {
            if (!canSkipFullScanTargetsBeforeContentLoad)
                return null;

            var target = fileTargets[fileIndex];
            var language = target.Language;
            var targetRequiresRefresh = TargetRequiresJavaScriptTypeScriptRefresh(language, target.IndexPath);
            var allowReuse = symbolKindFilterMatchesPrior
                && !targetRequiresRefresh
                && !priorSymbolsOnlyGraphOmitted
                && (language != "csharp" || csharpIndexedProjectRootCompatible)
                && (language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                && (language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                && (language != "sql" || sqlGraphContractMatchesCurrent)
                && (language is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(language, hotspotFamilyTrustMatchesCurrent);
            var existingFile = !allowReuse
                ? null
                : allowCSharpPrepassCache
                  && language == "csharp"
                  && csharpPrepassStatReuse != null
                  && csharpPrepassStatReuse.TryGetValue(target.IndexPath, out var cachedCSharpPrepassReuse)
                    ? cachedCSharpPrepassReuse
                    : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        language,
                        target.GeneratedExtractionSuppressed);
            return existingFile;
        }

        void RecordFullScanTargetStatSkip(int fileIndex, IndexedFileStatReuseResult existingFile)
        {
            var target = fileTargets[fileIndex];
            var language = target.Language;
            skipped++;
            processed++;
            readableFileBytes.Remember(fileIndex, existingFile.Size);
            if (!string.IsNullOrWhiteSpace(language))
            {
                skippedSymbolExtractorLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                skippedSymbolExtractorLanguages.Add(language);
            }
            if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(language) && language != null)
            {
                reusedHotspotFamilyLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                reusedHotspotFamilyLanguages.Add(language);
            }
            if (options.Verbose && !options.Json && !options.Quiet)
                CommandOutputWriter.WriteLine($"  [SKIP] {target.IndexPath} (unchanged)");
        }

        var targetSelection = PrepareFullScanExtractionTargets(
            new FullScanTargetSelectionContext
            {
                Writer = writer,
                Indexer = indexer,
                Options = options,
                ProjectRoot = projectRoot,
                FileTargets = fileTargets,
                CSharpPrepassTargets = csharpPrepassTargets,
                StaleFilePurgePlan = staleFilePurgePlan,
                CanSkipTargetsBeforeContentLoad =
                    canSkipFullScanTargetsBeforeContentLoad,
                StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
                CSharpIndexedProjectRootCompatible =
                    csharpIndexedProjectRootCompatible,
                ExtractionParallelism = extractionParallelism,
                PriorCSharpStaticInterfaceSourceEvidence =
                    priorCSharpStaticInterfaceSourceEvidence,
                CSharpWorkspace = csharpWorkspace,
                CSharpWorkspaceFileSnapshots =
                    csharpWorkspaceFileSnapshots,
                ForceFullCSharpRefreshFromInvalidatedNoOp =
                    forceFullCSharpRefreshFromInvalidatedNoOp,
                PreservePriorPositiveCSharpSourceNoOp =
                    preservePriorPositiveCSharpSourceNoOp,
                CSharpSourceEvidenceForStamp =
                    csharpSourceEvidenceForStamp,
                CSharpSourceEvidenceComplete =
                    csharpSourceEvidenceComplete,
                CancellationToken = cancellationToken,
                ThrowIfFullScanCancelled =
                    () => ThrowIfFullScanCancelled(
                        processed,
                        files.Count),
                GetDeferCSharpMutationsForIncompleteScan =
                    () => deferCSharpMutationsForIncompleteScan,
                GetFullScanTargetStatMatch =
                    GetFullScanTargetStatMatch,
                RecordFullScanTargetStatSkip =
                    RecordFullScanTargetStatSkip,
                DeferCSharpMutationsForIncompleteWorkspace =
                    DeferCSharpMutationsForIncompleteWorkspace,
                IsExistingCSharpSymbolPathNowNonCSharp =
                    IsExistingCSharpSymbolPathNowNonCSharp,
            });
        var extractionFileIndexes =
            targetSelection.ExtractionFileIndexes;
        var extractionWorkItemCount =
            targetSelection.ExtractionWorkItemCount;
        csharpWorkspace = targetSelection.CSharpWorkspace;
        csharpWorkspaceFileSnapshots =
            targetSelection.CSharpWorkspaceFileSnapshots;
        forceFullCSharpRefreshFromInvalidatedNoOp =
            targetSelection
                .ForceFullCSharpRefreshFromInvalidatedNoOp;
        preservePriorPositiveCSharpSourceNoOp =
            targetSelection.PreservePriorPositiveCSharpSourceNoOp;
        csharpSourceEvidenceForStamp =
            targetSelection.CSharpSourceEvidenceForStamp;
        csharpSourceEvidenceComplete =
            targetSelection.CSharpSourceEvidenceComplete;

        var useFtsBulkLoad = ShouldUseFullScanFtsBulkLoad(
            options.Rebuild,
            startedWithNoIndexedFiles,
            extractionWorkItemCount,
            staleFilePurgePlan,
            scanHadErrors,
            readableFileBytes,
            reusableIndexedFileStats!,
            fileTargets,
            extractionFileIndexes,
            () => ThrowIfFullScanCancelled(processed, files.Count));

        var finalCSharpRevalidation =
            RevalidateFinalFullScanCSharpNoOp(
                new FullScanCSharpFinalRevalidationContext
                {
                    Writer = writer,
                    Indexer = indexer,
                    Options = options,
                    ProjectRoot = projectRoot,
                    FileTargets = fileTargets,
                    CSharpPrepassTargets = csharpPrepassTargets,
                    StaleFilePurgePlan = staleFilePurgePlan,
                    StartedWithNoIndexedFiles =
                        startedWithNoIndexedFiles,
                    CSharpIndexedProjectRootCompatible =
                        csharpIndexedProjectRootCompatible,
                    ExtractionParallelism = extractionParallelism,
                    PriorCSharpStaticInterfaceSourceEvidence =
                        priorCSharpStaticInterfaceSourceEvidence,
                    ReusableIndexedFileStats =
                        reusableIndexedFileStats!,
                    ExtractionFileIndexes =
                        extractionFileIndexes,
                    ExtractionWorkItemCount =
                        extractionWorkItemCount,
                    UseFtsBulkLoad = useFtsBulkLoad,
                    CSharpWorkspace = csharpWorkspace,
                    CSharpWorkspaceFileSnapshots =
                        csharpWorkspaceFileSnapshots,
                    ForceFullCSharpRefreshFromInvalidatedNoOp =
                        forceFullCSharpRefreshFromInvalidatedNoOp,
                    PreservePriorPositiveCSharpSourceNoOp =
                        preservePriorPositiveCSharpSourceNoOp,
                    CSharpSourceEvidenceForStamp =
                        csharpSourceEvidenceForStamp,
                    CSharpSourceEvidenceComplete =
                        csharpSourceEvidenceComplete,
                    CancellationToken = cancellationToken,
                    DeferCSharpMutationsForIncompleteWorkspace =
                        DeferCSharpMutationsForIncompleteWorkspace,
                    IsExistingCSharpSymbolPathNowNonCSharp =
                        IsExistingCSharpSymbolPathNowNonCSharp,
                });
        extractionFileIndexes =
            finalCSharpRevalidation.ExtractionFileIndexes;
        extractionWorkItemCount =
            finalCSharpRevalidation.ExtractionWorkItemCount;
        useFtsBulkLoad = finalCSharpRevalidation.UseFtsBulkLoad;
        csharpWorkspace = finalCSharpRevalidation.CSharpWorkspace;
        csharpWorkspaceFileSnapshots =
            finalCSharpRevalidation.CSharpWorkspaceFileSnapshots;
        forceFullCSharpRefreshFromInvalidatedNoOp =
            finalCSharpRevalidation
                .ForceFullCSharpRefreshFromInvalidatedNoOp;
        preservePriorPositiveCSharpSourceNoOp =
            finalCSharpRevalidation
                .PreservePriorPositiveCSharpSourceNoOp;
        csharpSourceEvidenceForStamp =
            finalCSharpRevalidation.CSharpSourceEvidenceForStamp;
        csharpSourceEvidenceComplete =
            finalCSharpRevalidation.CSharpSourceEvidenceComplete;
        skipped -= finalCSharpRevalidation.PromotedCSharpTargetCount;
        processed -= finalCSharpRevalidation.PromotedCSharpTargetCount;
        if (finalCSharpRevalidation.PromotedAllCSharpTargets)
        {
            skippedSymbolExtractorLanguages?.Remove("csharp");
            reusedHotspotFamilyLanguages?.Remove("csharp");
        }

        if (discovery.InputSnapshot != null)
        {
            FullScanInputSnapshotBarrierForTesting?.Invoke("before_write");
            if (!indexer.TryValidateScanInputSnapshot(
                    discovery.InputSnapshot,
                    out var changedScanInputPath,
                    cancellationToken))
            {
                return WriteFullScanSnapshotFailure(
                    changedScanInputPath,
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
                        FilesCount = files.Count,
                        Skipped = skipped,
                        DanglingSymlinkCount = scanResult.DanglingSymlinks.Count,
                        Warnings = warnings,
                        Errors = errors,
                        SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                        ErrorList = errorList,
                        FileErrorList = fileErrorList,
                        WarningList = warningList,
                    });
            }
        }

        if (!options.SymbolsOnly && !deferCSharpMutationsForIncompleteScan)
        {
            var changedFilePath = string.Empty;
            var stableFiles = csharpWorkspaceFileSnapshots == null
                || CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    csharpPrepassTargets,
                    csharpWorkspaceFileSnapshots,
                    out changedFilePath,
                    cancellationToken);
            if (!stableFiles)
            {
                var driftPath = FormatCSharpWorkspaceSnapshotPath(projectRoot, changedFilePath);
                var incompleteWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: [driftPath]);
                DeferCSharpMutationsForIncompleteWorkspace(incompleteWorkspace);
                preservePriorPositiveCSharpSourceNoOp = false;
                csharpSourceEvidenceForStamp = false;
                csharpSourceEvidenceComplete = false;
                csharpWorkspaceFileSnapshots = null;
                csharpWorkspace = incompleteWorkspace;
                useFtsBulkLoad = false;

                var deferredCSharpIndexes = new List<int>(csharpPrepassTargets.Count);
                if (extractionFileIndexes == null)
                {
                    extractionFileIndexes = new List<int>(fileTargets.Length);
                    for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
                    {
                        if (fileTargets[fileIndex].Language == "csharp")
                            deferredCSharpIndexes.Add(fileIndex);
                        else
                            extractionFileIndexes.Add(fileIndex);
                    }
                }
                else
                {
                    for (var extractionIndex = extractionFileIndexes.Count - 1; extractionIndex >= 0; extractionIndex--)
                    {
                        var fileIndex = extractionFileIndexes[extractionIndex];
                        if (fileTargets[fileIndex].Language != "csharp")
                            continue;
                        deferredCSharpIndexes.Add(fileIndex);
                        extractionFileIndexes.RemoveAt(extractionIndex);
                    }
                }

                foreach (var fileIndex in deferredCSharpIndexes)
                {
                    long currentSize = 0;
                    try
                    {
                        var info = new FileInfo(fileTargets[fileIndex].FilePath);
                        if (info.Exists && info.Length >= 0)
                            currentSize = info.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                    }
                    RecordFullScanTargetStatSkip(
                        fileIndex,
                        new IndexedFileStatReuseResult(0, currentSize));
                }

                extractionFileIndexes.Sort();
                extractionWorkItemCount = extractionFileIndexes.Count;
            }
        }

        // The captured scan has now crossed its only pre-write authority barrier. Start the
        // outer write scopes immediately afterwards so no durable readiness, evidence, purge,
        // or file mutation can precede the validation above.
        // scan snapshot の write前 authority barrier 通過直後に outer write scope を開始する。
        if (options.Rebuild)
            db.RepairIncompleteBatchReadiness();
        using var referenceGraphRefresh = writer.BeginReferenceGraphRefreshScope(
            options.Rebuild || !writer.HasAnyIndexedFiles());
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh(
            deferSecondaryIndexes: !options.SymbolsOnly && useFtsBulkLoad);
        using var fullScanTxn = writer.BeginTransaction(cancellationToken, "full scan write phase");
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

        var extractionPipeline = RunFullScanExtractionPipeline(
            new FullScanExtractionPipelineContext
            {
                Writer = writer,
                Indexer = indexer,
                Options = options,
                ProjectRoot = projectRoot,
                FileTargets = fileTargets,
                ExtractionFileIndexes = extractionFileIndexes,
                ExtractionWorkItemCount = extractionWorkItemCount,
                ExtractionParallelism = extractionParallelism,
                FilesCount = files.Count,
                ForceExtractorRefresh = forceExtractorRefresh,
                StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
                PriorSymbolsOnlyGraphOmitted =
                    priorSymbolsOnlyGraphOmitted,
                SymbolKindFilterMatchesPrior =
                    symbolKindFilterMatchesPrior,
                CSharpIndexedProjectRootCompatible =
                    csharpIndexedProjectRootCompatible,
                CSharpSymbolNameContractMatchesCurrent =
                    csharpSymbolNameContractMatchesCurrent,
                SqlGraphContractMatchesCurrent =
                    sqlGraphContractMatchesCurrent,
                HdlGraphContractMatchesCurrent =
                    hdlGraphContractMatchesCurrent,
                ReadableFileBytes = readableFileBytes,
                IndexProgress = indexProgress,
                FullScanProgress = fullScanProgress,
                CancellationToken = cancellationToken,
                GetProcessedCount = () => processed,
                PublishProcessedCount = value => processed = value,
                ThrowIfFullScanCancelled =
                    ThrowIfFullScanCancelled,
                SetIndexProgressVisible =
                    value => indexProgressVisible = value,
                SetActiveExtractionPhases =
                    phases => activeExtractionPhases = phases,
                SetCurrentJsonIndexFile =
                    path => currentJsonIndexFile = path,
                GetCurrentJsonIndexFile =
                    () => currentJsonIndexFile,
                GetDeferCSharpMutationsForIncompleteScan =
                    () => deferCSharpMutationsForIncompleteScan,
                GetFtsMutated = () => ftsMutated,
                GetCSharpWorkspace = () => csharpWorkspace,
                GetCSharpWorkspaceFileSnapshots =
                    () => csharpWorkspaceFileSnapshots,
                DeferCSharpMutationsForLoadedSnapshotDrift =
                    DeferCSharpMutationsForLoadedSnapshotDrift,
                TargetRequiresJavaScriptTypeScriptRefresh =
                    TargetRequiresJavaScriptTypeScriptRefresh,
                AllowReuseWithCurrentHotspotFamilyTrust = language =>
                    AllowReuseWithCurrentHotspotFamilyTrust(
                        language,
                        hotspotFamilyTrustMatchesCurrent),
                RequireTypeScriptAugmentationRefresh =
                    RequireTypeScriptAugmentationRefresh,
                WriteProjectRootOnce = WriteProjectRootOnce,
                InsertIssuesForIndexedFile =
                    InsertIssuesForIndexedFile,
                CountFreshInsertedRows = CountFreshInsertedRows,
                ConsumerState = new FullScanExtractionConsumerState
                {
                    FtsMutated = ftsMutated,
                    MutualRecursionRefreshNeeded =
                        mutualRecursionRefreshNeeded,
                    CSharpMetadataTargetsNeedRefresh =
                        csharpMetadataTargetsNeedRefresh,
                    SymbolsDroppedByKindFilter =
                        symbolsDroppedByKindFilter,
                    ReusedHotspotFamilyLanguages =
                        reusedHotspotFamilyLanguages,
                    SkippedSymbolExtractorLanguages =
                        skippedSymbolExtractorLanguages,
                    IndexedSymbolExtractorLanguages =
                        indexedSymbolExtractorLanguages,
                    ErrorList = errorList,
                    FileErrorList = fileErrorList,
                    WarningList = warningList,
                },
            });
        var postExtractionHooks =
            extractionPipeline.PostExtractionHooks;
        var extractionState = extractionPipeline.ConsumerState;
        if (extractionState != null)
        {
            skipped += extractionState.Skipped;
            warnings += extractionState.Warnings;
            errors += extractionState.ErrorsAdded;
            ftsMutated = extractionState.FtsMutated;
            mutualRecursionRefreshNeeded =
                extractionState.MutualRecursionRefreshNeeded;
            csharpMetadataTargetsNeedRefresh =
                extractionState.CSharpMetadataTargetsNeedRefresh;
            symbolsDroppedByKindFilter =
                extractionState.SymbolsDroppedByKindFilter;
            extractedFiles += extractionState.ExtractedFiles;
            extractedChunks += extractionState.ExtractedChunks;
            extractedSymbols += extractionState.ExtractedSymbols;
            extractedReferences += extractionState.ExtractedReferences;
            reusedHotspotFamilyLanguages =
                extractionState.ReusedHotspotFamilyLanguages;
            skippedSymbolExtractorLanguages =
                extractionState.SkippedSymbolExtractorLanguages;
        }

        indexProgress.Pause();

        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));

        ThrowIfFullScanCancelled(processed, files.Count);
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
        ThrowIfFullScanCancelled(processed, files.Count);
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
        ThrowIfFullScanCancelled(processed, files.Count);
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
                DeferCSharpMutationsForLoadedSnapshotDrift(
                    readinessChangedFilePath
                    ?? readinessChangedScanInputPath
                    ?? "<csharp_workspace>");
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
            Processed = processed,
            FileCount = files.Count,
            Skipped = skipped,
            Purged = purged,
            ScanHadErrors = scanHadErrors,
            StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
            HasCSharpFilesAfter = hasCSharpFilesAfter,
            CSharpSourceEvidenceComplete = csharpSourceEvidenceComplete,
            CSharpSourceEvidenceForStamp = csharpSourceEvidenceForStamp,
            PreservePriorPositiveCSharpSourceNoOp = preservePriorPositiveCSharpSourceNoOp,
            CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
            TypeScriptAugmentationNeedsRefresh = typeScriptAugmentationNeedsRefresh,
            TypeScriptAugmentationDirtyNames = typeScriptAugmentationDirtyNames,
            UseScopedTypeScriptAugmentationRefresh = useScopedTypeScriptAugmentationRefresh,
            LanguageCounts = languageCounts,
            ReusedHotspotFamilyLanguages = reusedHotspotFamilyLanguages,
            PriorHotspotFamilyVersions = priorHotspotFamilyVersions,
            PriorHotspotFamilyMarkerFingerprints = priorHotspotFamilyMarkerFingerprints,
            CurrentHotspotFamilyMarkerFingerprints = currentHotspotFamilyMarkerFingerprints,
            IndexedSymbolExtractorLanguages = indexedSymbolExtractorLanguages,
            SkippedSymbolExtractorLanguages = skippedSymbolExtractorLanguages,
            PriorFoldVersion = priorFoldVersion,
            PriorFoldFingerprint = priorFoldFingerprint,
            ScanResult = scanResult,
            ReadableFileBytes = readableFileBytes,
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
        if (referenceSecondaryIndexBulkLoad != null
            && willRebuildTypeScriptAugmentationAfterReadinessValidation)
            writer.ReportReferenceSecondaryIndexBulkLoadState("full_scan_committed");
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
            Skipped = skipped,
            Purged = purged,
            ScanResult = scanResult,
            LanguageCounts = languageCounts,
            HeadChangeDetected = headChangeDetected,
            PriorIndexedHeadCommit = priorIndexedHeadCommit,
            CurrentHeadCommit = currentHeadCommit,
            ShowNextSteps = showNextSteps,
        });
    }
}
