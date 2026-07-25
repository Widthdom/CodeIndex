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

        IReadOnlyDictionary<string, string>? scannedUpdateLanguages = null;
        ThrowIfUpdateCancelled();
        WriteIndexJsonLiveness(options, "checking C# workspace contracts...");
        var csharpWorkspaceHeartbeat = StartIndexJsonPhaseHeartbeat(options, "checking C# workspace contracts");
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

        var csharpPrepassTargets = BuildUpdateCSharpPrepassTargets(
            indexer,
            projectRoot,
            targetPaths,
            scannedUpdateLanguages,
            out var existingCSharpPathsNowUnsupportedOrNonCSharp);
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? csharpWorkspaceSnapshots = null;
        FileIndexer.ScanInputSnapshot? csharpWorkspaceInputSnapshot = null;
        var deferCSharpMutationsForIncompleteWorkspace = false;
        bool? csharpSourceEvidenceForStamp = null;
        var csharpSourceEvidenceCompleteForStamp = false;
        var preserveConservativePersistedContractEvidence = false;
        var csharpTargetAffected = false;
        bool TryValidateCSharpWorkspaceInputSnapshot(out string? changedPath)
        {
            if (csharpWorkspaceInputSnapshot == null)
            {
                changedPath = null;
                return true;
            }

            var stable = indexer.TryValidateScanInputSnapshot(
                csharpWorkspaceInputSnapshot,
                out var changedInputPath,
                cancellationToken);
            changedPath = changedInputPath;
            return stable;
        }
        try
        {
            var transitionedPathWasCSharp = existingCSharpPathsNowUnsupportedOrNonCSharp is { Count: > 0 }
                && writer.HasCSharpFilesInPaths(
                    existingCSharpPathsNowUnsupportedOrNonCSharp,
                    cancellationToken);
            var transitionedPathHadContract = existingCSharpPathsNowUnsupportedOrNonCSharp is { Count: > 0 }
                && writer.HasCSharpStaticInterfaceContractSymbolsInPaths(
                    existingCSharpPathsNowUnsupportedOrNonCSharp,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        priorCSharpStaticInterfaceSourceEvidence == null
                        || !priorFilterRetainedCSharpContractMembers,
                    cancellationToken);
            csharpTargetAffected = csharpPrepassTargets.Count > 0
                || transitionedPathWasCSharp
                || scopedCleanupHadCSharp;
            var persistedContractEvidence = scopedCleanupHadContract
                || transitionedPathHadContract
                || (csharpTargetAffected
                    && hadIndexedCSharpFilesBeforeUpdate
                    && priorCSharpStaticInterfaceSourceEvidence != false);
            preserveConservativePersistedContractEvidence = persistedContractEvidence;
            if (csharpPrepassTargets.Count == 0 && !persistedContractEvidence)
            {
                csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], transitionedPathHadContract);
            }
            else if (persistedContractEvidence)
            {
                // Persisted contracts already require the complete C# update set. Defer
                // candidate reads and workspace materialization to that authoritative pass.
                // 永続化済みcontractがある場合は全C# update setが必要なため、candidate
                // readとworkspace materializationを後続のauthoritative passへ委譲する。
                csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], true);
            }
            else
            {
                var capturedBefore = CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                    csharpPrepassTargets,
                    out var beforeSnapshots,
                    out _,
                    cancellationToken);
                if (!capturedBefore)
                {
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        HasStaticInterfaceContracts: true,
                        SourceContractEvidenceComplete: false);
                }
                else
                {
                    UpdateCSharpPrepassForTesting?.Invoke();
                    csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        includeExistingSymbols: false,
                        parallelism: options.Parallelism,
                        cancellationToken: cancellationToken);
                    if (!CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                        csharpPrepassTargets,
                        beforeSnapshots,
                        out _,
                        cancellationToken))
                    {
                        csharpWorkspace = csharpWorkspace with
                        {
                            HasStaticInterfaceContracts = true,
                            SourceContractEvidenceComplete = false,
                        };
                    }
                    else
                    {
                        csharpWorkspaceSnapshots = beforeSnapshots;
                    }
                }
            }

            if (csharpTargetAffected && priorCSharpStaticInterfaceSourceEvidence == false)
            {
                csharpSourceEvidenceForStamp = csharpWorkspace.HasSourceStaticInterfaceContracts;
                csharpSourceEvidenceCompleteForStamp = csharpWorkspace.SourceContractEvidenceComplete;
            }

            if (!csharpWorkspace.SourceContractEvidenceComplete)
            {
                csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(csharpWorkspaceHeartbeat);
        }
        if (csharpWorkspace.HasStaticInterfaceContracts)
        {
            WriteIndexJsonLiveness(options, "expanding C# update set for static interface contracts...");
            var expandHeartbeat = StartIndexJsonPhaseHeartbeat(options, "expanding C# update set for static interface contracts");
            try
            {
                UpdateCSharpExpansionScanStartingForTesting?.Invoke();
                var scanWithDirectorySnapshots =
                    indexer.ScanFilesDetailedWithDirectoryListingSnapshots(
                    cancellationToken: cancellationToken);
                var scanResult = scanWithDirectorySnapshots.ScanResult;
                csharpWorkspaceInputSnapshot = scanWithDirectorySnapshots.InputSnapshot;
                var expandedScanHadFatalErrors = scanResult.Errors.Any(error => error.IsFatal);
                RecordScanErrors(scanResult.Errors);
                scannedUpdateLanguages = scanResult.FileLanguages;
                if (expandedScanHadFatalErrors)
                {
                    // An incomplete enumeration cannot prove that a hook-hidden source
                    // contract was absent from the omitted subtree. Preserve every C# row
                    // and reference instead of rebuilding visible implementations against
                    // a partial lookup. Non-C# caller targets may still make progress.
                    // 不完全列挙では omitted subtree の hook-hidden contract 不在を証明
                    // できないため、C# row/ref は全て保持し、non-C# target のみ進める。
                    deferCSharpMutationsForIncompleteWorkspace = true;
                    csharpSourceEvidenceForStamp = null;
                    csharpSourceEvidenceCompleteForStamp = false;
                    csharpWorkspaceSnapshots = null;
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        HasStaticInterfaceContracts: true,
                        SourceContractEvidenceComplete: false);
                    DeferCSharpTargetsAfterIncompleteWorkspace(
                        writer,
                        projectRoot,
                        targetPaths,
                        cancellationToken);
                }
                else
                {
                    var expandedTargetIndexPaths = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var existingTargetPath in targetPaths)
                    {
                        expandedTargetIndexPaths.Add(
                            UpdateFileTarget.Create(projectRoot, existingTargetPath).IndexPath);
                    }
                    foreach (var filePath in scanResult.Files)
                    {
                        if (scanResult.FileLanguages.TryGetValue(filePath, out var language)
                            && language == "csharp"
                            && expandedTargetIndexPaths.Add(
                                UpdateFileTarget.Create(projectRoot, filePath).IndexPath))
                        {
                            targetPaths.Add(filePath);
                        }
                    }

                    csharpPrepassTargets = BuildUpdateCSharpPrepassTargets(
                        indexer,
                        projectRoot,
                        targetPaths,
                        scannedUpdateLanguages,
                        out existingCSharpPathsNowUnsupportedOrNonCSharp);
                    var capturedBefore = CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                        csharpPrepassTargets,
                        out var beforeSnapshots,
                        out var snapshotFailurePath,
                        cancellationToken);
                    if (csharpPrepassTargets.Count == 0)
                    {
                        csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
                    }
                    else if (!capturedBefore)
                    {
                        csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                            [],
                            HasStaticInterfaceContracts: true,
                            SourceContractEvidenceComplete: false);
                    }
                    else
                    {
                        UpdateCSharpPrepassForTesting?.Invoke();
                        csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                            writer,
                            indexer,
                            csharpPrepassTargets,
                            isExistingSymbolPathExcluded: path =>
                                existingCSharpPathsNowUnsupportedOrNonCSharp?.Contains(path) == true,
                            parallelism: options.Parallelism,
                            excludedExistingFileIds: scopedCleanupPlan.FileIds,
                            cancellationToken: cancellationToken);
                    }

                    string? afterSnapshotFailurePath = null;
                    var stableFilesAfterPrepass = capturedBefore
                        && CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                            csharpPrepassTargets,
                            beforeSnapshots,
                            out afterSnapshotFailurePath,
                            cancellationToken);
                    var stableSnapshot = stableFilesAfterPrepass
                        && csharpWorkspace.SourceContractEvidenceComplete;
                    if (!stableSnapshot)
                    {
                        deferCSharpMutationsForIncompleteWorkspace = true;
                        RecordCSharpWorkspaceDrift(
                            csharpWorkspace.IncompleteSourcePaths?.FirstOrDefault()
                                ?? snapshotFailurePath
                                ?? afterSnapshotFailurePath
                                ?? "<csharp_workspace>",
                            "The C# workspace changed or became unreadable during contract preflight.");
                        csharpSourceEvidenceForStamp = null;
                        csharpSourceEvidenceCompleteForStamp = false;
                        csharpWorkspaceSnapshots = null;
                        csharpWorkspace = csharpWorkspace with
                        {
                            HasStaticInterfaceContracts = true,
                            SourceContractEvidenceComplete = false,
                        };
                        DeferCSharpTargetsAfterIncompleteWorkspace(
                            writer,
                            projectRoot,
                            targetPaths,
                            cancellationToken);
                    }
                    else
                    {
                        csharpWorkspaceSnapshots = beforeSnapshots;
                        csharpSourceEvidenceForStamp = csharpWorkspace.HasSourceStaticInterfaceContracts;
                        csharpSourceEvidenceCompleteForStamp = true;

                        // Persisted positive/legacy evidence remains conservative until
                        // every C# file has been refreshed successfully. Even when the new
                        // source snapshot is negative, disable C# stat reuse for this pass.
                        // persisted positive/legacy evidence は全C# refresh成功まで保持し、
                        // 新 snapshot がnegativeでも今回のC# stat reuseは無効化する。
                        if (preserveConservativePersistedContractEvidence)
                            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new IndexInterruptedException(updated + removed, targetPaths.Count);
            }
            finally
            {
                StopIndexJsonPhaseHeartbeat(expandHeartbeat);
            }
        }

        if (csharpWorkspaceInputSnapshot != null)
        {
            UpdateScanInputSnapshotBarrierForTesting?.Invoke("before_write");
            if (!TryValidateCSharpWorkspaceInputSnapshot(out var changedInputPath))
                return WriteUpdateSnapshotFailure(
                    changedInputPath ?? projectRoot,
                    new UpdateSnapshotFailureContext
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
                        CurrentFoldVersion = currentFoldVersion,
                        CurrentFoldFingerprint = currentFoldFingerprint,
                        MemorySamples = memorySamples,
                        Skipped = skipped,
                        Warnings = warnings,
                        SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                        ErrorList = errorList,
                        FileErrorList = fileErrorList,
                        WarningList = warningList,
                        RecordCSharpWorkspaceDrift = RecordCSharpWorkspaceDrift,
                        GetErrorCount = () => errors,
                    });
        }

        string? changedCSharpTargetPath = null;
        var stableCSharpWorkspaceBeforeMutation = csharpWorkspaceSnapshots == null
            || TryValidateCurrentCSharpTargetSet(
                projectRoot,
                targetPaths,
                scannedUpdateLanguages,
                csharpWorkspaceSnapshots,
                out changedCSharpTargetPath,
                cancellationToken);
        if (!deferCSharpMutationsForIncompleteWorkspace
            && !stableCSharpWorkspaceBeforeMutation)
        {
            deferCSharpMutationsForIncompleteWorkspace = true;
            RecordCSharpWorkspaceDrift(
                changedCSharpTargetPath ?? "<csharp_workspace>",
                "The C# workspace target set changed after contract preflight.");
            csharpSourceEvidenceForStamp = null;
            csharpSourceEvidenceCompleteForStamp = false;
            csharpWorkspaceSnapshots = null;
            csharpWorkspace = csharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
                SourceContractEvidenceComplete = false,
            };
            DeferCSharpTargetsAfterIncompleteWorkspace(
                writer,
                projectRoot,
                targetPaths,
                cancellationToken);
        }

        // The workspace lookup was built with these immutable IDs excluded. Apply exactly
        // that snapshot only after complete C# discovery/preflight; fatal scans leave the
        // old rows and references untouched for the retry.
        // workspace lookup から除外した immutable ID snapshot は complete な C# discovery
        // 後だけ適用し、fatal scan 時は旧 row/ref を retry まで保持する。
        if (!deferCSharpMutationsForIncompleteWorkspace && scopedCleanupPlan.Count > 0)
        {
            UpdateScanInputSnapshotBarrierForTesting?.Invoke("before_cleanup_apply");
            Dictionary<string, HashSet<FileIndexer.FileIdentity>>?
                retainedFileIdentitiesByCaseFold = null;
            var retainedPathsExact = new HashSet<string>(StringComparer.Ordinal);
            foreach (var retainedTargetPath in targetPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retainedTarget = UpdateFileTarget.Create(projectRoot, retainedTargetPath);
                retainedPathsExact.Add(retainedTarget.IndexPath);
                var ioPath = LongPath.EnsureWindowsPrefix(retainedTarget.FilePath);
                if (!File.Exists(ioPath))
                    continue;

                if (FileIndexer.TryGetFileIdentity(ioPath, out var retainedIdentity))
                {
                    retainedFileIdentitiesByCaseFold ??= new Dictionary<
                        string,
                        HashSet<FileIndexer.FileIdentity>>(StringComparer.OrdinalIgnoreCase);
                    if (!retainedFileIdentitiesByCaseFold.TryGetValue(
                            retainedTarget.IndexPath,
                            out var retainedIdentities))
                    {
                        retainedIdentities = [];
                        retainedFileIdentitiesByCaseFold.Add(
                            retainedTarget.IndexPath,
                            retainedIdentities);
                    }

                    retainedIdentities.Add(retainedIdentity);
                }
            }

            var reappearedCleanupPath = writer.FindReappearedFileInScopedCleanupPlan(
                projectRoot,
                scopedCleanupPlan.FileIds,
                retainedPathsExact,
                retainedFileIdentitiesByCaseFold,
                cancellationToken);
            if (reappearedCleanupPath != null)
            {
                deferCSharpMutationsForIncompleteWorkspace = true;
                RecordCSharpWorkspaceDrift(
                    reappearedCleanupPath,
                    "A cleanup-planned path reappeared after C# workspace discovery.");
                csharpSourceEvidenceForStamp = null;
                csharpSourceEvidenceCompleteForStamp = false;
                csharpWorkspaceSnapshots = null;
                csharpWorkspace = csharpWorkspace with
                {
                    HasStaticInterfaceContracts = true,
                    SourceContractEvidenceComplete = false,
                };
                DeferCSharpTargetsAfterIncompleteWorkspace(
                    writer,
                    projectRoot,
                    targetPaths,
                    cancellationToken);
            }
        }

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

        if (!deferCSharpMutationsForIncompleteWorkspace && scopedCleanupPlan.Count > 0)
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

        updateProgress.Start();

        var updateTargets = new UpdateFileTarget[targetPaths.Count];
        var updateTargetIndex = 0;
        foreach (var targetPath in targetPaths)
            updateTargets[updateTargetIndex++] = UpdateFileTarget.Create(projectRoot, targetPath);
        var readableFileBytes = new ReadableFileByteTracker(
            updateTargets.Length,
            targetIndex => updateTargets[targetIndex].FilePath,
            projectRoot,
            indexRunDiagnostics);

        WriteIndexJsonLiveness(options, $"updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        string? currentUpdatePath = null;
        var currentUpdatePhase = "preparing";
        var updateHeartbeat = StartIndexJsonPhaseHeartbeat(
            options,
            "updating index",
            () => currentUpdatePath == null
                ? $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed"
                : $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed, current {currentUpdatePath}");
        using var symbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(() =>
        {
            UpdateExtractionWorkStartedForTesting?.Invoke();
            return new SymbolExtractionWorkerClient(options.MaxFileSizeBytes);
        });
        try
        {
            for (var targetIndex = 0; targetIndex < updateTargets.Length; targetIndex++)
            {
                var target = updateTargets[targetIndex];
                ThrowIfUpdateCancelled();
                updateProgress.Start();
                var relPath = target.RelativePath;
                currentUpdatePath = relPath;
                currentUpdatePhase = "preparing";
                var absPath = target.FilePath;
                var dbPath = target.IndexPath;
                var fileBatchMarked = false;
                string? knownLanguage = null;
                CSharpStaticInterfacePrepass.FileStatSnapshot csharpWorkspaceSnapshot = default;
                var hasCSharpWorkspaceSnapshot = csharpWorkspaceSnapshots != null
                    && csharpWorkspaceSnapshots.TryGetValue(dbPath, out csharpWorkspaceSnapshot);
                try
                {
                    if (hasCSharpWorkspaceSnapshot
                        && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                            absPath,
                            dbPath,
                            relPath,
                            csharpWorkspaceSnapshot.Size,
                            csharpWorkspaceSnapshot.ModifiedUtc,
                            csharpWorkspaceSnapshots!,
                            out _,
                            cancellationToken))
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "The C# file changed before its authoritative update pass.");
                        skipped++;
                        continue;
                    }

                    if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                    {
                        if (hasCSharpWorkspaceSnapshot)
                        {
                            RecordCSharpWorkspaceDrift(
                                relPath,
                                "The C# file disappeared after contract preflight.");
                            skipped++;
                            continue;
                        }

                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing target");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                            updateProgress.WriteVerbose($"  [DEL ] {relPath}");
                        }
                        else
                        {
                            skipped++;
                            updateProgress.WriteVerbose($"  [SKIP] {relPath} (not in DB)");
                        }
                        continue;
                    }

                    var pathFilter = indexer.EvaluatePathFilter(absPath);
                    RecordScanErrors(pathFilter.Errors);
                    if (pathFilter.ShouldSkip)
                    {
                        if (!pathFilter.ShouldDeleteExisting)
                        {
                            skipped++;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                updateProgress.Resume();
                            }
                            continue;
                        }

                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete skipped path");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [DEL ] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                updateProgress.Resume();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                updateProgress.Resume();
                            }
                        }
                        continue;
                    }

                    var indexability = indexer.GetFileIndexabilityForIndexing(absPath);
                    var detection = indexer.TryDetectLanguageForIndexing(absPath, knownIndexability: indexability);
                    if (hasCSharpWorkspaceSnapshot
                        && (indexability != FileIndexer.FileProbeStatus.Supported
                            || detection.Status != FileIndexer.FileProbeStatus.Supported
                            || detection.Language != "csharp"))
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "The C# file changed language or indexability after contract preflight.");
                        skipped++;
                        continue;
                    }
                    if (!hasCSharpWorkspaceSnapshot
                        && csharpWorkspaceSnapshots != null
                        && indexability == FileIndexer.FileProbeStatus.Supported
                        && detection.Status == FileIndexer.FileProbeStatus.Supported
                        && detection.Language == "csharp")
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "A C# target appeared after the authoritative workspace target set was captured.");
                        skipped++;
                        continue;
                    }
                    if (indexability == FileIndexer.FileProbeStatus.Missing || detection.Status == FileIndexer.FileProbeStatus.Missing)
                    {
                        var message = $"{relPath}: skipped because it was deleted during indexing.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            ConsoleUi.PrintWarning(message);
                            updateProgress.Resume();
                        }

                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing during probe");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                        }
                        else
                        {
                            skipped++;
                        }
                        continue;
                    }

                    if (indexability == FileIndexer.FileProbeStatus.ProbeFailed || detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                    {
                        DemoteReadinessOnce();

                        errors++;
                        errorList.Add(new CliJsonMessage(relPath, "Could not probe file for indexability/language."));
                        if (fileErrorList.Count < PartialIndexFileErrorLimit)
                        {
                            fileErrorList.Add(new StatusIndexFileError
                            {
                                File = FileIndexer.NormalizePathSeparators(relPath),
                                Category = "file_read_error",
                                Phase = "reading",
                                Detail = "Could not probe file for indexability/language.",
                            });
                        }
                        if (!options.Json)
                        {
                            updateProgress.Pause();
                            if (options.Verbose)
                                CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            else
                                CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            updateProgress.Resume();
                        }
                        continue;
                    }

                    if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                    {
                        if (!writer.HasFileAtPath(dbPath))
                        {
                            using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported renamed target");
                            var purged = PurgeStaleUpdateCleanupPaths(
                                dbPath,
                                checksum: null,
                                includeDirectoryAndStem: projectRootWritten);
                            if (purged > 0)
                            {
                                DemoteReadinessOnce();
                                WriteProjectRootOnce();
                                RequireTypeScriptAugmentationRefresh();
                                purgeTxn.Commit();
                                removed += purged;
                                ftsMutated = true;
                                mutualRecursionRefreshNeeded = true;
                                if (options.Verbose && !options.Json && !options.Quiet)
                                {
                                    updateProgress.Pause();
                                    CommandOutputWriter.WriteLine($"  [DEL ] {relPath} (unsupported renamed target)");
                                    updateProgress.Resume();
                                }
                            }
                            else
                            {
                                skipped++;
                                if (options.Verbose && !options.Json && !options.Quiet)
                                {
                                    updateProgress.Pause();
                                    CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                                    updateProgress.Resume();
                                }
                            }
                            continue;
                        }

                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete unsupported target");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [DEL ] {relPath} (no longer indexable)");
                                updateProgress.Resume();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json)
                            {
                                updateProgress.Pause();
                                CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                                updateProgress.Resume();
                            }
                        }
                        continue;
                    }

                    if (FileIndexer.TryGetFileIdentity(absPath, out var identity, out var linkCount)
                        && linkCount > 1
                        && !visitedFileIdentities.Add(identity))
                    {
                        var message = "Skipped hardlinked file because the same file content was already indexed from another path.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            ConsoleUi.PrintWarning($"{relPath}: {message}");
                            updateProgress.Resume();
                        }

                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                        }
                        else
                        {
                            skipped++;
                        }
                        continue;
                    }

                    var statReusableLanguage = GetStatReusableLanguage(absPath, detection);
                    var generatedExtractionSuppressed = indexer.IsGeneratedCodeExtractionSuppressed(dbPath);
                    var statMatchedFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        writer,
                        absPath,
                        dbPath,
                        statReusableLanguage,
                        options.MaxSymbolsPerFile,
                        options.MaxReferencesPerFile,
                        generatedExtractionSuppressed,
                        allowReuse: symbolKindFilterMatchesPrior
                            && (statReusableLanguage != "csharp" || csharpSymbolNameContractMatchesCurrent)
                            && (statReusableLanguage != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                            && (statReusableLanguage != "sql" || sqlGraphContractMatchesCurrent)
                            && (statReusableLanguage is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent));
                    if (statMatchedFile != null)
                    {
                        skipped++;
                        readableFileBytes.Remember(targetIndex, statMatchedFile.Value.Size);
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine($"  [SKIP] {relPath} (unchanged)");
                            updateProgress.Resume();
                        }
                        continue;
                    }

                    knownLanguage = scannedUpdateLanguages == null
                        ? statReusableLanguage
                        : FileIndexer.GetReusableDetectedLanguage(absPath, scannedUpdateLanguages);

                    currentUpdatePhase = "reading";
                    UpdateFileContentLoadForTesting?.Invoke(relPath);
                    var loaded = indexer.BuildLoadedRecordWithRawBytes(
                        absPath,
                        relPath,
                        knownLanguage,
                        cancellationToken);
                    var record = loaded.Record;
                    if (hasCSharpWorkspaceSnapshot
                        && (record.Lang != "csharp"
                            || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                absPath,
                                dbPath,
                                relPath,
                                record.Size,
                                record.Modified,
                                csharpWorkspaceSnapshots!,
                                out _,
                                cancellationToken)))
                    {
                        RecordCSharpWorkspaceDrift(
                            relPath,
                            "The C# file changed while the authoritative update pass was reading it.");
                        skipped++;
                        continue;
                    }
                    readableFileBytes.Remember(targetIndex, record.Size);
                    var warning = loaded.Warning;
                    var generatedSuppressionIssue = generatedExtractionSuppressed
                        ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                        : null;

                    if (warning != null && !options.Json && !options.Quiet)
                    {
                        updateProgress.Pause();
                        ConsoleUi.PrintWarning(warning);
                        updateProgress.Resume();
                    }

                    var existingId = writer.GetReusableUnchangedFileId(
                        record.Path,
                        record.Modified,
                        record.Checksum,
                        size: record.Size,
                        lines: record.Lines,
                        language: record.Lang,
                        generated: record.Generated,
                        maxSymbolsPerFile: options.MaxSymbolsPerFile,
                        maxReferencesPerFile: options.MaxReferencesPerFile,
                        generatedExtractionSuppressed: generatedExtractionSuppressed,
                        allowReuse: symbolKindFilterMatchesPrior
                            && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                            && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                            && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                            && (record.Lang is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent));
                    if (existingId != null)
                    {
                        using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unchanged stale paths");
                        var purged = PurgeStaleUpdateCleanupPaths(
                            record.Path,
                            record.Checksum,
                            includeDirectoryAndStem: projectRootWritten);
                        if (purged > 0)
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            purgeTxn.Commit();
                            removed += purged;
                            ftsMutated = true;
                            mutualRecursionRefreshNeeded = true;
                        }
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            CommandOutputWriter.WriteLine(purged > 0
                                ? $"  [SKIP] {relPath} (unchanged; purged {purged:N0} stale renamed path(s))"
                                : $"  [SKIP] {relPath} (unchanged)");
                            updateProgress.Resume();
                        }
                        continue;
                    }

                    DemoteReadinessOnce();
                    if (record.Lang == "csharp")
                        csharpMetadataTargetsNeedRefresh = true;
                    var persistence = PersistUpdateFile(new UpdateFilePersistenceContext
                    {
                        Writer = writer,
                        Indexer = indexer,
                        Options = options,
                        ProjectRoot = projectRoot,
                        RelativePath = relPath,
                        AbsolutePath = absPath,
                        Record = record,
                        Loaded = loaded,
                        GeneratedSuppressionIssue = generatedSuppressionIssue,
                        CSharpWorkspace = csharpWorkspace,
                        PostExtractionHooks = postExtractionHooks.Value,
                        SymbolExtractionWorker = symbolExtractionWorker.Value,
                        ProjectRootWritten = projectRootWritten,
                        CancellationToken = cancellationToken,
                        RequireTypeScriptAugmentationRefresh = RequireTypeScriptAugmentationRefresh,
                        PurgeStaleUpdateCleanupPaths = PurgeStaleUpdateCleanupPaths,
                        WriteProjectRootOnce = WriteProjectRootOnce,
                        RecordDynamicGraphFileRefresh = RecordDynamicGraphFileRefresh,
                        SetBatchMarkerOwned = owned => fileBatchMarked = owned,
                        SetPhase = (path, phase) =>
                        {
                            currentUpdatePath = path;
                            currentUpdatePhase = phase;
                        },
                    });
                    symbolsDroppedByKindFilter += persistence.SymbolsDroppedByKindFilter;
                    mutualRecursionRefreshNeeded |= persistence.MutualRecursionRefreshNeeded;
                    updated++;
                    ftsMutated = true;
                    UpdateFileCommittedForTesting?.Invoke(updated + removed, targetPaths.Count);
                    ThrowIfUpdateCancelled();
                    updateProgress.WriteVerbose(persistence.VerboseMessage);
                }
                catch (IndexExtractionStalledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is CSharpWorkspaceChangedException)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();
                        RecordCSharpWorkspaceDrift(relPath, ex.Message);
                        skipped++;
                        continue;
                    }

                    if (ex is FileIndexer.BinaryFileSkippedException binaryFile)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        if (hasCSharpWorkspaceSnapshot
                            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                absPath,
                                dbPath,
                                relPath,
                                csharpWorkspaceSnapshot.Size,
                                csharpWorkspaceSnapshot.ModifiedUtc,
                                csharpWorkspaceSnapshots!,
                                out _,
                                cancellationToken))
                        {
                            RecordCSharpWorkspaceDrift(
                                relPath,
                                "The C# file changed to binary content after contract preflight.");
                            skipped++;
                            continue;
                        }

                        warnings++;
                        var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                        warningList.Add(new CliJsonMessage(relPath, sanitizedMessage));
                        if (!options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            ConsoleUi.PrintWarning(sanitizedMessage);
                            updateProgress.Resume();
                        }

                        DemoteReadinessOnce();
                        currentUpdatePhase = "writing";
                        writer.MarkBatchInProgress();
                        var skippedBinaryBatchMarkerOwned = true;
                        try
                        {
                            using var txn = writer.BeginTransaction(cancellationToken, "update skipped binary");
                            var skippedRecord = indexer.BuildSkippedFileRecord(absPath, relPath, knownLanguage);
                            UpdateSkippedFileRecordBuiltForTesting?.Invoke(relPath);
                            if (hasCSharpWorkspaceSnapshot
                                && (skippedRecord.Lang != "csharp"
                                    || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                        absPath,
                                        dbPath,
                                        relPath,
                                        skippedRecord.Size,
                                        skippedRecord.Modified,
                                        csharpWorkspaceSnapshots!,
                                        out _,
                                        cancellationToken)))
                            {
                                throw new CSharpWorkspaceChangedException(
                                    "The C# file changed while recording its binary skip state.");
                            }
                            readableFileBytes.Remember(targetIndex, skippedRecord.Size);
                            var stalePurged = PurgeStaleUpdateCleanupPaths(
                                skippedRecord.Path,
                                skippedRecord.Checksum,
                                includeDirectoryAndStem: projectRootWritten);
                            if (skippedRecord.Lang == "typescript" || stalePurged > 0)
                                RequireTypeScriptAugmentationRefresh();
                            if (!options.SymbolsOnly && stalePurged > 0)
                                mutualRecursionRefreshNeeded = true;
                            WriteProjectRootOnce();
                            var fileId = writer.UpsertFile(skippedRecord, out var referenceIdentityChanged);
                            if (!options.SymbolsOnly && referenceIdentityChanged)
                                mutualRecursionRefreshNeeded = true;
                            writer.InsertChunks([], cancellationToken);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], refreshMutualRecursionFlags: false, cancellationToken);
                            writer.InsertIssues(fileId, [BuildNullByteIssue(binaryFile)]);
                            writer.ClearBatchInProgress();
                            txn.Commit();
                            RecordDynamicGraphFileRefresh(skippedRecord.Lang);
                            skippedBinaryBatchMarkerOwned = false;
                        }
                        catch (CSharpWorkspaceChangedException workspaceChanged)
                        {
                            RecordCSharpWorkspaceDrift(relPath, workspaceChanged.Message);
                            skipped++;
                            continue;
                        }
                        catch (Exception skippedWriteException)
                        {
                            if (skippedWriteException is IndexExtractionStalledException
                                or IndexInterruptedException
                                or OperationCanceledException)
                            {
                                throw;
                            }

                            RecordUpdateFileFailure(relPath, currentUpdatePhase, skippedWriteException);
                            continue;
                        }
                        finally
                        {
                            // MarkBatchInProgress is committed before the nested file
                            // transaction. Any in-process unwind must clear it only after that
                            // transaction has committed or disposed/rolled back. A process crash
                            // intentionally leaves the durable marker for startup repair.
                            // marker は file transaction の外で先に永続化されるため、正常 commit
                            // または rollback/dispose 後の全 unwind で durable に解除する。
                            if (skippedBinaryBatchMarkerOwned)
                                writer.ClearBatchInProgress();
                        }

                        updated++;
                        ftsMutated = true;
                        continue;
                    }

                    if (ex is FileIndexer.FileTooLargeSkippedException fileTooLarge)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        if (hasCSharpWorkspaceSnapshot
                            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                absPath,
                                dbPath,
                                relPath,
                                csharpWorkspaceSnapshot.Size,
                                csharpWorkspaceSnapshot.ModifiedUtc,
                                csharpWorkspaceSnapshots!,
                                out _,
                                cancellationToken))
                        {
                            RecordCSharpWorkspaceDrift(
                                relPath,
                                "The C# file changed size or timestamp after contract preflight.");
                            skipped++;
                            continue;
                        }

                        DemoteReadinessOnce();
                        currentUpdatePhase = "writing";
                        writer.MarkBatchInProgress();
                        var skippedOversizedBatchMarkerOwned = true;
                        try
                        {
                            using var txn = writer.BeginTransaction(cancellationToken, "update skipped oversized file");
                            var skippedRecord = indexer.BuildSkippedFileRecord(absPath, relPath, knownLanguage);
                            UpdateSkippedFileRecordBuiltForTesting?.Invoke(relPath);
                            if (hasCSharpWorkspaceSnapshot
                                && (skippedRecord.Lang != "csharp"
                                    || !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                        absPath,
                                        dbPath,
                                        relPath,
                                        skippedRecord.Size,
                                        skippedRecord.Modified,
                                        csharpWorkspaceSnapshots!,
                                        out _,
                                        cancellationToken)))
                            {
                                throw new CSharpWorkspaceChangedException(
                                    "The C# file changed while recording its oversized skip state.");
                            }
                            readableFileBytes.Remember(targetIndex, skippedRecord.Size);
                            var stalePurged = PurgeStaleUpdateCleanupPaths(
                                skippedRecord.Path,
                                skippedRecord.Checksum,
                                includeDirectoryAndStem: projectRootWritten);
                            if (skippedRecord.Lang == "typescript" || stalePurged > 0)
                                RequireTypeScriptAugmentationRefresh();
                            if (!options.SymbolsOnly && stalePurged > 0)
                                mutualRecursionRefreshNeeded = true;
                            WriteProjectRootOnce();
                            var fileId = writer.UpsertFile(skippedRecord, out var referenceIdentityChanged);
                            if (!options.SymbolsOnly && referenceIdentityChanged)
                                mutualRecursionRefreshNeeded = true;
                            writer.InsertChunks([], cancellationToken);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], refreshMutualRecursionFlags: false, cancellationToken);
                            writer.InsertIssues(fileId,
                            [
                                new FileIssue
                                {
                                    Path = fileTooLarge.RelativePath,
                                    Kind = "file_too_large",
                                    Line = 0,
                                    Message = fileTooLarge.Message,
                                },
                            ]);
                            writer.ClearBatchInProgress();
                            txn.Commit();
                            RecordDynamicGraphFileRefresh(skippedRecord.Lang);
                            skippedOversizedBatchMarkerOwned = false;
                        }
                        catch (CSharpWorkspaceChangedException workspaceChanged)
                        {
                            RecordCSharpWorkspaceDrift(relPath, workspaceChanged.Message);
                            skipped++;
                            continue;
                        }
                        catch (Exception skippedWriteException)
                        {
                            if (skippedWriteException is IndexExtractionStalledException
                                or IndexInterruptedException
                                or OperationCanceledException)
                            {
                                throw;
                            }

                            RecordUpdateFileFailure(relPath, currentUpdatePhase, skippedWriteException);
                            continue;
                        }
                        finally
                        {
                            // Match the binary path: rollback preserves the prior row and the
                            // independently committed marker is cleared after transaction disposal.
                            // binary 経路と同様、旧 row を rollback で保持し、marker は dispose 後に解除する。
                            if (skippedOversizedBatchMarkerOwned)
                                writer.ClearBatchInProgress();
                        }

                        updated++;
                        ftsMutated = true;
                        continue;
                    }

                    if (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        if (hasCSharpWorkspaceSnapshot)
                        {
                            RecordCSharpWorkspaceDrift(
                                relPath,
                                "The C# file disappeared during its authoritative update pass.");
                            skipped++;
                            continue;
                        }

                        var message = $"{relPath}: skipped because it was deleted during indexing.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            updateProgress.Pause();
                            ConsoleUi.PrintWarning(message);
                            updateProgress.Resume();
                        }

                        if (writer.HasFileAtPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing during write");
                            if (writer.DeleteFileByPath(dbPath))
                            {
                                WriteProjectRootOnce();
                                RequireTypeScriptAugmentationRefresh();
                                deleteTxn.Commit();
                                removed++;
                                ftsMutated = true;
                                mutualRecursionRefreshNeeded = true;
                            }
                        }
                        else
                        {
                            skipped++;
                        }
                        continue;
                    }

                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();
                    RecordUpdateFileFailure(relPath, currentUpdatePhase, ex);
                }
            }
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(updateHeartbeat);
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));

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
