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
        var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer, requestToken);
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = indexSnapshot.CSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpMetadataTargetsNeedRefresh = indexSnapshot.MetadataTargetCSharp != currentMetadataTargetVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = indexSnapshot.SqlGraphContractVersion == currentSqlGraphContractVersion;
        var currentHdlGraphContractVersion = DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hdlGraphContractMatchesCurrent = indexSnapshot.HdlGraphContractVersion == currentHdlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            indexSnapshot.HotspotFamilyVersions,
            indexSnapshot.HotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
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
        var scanWithDirectorySnapshots = indexer.ScanFilesDetailedWithDirectoryListingSnapshots(
            cancellationToken: requestToken);
        var scanResult = scanWithDirectorySnapshots.ScanResult;
        var scanInputSnapshot = scanWithDirectorySnapshots.InputSnapshot;
        var scanHadErrors = scanResult.HadErrors;
        var deferCSharpMutationsForIncompleteScan = !startedWithNoIndexedFiles
            && scanHadErrors
            && indexSnapshot.CSharpStaticInterfaceSourceEvidence != false;
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
        var files = scanResult.Files;
        var targets = BuildMcpIndexTargetSet(projectPath, indexer, scanResult);
        var fileTargets = targets.All;
        var csharpPrepassTargets = targets.CSharp;
        var languageCounts = scanResult.LanguageCounts;
        var hasSqlTargets = languageCounts.ContainsKey("sql");
        var hasTypeScriptTargets = languageCounts.ContainsKey("typescript");
        var knownReadableFileSizes = new Dictionary<string, long>(files.Count, StringComparer.Ordinal);
        long knownReadableBytesRead = 0;
        var knownReadableByteEstimateComplete = true;
        void RememberReadableFileSize(string path, long size)
        {
            var priorSize = knownReadableFileSizes.TryGetValue(path, out var prior)
                ? prior
                : (long?)null;
            if (knownReadableByteEstimateComplete
                && !FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
                    knownReadableBytesRead,
                    priorSize,
                    size,
                    out knownReadableBytesRead))
            {
                knownReadableByteEstimateComplete = false;
            }
            knownReadableFileSizes[path] = size;
        }
        var discoveryPlan = BuildMcpIndexDiscoveryPlan(
            writer,
            projectPath,
            scanResult,
            targets,
            initialStaleFilePurgePlan,
            startedWithNoIndexedFiles,
            deferCSharpMutationsForIncompleteScan,
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
        Dictionary<string, IndexedFileStatReuseResult?>? csharpPrepassStatReuse = null;
        var priorPositiveCSharpSourceNoOpCandidate = false;
        var allCSharpPrepassTargetsReusable = false;
        bool IsGeneratedExtractionSuppressed(CSharpStaticInterfacePrepass.FileTarget target)
            => target.GeneratedExtractionSuppressed == true;

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
                allCSharpPrepassTargetsReusable = false;
                (csharpPrepassStatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                    csharpPrepassTargets.Count,
                    StringComparer.Ordinal))[target.IndexPath] = null;
                return false;
            }

            (csharpPrepassStatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                csharpPrepassTargets.Count,
                StringComparer.Ordinal))[target.IndexPath] = existingFile.Value;
            return true;
        }

        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? csharpWorkspaceFileSnapshots = null;

        string FormatCSharpWorkspaceSnapshotPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "<csharp_workspace>")
                return "<csharp_workspace>";
            if (!Path.IsPathRooted(path))
                return FileIndexer.NormalizePathSeparators(path);
            return FormatDiagnosticPath(projectPath, path);
        }

        CSharpStaticInterfaceWorkspaceSymbols BuildStableCSharpWorkspace(
            Func<CSharpStaticInterfaceWorkspaceSymbols> buildWorkspace)
        {
            csharpWorkspaceFileSnapshots = null;
            Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> fileSnapshots = [];
            string? failedFilePath = null;
            var capturedFiles = CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                csharpPrepassTargets,
                out fileSnapshots,
                out failedFilePath,
                requestToken,
                target => authorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            if (!capturedFiles)
            {
                return new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths:
                    [
                        FormatCSharpWorkspaceSnapshotPath(failedFilePath)
                    ]);
            }

            McpIndexCSharpPrepassForTesting?.Invoke();
            var workspace = buildWorkspace();
            var stableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                csharpPrepassTargets,
                fileSnapshots,
                out var changedFilePath,
                requestToken,
                target => authorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            if (!stableFiles || !workspace.SourceContractEvidenceComplete)
            {
                var incompletePath = workspace.IncompleteSourcePaths?.FirstOrDefault()
                    ?? changedFilePath
                    ?? "<csharp_workspace>";
                return workspace with
                {
                    HasStaticInterfaceContracts = true,
                    SourceContractEvidenceComplete = false,
                    IncompleteSourcePaths = [FormatCSharpWorkspaceSnapshotPath(incompletePath)],
                };
            }

            csharpWorkspaceFileSnapshots = fileSnapshots;
            return workspace;
        }

        priorPositiveCSharpSourceNoOpCandidate = csharpPositiveNoOpPolicyCandidate
            && !hasCSharpLanguageTransitions;
        if (priorPositiveCSharpSourceNoOpCandidate)
        {
            allCSharpPrepassTargetsReusable = true;
            csharpPrepassStatReuse = new Dictionary<string, IndexedFileStatReuseResult?>(
                csharpPrepassTargets.Count,
                StringComparer.Ordinal);
            foreach (var target in csharpPrepassTargets)
            {
                requestToken.ThrowIfCancellationRequested();
                authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
                var existingFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    reusableIndexedFileStats!,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    IsGeneratedExtractionSuppressed(target));
                csharpPrepassStatReuse[target.IndexPath] = existingFile;
                allCSharpPrepassTargetsReusable &= existingFile != null;
            }
        }

        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        IReadOnlyList<string> incompleteCSharpPrepassPaths = [];
        var forceFullCSharpRefreshFromInvalidatedNoOp = false;
        if (csharpPrepassTargets.Count == 0 || deferCSharpMutationsForIncompleteScan)
        {
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else if (priorPositiveCSharpSourceNoOpCandidate
                 && allCSharpPrepassTargetsReusable)
        {
            // Existing graph rows are already complete and every C# stat matched. Avoid
            // loading persisted contract symbols or constructing a lookup that no file will use.
            // 全C# stat一致のpositive no-opではDB symbol/lookup loadを完全に省略する。
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            csharpWorkspace = BuildStableCSharpWorkspace(() =>
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
                    symbolArtifactCache: csharpPrepassSymbolArtifacts));
            forceFullCSharpRefreshFromInvalidatedNoOp =
                indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
                || csharpWorkspace.HasStaticInterfaceContracts
                || csharpWorkspace.RequiresMemberReadReferenceRefresh;
        }
        if (!csharpWorkspace.SourceContractEvidenceComplete)
        {
            csharpPrepassSymbolArtifacts?.Clear();
            csharpPrepassSymbolArtifacts = null;
            incompleteCSharpPrepassPaths = csharpWorkspace.IncompleteSourcePaths ?? [];
            deferCSharpMutationsForIncompleteScan = true;
            staleFilePurgePlan = FilePurgePlan.Empty;
            purged = 0;
            hadCSharpStaticInterfaceContractsBeforePurge = false;
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                false,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: incompleteCSharpPrepassPaths);
        }
        var preservePriorPositiveCSharpSourceNoOp = priorPositiveCSharpSourceNoOpCandidate
            && allCSharpPrepassTargetsReusable
            && !deferCSharpMutationsForIncompleteScan;
        var csharpSourceEvidenceForStamp = preservePriorPositiveCSharpSourceNoOp
            ? indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
            : csharpWorkspace.HasSourceStaticInterfaceContracts;
        var csharpSourceEvidenceComplete = preservePriorPositiveCSharpSourceNoOp
            || csharpWorkspace.SourceContractEvidenceComplete;
        if (preservePriorPositiveCSharpSourceNoOp)
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = false };
        if (!deferCSharpMutationsForIncompleteScan
            && !preservePriorPositiveCSharpSourceNoOp
            && (forceFullCSharpRefreshFromInvalidatedNoOp
                || requiresConservativeCSharpSourceRefresh
                || !csharpSourceEvidenceComplete
                || (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)))
        {
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
        }
        var failures = new List<IndexFileFailure>();
        if (scanHadErrors)
        {
            foreach (var error in scanResult.Errors)
            {
                if (error.IsFatal)
                    failures.Add(BuildScanFailure(error));
            }
        }
        int processed = 0, skipped = 0, errors = failures.Count;
        var reportedCSharpWorkspaceFailures = new HashSet<string>(StringComparer.Ordinal);

        void RecordCSharpWorkspaceFailure(string path, string stage, Exception exception)
        {
            path = string.IsNullOrWhiteSpace(path) ? "<csharp_workspace>" : path;
            if (!reportedCSharpWorkspaceFailures.Add($"{stage}\n{path}"))
                return;

            errors++;
            var platformRelativePath = FileIndexer.NormalizeRelativePathForCurrentPlatform(path);
            failures.Add(BuildIndexFileFailure(
                projectPath,
                Path.Combine(projectPath, platformRelativePath),
                exception,
                stage));
        }

        void RecordIncompleteCSharpPrepassFailures(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
            {
                RecordCSharpWorkspaceFailure(
                    "<csharp_workspace>",
                    "csharp_prepass",
                    new IOException("C# static-interface workspace preflight could not read a source file."));
                return;
            }

            foreach (var path in paths.Take(50))
            {
                RecordCSharpWorkspaceFailure(
                    path,
                    "csharp_prepass",
                    new IOException("C# static-interface workspace preflight could not read this source file."));
            }
        }

        void DeferCSharpMutationsForLoadedSnapshotDrift(string path)
        {
            csharpPrepassSymbolArtifacts?.Clear();
            csharpPrepassSymbolArtifacts = null;
            deferCSharpMutationsForIncompleteScan = true;
            preservePriorPositiveCSharpSourceNoOp = false;
            csharpSourceEvidenceForStamp = false;
            csharpSourceEvidenceComplete = false;
            incompleteCSharpPrepassPaths = [path];
            csharpWorkspaceFileSnapshots = null;
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: incompleteCSharpPrepassPaths);
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            RecordCSharpWorkspaceFailure(
                path,
                "csharp_workspace_validation",
                new IOException(
                    "A C# source changed after workspace preflight; rerun indexing to refresh the complete C# graph."));
        }

        bool LoadedCSharpWorkspaceSnapshotMatches(
            in CSharpStaticInterfacePrepass.FileTarget target,
            FileRecord record)
        {
            if (target.Language != "csharp" || csharpWorkspaceFileSnapshots == null)
                return true;

            if (CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                    target.FilePath,
                    target.IndexPath,
                    target.DisplayRelativePath,
                    record.Size,
                    record.Modified,
                    csharpWorkspaceFileSnapshots,
                    out var changedPath,
                    requestToken,
                    authorizedRoot.EnsureAuthorizedEntry))
            {
                return true;
            }

            DeferCSharpMutationsForLoadedSnapshotDrift(
                FormatCSharpWorkspaceSnapshotPath(changedPath ?? target.DisplayRelativePath));
            return false;
        }

        if (!csharpSourceEvidenceComplete)
            RecordIncompleteCSharpPrepassFailures(incompleteCSharpPrepassPaths);
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
            in CSharpStaticInterfacePrepass.FileTarget target)
        {
            var allowStatReuse = !rebuild
                && !startedWithNoIndexedFiles
                && !indexSnapshot.SymbolsOnlyGraphOmitted
                && symbolKindFilterMatchesPrior
                && (target.Language != "csharp" || csharpIndexedProjectRootCompatible)
                && (target.Language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                && (target.Language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                && (target.Language != "sql" || sqlGraphContractMatchesCurrent)
                && (target.Language is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(target.Language, hotspotFamilyTrustMatchesCurrent);
            if (!allowStatReuse)
                return null;

            return target.Language == "csharp"
                && csharpPrepassStatReuse != null
                && csharpPrepassStatReuse.TryGetValue(target.IndexPath, out var cachedCSharpPrepassReuse)
                    ? cachedCSharpPrepassReuse
                    : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        target.Language,
                        target.GeneratedExtractionSuppressed == true);
        }

        IndexedFileStatReuseResult?[]? statMatchedFiles = null;
        bool[]? statPreflightCompleted = null;
        var estimatedDirtyBytes = staleFilePurgePlan.DeletedBytes;
        var persistedSizeExcessBytes = 0L;
        var byteEstimateComplete = !scanHadErrors
            && staleFilePurgePlan.ByteEstimateComplete
            && knownReadableByteEstimateComplete;
        var useFtsBulkLoad = rebuild || startedWithNoIndexedFiles;
        var everyTargetMatchedFtsStatPreflight = !useFtsBulkLoad
            && staleFilePurgePlan.Count == 0;
        if (!useFtsBulkLoad)
        {
            statMatchedFiles = new IndexedFileStatReuseResult?[fileTargets.Length];
            statPreflightCompleted = new bool[fileTargets.Length];
            McpIndexFtsStatPreflightBufferAllocatedForTesting?.Invoke(fileTargets.Length);
            for (var targetIndex = 0; targetIndex < fileTargets.Length; targetIndex++)
            {
                requestToken.ThrowIfCancellationRequested();
                var target = fileTargets[targetIndex];
                try
                {
                    authorizedRoot.EnsureAuthorizedEntry(target.FilePath);
                    var statMatchedFile = GetStatMatchedFile(target);
                    statMatchedFiles[targetIndex] = statMatchedFile;
                    statPreflightCompleted[targetIndex] = true;
                    if (statMatchedFile != null)
                    {
                        RememberReadableFileSize(target.FilePath, statMatchedFile.Value.Size);
                        continue;
                    }

                    everyTargetMatchedFtsStatPreflight = false;

                    var info = new FileInfo(target.FilePath);
                    if (!info.Exists || info.Length < 0)
                    {
                        byteEstimateComplete = false;
                        continue;
                    }

                    RememberReadableFileSize(target.FilePath, info.Length);
                    var persistedSize = reusableIndexedFileStats!.GetPersistedSize(target.IndexPath);
                    if (!FtsBulkLoadTriggerGuard.TryAccumulateDirtyFileBytes(
                            estimatedDirtyBytes,
                            persistedSizeExcessBytes,
                            info.Length,
                            persistedSize,
                            out estimatedDirtyBytes,
                            out persistedSizeExcessBytes))
                    {
                        byteEstimateComplete = false;
                    }
                }
                catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (McpIndexAuthorizationException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    // Estimation must never bypass the existing per-file failure contract.
                    // The real loop retries authorization/stat work inside its normal try/catch.
                    byteEstimateComplete = false;
                    everyTargetMatchedFtsStatPreflight = false;
                    statPreflightCompleted[targetIndex] = false;
                }
            }

            byteEstimateComplete &= knownReadableByteEstimateComplete;
            var totalBytes = knownReadableBytesRead;
            if (!knownReadableByteEstimateComplete
                || totalBytes > long.MaxValue - staleFilePurgePlan.DeletedBytes)
                byteEstimateComplete = false;
            else
                totalBytes += staleFilePurgePlan.DeletedBytes;
            if (totalBytes > long.MaxValue - persistedSizeExcessBytes)
                byteEstimateComplete = false;
            else
                totalBytes += persistedSizeExcessBytes;

            useFtsBulkLoad = byteEstimateComplete
                && FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(estimatedDirtyBytes, totalBytes);
        }

        var reuseFinalCSharpStatAtActualSkip = false;
        if (preservePriorPositiveCSharpSourceNoOp
            && everyTargetMatchedFtsStatPreflight)
        {
            // A pure no-op performs its second and final C# stat pass at the readiness
            // boundary. Reuse the candidate result in the per-file skip loop meanwhile.
            // 純粋no-opの2巡目C# statはreadiness直前へ統合し、skip loopではcandidateを再利用する。
            reuseFinalCSharpStatAtActualSkip = true;
        }

        if (preservePriorPositiveCSharpSourceNoOp
            && !everyTargetMatchedFtsStatPreflight)
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
                    IsGeneratedExtractionSuppressed(target));
                if (revalidated == null)
                    invalidatedCSharpTargetIndexes.Add(targetIndex);
            }

            if (invalidatedCSharpTargetIndexes.Count > 0)
            {
                csharpWorkspace = BuildStableCSharpWorkspace(() =>
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
                preservePriorPositiveCSharpSourceNoOp = false;
                if (!csharpWorkspace.SourceContractEvidenceComplete)
                {
                    incompleteCSharpPrepassPaths = csharpWorkspace.IncompleteSourcePaths ?? [];
                    deferCSharpMutationsForIncompleteScan = true;
                    staleFilePurgePlan = FilePurgePlan.Empty;
                    purged = 0;
                    hadCSharpStaticInterfaceContractsBeforePurge = false;
                    csharpSourceEvidenceForStamp = false;
                    csharpSourceEvidenceComplete = false;
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: incompleteCSharpPrepassPaths);
                    RecordIncompleteCSharpPrepassFailures(incompleteCSharpPrepassPaths);
                    useFtsBulkLoad = false;
                }
                else
                {
                    var requiresFullCSharpRefresh =
                        indexSnapshot.CSharpStaticInterfaceSourceEvidence == true
                        || csharpWorkspace.HasStaticInterfaceContracts;
                    forceFullCSharpRefreshFromInvalidatedNoOp = requiresFullCSharpRefresh;
                    csharpSourceEvidenceForStamp = csharpWorkspace.HasSourceStaticInterfaceContracts;
                    csharpSourceEvidenceComplete = true;
                    useFtsBulkLoad = false;
                    if (requiresFullCSharpRefresh)
                    {
                        csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
                        invalidatedCSharpTargetIndexes.Clear();
                        for (var targetIndex = 0; targetIndex < fileTargets.Length; targetIndex++)
                        {
                            if (fileTargets[targetIndex].Language == "csharp")
                                invalidatedCSharpTargetIndexes.Add(targetIndex);
                        }
                    }

                    foreach (var targetIndex in invalidatedCSharpTargetIndexes)
                    {
                        statMatchedFiles![targetIndex] = null;
                        statPreflightCompleted![targetIndex] = true;
                    }
                }
            }
        }

        async Task<JsonNode> ReturnBeforeWriteSnapshotFailureAsync(string changedPath)
        {
            var formattedPath = FormatCSharpWorkspaceSnapshotPath(changedPath);
            RecordCSharpWorkspaceFailure(
                formattedPath,
                "csharp_workspace_validation",
                new IOException(
                    "Directory entries or scan configuration changed after source discovery; rerun indexing from a stable workspace snapshot."));

            var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
            await EmitProgressNotificationAsync(
                progressToken,
                0,
                files.Count,
                "Indexing stopped before index-data mutation because scan inputs changed.").ConfigureAwait(false);
            if (memorySamples != null)
                memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));

            var discoveredCSharpFiles = languageCounts.ContainsKey("csharp");
            var discoveredSqlFiles = languageCounts.ContainsKey("sql");
            var persistedCSharpFiles = writer.HasAnyFilesWithLanguage("csharp");
            var persistedSqlFiles = writer.HasAnyFilesWithLanguage("sql");
            var hasCSharpFiles = discoveredCSharpFiles || persistedCSharpFiles;
            var hasSqlFiles = discoveredSqlFiles || persistedSqlFiles;
            var sqlGraphContractReady = !hasSqlFiles
                || (persistedSqlFiles && sqlGraphContractMatchesCurrent);
            var csharpSymbolNameReady = !hasCSharpFiles
                || (persistedCSharpFiles && csharpSymbolNameContractMatchesCurrent);
            var csharpMetadataTargetReady = !hasCSharpFiles
                || (persistedCSharpFiles && indexSnapshot.MetadataTargetCSharp == currentMetadataTargetVersion);
            var structured = new JsonObject
            {
                ["path"] = projectPath,
                ["checked_root_identity"] = authorizedRoot.CheckedRootIdentity,
                ["rebuild"] = rebuild,
                ["dry_run"] = false,
                ["max_file_bytes"] = maxFileBytes,
                ["index_options"] = optionsPayload,
                ["unsupported_modes"] = unsupportedModesJson,
                ["summary"] = new JsonObject
                {
                    ["files"] = totalFiles,
                    ["chunks"] = totalChunks,
                    ["symbols"] = totalSymbols,
                    ["references"] = totalReferences,
                    ["scanned"] = files.Count,
                    ["skipped"] = 0,
                    ["purged"] = 0,
                    ["unknown_extension_file_count"] = scanResult.UnknownExtensionFiles.Count,
                    ["errors"] = errors,
                    ["failed_count"] = failures.Count,
                    ["symbols_dropped_by_kind_filter"] = symbolsDroppedByKindFilter,
                },
                ["symbol_kind_filter"] = new JsonObject
                {
                    ["include"] = ToJsonStringArray(symbolKindFilter.Include),
                    ["exclude"] = ToJsonStringArray(symbolKindFilter.Exclude),
                    ["active"] = symbolKindFilter.IsActive,
                },
                ["duration_ms"] = runStopwatch.ElapsedMilliseconds,
                ["started_at"] = runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["sql_graph_contract_ready"] = sqlGraphContractReady,
                ["csharp_symbol_name_ready"] = csharpSymbolNameReady,
                ["csharp_metadata_target_ready"] = csharpMetadataTargetReady,
                ["fold_ready"] = (indexSnapshot.Readiness & DbContext.FoldReadyFlag) != 0,
                ["fold_ready_reason"] = (indexSnapshot.Readiness & DbContext.FoldReadyFlag) != 0
                    ? null
                    : DegradationReasonCodes.MissingFoldBackfill,
            };
            if (memorySamples != null)
                structured["memory_trace"] = memorySamples;

            var failureArray = new JsonArray();
            foreach (var failure in failures.Take(50))
            {
                failureArray.Add(new JsonObject
                {
                    ["path"] = failure.Path,
                    ["stage"] = failure.Stage,
                    ["exception_type"] = failure.ExceptionType,
                    ["message"] = failure.Message,
                    ["message_truncated"] = failure.MessageTruncated,
                });
            }
            structured["failed_count"] = failures.Count;
            structured["failures"] = failureArray;
            if (failures.Count > 50)
                structured["failures_truncated"] = failures.Count - 50;
            AddMcpIndexDiagnostics(structured, failures, mcpIndexDiagnostics);
            var referenceExtractionCapHits = writer.GetReferenceExtractionCapHits(
                issuesStateAvailable: (indexSnapshot.Readiness & DbContext.IssuesReadyFlag) != 0);
            using var referenceSignalReader = new DbReader(writer.Connection, isReadOnly: true);
            var persistedReadiness = referenceSignalReader.GetPersistedIndexGenerationReadiness(
                referenceExtractionCapHits);
            AddIndexGenerationReadinessSignal(structured, persistedReadiness);
            AddReferenceGraphCompletenessSignal(structured, persistedReadiness);
            if (!sqlGraphContractReady)
            {
                AddSqlGraphContractSignal(
                    structured,
                    new SqlGraphContractSignal(
                        Ready: false,
                        Relevant: true,
                        DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason()));
            }
            return CreateToolResult(
                id,
                "Indexing stopped before index-data mutation because the scan snapshot changed.",
                structured);
        }

        McpIndexInputSnapshotBarrierForTesting?.Invoke("before_write");
        if (!indexer.TryValidateScanInputSnapshot(
                scanInputSnapshot,
                out var beforeWriteChangedScanInputPath,
                requestToken))
        {
            return await ReturnBeforeWriteSnapshotFailureAsync(
                beforeWriteChangedScanInputPath).ConfigureAwait(false);
        }

        if (!deferCSharpMutationsForIncompleteScan)
        {
            var stableFiles = true;
            string? changedFilePath = null;
            if (csharpWorkspaceFileSnapshots != null)
            {
                stableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    csharpPrepassTargets,
                    csharpWorkspaceFileSnapshots,
                    out changedFilePath,
                    requestToken,
                    target => authorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            }
            if (!stableFiles)
            {
                csharpPrepassSymbolArtifacts?.Clear();
                csharpPrepassSymbolArtifacts = null;
                var driftPath = FormatCSharpWorkspaceSnapshotPath(changedFilePath);
                incompleteCSharpPrepassPaths = [driftPath];
                deferCSharpMutationsForIncompleteScan = true;
                staleFilePurgePlan = FilePurgePlan.Empty;
                purged = 0;
                hadCSharpStaticInterfaceContractsBeforePurge = false;
                preservePriorPositiveCSharpSourceNoOp = false;
                csharpSourceEvidenceForStamp = false;
                csharpSourceEvidenceComplete = false;
                csharpWorkspaceFileSnapshots = null;
                csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: incompleteCSharpPrepassPaths);
                RecordIncompleteCSharpPrepassFailures(incompleteCSharpPrepassPaths);
                useFtsBulkLoad = false;
            }
        }

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
        if (!preservePriorPositiveCSharpSourceNoOp)
        {
            writer.SetCSharpStaticInterfaceSourceEvidence(
                csharpSourceEvidenceComplete && csharpSourceEvidenceForStamp ? true : null);
        }
        writer.ClearReadyFlags();
        writer.ClearReferenceIdentityContractReady();
        writer.ClearHotspotFamilyReady();
        writer.ClearSqlGraphContractReady();
        writer.ClearMetadataTargetReady();
        if (hadCSharpStaticInterfaceContractsBeforePurge
            || csharpWorkspace.HasStaticInterfaceContracts)
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
        var purgedRefs = deferCSharpMutationsForIncompleteScan || startedWithNoIndexedFiles
            ? 0
            : writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages(projectPath));
        if (purgedRefs > 0)
            mutualRecursionRefreshNeeded = true;

        void DeferCSharpMutationsForStatRevalidation(
            in CSharpStaticInterfacePrepass.FileTarget target)
        {
            // The final global C# check passed, but this file changed before its
            // actual skip. Row mutations have already begun, so preserve every C#
            // row from this point, leave evidence unknown, and require a clean retry.
            // global最終check後にC# statが崩れた場合、以後のC# rowを保持してpartialにする。
            preservePriorPositiveCSharpSourceNoOp = false;
            deferCSharpMutationsForIncompleteScan = true;
            csharpSourceEvidenceForStamp = false;
            csharpSourceEvidenceComplete = false;
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            RecordCSharpWorkspaceFailure(
                target.IndexPath,
                "csharp_stat_revalidation",
                new IOException(
                    "A C# source changed after final workspace preflight; rerun indexing to refresh the complete C# graph."));
            try
            {
                var info = new FileInfo(target.FilePath);
                if (info.Exists && info.Length >= 0)
                    RememberReadableFileSize(target.FilePath, info.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
            }
        }

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
            StatMatchedFiles = statMatchedFiles,
            StatPreflightCompleted = statPreflightCompleted,
            SymbolKindFilter = symbolKindFilter,
            PostExtractionHooks = postExtractionHooks,
            GetCSharpWorkspace = () => csharpWorkspace,
            GetCSharpPrepassArtifacts = () => csharpPrepassSymbolArtifacts,
            DeferCSharpMutations = () => deferCSharpMutationsForIncompleteScan,
            PreservePriorPositiveCSharpSourceNoOp =
                () => preservePriorPositiveCSharpSourceNoOp,
            HasCSharpWorkspaceSnapshots = () => csharpWorkspaceFileSnapshots != null,
            GetStatMatchedFile = GetStatMatchedFile,
            LoadedCSharpWorkspaceSnapshotMatches = LoadedCSharpWorkspaceSnapshotMatches,
            DeferCSharpStatRevalidation = DeferCSharpMutationsForStatRevalidation,
            DeferCSharpLoadedSnapshotDrift = DeferCSharpMutationsForLoadedSnapshotDrift,
            RememberReadableFileSize = RememberReadableFileSize,
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
        // Workspace-drift callbacks still own their outer validation state and append
        // directly to the shared failure list. Per-file failures do the same through
        // the session, so the list remains the authoritative error count at the join.
        // workspace drift callback と file loop の双方が共有 failure list へ追記するため、
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

        csharpPrepassSymbolArtifacts?.Clear();
        csharpPrepassSymbolArtifacts = null;

        var referenceIdentityReadyForMutualRecursionRefresh =
            !deferCSharpMutationsForIncompleteScan && mutualRecursionRefreshNeeded
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
            !deferCSharpMutationsForIncompleteScan
            && TypeScriptAugmentationRefreshPolicy.ShouldRebuildReferences(
                symbolsOnly: false,
                canFinalize: !scanHadErrors && errors == 0,
                typeScriptAugmentationNeedsRefresh,
                typeScriptAugmentationDirtyNames?.RequiresRefresh == true,
                canStampReadyWithoutRebuild:
                    canStampTypeScriptAugmentationReadyWithoutRebuild);
        var deferMutualRecursionRefreshToTypeScriptAugmentation =
            mutualRecursionRefreshNeeded && willRebuildTypeScriptAugmentation;
        if (!deferCSharpMutationsForIncompleteScan
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
        if (errors == 0 && !deferCSharpMutationsForIncompleteScan)
        {
            McpIndexCSharpReadinessValidationForTesting?.Invoke();
            if (csharpWorkspaceFileSnapshots != null)
            {
                readinessStableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    csharpPrepassTargets,
                    csharpWorkspaceFileSnapshots,
                    out readinessChangedFilePath,
                    requestToken,
                    target => authorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            }
            else if (preservePriorPositiveCSharpSourceNoOp)
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
                DeferCSharpMutationsForLoadedSnapshotDrift(
                    FormatCSharpWorkspaceSnapshotPath(
                        readinessChangedFilePath
                        ?? readinessChangedScanInputPath
                        ?? "<csharp_workspace>"));
            }
        }
        // MCP index now runs ValidateContent + InsertIssues per file (bdbb2bd) on par with CLI
        // index, so stamp both graph-ready and issues-ready on clean runs — the old "graph only"
        // path is no longer accurate. Bits are only stamped when every file committed without
        // throwing, so a partial failure leaves trust degraded and `validate` still surfaces it.
        // MCP index は CLI と同等に file_issues を永続化するため、成功時は graph / issues の両方を stamp する。
        if (!deferCSharpMutationsForIncompleteScan
            && postExtractionHooks.ValueIfCreated?.SawCSharpStaticInterfaceSourceContract == true)
        {
            if (!csharpWorkspace.HasSourceStaticInterfaceContracts)
            {
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
        // The TypeScript augmentation rebuild performs the same graph refresh after adding
        // synthetic edges. If late input validation makes that rebuild ineligible, complete
        // the deferred pass before publishing partial metadata.
        // late validation で augmentation を実行できない場合だけ、partial metadata 前に補完する。
        var willRebuildTypeScriptAugmentationAfterReadinessValidation =
            !deferCSharpMutationsForIncompleteScan
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
                .Take(50)
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
            if (csharpSourceEvidenceComplete && !preservePriorPositiveCSharpSourceNoOp)
                writer.SetCSharpStaticInterfaceSourceEvidence(csharpSourceEvidenceForStamp);
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
            var bytesRead = knownReadableFileSizes.Count == files.Count
                ? (BytesRead: knownReadableBytesRead,
                    SkippedFileCount: knownReadableByteEstimateComplete ? 0L : 1L)
                : SumReadableFileBytes(
                        files,
                        projectPath,
                        indexRunDiagnostics,
                        mcpIndexDiagnostics,
                        authorizedRoot.EnsureAuthorizedEntry,
                        knownReadableFileSizes);
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
