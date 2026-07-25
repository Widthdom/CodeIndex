using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static Action<string>? UpdateScanInputSnapshotBarrierForTesting { get; set; }

    private static int RunUpdateMode(
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
        bool priorFileIndexIncomplete,
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
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentHotspotFamilyMarkerFingerprints,
        string? priorIndexedProjectRoot,
        string? priorIndexedHeadCommit,
        string? currentHeadCommit,
        string? priorSymbolKindFilterSignature,
        string? initialCwd,
        List<string>? indexRunDiagnostics,
        CancellationToken cancellationToken)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        var memorySamples = options.MemoryTrace ? new List<IndexMemorySampleJsonResult> { CaptureMemorySample("start", stopwatch) } : [];
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var currentHdlGraphContractVersion = DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hdlGraphContractMatchesCurrent = priorHdlGraphContractVersion == currentHdlGraphContractVersion;
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions, cancellationToken);
        if (unresolvedMergeExitCode != null)
            return unresolvedMergeExitCode.Value;
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            options.SymbolKindFilter.Signature,
            StringComparison.Ordinal);
        var scopedUpdateSymbolKindFilterMatchesPrior = symbolKindFilterMatchesPrior
            || (priorSymbolKindFilterSignature == null && !options.SymbolKindFilter.IsActive);
        if (!scopedUpdateSymbolKindFilterMatchesPrior)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "symbol-kind filter policy cannot change during a scoped update because existing files would keep symbols from the prior index policy",
                CommandExitCodes.UsageError,
                "Run a full index refresh without --files, --commits, or --changed-between when changing --include-symbol-kind or --exclude-symbol-kind.",
                CommandErrorCodes.UsageError);
        }
        var priorFilterRetainedCSharpContractMembers =
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(
                priorSymbolKindFilterSignature);

        var resolveTargetsExitCode = TryResolveUpdateTargets(
            projectRoot,
            options,
            spinnerFrames,
            jsonOptions,
            cancellationToken,
            out var targetPaths,
            out var gitTargetPaths,
            out var explicitFileTargetPaths,
            out var relevantIgnoreFileChanged);
        if (resolveTargetsExitCode != null)
            return resolveTargetsExitCode.Value;

        // Keep the caller-selected set stable. Static-interface refresh may later add every
        // C# file, but rename cleanup and --changed-between purge planning must remain scoped
        // to the paths that actually triggered this update.
        // static-interface refresh が全 C# を追加しても、rename cleanup / changed-between
        // purge の計画対象は caller が選んだ path のまま固定する。
        var originalTargetPaths = targetPaths.ToArray();

        var typeScriptJavaScriptConfigChanged = ContainsJavaScriptTypeScriptConfigPath(targetPaths);
        var extractorConfigurationChanged = ContainsExtractorConfigurationPath(projectRoot, targetPaths);
        var ambiguousLanguageProjectMarkerChanged = targetPaths.Any(FileIndexer.IsAmbiguousLanguageProjectMarkerPath);
        if (priorFileIndexIncomplete
            || relevantIgnoreFileChanged
            || ContainsIgnoreFilePath(targetPaths)
            || typeScriptJavaScriptConfigChanged
            || extractorConfigurationChanged
            || ambiguousLanguageProjectMarkerChanged)
        {
            if (extractorConfigurationChanged)
                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(projectRoot);

            if (!options.Json && !options.Quiet)
            {
                var reason = priorFileIndexIncomplete
                    ? "an earlier partial index still has unresolved file failures"
                    : extractorConfigurationChanged
                    ? "extractor configuration changes"
                    : typeScriptJavaScriptConfigChanged
                        ? "JavaScript/TypeScript config changes"
                        : ambiguousLanguageProjectMarkerChanged
                            ? "ambiguous-language project marker changes"
                            : "ignore-file changes";
                CommandOutputWriter.WriteLine($"  Detected {reason}; falling back to a full scan to keep the index aligned.");
                CommandOutputWriter.WriteLine();
            }

            // A scoped pass cannot prove that failures outside its target set recovered, and
            // the partial pass deliberately cleared workspace-wide readiness stamps. Reuse the
            // normal incremental full-scan path until every failed file has been revisited; this
            // preserves the failure on unrelated updates and restores all contracts without a
            // destructive rebuild once the source problem is fixed. Issue #4609 review.
            // scoped pass だけでは対象外の失敗回復を証明できず、partial pass は workspace 全体の
            // readiness を落としている。失敗解消までは通常の incremental full-scan に切り替え、
            // 無関係 update で failure を消さず、修正後は rebuild なしで全 contract を復元する。
            return RunFullScan(
                db,
                writer,
                indexer,
                projectRoot,
                resolvedDbPath,
                options,
                stopwatch,
                runStartedAtUtc,
                spinnerFrames,
                jsonOptions,
                priorReadiness,
                priorIndexComplete,
                priorSymbolsOnlyGraphOmitted,
                priorFoldVersion,
                priorFoldFingerprint,
                priorSymbolExtractorVersionsMatchCurrent,
                priorCSharpSymbolNameContractVersion,
                priorMetadataTargetCsharp,
                priorSqlGraphContractVersion,
                priorHdlGraphContractVersion,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints,
                priorIndexedProjectRoot,
                priorIndexedHeadCommit,
                currentHeadCommit,
                priorSymbolKindFilterSignature,
                initialCwd,
                indexRunDiagnostics,
                showNextSteps: false,
                cancellationToken: cancellationToken,
                forceJavaScriptTypeScriptRefresh: typeScriptJavaScriptConfigChanged,
                forceExtractorRefresh: extractorConfigurationChanged || ambiguousLanguageProjectMarkerChanged);
        }

        if (!options.Json && !options.Quiet)
            CommandOutputWriter.WriteLine($"Updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        int updated = 0, removed = 0, skipped = 0, warnings = 0, errors = 0;
        var updateProgress = new IndexProgressReporter(
            options,
            "Updating...",
            spinnerFrames,
            CommandErrorWriter.WriteStderr);
        var errorList = new List<CliJsonMessage>();
        var fileErrorList = new List<StatusIndexFileError>();
        var warningList = new List<CliJsonMessage>();
        warnings += AddProjectMarkerFingerprintWarnings(currentHotspotFamilyMarkerFingerprints, warningList, options);
        var scanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIndexer.FileIdentity>();
        var readinessDemoted = false;
        var mutationPhaseStarted = false;
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = !options.SymbolsOnly
            && (!projectRootWritten || !typeScriptAugmentationVersionMatchesCurrent);
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var useScopedTypeScriptAugmentationRefresh = !options.SymbolsOnly && projectRootWritten;
        using var typeScriptAugmentationDirtyNames = typeScriptAugmentationVersionMatchesCurrent
                ? writer.BeginTypeScriptAugmentationDirtyNameTracking(useScopedTypeScriptAugmentationRefresh)
                : null;
        var ftsMutated = false;
        var referenceIdentityContractMatchedBeforeMutation = writer.ReferenceIdentityContractMatchesCurrent();
        var mutualRecursionRefreshNeeded = !options.SymbolsOnly
            && !referenceIdentityContractMatchedBeforeMutation;
        var purgedRefs = 0;
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
        var supportedGraphLanguages = ReferenceExtractor.GetSupportedLanguages(projectRoot);
        using var postExtractionHooks = new LazyDisposable<PostExtractionHookRunner>(
            () => PostExtractionHookRunner.DiscoverDefault(
                options.MaxFileSizeBytes,
                maxSymbolCount: options.MaxSymbolsPerFile + 1,
                maxReferenceCount: options.MaxReferencesPerFile + 1));
        var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var currentFoldFingerprint = NameFold.Fingerprint();
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var priorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharp == currentMetadataTargetVersion;
        var csharpMetadataTargetsNeedRefresh = !priorMetadataTargetCsharpMatchesCurrent;
        var symbolsDroppedByKindFilter = 0;
        var refreshedDynamicGraphFileCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        void RecordDynamicGraphFileRefresh(string? language)
        {
            if (options.SymbolsOnly
                || !SymbolExtractor.RequiresExplicitReferenceGraphContractStamp(language))
            {
                return;
            }

            refreshedDynamicGraphFileCounts.TryGetValue(language!, out var refreshedCount);
            refreshedDynamicGraphFileCounts[language!] = refreshedCount + 1;
        }

        string[] GetFullyRefreshedDynamicGraphLanguages()
        {
            if (options.SymbolsOnly || refreshedDynamicGraphFileCounts.Count == 0)
                return [];

            using var reader = new DbReader(writer.Connection);
            var currentLanguageCounts = reader.GetIndexedLanguageCounts();
            return refreshedDynamicGraphFileCounts
                .Where(entry =>
                    currentLanguageCounts.TryGetValue(entry.Key, out var currentCount)
                    && currentCount == entry.Value)
                .Select(entry => entry.Key)
                .ToArray();
        }

        void DemoteReadinessOnce()
        {
            if (readinessDemoted)
                return;

            // Demote readiness in its own committed step once we know a real mutation is
            // about to happen. If the following file update rolls back, readers must still
            // see the DB as degraded rather than trusting stale ready bits. No-op updates
            // never call this path, so shared explicit DB metadata stays stable.
            // 実 mutation が必要と確定した時点で readiness を別コミットで下げる。直後の
            // file update が rollback しても reader は stale ready bit を信じない。
            // no-op update では呼ばないので、shared explicit DB の metadata も安定する。
            writer.ClearReadyFlags();
            writer.ClearHotspotFamilyReady();
            writer.ClearMetadataTargetReady();
            writer.ClearReferenceIdentityContractReady();
            readinessDemoted = true;
        }

        void RequireTypeScriptAugmentationRefresh()
        {
            if (!typeScriptAugmentationReadyCleared)
            {
                writer.ClearTypeScriptAugmentationReady();
                typeScriptAugmentationReadyCleared = true;
            }

            if (!options.SymbolsOnly)
                typeScriptAugmentationNeedsRefresh = true;
        }

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectRoot);
                projectRootWritten = true;
            }
        }

        void RecordScanErrors(
            IEnumerable<FileIndexer.ScanError> scanErrors,
            string fatalPhase = "discovery")
        {
            foreach (var scanError in scanErrors)
            {
                var key = $"{scanError.Severity}\n{scanError.Path}\n{scanError.Message}";
                if (!scanErrorKeys.Add(key))
                    continue;

                if (scanError.IsFatal)
                {
                    if (mutationPhaseStarted)
                        DemoteReadinessOnce();
                    errors++;
                    errorList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                    if (fileErrorList.Count < PartialIndexFileErrorLimit)
                    {
                        fileErrorList.Add(new StatusIndexFileError
                        {
                            File = FileIndexer.NormalizePathSeparators(scanError.Path),
                            Category = "file_read_error",
                            Phase = fatalPhase,
                            Detail = scanError.Message.Length <= 240
                                ? scanError.Message
                                : string.Concat(scanError.Message.AsSpan(0, 239), "\u2026"),
                        });
                    }
                }
                else
                {
                    warnings++;
                    warningList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }

                if (!options.Json)
                {
                    updateProgress.Pause();
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
                    updateProgress.Resume();
                }
            }
        }

        void RecordUpdateFileFailure(
            string relativePath,
            string phase,
            Exception exception)
        {
            DemoteReadinessOnce();
            LogIndexFileFailure("index_update_file_failed", relativePath, phase, exception);

            errors++;
            var errorMessage = FormatIndexFileException(exception);
            errorList.Add(new CliJsonMessage(relativePath, errorMessage));
            if (fileErrorList.Count < PartialIndexFileErrorLimit)
                fileErrorList.Add(BuildIndexFileError(relativePath, phase, exception));
            if (!options.Json)
            {
                updateProgress.Pause();
                CommandErrorWriter.WriteStderr(
                    FormatPerFileErrorLine("ERR ", relativePath, exception, errorMessage));
                updateProgress.Resume();
            }
        }

        void ThrowIfUpdateCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            updateProgress.Pause();
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }

        var csharpWorkspaceDriftDetected = false;

        void RecordCSharpWorkspaceDrift(
            string relativePath,
            string detail,
            string fatalPhase = "reading")
        {
            csharpWorkspaceDriftDetected = true;
            if (mutationPhaseStarted)
                writer.SetCSharpStaticInterfaceSourceEvidence(null);
            RecordScanErrors(
            [
                new FileIndexer.ScanError(
                    relativePath,
                    $"{detail} Rerun indexing to rebuild a stable C# workspace snapshot.")
            ], fatalPhase);
        }

        var priorCSharpStaticInterfaceSourceEvidence =
            writer.GetCSharpStaticInterfaceSourceEvidence();
        var scopedCleanupPlan = PlanUpdateCSharpCleanup(
            writer,
            indexer,
            projectRoot,
            targetPaths,
            gitTargetPaths,
            explicitFileTargetPaths,
            options,
            projectRootWritten,
            priorCSharpStaticInterfaceSourceEvidence,
            ThrowIfUpdateCancelled,
            cancellationToken);
        var scopedCleanupHadCSharp = scopedCleanupPlan.FileIds.Count > 0;
        var scopedCleanupHadContract = scopedCleanupHadCSharp
            && (priorCSharpStaticInterfaceSourceEvidence == true
                || writer.HasCSharpStaticInterfaceContractMembersInFileIds(
                    scopedCleanupPlan.FileIds,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        priorCSharpStaticInterfaceSourceEvidence == null
                        || !priorFilterRetainedCSharpContractMembers,
                    cancellationToken));
        var hadIndexedCSharpFilesBeforeUpdate = writer.HasAnyFilesWithLanguage("csharp");
        int PurgeStaleUpdateCleanupPaths(
            string retainedRelativePath,
            string? checksum,
            bool includeDirectoryAndStem)
        {
            var livePlan = writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                retainedRelativePath,
                checksum,
                includeDirectoryAndStem,
                cancellationToken);
            if (priorCSharpStaticInterfaceSourceEvidence != false
                && writer.HasCSharpFilesInFileIds(livePlan.FileIds, cancellationToken))
            {
                // A C# row that became stale after the immutable preflight was not excluded
                // from the workspace snapshot. Defer that rare cleanup to the next clean scan
                // instead of deleting an unplanned contract definition under live references.
                // immutable preflight 後に stale 化した C# row は workspace から除外されて
                // いないため、未計画 contract deletion は次の clean scan へ延期する。
                throw new CSharpWorkspaceChangedException(
                    "A C# cleanup candidate appeared after the immutable cleanup preflight.");
            }

            return writer.ApplyScopedFileCleanupPlan(livePlan, cancellationToken);
        }

        var csharpPreflight = PrepareUpdateCSharpWorkspace(
            new UpdateCSharpPreflightContext
            {
                Writer = writer,
                Indexer = indexer,
                Options = options,
                ProjectRoot = projectRoot,
                TargetPaths = targetPaths,
                PriorFilterRetainedCSharpContractMembers =
                    priorFilterRetainedCSharpContractMembers,
                PriorCSharpStaticInterfaceSourceEvidence =
                    priorCSharpStaticInterfaceSourceEvidence,
                ScopedCleanupPlan = scopedCleanupPlan,
                ScopedCleanupHadCSharp = scopedCleanupHadCSharp,
                ScopedCleanupHadContract = scopedCleanupHadContract,
                HadIndexedCSharpFilesBeforeUpdate =
                    hadIndexedCSharpFilesBeforeUpdate,
                Updated = updated,
                Removed = removed,
                CancellationToken = cancellationToken,
                ThrowIfUpdateCancelled = ThrowIfUpdateCancelled,
                RecordScanErrors = errors => RecordScanErrors(errors),
                RecordCSharpWorkspaceDrift = (path, detail) =>
                    RecordCSharpWorkspaceDrift(path, detail),
            });
        var scannedUpdateLanguages =
            csharpPreflight.ScannedUpdateLanguages;
        var csharpPrepassTargets =
            csharpPreflight.CSharpPrepassTargets;
        var csharpWorkspace = csharpPreflight.CSharpWorkspace;
        var csharpWorkspaceSnapshots =
            csharpPreflight.CSharpWorkspaceSnapshots;
        var csharpWorkspaceInputSnapshot =
            csharpPreflight.CSharpWorkspaceInputSnapshot;
        var deferCSharpMutationsForIncompleteWorkspace =
            csharpPreflight.DeferCSharpMutationsForIncompleteWorkspace;
        var csharpSourceEvidenceForStamp =
            csharpPreflight.CSharpSourceEvidenceForStamp;
        var csharpSourceEvidenceCompleteForStamp =
            csharpPreflight.CSharpSourceEvidenceCompleteForStamp;
        var csharpTargetAffected =
            csharpPreflight.CSharpTargetAffected;

        var csharpMutationGuard = GuardUpdateCSharpMutationInputs(
            new UpdateCSharpMutationGuardContext
            {
                Writer = writer,
                Indexer = indexer,
                ProjectRoot = projectRoot,
                TargetPaths = targetPaths,
                ScannedUpdateLanguages = scannedUpdateLanguages,
                ScopedCleanupPlan = scopedCleanupPlan,
                CSharpWorkspaceInputSnapshot =
                    csharpWorkspaceInputSnapshot,
                CSharpWorkspaceSnapshots = csharpWorkspaceSnapshots,
                CSharpWorkspace = csharpWorkspace,
                DeferCSharpMutationsForIncompleteWorkspace =
                    deferCSharpMutationsForIncompleteWorkspace,
                CSharpSourceEvidenceForStamp =
                    csharpSourceEvidenceForStamp,
                CSharpSourceEvidenceCompleteForStamp =
                    csharpSourceEvidenceCompleteForStamp,
                CancellationToken = cancellationToken,
                RecordCSharpWorkspaceDrift = (path, detail) =>
                    RecordCSharpWorkspaceDrift(path, detail),
            });
        if (csharpMutationGuard.InputSnapshotFailurePath != null)
        {
            return WriteUpdateSnapshotFailure(
                csharpMutationGuard.InputSnapshotFailurePath,
                new UpdateSnapshotFailureContext
                {
                    Writer = writer,
                    Options = options,
                    Stopwatch = stopwatch,
                    JsonContext = jsonContext,
                    ProjectRoot = projectRoot,
                    PriorReadiness = priorReadiness,
                    CSharpSymbolNameContractMatchesCurrent =
                        csharpSymbolNameContractMatchesCurrent,
                    PriorMetadataTargetCsharpMatchesCurrent =
                        priorMetadataTargetCsharpMatchesCurrent,
                    PriorFoldVersion = priorFoldVersion,
                    PriorFoldFingerprint = priorFoldFingerprint,
                    CurrentFoldVersion = currentFoldVersion,
                    CurrentFoldFingerprint = currentFoldFingerprint,
                    MemorySamples = memorySamples,
                    Skipped = skipped,
                    Warnings = warnings,
                    SymbolsDroppedByKindFilter =
                        symbolsDroppedByKindFilter,
                    ErrorList = errorList,
                    FileErrorList = fileErrorList,
                    WarningList = warningList,
                    RecordCSharpWorkspaceDrift =
                        RecordCSharpWorkspaceDrift,
                    GetErrorCount = () => errors,
                });
        }

        deferCSharpMutationsForIncompleteWorkspace =
            csharpMutationGuard
                .DeferCSharpMutationsForIncompleteWorkspace;
        csharpSourceEvidenceForStamp =
            csharpMutationGuard.CSharpSourceEvidenceForStamp;
        csharpSourceEvidenceCompleteForStamp =
            csharpMutationGuard.CSharpSourceEvidenceCompleteForStamp;
        csharpWorkspaceSnapshots =
            csharpMutationGuard.CSharpWorkspaceSnapshots;
        csharpWorkspace = csharpMutationGuard.CSharpWorkspace;

        bool TryValidateCSharpWorkspaceInputSnapshot(
            out string? changedPath)
        {
            if (csharpWorkspaceInputSnapshot == null)
            {
                changedPath = null;
                return true;
            }

            return indexer.TryValidateScanInputSnapshot(
                csharpWorkspaceInputSnapshot,
                out changedPath,
                cancellationToken);
        }

        // The workspace lookup was built with immutable cleanup IDs excluded.
        // The guard above applies target and path authority checks before mutation.
        // workspace lookup から除外した immutable cleanup ID は、上のguardで
        // target/path authorityを検証してから適用する。

        // Expanded discovery has crossed its sole pre-write snapshot barrier. Start graph
        // tracking only now, then publish conservative C# evidence immediately before the
        // first possible cleanup/reference/file mutation.
        // expanded discovery の write前 barrier 通過後に graph tracking と mutation を開始する。
        using var referenceGraphRefresh = writer.BeginReferenceGraphRefreshScope();
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh();
        mutationPhaseStarted = true;
        // Preflight errors are recorded before the scan barrier, where readiness writes are
        // forbidden. Once that barrier succeeds, demote before recovery/evidence or the
        // later partial-run metadata could leave Issues/Fold readiness falsely authoritative.
        // first barrier failure never reaches this mutation boundary and keeps prior trust.
        // preflight error は barrier 前に記録するため、通過後の mutation 境界で readiness を落とす。
        if (errors > 0)
            DemoteReadinessOnce();
        writer.RecoverInterruptedFtsBulkLoadIfNeeded(cancellationToken);
        if (csharpTargetAffected)
        {
            writer.SetCSharpStaticInterfaceSourceEvidence(
                !deferCSharpMutationsForIncompleteWorkspace
                && csharpSourceEvidenceCompleteForStamp
                && csharpSourceEvidenceForStamp == true
                    ? true
                    : null);
        }

        if (!deferCSharpMutationsForIncompleteWorkspace
            && scopedCleanupPlan.Count > 0)
        {
            using var cleanupTxn = writer.BeginTransaction(
                cancellationToken,
                "update planned stale-file cleanup");
            if (scopedCleanupHadCSharp)
                writer.SetCSharpStaticInterfaceSourceEvidence(null);
            DemoteReadinessOnce();
            WriteProjectRootOnce();
            RequireTypeScriptAugmentationRefresh();
            var plannedPurged = writer.ApplyScopedFileCleanupPlan(
                scopedCleanupPlan,
                cancellationToken);
            cleanupTxn.Commit();
            removed += plannedPurged;
            ftsMutated = true;
            mutualRecursionRefreshNeeded = true;
            csharpMetadataTargetsNeedRefresh |= scopedCleanupHadCSharp;
            updateProgress.WriteVerbose(
                $"  [DEL ] purged {plannedPurged:N0} planned missing indexed path(s)");
        }

        using (var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported references"))
        {
            purgedRefs = writer.PurgeUnsupportedReferences(supportedGraphLanguages);
            if (purgedRefs > 0)
            {
                // Keep the graph cleanup and trust-marker demotion atomic. This avoids a
                // full COUNT scan before the DELETE while still ensuring readers never
                // observe ready graph metadata after stale edges have been committed.
                // graph cleanup と trust-marker の降格を同一 transaction にまとめる。
                // DELETE 前の全件 COUNT scan を省きつつ、stale edge の commit 後に
                // ready metadata が見える状態を防ぐ。
                DemoteReadinessOnce();
                mutualRecursionRefreshNeeded = true;
            }
            purgeTxn.Commit();
        }

        var updateLoop = RunUpdateFileLoop(new UpdateFileLoopContext
        {
            Writer = writer,
            Indexer = indexer,
            Options = options,
            Stopwatch = stopwatch,
            ProjectRoot = projectRoot,
            IndexRunDiagnostics = indexRunDiagnostics,
            TargetPaths = targetPaths,
            UpdateProgress = updateProgress,
            MemorySamples = memorySamples,
            Updated = updated,
            Removed = removed,
            Skipped = skipped,
            FtsMutated = ftsMutated,
            MutualRecursionRefreshNeeded = mutualRecursionRefreshNeeded,
            CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
            SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
            CSharpWorkspace = csharpWorkspace,
            CSharpWorkspaceSnapshots = csharpWorkspaceSnapshots,
            ScannedUpdateLanguages = scannedUpdateLanguages,
            SymbolKindFilterMatchesPrior = symbolKindFilterMatchesPrior,
            CSharpSymbolNameContractMatchesCurrent = csharpSymbolNameContractMatchesCurrent,
            SqlGraphContractMatchesCurrent = sqlGraphContractMatchesCurrent,
            HdlGraphContractMatchesCurrent = hdlGraphContractMatchesCurrent,
            PostExtractionHooks = postExtractionHooks,
            VisitedFileIdentities = visitedFileIdentities,
            ErrorList = errorList,
            FileErrorList = fileErrorList,
            WarningList = warningList,
            CancellationToken = cancellationToken,
            RecordScanErrors = RecordScanErrors,
            RecordCSharpWorkspaceDrift = RecordCSharpWorkspaceDrift,
            DemoteReadinessOnce = DemoteReadinessOnce,
            WriteProjectRootOnce = WriteProjectRootOnce,
            RequireTypeScriptAugmentationRefresh = RequireTypeScriptAugmentationRefresh,
            PurgeStaleUpdateCleanupPaths = PurgeStaleUpdateCleanupPaths,
            RecordDynamicGraphFileRefresh = RecordDynamicGraphFileRefresh,
            RecordUpdateFileFailure = RecordUpdateFileFailure,
            IsProjectRootWritten = () => projectRootWritten,
        });
        updated = updateLoop.Updated;
        removed = updateLoop.Removed;
        skipped = updateLoop.Skipped;
        warnings += updateLoop.Warnings;
        errors += updateLoop.Errors;
        ftsMutated = updateLoop.FtsMutated;
        mutualRecursionRefreshNeeded = updateLoop.MutualRecursionRefreshNeeded;
        csharpMetadataTargetsNeedRefresh = updateLoop.CSharpMetadataTargetsNeedRefresh;
        symbolsDroppedByKindFilter = updateLoop.SymbolsDroppedByKindFilter;
        var readableFileBytes = updateLoop.ReadableFileBytes;

        if (options.ChangedBetweenSpecified
            && priorCSharpStaticInterfaceSourceEvidence == false)
        {
            ThrowIfUpdateCancelled();

            var skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectRoot, cancellationToken);
            if (skipWorktreePaths != null)
            {
                // A prior authoritative false marker needs no repository-wide C# contract
                // preflight, so retain the historical missing-file reconciliation. If this
                // update discovered a new contract (or C# discovery became incomplete),
                // exclude C# from the late plan: only a pre-workspace immutable C# plan may
                // feed a contract-sensitive workspace lookup.
                // prior false marker では従来の missing-file reconciliation を維持する。
                // 今回 contract を新規発見した場合や C# discovery が incomplete の場合は
                // late plan から C# を除外し、workspace 後の C# row を吸収しない。
                var postUpdatePurgePlan = csharpWorkspace.HasStaticInterfaceContracts
                    ? writer.PlanStaleFilesExcludingLanguage(
                        projectRoot,
                        skipWorktreePaths,
                        excludedLanguage: "csharp",
                        cancellationToken)
                    : writer.PlanStaleFiles(
                        projectRoot,
                        skipWorktreePaths,
                        cancellationToken);
                var purgedMissing = writer.ApplyFilePurgePlan(
                    postUpdatePurgePlan,
                    beforeCommit: () =>
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                    },
                    cancellationToken);
                if (purgedMissing > 0)
                {
                    removed += purgedMissing;
                    ftsMutated = true;
                    mutualRecursionRefreshNeeded = true;
                    updateProgress.WriteVerbose(
                        $"  [DEL ] purged {purgedMissing:N0} missing indexed path(s) after --changed-between");
                }
            }
        }

        ThrowIfUpdateCancelled();
        mutualRecursionRefreshNeeded |= !options.SymbolsOnly && (removed > 0 || purgedRefs > 0);
        if (mutualRecursionRefreshNeeded)
            writer.RefreshMutualRecursionFlags(cancellationToken);
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("reference_graph", stopwatch));
        ThrowIfUpdateCancelled();
        updateProgress.Pause();

        if (purgedRefs > 0 && !options.Json && !options.Quiet)
            CommandOutputWriter.WriteLine($"  Purged {purgedRefs:N0} stale references (unsupported language)");

        var ftsMergeRan = false;
        if (ftsMutated)
        {
            ftsMergeRan = writer.RecordFtsIncrementalWriteAndMergeIfThresholdReached(
                cancellationToken: cancellationToken);
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("text_index", stopwatch));
        ThrowIfUpdateCancelled();
        if (csharpWorkspaceInputSnapshot != null)
        {
            UpdateScanInputSnapshotBarrierForTesting?.Invoke("before_readiness");
            var finalChangedCSharpPath = string.Empty;
            var stableFinalFiles = deferCSharpMutationsForIncompleteWorkspace
                || csharpWorkspaceSnapshots == null
                || CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    csharpPrepassTargets,
                    csharpWorkspaceSnapshots,
                    out finalChangedCSharpPath,
                    cancellationToken);
            var stableFinalInputs = TryValidateCSharpWorkspaceInputSnapshot(
                out var finalChangedCSharpDirectoryPath);
            if (!stableFinalFiles || !stableFinalInputs)
            {
                RecordCSharpWorkspaceDrift(
                    !string.IsNullOrEmpty(finalChangedCSharpPath)
                        ? finalChangedCSharpPath
                        : FormatCSharpWorkspaceSnapshotPath(projectRoot, finalChangedCSharpDirectoryPath),
                    "The C# workspace changed before final source-evidence validation.");
                csharpSourceEvidenceForStamp = null;
                csharpSourceEvidenceCompleteForStamp = false;
                csharpWorkspaceSnapshots = null;
                csharpWorkspace = csharpWorkspace with
                {
                    HasStaticInterfaceContracts = true,
                    SourceContractEvidenceComplete = false,
                };
            }
        }
        var readiness = FinalizeUpdateReadiness(new UpdateReadinessContext
        {
            Writer = writer,
            Options = options,
            Stopwatch = stopwatch,
            RunStartedAtUtc = runStartedAtUtc,
            ProjectRoot = projectRoot,
            CancellationToken = cancellationToken,
            PriorReadiness = priorReadiness,
            PriorFoldVersion = priorFoldVersion,
            PriorFoldFingerprint = priorFoldFingerprint,
            CurrentFoldVersion = currentFoldVersion,
            CurrentFoldFingerprint = currentFoldFingerprint,
            PriorSymbolExtractorVersionsMatchCurrent = priorSymbolExtractorVersionsMatchCurrent,
            CSharpSymbolNameContractMatchesCurrent = csharpSymbolNameContractMatchesCurrent,
            PriorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharpMatchesCurrent,
            SqlGraphContractMatchesCurrent = sqlGraphContractMatchesCurrent,
            HdlGraphContractMatchesCurrent = hdlGraphContractMatchesCurrent,
            PriorHotspotFamilyVersions = priorHotspotFamilyVersions,
            PriorHotspotFamilyMarkerFingerprints = priorHotspotFamilyMarkerFingerprints,
            CurrentHotspotFamilyMarkerFingerprints = currentHotspotFamilyMarkerFingerprints,
            ReadinessDemoted = readinessDemoted,
            MutualRecursionRefreshNeeded = mutualRecursionRefreshNeeded,
            ReferenceIdentityContractMatchedBeforeMutation = referenceIdentityContractMatchedBeforeMutation,
            CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
            TypeScriptAugmentationNeedsRefresh = typeScriptAugmentationNeedsRefresh,
            TypeScriptAugmentationDirtyNames = typeScriptAugmentationDirtyNames,
            UseScopedTypeScriptAugmentationRefresh = useScopedTypeScriptAugmentationRefresh,
            Updated = updated,
            Removed = removed,
            Skipped = skipped,
            TargetCount = targetPaths.Count,
            Errors = errors,
            FileErrorList = fileErrorList,
            FullyRefreshedDynamicGraphLanguages = errors == 0 && readinessDemoted
                ? GetFullyRefreshedDynamicGraphLanguages()
                : [],
        });
        var graphTableAvailableAfter = readiness.GraphTableAvailable;
        var issuesTableAvailableAfter = readiness.IssuesTableAvailable;
        var csharpSymbolNameReadyAfter = readiness.CSharpSymbolNameReady;
        var csharpMetadataTargetReadyAfter = readiness.CSharpMetadataTargetReady;
        var foldReadyAfter = readiness.FoldReady;
        var foldReadyReasonAfter = readiness.FoldReadyReason;
        if (postExtractionHooks.ValueIfCreated?.SawCSharpStaticInterfaceSourceContract == true
            && !csharpWorkspaceDriftDetected)
        {
            csharpSourceEvidenceForStamp = true;
            csharpSourceEvidenceCompleteForStamp = true;
            writer.SetCSharpStaticInterfaceSourceEvidence(true);
        }
        hotspotAggregateRefresh.Complete(cancellationToken);
        if (errors == 0)
        {
            if (csharpSourceEvidenceForStamp.HasValue && csharpSourceEvidenceCompleteForStamp)
            {
                writer.SetCSharpStaticInterfaceSourceEvidence(
                    csharpSourceEvidenceForStamp.Value);
            }
            StampIndexedHeadMetadata(writer, projectRoot, indexRunDiagnostics, cancellationToken);
            StampIndexedSymlinkPolicy(writer, options.SymlinkPolicy, indexRunDiagnostics);
            StampCommitScopedFreshHeadMetadata(writer, options, projectRoot, currentHeadCommit, indexRunDiagnostics, cancellationToken);
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(memorySamples);
            var bytesRead = readableFileBytes.MeasureRemaining();
            StampLastIndexRunMetadata(
                writer,
                "update",
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                updated + removed + skipped,
                skipped,
                errors,
                bytesRead.BytesRead,
                bytesRead.SkippedFileCount,
                updated,
                removed,
                memoryTimelineForStamp,
                indexRunDiagnostics,
                writer.GetReferenceExtractionCapHits(issuesTableAvailableAfter));
        }
        return WriteUpdateFinalOutput(new UpdateFinalOutputContext
        {
            Writer = writer,
            Options = options,
            Stopwatch = stopwatch,
            JsonContext = jsonContext,
            ProjectRoot = projectRoot,
            ResolvedDbPath = resolvedDbPath,
            InitialCwd = initialCwd,
            MemorySamples = memorySamples,
            PostExtractionHooks = postExtractionHooks.ValueIfCreated,
            WarningList = warningList,
            ErrorList = errorList,
            FileErrorList = fileErrorList,
            Warnings = warnings,
            Errors = errors,
            SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
            GraphTableAvailableAfter = graphTableAvailableAfter,
            IssuesTableAvailableAfter = issuesTableAvailableAfter,
            CSharpSymbolNameReadyAfter = csharpSymbolNameReadyAfter,
            CSharpMetadataTargetReadyAfter = csharpMetadataTargetReadyAfter,
            FoldReadyAfter = foldReadyAfter,
            FoldReadyReasonAfter = foldReadyReasonAfter,
            Updated = updated,
            Removed = removed,
            Skipped = skipped,
            FtsMergeRan = ftsMergeRan,
        });
    }

    private sealed class CSharpWorkspaceChangedException(string message) : Exception(message);
}
