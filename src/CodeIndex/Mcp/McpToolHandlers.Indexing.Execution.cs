using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{

    private async Task<JsonNode> ExecuteIndexCoreAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken)
    {
        if (!TryReadMcpIndexRequestOptions(id, args, out var indexOptions, out var indexOptionsError))
            return indexOptionsError!;

        var rebuild = indexOptions!.Rebuild;
        var dryRun = indexOptions.DryRun;
        var memoryTrace = indexOptions.MemoryTrace;
        var maxFileBytes = indexOptions.MaxFileBytes;
        var maxSymbolsPerFile = indexOptions.MaxSymbolsPerFile;
        var maxReferencesPerFile = indexOptions.MaxReferencesPerFile;
        var symlinkPolicy = indexOptions.SymlinkPolicy;
        var symbolKindFilter = indexOptions.SymbolKindFilter;
        var unsupportedModes = indexOptions.UnsupportedModes;
        var optionsPayload = indexOptions.OptionsPayload;
        var requestedProjectPath = Path.GetFullPath(indexOptions.Path);
        var runStartedAtUtc = GetUtcNow();
        var runStopwatch = Stopwatch.StartNew();
        var memorySamples = memoryTrace
            ? new JsonArray { CaptureMcpIndexMemorySample("start", runStopwatch) }
            : null;

        var authorizationResult = await CaptureIndexAuthorizationAsync(id, requestedProjectPath).ConfigureAwait(false);
        if (authorizationResult.ErrorResponse != null)
            return authorizationResult.ErrorResponse;
        var cwd = authorizationResult.CurrentWorkingDirectory;
        bool IsPathAuthorized(string path)
            => IsIndexPathAuthorized(cwd, path);
        using var authorizedRoot = authorizationResult.Authorization!;
        using var authorizedExtractorConfiguration = ExtractorPluginRegistry.BeginAuthorizedConfigurationScope();
        if (_currentIndexAuditContext.Value is { } auditContext)
            auditContext.CheckedRootIdentity = authorizedRoot.CheckedRootIdentity;
        McpIndexAuthorizationCompletedForTesting?.Invoke();
        authorizedRoot.EnsureAuthorizedEntry(authorizedRoot.CanonicalPath);
        var projectPath = authorizedRoot.CanonicalPath;

        var unsupportedModesJson = BuildMcpIndexUnsupportedModesJson(unsupportedModes);
        if (dryRun)
            return BuildIndexDryRunResult(
                id,
                indexOptions,
                projectPath,
                cwd,
                authorizedRoot,
                unsupportedModesJson,
                runStartedAtUtc,
                runStopwatch,
                memorySamples);

        if (HasBlockingMcpIndexUnsupportedMode(unsupportedModes))
            return CreateUnsupportedIndexModeResponse(
                id,
                indexOptions,
                unsupportedModesJson,
                authorizedRoot.CheckedRootIdentity);

        if (!McpIndexRunLock.TryAcquire(_dbPath, out var indexLock, out var lockError))
            return CreateToolErrorResponse(id, lockError!);
        using var acquiredIndexLock = indexLock;

        // Direct in-process calls keep the warm per-session DbContext (#1494). Transport
        // requests can outlive their cancellation response while durable cleanup unwinds, so
        // give those isolated actions a request-owned connection that server disposal cannot
        // close underneath them. InitializeSchema below remains the write-path migration boundary.
        // direct 呼び出しはセッション共有 DbContext を再利用する（#1494）。transport request は
        // cancellation 応答後も durable cleanup を unwind し得るため、server Dispose に途中で
        // close されない request-owned connection を isolated action に持たせる。
        var openIntent = rebuild ? DbOpenIntent.Repair : DbOpenIntent.WriteIndex;
        using var isolatedRequestDb = _isolateDbForCurrentRequest.Value
            ? new DbContext(openIntent, _dbPath, _currentRequestToken.Value)
            : null;
        var db = isolatedRequestDb ?? GetOrOpenSharedDb(openIntent);
        var indexSnapshot = CaptureIndexDatabaseSnapshot(db);
        var requestToken = _currentRequestToken.Value;
        using var suppressDisposeMaintenanceOnCancellation = requestToken.CanBeCanceled
            ? requestToken.UnsafeRegister(
                static state => ((DbContext)state!).SuppressPlannerStatisticsMaintenanceOnClose(),
                db)
            : default;
        requestToken.ThrowIfCancellationRequested();
        // Capture git HEAD so subsequent queries can detect a worktree branch / HEAD switch
        // (`git switch other-branch` inside the worktree) without a `--check` workspace scan.
        // Like the CLI full-scan path, the value is only persisted at the end of a successful
        // run (errors == 0) so a crashed / partial index keeps the previous HEAD and surfaces
        // staleness until the next clean refresh. Issues #1508 and #1512.
        // worktree 内の HEAD 切替検出のため HEAD を捕捉。CLI full-scan と同じく成功時のみ
        // 書き込み、partial 失敗は旧 HEAD を残して次回 full scan で更新する。
        var currentHeadCommit = GitHelper.TryGetHeadCommit(projectPath, requestToken);

        db.InitializeSchema();

        var writer = new DbWriter(db);
        var repositoryRoot = GitHelper.TryGetRepositoryRoot(projectPath, requestToken);
        var ignoreRuleRoot = repositoryRoot != null && IsPathAuthorized(repositoryRoot)
            ? repositoryRoot
            : projectPath;
        var indexer = new FileIndexer(
            projectPath,
            GitHelper.ResolveIgnoreCase(projectPath, requestToken),
            ignoreRuleRoot,
            maxFileBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: symlinkPolicy,
            generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment(),
            pathAccessValidator: authorizedRoot.EnsureAuthorizedEntry,
            openReadForIndexContent: authorizedRoot.OpenAuthorizedRead,
            enumerateFileSystemEntries: authorizedRoot.EnumerateAuthorizedFileSystemEntries,
            bindConfigurationReadsToFileSystemIdentity: true,
            internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(_dbPath));
        using var postExtractionHooks = new IndexCommandRunner.LazyDisposable<PostExtractionHookRunner>(() =>
        {
            McpIndexPostExtractionHookDiscoveryForTesting?.Invoke();
            return PostExtractionHookRunner.DiscoverDefault(maxFileBytes);
        });
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = indexSnapshot.CSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpMetadataTargetsNeedRefresh = indexSnapshot.MetadataTargetCSharp != currentMetadataTargetVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = indexSnapshot.SqlGraphContractVersion == currentSqlGraphContractVersion;
        var currentHdlGraphContractVersion = DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hdlGraphContractMatchesCurrent = indexSnapshot.HdlGraphContractVersion == currentHdlGraphContractVersion;
        var symbolKindFilterMatchesPrior = string.Equals(
            indexSnapshot.SymbolKindFilterSignature,
            symbolKindFilter.Signature,
            StringComparison.Ordinal);
        var priorFilterRetainedCSharpContractMembers =
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(
                indexSnapshot.SymbolKindFilterSignature);
        var symbolKindFilterMetaMarkedIncomplete = symbolKindFilterMatchesPrior;
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(indexSnapshot.IndexedProjectRoot)
            ? null
            : Path.GetFullPath(indexSnapshot.IndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectPath);
        var csharpIndexedProjectRootCompatible = normalizedPriorIndexedProjectRoot == null
            || projectRootWritten;
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = rebuild
            || !projectRootWritten
            || !typeScriptAugmentationVersionMatchesCurrent;
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var ftsMutated = false;
        var startedWithNoIndexedFilesBeforeRebuild = !writer.HasAnyIndexedFiles();
        var startedWithNoIndexedFiles = rebuild || startedWithNoIndexedFilesBeforeRebuild;
        if (rebuild || startedWithNoIndexedFiles)
            indexSnapshot.CSharpStaticInterfaceSourceEvidence = null;
        var requiresConservativeCSharpSourceRefresh = !rebuild
            && !startedWithNoIndexedFiles
            && indexSnapshot.CSharpStaticInterfaceSourceEvidence != false;
        // Delay source-evidence invalidation until scan, workspace preflight, and the final
        // uncached C# stat check finish. This avoids a committed null/true round trip for a
        // strict positive no-op while dirty runs still publish safe evidence before row writes.
        // source evidence更新は最終C# stat確認後まで遅延し、true no-opのnull往復を避ける。
        var useScopedTypeScriptAugmentationRefresh = !rebuild
            && !startedWithNoIndexedFiles
            && projectRootWritten;
        using var typeScriptAugmentationDirtyNames = !rebuild
            && typeScriptAugmentationVersionMatchesCurrent
                ? writer.BeginTypeScriptAugmentationDirtyNameTracking(useScopedTypeScriptAugmentationRefresh)
                : null;

        void InsertIssuesForIndexedFile(long fileId, IReadOnlyList<FileIssue> issues)
        {
            if (startedWithNoIndexedFiles)
                writer.InsertIssuesForNewFile(fileId, issues);
            else
                writer.InsertIssues(fileId, issues);
        }

        static bool PathsEqual(string? left, string? right)
        {
            if (left == null || right == null)
                return false;

            return CodeIndex.Cli.PathCasing.PathsEqual(left, right);
        }

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectPath);
                projectRootWritten = true;
            }
        }

        void MarkSymbolKindFilterMetaIncompleteOnce()
        {
            if (symbolKindFilterMetaMarkedIncomplete)
                return;
            writer.SetMeta(IndexCommandRunner.SymbolKindFilterMetaKey, null);
            symbolKindFilterMetaMarkedIncomplete = true;
        }

        void RequireTypeScriptAugmentationRefresh()
        {
            if (!typeScriptAugmentationReadyCleared)
            {
                writer.ClearTypeScriptAugmentationReady();
                typeScriptAugmentationReadyCleared = true;
            }

            typeScriptAugmentationNeedsRefresh = true;
        }

        static (long BytesRead, long SkippedFileCount) SumReadableFileBytes(
            IEnumerable<string> paths,
            string projectRoot,
            List<string> diagnostics,
            List<McpIndexDiagnostic> structuredDiagnostics,
            Action<string> validatePath,
            IReadOnlyDictionary<string, long>? knownFileSizes = null)
        {
            long total = 0;
            long skipped = 0;
            var totalComplete = true;
            foreach (var filePath in paths)
            {
                if (knownFileSizes != null && knownFileSizes.TryGetValue(filePath, out var knownSize))
                {
                    if (totalComplete
                        && !FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                            total,
                            previousBytes: null,
                            knownSize,
                            out total))
                    {
                        totalComplete = false;
                        skipped++;
                    }
                    continue;
                }

                try
                {
                    validatePath(filePath);
                    var info = new FileInfo(filePath);
                    if (info.Exists
                        && totalComplete
                        && !FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                            total,
                            previousBytes: null,
                            info.Length,
                            out total))
                    {
                        totalComplete = false;
                        skipped++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    skipped++;
                    diagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic(
                        "file_size_bytes_skipped",
                        FormatDiagnosticPath(projectRoot, filePath),
                        ex));
                    structuredDiagnostics.Add(BuildMcpIndexExceptionDiagnostic(
                        "file_size_bytes_skipped",
                        "skipped_file_sizing",
                        "measure_file_size",
                        projectRoot,
                        filePath,
                        ex));
                }
            }

            return (total, skipped);
        }

        static string FormatDiagnosticPath(string projectRoot, string path)
        {
            try
            {
                var relative = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
                return relative == "."
                    || relative.StartsWith("../", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                    ? path
                    : relative;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return path;
            }
        }

        var indexRunDiagnostics = new List<string>();
        var mcpIndexDiagnostics = new List<McpIndexDiagnostic>();
        var referenceIdentityContractMatchedBeforeMutation = writer.ReferenceIdentityContractMatchesCurrent();
        var useFullRunBatchMarker = rebuild || startedWithNoIndexedFiles;

        // Plan stale-file cleanup before FTS trigger policy is selected. The exact IDs are
        // applied only after a bulk guard, when selected, has suspended synchronization.
        // FTS trigger policy 選択前に stale file cleanup を plan し、実削除は bulk guard
        // 選択時に同期を停止した後だけ行う。
        var initialStaleFilePurgePlan = PlanInitialMcpIndexPurge(
            writer,
            projectPath,
            startedWithNoIndexedFiles,
            requestToken);

        // Load current reference-language support before the deferred mutation phase.
        // deferred mutation phase の前に現在の reference-language support を読み込む。
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectPath);
        var freshFoldProducerSnapshot =
            ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectPath);
        var authoritativeFreshFoldRowsClaim = startedWithNoIndexedFilesBeforeRebuild
            && !rebuild
            && freshFoldProducerSnapshot.UsesOnlyBuiltInProducers
            ? writer.TryClaimAuthoritativeFreshFoldRows(requestToken)
            : null;
        var csharpPrepassSymbolArtifacts = CSharpPrepassSymbolArtifactCache
            .CreateForFreshBuiltInExtraction(
                startedWithNoIndexedFilesBeforeRebuild && !rebuild);
        // Scan and index / スキャン・インデックス
        var scanWithDirectorySnapshots = indexer.ScanFilesDetailedWithDirectoryListingSnapshotsAndIndexingTargets(
            cancellationToken: requestToken);
        var scanResult = scanWithDirectorySnapshots.ScanResult;
        var scanInputSnapshot = scanWithDirectorySnapshots.InputSnapshot;
        var indexingTargets = scanWithDirectorySnapshots.IndexingTargets
            ?? throw new InvalidOperationException(
                "MCP index discovery did not capture indexing targets.");
        var currentHotspotFamilyMarkerFingerprints =
            scanResult.ProjectMarkerFingerprints;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            indexSnapshot.HotspotFamilyVersions,
            indexSnapshot.HotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var scanHadErrors = scanResult.HadErrors;
        var initialDeferCSharpMutations = !startedWithNoIndexedFiles
            && scanHadErrors
            && indexSnapshot.CSharpStaticInterfaceSourceEvidence != false;
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
        var files = scanResult.Files;
        var targets = BuildMcpIndexTargetSet(indexer, scanResult, indexingTargets);
        var fileTargets = targets.All;
        var csharpPrepassTargets = targets.CSharp;
        var languageCounts = scanResult.LanguageCounts;
        var hasSqlTargets = languageCounts.ContainsKey("sql");
        var hasTypeScriptTargets = languageCounts.ContainsKey("typescript");
        var readableBytes = new McpIndexReadableByteTracker(files.Count);
        var discoveryPlan = BuildMcpIndexDiscoveryPlan(
            writer,
            projectPath,
            scanResult,
            targets,
            initialStaleFilePurgePlan,
            startedWithNoIndexedFiles,
            initialDeferCSharpMutations,
            indexSnapshot.CSharpStaticInterfaceSourceEvidence,
            priorFilterRetainedCSharpContractMembers,
            requestToken);
        var staleFilePurgePlan = discoveryPlan.PurgePlan;
        var retainedPathsForReuse = discoveryPlan.RetainedPathsForReuse;
        var hadCSharpStaticInterfaceContractsBeforePurge =
            discoveryPlan.HadCSharpStaticInterfaceContractsBeforePurge;
        var purged = staleFilePurgePlan.Count;
        if (purged > 0)
            csharpMetadataTargetsNeedRefresh = true;
        await EmitProgressNotificationAsync(progressToken, 0, files.Count, "Index scan complete; indexing files.").ConfigureAwait(false);
        var csharpPositiveNoOpPolicyCandidate = indexSnapshot.CSharpStaticInterfaceSourceEvidence is not null
            && indexSnapshot.IndexComplete
            && (indexSnapshot.Readiness & DbContext.GraphReadyFlag) != 0
            && !scanHadErrors
            && !hadCSharpStaticInterfaceContractsBeforePurge
            && !indexSnapshot.SymbolsOnlyGraphOmitted
            && symbolKindFilterMatchesPrior
            && csharpSymbolNameContractMatchesCurrent
            && csharpIndexedProjectRootCompatible
            && AllowReuseWithCurrentHotspotFamilyTrust(
                "csharp",
                hotspotFamilyTrustMatchesCurrent)
            && csharpPrepassTargets.Count > 0;
        var hasCSharpLanguageTransitions = false;
        void ObservePersistedCSharpPath(string indexPath)
        {
            if (!hasCSharpLanguageTransitions
                && discoveryPlan.ScanAuthority.IsExistingCSharpSymbolPathNowNonCSharp(indexPath))
                hasCSharpLanguageTransitions = true;
        }

        var reusableIndexedFileStats = !startedWithNoIndexedFiles
            ? writer.LoadReusableIndexedFileStats(
                maxSymbolsPerFile,
                maxReferencesPerFile,
                _currentRequestToken.Value,
                files.Count,
                retainedPathsForReuse,
                staleFilePurgePlan.FileIds,
                csharpPositiveNoOpPolicyCandidate
                    ? ObservePersistedCSharpPath
                    : null,
                maxFileSizeBytes:
                    maxFileBytes ?? FileIndexer.DefaultMaxFileSizeBytes)
            : null;
        var priorPositiveCSharpSourceNoOpCandidate = false;
        bool IsGeneratedExtractionSuppressed(CSharpStaticInterfacePrepass.FileTarget target)
            => target.GeneratedExtractionSuppressed == true;

        var failures = new List<IndexFileFailure>();
        if (scanHadErrors)
        {
            foreach (var error in scanResult.Errors)
            {
                if (error.IsFatal)
                    failures.Add(BuildScanFailure(error));
            }
        }
        var csharpFailures = new McpIndexCSharpFailureCollector(projectPath, failures);
        var csharpState = new McpIndexCSharpWorkspaceState(
            new McpIndexCSharpWorkspaceContext(
                projectPath,
                authorizedRoot,
                csharpPrepassTargets,
                reusableIndexedFileStats,
                readableBytes,
                csharpFailures,
                requestToken))
        {
            DeferMutations = initialDeferCSharpMutations,
            PrepassArtifacts = csharpPrepassSymbolArtifacts,
        };

        bool CanReuseCSharpPrepassTargetWithoutRead(CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (rebuild
                || startedWithNoIndexedFiles
                || !csharpIndexedProjectRootCompatible
                || (requiresConservativeCSharpSourceRefresh
                    && !priorPositiveCSharpSourceNoOpCandidate)
                || indexSnapshot.SymbolsOnlyGraphOmitted
                || !symbolKindFilterMatchesPrior
                || !csharpSymbolNameContractMatchesCurrent)
                return false;
            if (target.Language != "csharp")
                return false;

            authorizedRoot.EnsureAuthorizedEntry(target.FilePath);

            var existingFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                reusableIndexedFileStats!,
                target.FilePath,
                target.IndexPath,
                target.Language,
                IsGeneratedExtractionSuppressed(target));
            if (existingFile == null)
            {
                csharpState.AllPrepassTargetsReusable = false;
                csharpState.EnsurePrepassStatReuse()[target.IndexPath] = null;
                return false;
            }

            csharpState.EnsurePrepassStatReuse()[target.IndexPath] = existingFile.Value;
            return true;
        }

        priorPositiveCSharpSourceNoOpCandidate = csharpPositiveNoOpPolicyCandidate
            && !hasCSharpLanguageTransitions;
        if (priorPositiveCSharpSourceNoOpCandidate)
        {
            csharpState.AllPrepassTargetsReusable = true;
            var prepassStatReuse = csharpState.EnsurePrepassStatReuse();
            foreach (var target in csharpPrepassTargets)
            {
                requestToken.ThrowIfCancellationRequested();
                authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
                var existingFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    reusableIndexedFileStats!,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    target.GeneratedExtractionSuppressed);
                prepassStatReuse[target.IndexPath] = existingFile;
                csharpState.AllPrepassTargetsReusable &= existingFile != null;
            }
        }

        if (csharpPrepassTargets.Count == 0 || csharpState.DeferMutations)
        {
            csharpState.Workspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else if (priorPositiveCSharpSourceNoOpCandidate
                 && csharpState.AllPrepassTargetsReusable)
        {
            // Existing graph rows are already complete and every C# stat matched. Avoid
            // loading persisted contract symbols or constructing a lookup that no file will use.
            // 全C# stat一致のpositive no-opではDB symbol/lookup loadを完全に省略する。
            csharpState.Workspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            csharpState.Workspace = csharpState.BuildStableWorkspace(() =>
                CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    writer,
                    indexer,
                    csharpPrepassTargets,
                    includeExistingSymbols: csharpIndexedProjectRootCompatible && !rebuild && !startedWithNoIndexedFiles,
                    canReuseExistingSymbolsWithoutRead:
                        priorPositiveCSharpSourceNoOpCandidate
                            ? null
                            : CanReuseCSharpPrepassTargetWithoutRead,
                    isGeneratedCodeExtractionSuppressed: IsGeneratedExtractionSuppressed,
                    parallelism: 1,
                    excludedExistingFileIds: staleFilePurgePlan.FileIds,
                    isExistingSymbolPathExcluded:
                        discoveryPlan.ScanAuthority.IsExistingCSharpSymbolPathNowNonCSharp,
                    patternConfigsAlreadyLoaded: true,
                    cancellationToken: requestToken,
                    symbolArtifactCache: csharpState.PrepassArtifacts));
            csharpState.ForceFullRefreshFromInvalidatedNoOp =
                indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
                || csharpState.Workspace.HasStaticInterfaceContracts
                || csharpState.Workspace.RequiresMemberReadReferenceRefresh;
        }
        if (!csharpState.Workspace.SourceContractEvidenceComplete)
        {
            csharpState.DeferForIncompletePrepass();
            staleFilePurgePlan = FilePurgePlan.Empty;
            purged = 0;
            hadCSharpStaticInterfaceContractsBeforePurge = false;
        }
        csharpState.PreservePriorPositiveSourceNoOp = priorPositiveCSharpSourceNoOpCandidate
            && csharpState.AllPrepassTargetsReusable
            && !csharpState.DeferMutations;
        csharpState.SourceEvidenceForStamp = csharpState.PreservePriorPositiveSourceNoOp
            ? indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
            : csharpState.Workspace.HasSourceStaticInterfaceContracts;
        csharpState.SourceEvidenceComplete = csharpState.PreservePriorPositiveSourceNoOp
            || csharpState.Workspace.SourceContractEvidenceComplete;
        if (csharpState.PreservePriorPositiveSourceNoOp)
            csharpState.Workspace = csharpState.Workspace with { HasStaticInterfaceContracts = false };
        if (!csharpState.DeferMutations
            && !csharpState.PreservePriorPositiveSourceNoOp
            && (csharpState.ForceFullRefreshFromInvalidatedNoOp
                || requiresConservativeCSharpSourceRefresh
                || !csharpState.SourceEvidenceComplete
                || (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)))
        {
            csharpState.Workspace = csharpState.Workspace with { HasStaticInterfaceContracts = true };
        }
        int processed = 0, skipped = 0, errors = failures.Count;

        if (!csharpState.SourceEvidenceComplete)
        {
            csharpState.RecordIncompletePrepass();
            errors = failures.Count;
        }
        HashSet<string>? reusedHotspotFamilyLanguages = null;
        var indexedSymbolExtractorLanguages = new HashSet<string>(languageCounts.Count, StringComparer.Ordinal);
        var symbolsDroppedByKindFilter = 0;
        var mutualRecursionRefreshNeeded = !referenceIdentityContractMatchedBeforeMutation
            || purged > 0;
        var freshCountFiles = 0L;
        var freshCountChunks = 0L;
        var freshCountSymbols = 0L;
        var freshCountReferences = 0L;
        IndexedFileStatReuseResult? GetStatMatchedFile(
            in FileIndexer.IndexingFileTarget target)
        {
            var allowStatReuse = !rebuild
                && !startedWithNoIndexedFiles
                && !indexSnapshot.SymbolsOnlyGraphOmitted
                && symbolKindFilterMatchesPrior
                && (target.Language != "csharp" || csharpIndexedProjectRootCompatible)
                && (target.Language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                && (target.Language != "csharp" || !csharpState.Workspace.HasStaticInterfaceContracts)
                && (target.Language != "sql" || sqlGraphContractMatchesCurrent)
                && (target.Language is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(target.Language, hotspotFamilyTrustMatchesCurrent);
            if (!allowStatReuse)
                return null;

            return csharpState.GetStatMatchedFile(in target);
        }

        var ftsPreflight = BuildMcpIndexFtsPreflight(
            fileTargets,
            staleFilePurgePlan,
            rebuild,
            startedWithNoIndexedFiles,
            scanHadErrors,
            reusableIndexedFileStats,
            authorizedRoot,
            GetStatMatchedFile,
            readableBytes,
            requestToken);
        var useFtsBulkLoad = ftsPreflight.UseBulkLoad;

        var reuseFinalCSharpStatAtActualSkip = false;
        if (csharpState.PreservePriorPositiveSourceNoOp
            && ftsPreflight.EveryTargetMatched)
        {
            // A pure no-op performs its second and final C# stat pass at the readiness
            // boundary. Reuse the candidate result in the per-file skip loop meanwhile.
            // 純粋no-opの2巡目C# statはreadiness直前へ統合し、skip loopではcandidateを再利用する。
            reuseFinalCSharpStatAtActualSkip = true;
        }

        if (csharpState.PreservePriorPositiveSourceNoOp
            && !ftsPreflight.EveryTargetMatched)
        {
            // Re-stat directly after the potentially long whole-workspace dirty-byte pass.
            // This is the last read-only boundary before readiness/purge/file mutations.
            // 長いdirty-byte pass直後、最初のwrite直前にC# statをcacheなしで再確認する。
            McpIndexCSharpFinalStatRevalidationForTesting?.Invoke();
            var invalidatedCSharpTargetIndexes = new List<int>();
            for (var targetIndex = 0; targetIndex < fileTargets.Length; targetIndex++)
            {
                var target = fileTargets[targetIndex];
                if (target.Language != "csharp")
                    continue;

                requestToken.ThrowIfCancellationRequested();
                authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
                var revalidated = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    reusableIndexedFileStats!,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    target.GeneratedExtractionSuppressed);
                if (revalidated == null)
                    invalidatedCSharpTargetIndexes.Add(targetIndex);
            }

            if (invalidatedCSharpTargetIndexes.Count > 0)
            {
                csharpState.Workspace = csharpState.BuildStableWorkspace(() =>
                    CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        includeExistingSymbols: csharpIndexedProjectRootCompatible && !rebuild && !startedWithNoIndexedFiles,
                        canReuseExistingSymbolsWithoutRead: null,
                        isGeneratedCodeExtractionSuppressed: IsGeneratedExtractionSuppressed,
                        parallelism: 1,
                        excludedExistingFileIds: staleFilePurgePlan.FileIds,
                        isExistingSymbolPathExcluded:
                            discoveryPlan.ScanAuthority.IsExistingCSharpSymbolPathNowNonCSharp,
                        patternConfigsAlreadyLoaded: true,
                        cancellationToken: requestToken));
                csharpState.PreservePriorPositiveSourceNoOp = false;
                if (!csharpState.Workspace.SourceContractEvidenceComplete)
                {
                    csharpState.IncompletePrepassPaths = csharpState.Workspace.IncompleteSourcePaths ?? [];
                    csharpState.DeferMutations = true;
                    staleFilePurgePlan = FilePurgePlan.Empty;
                    purged = 0;
                    hadCSharpStaticInterfaceContractsBeforePurge = false;
                    csharpState.SourceEvidenceForStamp = false;
                    csharpState.SourceEvidenceComplete = false;
                    csharpState.Workspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: csharpState.IncompletePrepassPaths);
                    csharpState.RecordIncompletePrepass();
                    useFtsBulkLoad = false;
                }
                else
                {
                    var requiresFullCSharpRefresh =
                        indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
                        || csharpState.Workspace.HasStaticInterfaceContracts;
                    csharpState.ForceFullRefreshFromInvalidatedNoOp = requiresFullCSharpRefresh;
                    csharpState.SourceEvidenceForStamp = csharpState.Workspace.HasSourceStaticInterfaceContracts;
                    csharpState.SourceEvidenceComplete = true;
                    useFtsBulkLoad = false;
                    if (requiresFullCSharpRefresh)
                    {
                        csharpState.Workspace = csharpState.Workspace with { HasStaticInterfaceContracts = true };
                        invalidatedCSharpTargetIndexes.Clear();
                        for (var targetIndex = 0; targetIndex < fileTargets.Length; targetIndex++)
                        {
                            if (fileTargets[targetIndex].Language == "csharp")
                                invalidatedCSharpTargetIndexes.Add(targetIndex);
                        }
                    }

                    foreach (var targetIndex in invalidatedCSharpTargetIndexes)
                        ftsPreflight.InvalidateTarget(targetIndex);
                }
            }
        }

        var beforeWriteFailures = new McpIndexBeforeWriteFailures(
            failures,
            mcpIndexDiagnostics,
            symbolsDroppedByKindFilter);
        var beforeWrite = await ValidateMcpIndexBeforeWriteAsync(
            new McpIndexBeforeWriteContext(
                id,
                new McpIndexBeforeWriteRequest(
                    projectPath,
                    authorizedRoot.CheckedRootIdentity,
                    rebuild,
                    maxFileBytes,
                    optionsPayload,
                    unsupportedModesJson,
                    symbolKindFilter),
                new McpIndexBeforeWriteRun(
                    progressToken,
                    runStopwatch,
                    runStartedAtUtc,
                    memorySamples),
                indexer,
                scanInputSnapshot,
                scanResult,
                new McpIndexBeforeWriteReadiness(
                    indexSnapshot,
                    sqlGraphContractMatchesCurrent,
                    csharpSymbolNameContractMatchesCurrent,
                    currentMetadataTargetVersion),
                beforeWriteFailures),
            writer,
            csharpState,
            staleFilePurgePlan,
            purged,
            hadCSharpStaticInterfaceContractsBeforePurge,
            useFtsBulkLoad,
            requestToken).ConfigureAwait(false);
        if (beforeWrite.Response != null)
            return beforeWrite.Response;
        staleFilePurgePlan = beforeWrite.PurgePlan;
        purged = beforeWrite.Purged;
        hadCSharpStaticInterfaceContractsBeforePurge =
            beforeWrite.HadCSharpStaticInterfaceContractsBeforePurge;
        useFtsBulkLoad = beforeWrite.UseFtsBulkLoad;
        errors = failures.Count;

        // Rebuild destruction and interrupted-FTS recovery are deliberately delayed until
        // the same pre-write scan barrier as ordinary MCP indexing. A drifted scan therefore
        // leaves prior indexed rows and trust metadata untouched even for rebuild requests.
        // rebuild破棄とFTS recoveryもwrite前scan barrier通過後まで遅延する。
        requestToken.ThrowIfCancellationRequested();
        using var mmapBulkWrite = SqliteMmapBulkWriteGuard.Start(writer, useFtsBulkLoad);
        if (rebuild)
        {
            db.RepairIncompleteBatchReadiness();
            db.ClearReadyFlags();
            writer.ClearHotspotFamilyReady();
            writer.ClearMetadataTargetReady();
            db.DropAll();
            db.InitializeSchema();
            writer = new DbWriter(db);
        }
        writer.SetMeta(
            DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
            false.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var referenceGraphRefresh = writer.BeginReferenceGraphRefreshScope(
            rebuild || !writer.HasAnyIndexedFiles());
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh(
            deferSecondaryIndexes: useFtsBulkLoad);
        writer.RecoverInterruptedFtsBulkLoadIfNeeded(requestToken);
        if (!csharpState.PreservePriorPositiveSourceNoOp)
        {
            writer.SetCSharpStaticInterfaceSourceEvidence(
                csharpState.SourceEvidenceComplete && csharpState.SourceEvidenceForStamp ? true : null);
        }
        writer.ClearReadyFlags();
        writer.ClearReferenceIdentityContractReady();
        writer.ClearHotspotFamilyReady();
        writer.ClearSqlGraphContractReady();
        writer.ClearMetadataTargetReady();
        if (hadCSharpStaticInterfaceContractsBeforePurge
            || csharpState.Workspace.HasStaticInterfaceContracts)
            writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
        if (useFullRunBatchMarker)
            writer.MarkBatchInProgress();

        using var ftsBulkLoad = FtsBulkLoadTriggerGuard.Start(writer, useFtsBulkLoad, () => ftsMutated);
        using var referenceSecondaryIndexBulkLoad =
            ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                writer,
                enabled: useFtsBulkLoad,
                requestToken,
                refreshPlannerStatisticsBeforeCandidatePopulation:
                    startedWithNoIndexedFilesBeforeRebuild && !rebuild);

        if (staleFilePurgePlan.Count > 0)
        {
            McpIndexStaleFilePurgeForTesting?.Invoke(useFtsBulkLoad);
            purged = writer.ApplyFilePurgePlan(
                staleFilePurgePlan,
                RequireTypeScriptAugmentationRefresh,
                requestToken);
            if (purged > 0)
            {
                ftsMutated = true;
                WriteProjectRootOnce();
                if (McpIndexStaleFilePurgedForTesting is { } staleFilePurgedForTesting)
                    await staleFilePurgedForTesting(requestToken).ConfigureAwait(false);
            }
        }

        McpIndexReferencePurgeForTesting?.Invoke();
        var purgedRefs = csharpState.DeferMutations || startedWithNoIndexedFiles
            ? 0
            : writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages(projectPath));
        if (purgedRefs > 0)
            mutualRecursionRefreshNeeded = true;

        var fileLoopContext = new McpIndexFileLoopContext
        {
            Writer = writer,
            Indexer = indexer,
            AuthorizedRoot = authorizedRoot,
            Targets = fileTargets,
            ProjectPath = projectPath,
            TotalFileCount = files.Count,
            ProgressToken = progressToken,
            CancellationToken = requestToken,
            MaxSymbolsPerFile = maxSymbolsPerFile,
            MaxReferencesPerFile = maxReferencesPerFile,
            SymlinkPolicy = symlinkPolicy,
            Rebuild = rebuild,
            StartedWithNoIndexedFiles = startedWithNoIndexedFiles,
            UseFullRunBatchMarker = useFullRunBatchMarker,
            ReuseFinalCSharpStatAtActualSkip = reuseFinalCSharpStatAtActualSkip,
            SymbolKindFilterMatchesPrior = symbolKindFilterMatchesPrior,
            CSharpIndexedProjectRootCompatible = csharpIndexedProjectRootCompatible,
            CSharpSymbolNameContractMatchesCurrent = csharpSymbolNameContractMatchesCurrent,
            SqlGraphContractMatchesCurrent = sqlGraphContractMatchesCurrent,
            HdlGraphContractMatchesCurrent = hdlGraphContractMatchesCurrent,
            HotspotFamilyTrustMatchesCurrent = hotspotFamilyTrustMatchesCurrent,
            ReusableIndexedFileStats = reusableIndexedFileStats,
            StatMatchedFiles = ftsPreflight.StatMatches,
            StatPreflightCompleted = ftsPreflight.Completed,
            SymbolKindFilter = symbolKindFilter,
            PostExtractionHooks = postExtractionHooks,
            CSharpWorkspace = csharpState,
            GetStatMatchedFile = GetStatMatchedFile,
            RememberReadableFileSize = readableBytes.Remember,
            InsertIssuesForIndexedFile = InsertIssuesForIndexedFile,
            WriteProjectRootOnce = WriteProjectRootOnce,
            MarkSymbolKindFilterMetaIncompleteOnce = MarkSymbolKindFilterMetaIncompleteOnce,
            RequireTypeScriptAugmentationRefresh = RequireTypeScriptAugmentationRefresh,
            Failures = failures,
        };
        var fileLoopSession = new McpIndexFileLoopSession
        {
            Processed = processed,
            Skipped = skipped,
            FtsMutated = ftsMutated,
            MutualRecursionRefreshNeeded = mutualRecursionRefreshNeeded,
            CSharpMetadataTargetsNeedRefresh = csharpMetadataTargetsNeedRefresh,
            SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
            ReusedHotspotFamilyLanguages = reusedHotspotFamilyLanguages,
            IndexedSymbolExtractorLanguages = indexedSymbolExtractorLanguages,
            FreshCountFiles = freshCountFiles,
            FreshCountChunks = freshCountChunks,
            FreshCountSymbols = freshCountSymbols,
            FreshCountReferences = freshCountReferences,
        };
        await RunMcpIndexFileLoopAsync(fileLoopContext, fileLoopSession)
            .ConfigureAwait(false);
        processed = fileLoopSession.Processed;
        skipped = fileLoopSession.Skipped;
        // The typed C# workspace state appends drift failures directly to the shared
        // failure list. Per-file failures do the same through the session, so the list
        // remains the authoritative error count at the join.
        // typed C# workspace state と file loop の双方が共有 failure list へ追記するため、
        // join 時の error 数は同listから復元する。
        errors = failures.Count;
        ftsMutated = fileLoopSession.FtsMutated;
        mutualRecursionRefreshNeeded = fileLoopSession.MutualRecursionRefreshNeeded;
        csharpMetadataTargetsNeedRefresh = fileLoopSession.CSharpMetadataTargetsNeedRefresh;
        symbolsDroppedByKindFilter = fileLoopSession.SymbolsDroppedByKindFilter;
        reusedHotspotFamilyLanguages = fileLoopSession.ReusedHotspotFamilyLanguages;
        freshCountFiles = fileLoopSession.FreshCountFiles;
        freshCountChunks = fileLoopSession.FreshCountChunks;
        freshCountSymbols = fileLoopSession.FreshCountSymbols;
        freshCountReferences = fileLoopSession.FreshCountReferences;

        csharpState.ClearArtifacts();

        var referenceIdentityReadyForMutualRecursionRefresh =
            !csharpState.DeferMutations && mutualRecursionRefreshNeeded
                ? writer.CSharpFamilyTrustAllowsReferenceIdentityReady(
                    startedWithNoIndexedFiles
                    && !scanHadErrors
                    && errors == 0
                        ? csharpPrepassTargets.Count > 0
                        : null)
                : (bool?)null;
        var canStampTypeScriptAugmentationReadyWithoutRebuild =
            (startedWithNoIndexedFiles || rebuild)
            && !scanHadErrors
            && !hasTypeScriptTargets;
        var willRebuildTypeScriptAugmentation =
            !csharpState.DeferMutations
            && TypeScriptAugmentationRefreshPolicy.ShouldRebuildReferences(
                symbolsOnly: false,
                canFinalize: !scanHadErrors && errors == 0,
                typeScriptAugmentationNeedsRefresh,
                typeScriptAugmentationDirtyNames?.RequiresRefresh == true,
                canStampReadyWithoutRebuild:
                    canStampTypeScriptAugmentationReadyWithoutRebuild);
        var deferMutualRecursionRefreshToTypeScriptAugmentation =
            mutualRecursionRefreshNeeded && willRebuildTypeScriptAugmentation;
        if (!csharpState.DeferMutations
            && mutualRecursionRefreshNeeded
            && !deferMutualRecursionRefreshToTypeScriptAugmentation)
        {
            requestToken.ThrowIfCancellationRequested();
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing reference graph.").ConfigureAwait(false);
            writer.RefreshMutualRecursionFlags(
                requestToken,
                stampReferenceIdentityContractReady:
                    referenceIdentityReadyForMutualRecursionRefresh,
                referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
        }
        else if (referenceSecondaryIndexBulkLoad != null)
        {
            await EmitProgressNotificationAsync(
                progressToken,
                processed,
                files.Count,
                willRebuildTypeScriptAugmentation
                    ? "Preparing reference query indexes."
                    : "Restoring reference query indexes.").ConfigureAwait(false);
        }

        if (willRebuildTypeScriptAugmentation)
            referenceSecondaryIndexBulkLoad?.PrepareForDeferredGraphRefresh(requestToken);
        else
            referenceSecondaryIndexBulkLoad?.Complete(requestToken);

        if (ftsBulkLoad != null)
        {
            ftsBulkLoad.Complete(ftsMutated, McpIndexFtsOptimizeForTesting, requestToken);
        }
        else if (ftsMutated)
        {
            writer.RecordFtsIncrementalWriteAndMergeIfThresholdReached(
                McpIndexFtsMergeForTesting,
                cancellationToken: requestToken);
        }
        var readinessStableFiles = true;
        string? readinessChangedFilePath = null;
        if (errors == 0 && !csharpState.DeferMutations)
        {
            McpIndexCSharpReadinessValidationForTesting?.Invoke();
            if (csharpState.HasFileSnapshots)
            {
                readinessStableFiles = csharpState.TryValidateFileSnapshots(
                    out readinessChangedFilePath);
            }
            else if (csharpState.PreservePriorPositiveSourceNoOp)
            {
                foreach (var target in csharpPrepassTargets)
                {
                    requestToken.ThrowIfCancellationRequested();
                    authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
                    if (IndexedFileStatReuse.TryGetReusableUnchangedFile(
                            reusableIndexedFileStats!,
                            target.FilePath,
                            target.IndexPath,
                            target.Language,
                            IsGeneratedExtractionSuppressed(target)) != null)
                    {
                        continue;
                    }

                    readinessStableFiles = false;
                    readinessChangedFilePath = target.DisplayRelativePath;
                    break;
                }
            }
        }

        if (errors == 0)
        {
            McpIndexInputSnapshotBarrierForTesting?.Invoke("before_readiness");
            var readinessStableScanInputs = indexer.TryValidateScanInputSnapshot(
                scanInputSnapshot,
                out var readinessChangedScanInputPath,
                requestToken);
            if (!readinessStableFiles || !readinessStableScanInputs)
            {
                csharpState.DeferForLoadedSnapshotDrift(
                    csharpState.FormatSnapshotPath(
                        readinessChangedFilePath
                        ?? readinessChangedScanInputPath
                        ?? "<csharp_workspace>"),
                    writer);
                errors = failures.Count;
            }
        }
        // MCP index now runs ValidateContent + InsertIssues per file (bdbb2bd) on par with CLI
        // index, so stamp both graph-ready and issues-ready on clean runs — the old "graph only"
        // path is no longer accurate. Bits are only stamped when every file committed without
        // throwing, so a partial failure leaves trust degraded and `validate` still surfaces it.
        // MCP index は CLI と同等に file_issues を永続化するため、成功時は graph / issues の両方を stamp する。
        if (!csharpState.DeferMutations
            && postExtractionHooks.ValueIfCreated?.SawCSharpStaticInterfaceSourceContract == true)
        {
            if (!csharpState.Workspace.HasSourceStaticInterfaceContracts)
            {
                csharpState.SourceEvidenceForStamp = false;
                csharpState.SourceEvidenceComplete = false;
                writer.SetCSharpStaticInterfaceSourceEvidence(null);
                csharpFailures.Record(
                    "<csharp_workspace>",
                    "csharp_workspace_validation",
                    new InvalidOperationException(
                        "A C# static-interface contract appeared after workspace preflight; rerun indexing to repair unchanged implementers."));
                errors = failures.Count;
            }
            else
            {
                csharpState.SourceEvidenceForStamp = true;
                csharpState.SourceEvidenceComplete = true;
                writer.SetCSharpStaticInterfaceSourceEvidence(true);
            }
        }
        // The TypeScript augmentation rebuild performs the same graph refresh after adding
        // synthetic edges. If late input validation makes that rebuild ineligible, complete
        // the deferred pass before publishing partial metadata.
        // late validation で augmentation を実行できない場合だけ、partial metadata 前に補完する。
        var willRebuildTypeScriptAugmentationAfterReadinessValidation =
            !csharpState.DeferMutations
            && TypeScriptAugmentationRefreshPolicy.ShouldRebuildReferences(
                symbolsOnly: false,
                canFinalize: !scanHadErrors && errors == 0,
                typeScriptAugmentationNeedsRefresh,
                typeScriptAugmentationDirtyNames?.RequiresRefresh == true,
                canStampReadyWithoutRebuild:
                    canStampTypeScriptAugmentationReadyWithoutRebuild);
        if (willRebuildTypeScriptAugmentation
            && !willRebuildTypeScriptAugmentationAfterReadinessValidation)
        {
            if (deferMutualRecursionRefreshToTypeScriptAugmentation)
            {
                requestToken.ThrowIfCancellationRequested();
                await EmitProgressNotificationAsync(
                    progressToken,
                    processed,
                    files.Count,
                    "Finalizing reference graph after readiness validation.").ConfigureAwait(false);
                writer.RefreshMutualRecursionFlags(
                    requestToken,
                    stampReferenceIdentityContractReady:
                        referenceIdentityReadyForMutualRecursionRefresh,
                    referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
            }
            referenceSecondaryIndexBulkLoad?.Complete(requestToken);
        }
        // A complete fresh/rebuild discovery is authoritative for both presence and absence.
        // With a partial discovery, positive target evidence remains authoritative while
        // absence falls back to persisted rows. This prevents readiness from depending on
        // whether a failed C#/SQL target happened to persist before the failure.
        // complete fresh/rebuild discovery は presence/absence の双方に使い、partial discovery
        // でも発見済み target は保持する。absence だけを persisted row へ fallback する。
        var freshLanguageAbsenceAuthoritative =
            startedWithNoIndexedFiles && !scanHadErrors && errors == 0;
        var discoveredCSharpFiles = csharpPrepassTargets.Count > 0;
        var hasCSharpFilesAfter = discoveredCSharpFiles
            || (!freshLanguageAbsenceAuthoritative && writer.HasAnyFilesWithLanguage("csharp"));
        var hasSqlFilesAfter = hasSqlTargets
            || (!freshLanguageAbsenceAuthoritative && writer.HasAnyFilesWithLanguage("sql"));
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter;
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter;
        var sqlGraphContractReadyAfter = !hasSqlFilesAfter;
        var foldReadyAfter = false;
        string? foldReadyReason = null;
        if (errors > 0)
        {
            var statusFileErrors = failures
                .Take(StatusMetadataLimits.MaxFileErrors)
                .Select(failure => new StatusIndexFileError
                {
                    File = failure.Path,
                    Category = failure.Stage switch
                    {
                        "csharp_prepass" or "csharp_stat_revalidation" => "file_read_error",
                        "csharp_workspace_validation" => "extraction_error",
                        _ => "index_file_error",
                    },
                    Phase = failure.Stage,
                    Detail = failure.Message,
                })
                .ToList();
            writer.MarkIndexIncomplete(["file_index_error"]);
            writer.SetMetaValues(
                (DbContext.LastFailedIndexRunStatusMetaKey, "partial"),
                (DbContext.LastFailedIndexRunModeMetaKey, rebuild ? "rebuild" : "mcp"),
                (DbContext.LastFailedIndexRunStartedAtMetaKey, runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunDurationMsMetaKey, runStopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesProcessedMetaKey, processed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesTotalMetaKey, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunErrorCodeMetaKey, CommandErrorCodes.IndexPartial),
                (DbContext.LastFailedIndexRunReasonMetaKey, "file_index_error"),
                (DbContext.LastFailedIndexRunProgressPersistedMetaKey, true.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunRecoveryHintMetaKey, "Fix the reported file/extractor error, then rerun MCP index. Successful files and graph edges remain persisted; a rebuild is not required."),
                (DbContext.LastFailedIndexRunFileErrorsMetaKey, JsonSerializer.Serialize(
                    statusFileErrors,
                    StatusMetadataJsonContext.Default.ListStatusIndexFileError)));
        }
        if (!scanHadErrors && errors == 0)
        {
            var rebuildTypeScriptAugmentation =
                willRebuildTypeScriptAugmentationAfterReadinessValidation;
            await EmitProgressNotificationAsync(
                progressToken,
                processed,
                files.Count,
                rebuildTypeScriptAugmentation
                    ? "Rebuilding TypeScript augmentation."
                    : "Finalizing index metadata.").ConfigureAwait(false);
            if (!useFullRunBatchMarker)
                writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(requestToken, "mcp index readiness");
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkIndexReaderContractsReady(symbolsOnlyGraphOmitted: false);
            writer.MarkHdlGraphContractReady();
            if (csharpState.SourceEvidenceComplete && !csharpState.PreservePriorPositiveSourceNoOp)
                writer.SetCSharpStaticInterfaceSourceEvidence(csharpState.SourceEvidenceForStamp);
            csharpSymbolNameReadyAfter = true;
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    McpIndexCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets(requestToken);
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            sqlGraphContractReadyAfter = true;
            if (TypeScriptAugmentationRefreshPolicy.IsRefreshRequired(
                    symbolsOnly: false,
                    typeScriptAugmentationNeedsRefresh,
                    typeScriptAugmentationDirtyNames?.RequiresRefresh == true))
            {
                if (canStampTypeScriptAugmentationReadyWithoutRebuild)
                {
                    writer.MarkTypeScriptAugmentationReady();
                }
                else
                {
                    McpIndexTypeScriptAugmentationRebuildForTesting?.Invoke();
                    var augmentationReferences = writer.RebuildTypeScriptAugmentationReferences(
                        projectPath,
                        useScopedTypeScriptAugmentationRefresh
                            ? typeScriptAugmentationDirtyNames?.DirtyNames
                            : null,
                        deferMutualRecursionRefreshToTypeScriptAugmentation,
                        referenceSecondaryIndexBulkLoad,
                        requestToken);
                    if (startedWithNoIndexedFiles)
                        freshCountReferences += augmentationReferences;
                }
            }
            RestampHotspotFamilyTrust(
                writer,
                reusedHotspotFamilyLanguages,
                indexSnapshot.HotspotFamilyVersions,
                indexSnapshot.HotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            if (writer.CSharpFamilyTrustAllowsReferenceIdentityReady(hasCSharpFilesAfter))
                writer.MarkReferenceIdentityContractReady();
            else
                writer.ClearReferenceIdentityContractReady();
            // A successful refresh can stamp the languages it regenerated even when the
            // independent fold-key contract remains stale.
            // 成功した refresh で再生成した言語は、独立した fold-key 契約が stale の
            // ままでも extractor version を stamp できる。
            writer.StampSymbolExtractorVersions(indexedSymbolExtractorLanguages);
            writer.StampDynamicReferenceGraphContracts(indexedSymbolExtractorLanguages);
            // FoldReady must reflect reality (#86). Like CLI full-scan, MCP index_project skips
            // unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows keep NULL
            // name_folded / *_folded. Stamp only when every row is backfilled; otherwise readers
            // would silently miss legacy rows on the folded-equality path. Codex #86 review.
            // MCP も incremental で skip される legacy 行が残るため、実検証を通してから stamp。
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = indexSnapshot.FoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = indexSnapshot.FoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent && foldFingerprintMatchesCurrent;
            if (skipped == 0 || canRestampExistingFoldTrust)
            {
                var currentFoldProducerSnapshot =
                    ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectPath);
                if (!currentFoldProducerSnapshot.UsesOnlyBuiltInProducers
                    || currentFoldProducerSnapshot.MutationGeneration
                        != freshFoldProducerSnapshot.MutationGeneration
                    || postExtractionHooks.ValueIfCreated?.HasHooks == true)
                {
                    authoritativeFreshFoldRowsClaim?.Invalidate();
                }
                // The stamp transaction performs the only row verification for the common
                // current-metadata path and reports whether NULL or stale values blocked it.
                // current metadata 経路の row 検証は stamp transaction 内の一度だけにまとめ、
                // NULL と stale value のどちらが妨げたかも保持する。
                var foldStampResult = writer.MarkFoldReadyWithResult(
                    stampCurrentSymbolExtractorVersions: skipped == 0,
                    symbolExtractorLanguagesToStamp: skipped == 0 ? indexedSymbolExtractorLanguages : null,
                    authoritativeFreshRowsClaim: authoritativeFreshFoldRowsClaim);
                foldReadyAfter = foldStampResult == FoldReadyStampResult.Ready;
                if (foldStampResult == FoldReadyStampResult.MissingBackfill)
                    foldReadyReason = DegradationReasonCodes.MissingFoldBackfill;
                else if (foldStampResult == FoldReadyStampResult.NonCurrentFoldValues)
                    foldReadyReason = DegradationReasonCodes.FoldRowsNotRestamped;
            }
            else if (!writer.AllFoldedColumnsBackfilled())
            {
                foldReadyReason = DegradationReasonCodes.MissingFoldBackfill;
            }
            else if (!foldVersionMatchesCurrent)
            {
                foldReadyReason = DegradationReasonCodes.StaleFoldKeyVersion;
            }
            else if (!foldFingerprintMatchesCurrent)
            {
                foldReadyReason = DegradationReasonCodes.StaleFoldKeyFingerprint;
            }

            IndexCommandRunner.StampWriterVersionAndSymbolKindFilter(writer, _version, symbolKindFilter.Signature);

            // Successful no-op MCP full scans should repair explicit-DB roots only after
            // readiness is stamped, preserving the failure-path safety contract.
            // MCP の no-op full-scan root backfill も readiness stamp 後に限定する。
            WriteProjectRootOnce();
            writer.WriteUnknownExtensionFileMetadata(scanResult.UnknownExtensionFiles);
            var bytesRead = readableBytes.Sizes.Count == files.Count
                ? (BytesRead: readableBytes.Total,
                    SkippedFileCount: readableBytes.EstimateComplete ? 0L : 1L)
                : SumReadableFileBytes(
                        files,
                        projectPath,
                        indexRunDiagnostics,
                        mcpIndexDiagnostics,
                        authorizedRoot.EnsureAuthorizedEntry,
                        readableBytes.Sizes);
            var referenceExtractionCapHits = writer.GetReferenceExtractionCapHits(issuesStateAvailable: true);
            writer.SetMetaValues(
                (DbContext.LastIndexRunModeMetaKey, rebuild ? "rebuild" : "mcp"),
                (DbContext.LastIndexRunStartedAtMetaKey, runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunDurationMsMetaKey, runStopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunFilesScannedMetaKey, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunFilesSkippedMetaKey, skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunParseErrorsMetaKey, errors.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunBytesReadMetaKey, bytesRead.BytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey, bytesRead.SkippedFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunBytesReadIncompleteMetaKey, (bytesRead.SkippedFileCount > 0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunRowsUpsertedMetaKey, processed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunRowsDeletedMetaKey, purged.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey, JsonSerializer.Serialize(
                    referenceExtractionCapHits,
                    StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary)),
                (DbContext.LastIndexRunRebuildReclaimMetaKey, null));
            writer.MarkIndexCompleteness(writer.GetPersistedIndexOmissionReasons());
            writer.ClearLastFailedIndexRunMetadata();
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // Mirrors the CLI full-scan contract (Issue #1508) so MCP-driven re-indexes also
            // refresh `worktree_head_changed`; partial / failed runs leave the prior HEAD
            // untouched and surface staleness until the next clean refresh. Issues #1508 / #1512.
            // CLI full-scan と同じく成功時のみ HEAD を記録する。partial / 失敗は旧 HEAD を残す。
            var currentHeadBranch = GitHelper.TryGetHeadBranch(projectPath, requestToken);
            writer.SetMetaValues(
                (DbContext.IndexedHeadCommitMetaKey, currentHeadCommit),
                (DbContext.WorkspaceVerifiedHeadShaMetaKey, currentHeadCommit),
                (DbContext.WorkspaceVerificationPendingPathsMetaKey, null),
                (DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey, null),
                (DbContext.IndexedHeadCommitBranchMetaKey, currentHeadBranch));
            // #1509: also persist the always-updated HEAD/branch/timestamp triple so
            // status / consumers can detect cross-session staleness via
            // `commits_ahead_of_indexed_head`. Same best-effort contract — git unavailability
            // writes NULL stamps and stamp exceptions never fail the index itself.
            // #1509: HEAD / branch / timestamp を保存し、cross-session staleness 検出を可能にする。
            try
            {
                var timestamp = currentHeadCommit != null
                    ? GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                writer.SetMetaValues(
                    (DbContext.IndexedHeadShaMetaKey, currentHeadCommit),
                    (DbContext.IndexedHeadBranchMetaKey, currentHeadBranch),
                    (DbContext.IndexedHeadTimestampMetaKey, timestamp));
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort; never fail an otherwise-successful index run.
                indexRunDiagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic("indexed_head_metadata_write_failed", ex));
            }
            // #1546: stamp workspace path-case-sensitivity so MCP-driven indexes also
            // surface the diagnostic field through `cdidx status` / MCP status.
            // #1546: MCP 経由 index でも case-sensitivity stamp を残す。
            try
            {
                var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, requestToken);
                CodeIndex.Cli.PathCasing.SeedFromWorkspace(projectPath, ignoreCase);
                writer.SetMeta(
                    DbContext.WorkspacePathCaseSensitiveMetaKey,
                    (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort; never fail an otherwise-successful index run.
                indexRunDiagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic("path_case_sensitivity_metadata_write_failed", ex));
            }
            try
            {
                writer.SetMeta(
                    DbContext.IndexedFollowSymlinksPolicyMetaKey,
                    symlinkPolicy.ToString().ToLowerInvariant());
            }
            catch (Exception ex)
            {
                // Best-effort; never fail an otherwise-successful index run.
                indexRunDiagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic("indexed_symlink_policy_metadata_write_failed", ex));
            }
            IndexCommandRunner.StampLastIndexRunDiagnostics(writer, indexRunDiagnostics);
            writer.ClearBatchInProgress();
            readinessTxn.Commit();
        }
        else if (useFullRunBatchMarker)
        {
            writer.ClearBatchInProgress();
        }
        // A TypeScript-owned deferred graph pass keeps recoverable guard ownership until the
        // readiness transaction commits. A thrown readiness path is then repaired by Dispose
        // instead of exposing a partial schema.
        if (referenceSecondaryIndexBulkLoad != null
            && willRebuildTypeScriptAugmentationAfterReadinessValidation
            && !scanHadErrors
            && errors == 0)
            writer.ReportReferenceSecondaryIndexBulkLoadState("readiness_committed");
        referenceSecondaryIndexBulkLoad?.Complete(requestToken);
        hotspotAggregateRefresh.Complete(requestToken);
        StatusRebuildReclaim? rebuildReclaim = null;
        if (rebuild && !scanResult.HadErrors && errors == 0)
        {
            await EmitProgressNotificationAsync(
                progressToken,
                files.Count,
                files.Count,
                "Evaluating rebuild free-page reclaim.").ConfigureAwait(false);
            rebuildReclaim = db.RunRebuildReclaimIfRecommended(requestToken);
            IndexCommandRunner.TryStampRebuildReclaimMetadata(
                writer,
                rebuildReclaim,
                runStopwatch.ElapsedMilliseconds,
                memoryTimeline: null);
        }
        if (!scanResult.HadErrors && errors == 0)
        {
            var plannerMaintenanceFailure = db.RunPlannerStatisticsMaintenance(
                forceAnalyze: false,
                requestToken);
            if (plannerMaintenanceFailure != null)
                IndexCommandRunner.TryStampPlannerStatisticsMaintenanceDiagnostic(writer, indexRunDiagnostics, plannerMaintenanceFailure);
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) =
            startedWithNoIndexedFiles && !scanHadErrors && errors == 0
                ? (freshCountFiles, freshCountChunks, freshCountSymbols, freshCountReferences)
                : writer.GetCounts();
        await EmitProgressNotificationAsync(progressToken, files.Count, files.Count, errors == 0 ? "Indexing complete." : "Indexing completed with errors.").ConfigureAwait(false);
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));

        return BuildIndexCompletionResult(
            id,
            new IndexCompletionDetails(
                projectPath,
                authorizedRoot.CheckedRootIdentity,
                rebuild,
                maxFileBytes,
                optionsPayload,
                unsupportedModesJson,
                totalFiles,
                totalChunks,
                totalSymbols,
                totalReferences,
                files.Count,
                skipped,
                purged,
                scanResult.UnknownExtensionFiles.Count,
                errors,
                symbolsDroppedByKindFilter,
                symbolKindFilter,
                runStopwatch.ElapsedMilliseconds,
                runStartedAtUtc,
                GetUtcNow(),
                sqlGraphContractReadyAfter,
                csharpSymbolNameReadyAfter,
                csharpMetadataTargetReadyAfter,
                foldReadyAfter,
                foldReadyReason,
                rebuildReclaim,
                memorySamples,
                failures,
                mcpIndexDiagnostics,
                writer));
    }

}
