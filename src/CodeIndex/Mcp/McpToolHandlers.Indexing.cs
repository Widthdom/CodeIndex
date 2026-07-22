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
    internal static Action<string>? McpIndexInputSnapshotBarrierForTesting { get; set; }

    private async Task<JsonNode> ExecuteIndexAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        try
        {
            return await ExecuteIndexCoreAsync(id, args, progressToken).ConfigureAwait(false);
        }
        catch (McpIndexAuthorizationException ex)
        {
            return CreateIndexAuthorizationErrorResponse(id, ex);
        }
        catch (AggregateException ex) when (TryExtractIndexAuthorizationException(ex, out var authorizationException))
        {
            return CreateIndexAuthorizationErrorResponse(id, authorizationException);
        }
    }

    private JsonNode CreateIndexAuthorizationErrorResponse(
        JsonNode? id,
        McpIndexAuthorizationException exception)
        => CreateToolErrorResponse(
            id,
            "MCP index authorization changed after validation; indexing stopped.",
            category: McpErrorEnvelope.CategoryPermissionDenied,
            suggestion: "Restore a stable directory mapping within the current working directory and MCP client roots, then retry.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["authorization_failure_reason"] = exception.Reason,
                ["checked_root_identity"] = exception.CheckedRootIdentity,
            });

    private static bool TryExtractIndexAuthorizationException(
        AggregateException exception,
        out McpIndexAuthorizationException authorizationException)
    {
        foreach (var innerException in exception.Flatten().InnerExceptions)
        {
            if (innerException is McpIndexAuthorizationException matched)
            {
                authorizationException = matched;
                return true;
            }
        }

        authorizationException = null!;
        return false;
    }

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

        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!McpPathBoundary.IsPathWithinDirectory(cwd, requestedProjectPath))
            return CreateToolErrorResponse(id, "Path must be within the current working directory");
        await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        if (!IsPathWithinClientRoots(requestedProjectPath))
            return CreateToolErrorResponse(id, "Path must be within an MCP client root");

        bool IsPathAuthorized(string path)
            => McpPathBoundary.IsPathWithinDirectory(cwd, path) && IsPathWithinClientRoots(path);
        if (!McpPathBoundary.TryCaptureIndexRoot(
                requestedProjectPath,
                IsPathAuthorized,
                McpIndexEntryOpenBoundaryForTesting,
                McpIndexDirectoryEnumerationBoundaryForTesting,
                McpIndexDirectoryEnumerationCompletedForTesting,
                out var authorization,
                out var authorizationError))
        {
            return CreateToolErrorResponse(id, authorizationError!);
        }

        using var authorizedRoot = authorization!;
        using var authorizedExtractorConfiguration = ExtractorPluginRegistry.BeginAuthorizedConfigurationScope();
        if (_currentIndexAuditContext.Value is { } auditContext)
            auditContext.CheckedRootIdentity = authorizedRoot.CheckedRootIdentity;
        McpIndexAuthorizationCompletedForTesting?.Invoke();
        authorizedRoot.EnsureAuthorizedEntry(authorizedRoot.CanonicalPath);
        var projectPath = authorizedRoot.CanonicalPath;

        var unsupportedModesJson = BuildMcpIndexUnsupportedModesJson(unsupportedModes);
        if (dryRun)
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, _currentRequestToken.Value);
            var dryRunRepositoryRoot = GitHelper.TryGetRepositoryRoot(projectPath, _currentRequestToken.Value);
            var dryRunIgnoreRuleRoot = dryRunRepositoryRoot != null && IsPathAuthorized(dryRunRepositoryRoot)
                ? dryRunRepositoryRoot
                : projectPath;
            var dryRunIndexer = new FileIndexer(
                projectPath,
                ignoreCase,
                dryRunIgnoreRuleRoot,
                maxFileBytes,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: symlinkPolicy,
                generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment(),
                pathAccessValidator: authorizedRoot.EnsureAuthorizedEntry,
                openReadForIndexContent: authorizedRoot.OpenAuthorizedRead,
                enumerateFileSystemEntries: authorizedRoot.EnumerateAuthorizedFileSystemEntries,
                bindConfigurationReadsToFileSystemIdentity: true,
                internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(_dbPath));
            var scan = dryRunIndexer.ScanFilesDetailed(cancellationToken: _currentRequestToken.Value);
            if (memorySamples != null)
                memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
            var dryRunFatalScanErrors = scan.Errors.Where(error => error.IsFatal).ToList();
            var dryRunPayload = new JsonObject
            {
                ["path"] = projectPath,
                ["checked_root_identity"] = authorizedRoot.CheckedRootIdentity,
                ["dry_run"] = true,
                ["would_rebuild"] = rebuild,
                ["max_file_bytes"] = maxFileBytes,
                ["index_options"] = optionsPayload,
                ["unsupported_modes"] = unsupportedModesJson,
                ["summary"] = new JsonObject
                {
                    ["files_scanned"] = scan.Files.Count,
                    ["scan_errors"] = scan.Errors.Count,
                    ["fatal_scan_errors"] = dryRunFatalScanErrors.Count,
                    ["unknown_extension_file_count"] = scan.UnknownExtensionFiles.Count,
                    ["would_mutate_database"] = false,
                },
                ["duration_ms"] = runStopwatch.ElapsedMilliseconds,
                ["started_at"] = runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (memorySamples != null)
            {
                memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));
                dryRunPayload["memory_trace"] = memorySamples;
            }
            return CreateToolResult(id, "Index dry run complete.", dryRunPayload);
        }

        if (HasBlockingMcpIndexUnsupportedMode(unsupportedModes))
        {
            var unsupportedData = new JsonObject
            {
                ["unsupported_modes"] = unsupportedModesJson,
                ["index_options"] = optionsPayload,
                ["index_started"] = false,
                ["checked_root_identity"] = authorizedRoot.CheckedRootIdentity,
            };
            return CreateToolErrorResponse(
                id,
                "MCP index does not support the requested scoped or watch indexing mode; no indexing started.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use dryRun:true to inspect the plan, remove unsupported scope/watch arguments, or run the equivalent cdidx index command in the CLI.",
                retrySafe: false,
                extraData: unsupportedData);
        }

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
        var csharpMetadataTargetVersionMetaKey = DbContext.GetMetadataTargetVersionMetaKey("csharp");
        var priorMeta = db.GetMetaStrings(
        [
            "fold_key_version",
            "fold_key_fingerprint",
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey,
            csharpMetadataTargetVersionMetaKey,
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.SymbolsOnlyGraphOmittedMetaKey,
            DbContext.IndexCompletenessMetaKey,
            DbContext.IndexedProjectRootMetaKey,
            IndexCommandRunner.SymbolKindFilterMetaKey,
        ]);
        var priorFoldVersion = priorMeta["fold_key_version"];
        var priorFoldFingerprint = priorMeta["fold_key_fingerprint"];
        var priorCSharpSymbolNameContractVersion = priorMeta[DbContext.CSharpSymbolNameContractVersionMetaKey];
        var priorCSharpStaticInterfaceSourceEvidence =
            bool.TryParse(
                priorMeta[DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey],
                out var parsedCSharpStaticInterfaceSourceEvidence)
                ? parsedCSharpStaticInterfaceSourceEvidence
                : (bool?)null;
        var priorMetadataTargetCsharp = priorMeta[csharpMetadataTargetVersionMetaKey];
        var priorSqlGraphContractVersion = priorMeta[DbContext.SqlGraphContractVersionMetaKey];
        var priorSymbolsOnlyGraphOmitted = string.Equals(
            priorMeta[DbContext.SymbolsOnlyGraphOmittedMetaKey],
            "true",
            StringComparison.OrdinalIgnoreCase);
        var priorIndexComplete = string.Equals(
            priorMeta[DbContext.IndexCompletenessMetaKey],
            "complete",
            StringComparison.OrdinalIgnoreCase);
        var priorReadiness = db.GetUserVersion();
        var priorHotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyVersionMetaKey);
        var priorHotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey);
        var priorIndexedProjectRoot = priorMeta[DbContext.IndexedProjectRootMetaKey];
        var priorSymbolKindFilterSignature = priorMeta[IndexCommandRunner.SymbolKindFilterMetaKey];
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
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpMetadataTargetsNeedRefresh = priorMetadataTargetCsharp != currentMetadataTargetVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            symbolKindFilter.Signature,
            StringComparison.Ordinal);
        var priorFilterRetainedCSharpContractMembers =
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(
                priorSymbolKindFilterSignature);
        var symbolKindFilterMetaMarkedIncomplete = symbolKindFilterMatchesPrior;
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectPath);
        var csharpIndexedProjectRootCompatible = normalizedPriorIndexedProjectRoot == null
            || projectRootWritten;
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = rebuild
            || !projectRootWritten
            || !typeScriptAugmentationVersionMatchesCurrent;
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var ftsMutated = false;
        var startedWithNoIndexedFiles = rebuild || !writer.HasAnyIndexedFiles();
        if (rebuild || startedWithNoIndexedFiles)
            priorCSharpStaticInterfaceSourceEvidence = null;
        var requiresConservativeCSharpSourceRefresh = !rebuild
            && !startedWithNoIndexedFiles
            && priorCSharpStaticInterfaceSourceEvidence != false;
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
        var staleFilePurgePlan = startedWithNoIndexedFiles
            ? FilePurgePlan.Empty
            : writer.PlanStaleFiles(projectPath, cancellationToken: requestToken);
        var purged = staleFilePurgePlan.Count;
        McpIndexStaleFilePurgePlannedForTesting?.Invoke(purged);
        if (purged > 0)
            csharpMetadataTargetsNeedRefresh = true;

        // Load current reference-language support before the deferred mutation phase.
        // deferred mutation phase の前に現在の reference-language support を読み込む。
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectPath);
        var purgedRefs = 0;

        // Scan and index / スキャン・インデックス
        var scanWithDirectorySnapshots = indexer.ScanFilesDetailedWithDirectoryListingSnapshots(
            cancellationToken: requestToken);
        var scanResult = scanWithDirectorySnapshots.ScanResult;
        var scanInputSnapshot = scanWithDirectorySnapshots.InputSnapshot;
        var scanHadErrors = scanResult.HadErrors;
        var deferCSharpMutationsForIncompleteScan = !startedWithNoIndexedFiles
            && scanHadErrors
            && priorCSharpStaticInterfaceSourceEvidence != false;
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
        var files = scanResult.Files;
        var fileTargets = new CSharpStaticInterfacePrepass.FileTarget[files.Count];
        var languageCounts = scanResult.LanguageCounts;
        var csharpPrepassTargetCapacity = languageCounts.TryGetValue("csharp", out var csharpFileCount) ? csharpFileCount : 0;
        var csharpPrepassTargets = new List<CSharpStaticInterfacePrepass.FileTarget>(csharpPrepassTargetCapacity);
        var hasSqlTargets = languageCounts.ContainsKey("sql");
        var hasTypeScriptTargets = languageCounts.ContainsKey("typescript");
        var hasGeneratedCodeExtractionSuppressionPatterns = indexer.HasGeneratedCodeExtractionSuppressionPatterns;
        for (var i = 0; i < files.Count; i++)
        {
            var filePath = files[i];
            var language = FileIndexer.GetReusableDetectedLanguage(filePath, scanResult.FileLanguages);
            var target = CSharpStaticInterfacePrepass.FileTarget.Create(projectPath, filePath, language);
            target = target with
            {
                GeneratedExtractionSuppressed = hasGeneratedCodeExtractionSuppressionPatterns
                    && indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath)
            };
            fileTargets[i] = target;
            if (language == "csharp")
                csharpPrepassTargets.Add(target);
        }

        HashSet<string>? scanRetainedPaths = null;
        HashSet<string>? scanListedDirectories = null;
        HashSet<string>? scanAuthoritativeSubtreeDirectories = null;
        HashSet<string>? scanExplicitlyRemovedPaths = null;
        if (!startedWithNoIndexedFiles)
        {
            scanRetainedPaths = new HashSet<string>(fileTargets.Length, StringComparer.Ordinal);
            foreach (var target in fileTargets)
                scanRetainedPaths.Add(target.IndexPath);

            FilePurgePlan scanDerivedPurgePlan;
            if (scanHadErrors)
            {
                // A failed probe is not proof that a persisted row disappeared. Conversely,
                // a successful immediate-child listing is authoritative only for that parent,
                // while a fully scanned or deliberately pruned directory authorizes its subtree.
                // probe failureは既存row消滅の根拠にせず、partial scanのauthorityはlisted直下と
                // fully-scanned / deliberate-prune subtreeだけに厳密に限定する。
                scanRetainedPaths.UnionWith(
                    scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));
                scanListedDirectories = scanResult.ListedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                scanAuthoritativeSubtreeDirectories = scanResult.FullyScannedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                scanAuthoritativeSubtreeDirectories.UnionWith(
                    scanResult.AttributePrunedDirectories.Select(FileIndexer.NormalizeIndexPath));
                scanAuthoritativeSubtreeDirectories.UnionWith(
                    scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
                scanExplicitlyRemovedPaths = scanResult.NonIndexablePaths
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                scanDerivedPurgePlan = scanAuthoritativeSubtreeDirectories.Contains(string.Empty)
                    ? writer.PlanFilesOutsideRetainedSet(scanRetainedPaths, requestToken)
                    : writer.PlanFilesOutsideRetainedSetWithinListedDirectories(
                        scanRetainedPaths,
                        scanListedDirectories,
                        scanAuthoritativeSubtreeDirectories,
                        scanExplicitlyRemovedPaths,
                        requestToken);
            }
            else
            {
                // A clean recursive scan is authoritative for the complete retained set,
                // including files that still exist but became ignored or unsupported.
                // clean scanでは保持集合全体をauthorityとし、存在するignore/unsupported化も除外する。
                scanDerivedPurgePlan = writer.PlanFilesOutsideRetainedSet(
                    scanRetainedPaths,
                    requestToken);
            }

            if (scanDerivedPurgePlan.Count > 0)
            {
                var scanPlanContainsPreScanPlan = staleFilePurgePlan.Count <= scanDerivedPurgePlan.Count;
                for (var planIndex = 0;
                     scanPlanContainsPreScanPlan && planIndex < staleFilePurgePlan.FileIds.Count;
                     planIndex++)
                {
                    scanPlanContainsPreScanPlan = FilePurgePlan.ContainsSortedFileId(
                        scanDerivedPurgePlan.FileIds,
                        staleFilePurgePlan.FileIds[planIndex]);
                }

                // Preserve the immutable pre-scan plan when a path reappeared during scanning.
                // When the authoritative scan plan already contains every pre-scan ID, it is
                // the exact union and retains its deleted-byte estimate without double counting.
                // scan中に再出現したpathはpre-scan planを維持する。一方scan planが全IDを包含する
                // 場合はそれ自体が正確なunionなので、deleted-byte見積りを重複加算しない。
                staleFilePurgePlan = scanPlanContainsPreScanPlan
                    ? scanDerivedPurgePlan
                    : FilePurgePlan.Merge([staleFilePurgePlan, scanDerivedPurgePlan]);
            }
        }

        purged = staleFilePurgePlan.Count;
        if (purged > 0)
            csharpMetadataTargetsNeedRefresh = true;
        if (deferCSharpMutationsForIncompleteScan && staleFilePurgePlan.Count > 0)
        {
            // Do not combine an incomplete C# workspace with any planned deletion. A clean
            // retry can apply the same stale cleanup while rebuilding implicit references.
            // C# workspaceが不完全なrunではplanned deleteを延期し、clean retryへ委ねる。
            staleFilePurgePlan = FilePurgePlan.Empty;
            purged = 0;
        }
        var hadCSharpStaticInterfaceContractsBeforePurge = !startedWithNoIndexedFiles
            && staleFilePurgePlan.Count > 0
            && writer.HasCSharpFilesInFileIds(staleFilePurgePlan.FileIds, requestToken)
            && (priorCSharpStaticInterfaceSourceEvidence == true
                || writer.HasCSharpStaticInterfaceContractMembersInFileIds(
                    staleFilePurgePlan.FileIds,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        priorCSharpStaticInterfaceSourceEvidence == null
                        || !priorFilterRetainedCSharpContractMembers,
                    requestToken));
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
        HashSet<string>? retainedPathsForReuse = null;
        if (!rebuild
            && !startedWithNoIndexedFiles
            && staleFilePurgePlan.RemainingFileCount - fileTargets.LongLength > fileTargets.LongLength)
        {
            retainedPathsForReuse = scanRetainedPaths;
            McpIndexRetainedPathFilterAllocatedForTesting?.Invoke(fileTargets.Length);
        }
        await EmitProgressNotificationAsync(progressToken, 0, files.Count, "Index scan complete; indexing files.").ConfigureAwait(false);
        var csharpPositiveNoOpPolicyCandidate = priorCSharpStaticInterfaceSourceEvidence is not null
            && priorIndexComplete
            && (priorReadiness & DbContext.GraphReadyFlag) != 0
            && !scanHadErrors
            && !hadCSharpStaticInterfaceContractsBeforePurge
            && !priorSymbolsOnlyGraphOmitted
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
            if (!hasCSharpLanguageTransitions && IsExistingCSharpSymbolPathNowNonCSharp(indexPath))
                hasCSharpLanguageTransitions = true;
        }

        var reusableIndexedFileStats = !rebuild && !startedWithNoIndexedFiles
            ? writer.LoadReusableIndexedFileStats(
                maxSymbolsPerFile,
                maxReferencesPerFile,
                _currentRequestToken.Value,
                files.Count,
                retainedPathsForReuse,
                staleFilePurgePlan.FileIds,
                csharpPositiveNoOpPolicyCandidate
                    ? ObservePersistedCSharpPath
                    : null)
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
                || priorSymbolsOnlyGraphOmitted
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
                    csharpPrepassTargetCapacity,
                    StringComparer.Ordinal))[target.IndexPath] = null;
                return false;
            }

            (csharpPrepassStatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                csharpPrepassTargetCapacity,
                StringComparer.Ordinal))[target.IndexPath] = existingFile.Value;
            return true;
        }

        bool IsExistingCSharpSymbolPathNowNonCSharp(string indexPath)
        {
            var normalizedIndexPath = FileIndexer.NormalizeIndexPath(indexPath);
            var currentPath = Path.Combine(
                projectPath,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(normalizedIndexPath));
            if (scanResult.FileLanguages.TryGetValue(currentPath, out var currentLanguage))
                return currentLanguage != "csharp";

            if (scanRetainedPaths?.Contains(normalizedIndexPath) == true)
                return false;
            if (!scanHadErrors)
                return true;
            if (scanExplicitlyRemovedPaths?.Contains(normalizedIndexPath) == true)
                return true;

            var directory = GetIndexParentDirectory(normalizedIndexPath);
            if (scanListedDirectories?.Contains(directory) == true)
                return true;
            while (true)
            {
                if (scanAuthoritativeSubtreeDirectories?.Contains(directory) == true)
                    return true;
                if (directory.Length == 0)
                    break;
                directory = GetIndexParentDirectory(directory);
            }

            return false;
        }

        static string GetIndexParentDirectory(string path)
        {
            var separatorIndex = path.LastIndexOf('/');
            return separatorIndex >= 0 ? path[..separatorIndex] : string.Empty;
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
                csharpPrepassTargetCapacity,
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
                    isExistingSymbolPathExcluded: IsExistingCSharpSymbolPathNowNonCSharp,
                    cancellationToken: requestToken));
            forceFullCSharpRefreshFromInvalidatedNoOp =
                priorCSharpStaticInterfaceSourceEvidence == true
                || csharpWorkspace.HasStaticInterfaceContracts;
        }
        if (!csharpWorkspace.SourceContractEvidenceComplete)
        {
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
            ? priorCSharpStaticInterfaceSourceEvidence == true
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
            CSharpStaticInterfacePrepass.FileTarget target,
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
            || purged > 0
            || purgedRefs > 0;
        var freshCountFiles = 0L;
        var freshCountChunks = 0L;
        var freshCountSymbols = 0L;
        var freshCountReferences = 0L;
        void CountFreshInsertedRows(
            int chunkCount = 0,
            int symbolCount = 0,
            int referenceCount = 0)
        {
            if (!startedWithNoIndexedFiles)
                return;

            freshCountFiles++;
            freshCountChunks += chunkCount;
            freshCountSymbols += symbolCount;
            freshCountReferences += referenceCount;
        }

        IndexedFileStatReuseResult? GetStatMatchedFile(CSharpStaticInterfacePrepass.FileTarget target)
        {
            var allowStatReuse = !rebuild
                && !startedWithNoIndexedFiles
                && !priorSymbolsOnlyGraphOmitted
                && symbolKindFilterMatchesPrior
                && (target.Language != "csharp" || csharpIndexedProjectRootCompatible)
                && (target.Language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                && (target.Language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                && (target.Language != "sql" || sqlGraphContractMatchesCurrent)
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
                        IsGeneratedExtractionSuppressed(target));
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
                        isExistingSymbolPathExcluded: IsExistingCSharpSymbolPathNowNonCSharp,
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
                        priorCSharpStaticInterfaceSourceEvidence == true
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

            var hasCSharpFiles = writer.HasAnyFilesWithLanguage("csharp");
            var hasSqlFiles = writer.HasAnyFilesWithLanguage("sql");
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
                ["sql_graph_contract_ready"] = !hasSqlFiles || sqlGraphContractMatchesCurrent,
                ["csharp_symbol_name_ready"] = !hasCSharpFiles || csharpSymbolNameContractMatchesCurrent,
                ["csharp_metadata_target_ready"] = !hasCSharpFiles || priorMetadataTargetCsharp == currentMetadataTargetVersion,
                ["fold_ready"] = (priorReadiness & DbContext.FoldReadyFlag) != 0,
                ["fold_ready_reason"] = (priorReadiness & DbContext.FoldReadyFlag) != 0
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
                issuesStateAvailable: (priorReadiness & DbContext.IssuesReadyFlag) != 0);
            AddReferenceGraphCompletenessSignal(structured, referenceExtractionCapHits);
            if (hasSqlFiles && !sqlGraphContractMatchesCurrent)
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
        using var referenceGraphRefresh = writer.BeginReferenceGraphRefreshScope(
            rebuild || !writer.HasAnyIndexedFiles());
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh();
        writer.RecoverInterruptedFtsBulkLoadIfNeeded(requestToken);
        if (!preservePriorPositiveCSharpSourceNoOp)
        {
            writer.SetCSharpStaticInterfaceSourceEvidence(
                csharpSourceEvidenceComplete && csharpSourceEvidenceForStamp ? true : null);
        }
        writer.ClearReadyFlags();
        writer.ClearReferenceIdentityContractReady();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();
        if (hadCSharpStaticInterfaceContractsBeforePurge
            || csharpWorkspace.HasStaticInterfaceContracts)
            writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
        if (useFullRunBatchMarker)
            writer.MarkBatchInProgress();

        using var ftsBulkLoad = FtsBulkLoadTriggerGuard.Start(writer, useFtsBulkLoad, () => ftsMutated);

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
        purgedRefs = deferCSharpMutationsForIncompleteScan || startedWithNoIndexedFiles
            ? 0
            : writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages(projectPath));
        if (purgedRefs > 0)
            mutualRecursionRefreshNeeded = true;

        for (var targetIndex = 0; targetIndex < fileTargets.Length; targetIndex++)
        {
            var target = fileTargets[targetIndex];
            var filePath = target.FilePath;
            var fileBatchMarked = false;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                authorizedRoot.EnsureAuthorizedEntry(filePath);
                if (deferCSharpMutationsForIncompleteScan
                    && target.Language == "csharp")
                {
                    try
                    {
                        var info = new FileInfo(filePath);
                        if (info.Exists && info.Length >= 0)
                            RememberReadableFileSize(filePath, info.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                    }
                    skipped++;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }
                IndexedFileStatReuseResult? statMatchedFile = null;
                if (statPreflightCompleted != null)
                {
                    if (statPreflightCompleted[targetIndex])
                    {
                        var cachedStatMatch = statMatchedFiles![targetIndex];
                        if (cachedStatMatch != null)
                        {
                            // The dirty-byte preflight may span thousands of later files. Re-stat
                            // cached matches at their actual skip point so that window cannot turn
                            // a changed file into a clean-run stale-row skip. This intentionally
                            // bypasses the C# prepass cache too.
                            // dirty-byte preflight 後の skip 時点で再 stat し、後続 file の
                            // preflight 中に変化した row を stale のまま再利用しない。
                            statMatchedFile = reuseFinalCSharpStatAtActualSkip
                                              && target.Language == "csharp"
                                ? cachedStatMatch
                                : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                                    reusableIndexedFileStats!,
                                    target.FilePath,
                                    target.IndexPath,
                                    target.Language,
                                    IsGeneratedExtractionSuppressed(target));
                        }
                    }
                    else
                    {
                        statMatchedFile = GetStatMatchedFile(target);
                    }
                }
                if (preservePriorPositiveCSharpSourceNoOp
                    && target.Language == "csharp"
                    && statMatchedFile == null)
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
                        var info = new FileInfo(filePath);
                        if (info.Exists && info.Length >= 0)
                            RememberReadableFileSize(filePath, info.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                    }
                    skipped++;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }
                if (statMatchedFile != null)
                {
                    skipped++;
                    processed++;
                    RememberReadableFileSize(filePath, statMatchedFile.Value.Size);
                    if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(target.Language) && target.Language != null)
                    {
                        reusedHotspotFamilyLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                        reusedHotspotFamilyLanguages.Add(target.Language);
                    }
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }

                McpIndexFileContentLoadForTesting?.Invoke(target.IndexPath);
                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                    filePath,
                    target.RelativePath,
                    target.Language,
                    requestToken);
                var record = loaded.Record;
                if (!LoadedCSharpWorkspaceSnapshotMatches(target, record))
                {
                    RememberReadableFileSize(filePath, record.Size);
                    skipped++;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }
                RememberReadableFileSize(filePath, record.Size);
                var content = loaded.Content;
                var rawBytes = loaded.RawBytes;
                var generatedSuppressionIssue = IsGeneratedExtractionSuppressed(target)
                    ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                    : null;
                var existingId = writer.GetReusableUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    size: record.Size,
                    lines: record.Lines,
                    language: record.Lang,
                    generated: record.Generated,
                    maxSymbolsPerFile: maxSymbolsPerFile,
                    maxReferencesPerFile: maxReferencesPerFile,
                    generatedExtractionSuppressed: generatedSuppressionIssue != null,
                    allowReuse: !rebuild
                        && !startedWithNoIndexedFiles
                        && symbolKindFilterMatchesPrior
                        && (record.Lang != "csharp" || csharpIndexedProjectRootCompatible)
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                        && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                if (existingId != null)
                {
                    skipped++;
                    processed++;
                    if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang) && record.Lang != null)
                    {
                        reusedHotspotFamilyLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                        reusedHotspotFamilyLanguages.Add(record.Lang);
                    }
                    continue;
                }

                if (!useFullRunBatchMarker)
                {
                    writer.MarkBatchInProgress();
                    fileBatchMarked = true;
                }
                MarkSymbolKindFilterMetaIncompleteOnce();
                if (record.Lang == "csharp")
                    csharpMetadataTargetsNeedRefresh = true;
                var recordRequiresTypeScriptAugmentationRefresh = record.Lang == "typescript";
                using var txn = writer.BeginTransaction(requestToken, "mcp index file");
                if (recordRequiresTypeScriptAugmentationRefresh)
                    RequireTypeScriptAugmentationRefresh();
                var referenceIdentityChanged = false;
                var fileId = startedWithNoIndexedFiles
                    ? writer.InsertNewFile(record)
                    : writer.UpsertFile(record, out referenceIdentityChanged);
                if (referenceIdentityChanged)
                    mutualRecursionRefreshNeeded = true;
                var chunks = ChunkSplitter.SplitNormalized(fileId, content, loaded.HasOversizeLine, record.Lines);
                if (generatedSuppressionIssue != null)
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferencesInAtomicFileScope([], requestToken);
                    var issues = IndexCommandRunner.AppendIssueIfMissing(
                        FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine, loaded.ConflictMarkerLine),
                        generatedSuppressionIssue);
                    InsertIssuesForIndexedFile(fileId, issues);
                    WriteProjectRootOnce();
                    if (!useFullRunBatchMarker)
                        writer.ClearBatchInProgress();
                    txn.Commit();
                    CountFreshInsertedRows(chunkCount: chunks.Count);
                    ftsMutated = true;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    McpIndexFileCommittedForTesting?.Invoke(record.Path);
                    continue;
                }
                List<SymbolRecord> symbols;
                FileIssue? symbolRegexTimeoutIssue;
                using (var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "symbol_extraction"))
                {
                    symbols = SymbolExtractor.ExtractNormalized(
                        fileId,
                        record.Lang,
                        content,
                        loaded.HasOversizeLine,
                        filePath,
                        projectPath,
                        requestToken,
                        loaded.ConflictMarkerLine,
                        patternConfigsAlreadyLoaded: true);
                    symbolRegexTimeoutIssue = IndexCommandRunner.BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                }
                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                var fileContext = new FileContext(projectPath, record.Path, filePath, record.Lang);
                postExtractionHooks.Value.OnSymbolsExtracted(fileContext, symbols);
                symbolsDroppedByKindFilter += symbolKindFilter.Apply(symbols);
                var committedChunkCount = 0;
                var committedSymbolCount = 0;
                var committedReferenceCount = 0;
                if (symbols.Count > maxSymbolsPerFile)
                {
                    var issue = BuildMcpSymbolCountExceededIssue(record.Path, symbols.Count, maxSymbolsPerFile);
                    IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                        ? [issue]
                        : IndexCommandRunner.AppendIssue([symbolRegexTimeoutIssue], issue);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferencesInAtomicFileScope([], requestToken);
                    InsertIssuesForIndexedFile(fileId, capIssues);
                }
                else
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols(symbols, requestToken);
                    List<ReferenceRecord> references;
                    ReferenceExtractionResult referenceExtraction;
                    FileIssue? regexTimeoutIssue;
                    using (var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction"))
                    {
                        referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                            fileId,
                            record.Lang,
                            content,
                            loaded.HasOversizeLine,
                            symbols,
                            record.Path,
                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                            requestToken,
                            maxReferenceCount: maxReferencesPerFile + 1,
                            conflictMarkerLine: loaded.ConflictMarkerLine,
                            workspaceRoot: projectPath,
                            csharpStaticInterfaceMemberLookups: csharpWorkspace.StaticInterfaceMemberLookups);
                        references = referenceExtraction.References;
                        regexTimeoutIssue = IndexCommandRunner.BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                    }
                    postExtractionHooks.Value.OnReferencesExtracted(fileContext, references);
                    FileIssue? referenceCapIssue = null;
                    if (references.Count > maxReferencesPerFile)
                    {
                        referenceCapIssue = BuildMcpReferenceCountExceededIssue(record.Path, references.Count, maxReferencesPerFile);
                        references = [];
                    }
                    if (startedWithNoIndexedFiles)
                        writer.InsertReferencesForNewFilesInAtomicFileScope(references, refreshMutualRecursionFlags: false, requestToken);
                    else
                        writer.InsertReferencesInAtomicFileScope(references, refreshMutualRecursionFlags: false, requestToken);
                    if (symbols.Count > 0 || references.Count > 0)
                        mutualRecursionRefreshNeeded = true;
                    committedChunkCount = chunks.Count;
                    committedSymbolCount = symbols.Count;
                    committedReferenceCount = references.Count;
                    // Keep MCP index parity with CLI index: persist file-level validation issues too.
                    // MCPインデックスもCLIインデックスと同等に、ファイル検証issueを保存する。
                    IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine, loaded.ConflictMarkerLine);
                    if (symbolRegexTimeoutIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, symbolRegexTimeoutIssue);
                    if (regexTimeoutIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, regexTimeoutIssue);
                    issues = IndexCommandRunner.AppendReferenceExtractionDiagnosticIssues(
                        issues,
                        record.Path,
                        referenceExtraction.Diagnostics);
                    if (referenceCapIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, referenceCapIssue);
                    InsertIssuesForIndexedFile(fileId, issues);
                }
                WriteProjectRootOnce();
                if (!useFullRunBatchMarker)
                    writer.ClearBatchInProgress();
                txn.Commit();
                if (!string.IsNullOrWhiteSpace(record.Lang))
                    indexedSymbolExtractorLanguages.Add(record.Lang);
                CountFreshInsertedRows(committedChunkCount, committedSymbolCount, committedReferenceCount);
                ftsMutated = true;
                McpIndexFileCommittedForTesting?.Invoke(record.Path);
            }
            catch (McpIndexAuthorizationException)
            {
                if (fileBatchMarked || useFullRunBatchMarker)
                    writer.ClearBatchInProgress();
                throw;
            }
            catch (FileIndexer.BinaryFileSkippedException ex)
            {
                try
                {
                    var skippedRecord = indexer.BuildSkippedFileRecord(filePath, target.RelativePath, target.Language);
                    RememberReadableFileSize(filePath, skippedRecord.Size);
                    if (!LoadedCSharpWorkspaceSnapshotMatches(target, skippedRecord))
                    {
                        skipped++;
                    }
                    else
                    {
                        if (skippedRecord.Lang == "csharp")
                            csharpMetadataTargetsNeedRefresh = true;
                        var skippedRecordRequiresTypeScriptAugmentationRefresh = skippedRecord.Lang == "typescript";
                        using var txn = writer.BeginTransaction(requestToken, "mcp index skipped binary");
                        if (skippedRecordRequiresTypeScriptAugmentationRefresh)
                            RequireTypeScriptAugmentationRefresh();
                        var referenceIdentityChanged = false;
                        var fileId = startedWithNoIndexedFiles
                            ? writer.InsertNewFile(skippedRecord)
                            : writer.UpsertFile(skippedRecord, out referenceIdentityChanged);
                        if (referenceIdentityChanged)
                            mutualRecursionRefreshNeeded = true;
                        writer.InsertChunks([], requestToken);
                        writer.InsertSymbols([], requestToken);
                        writer.InsertReferencesInAtomicFileScope([], requestToken);
                        InsertIssuesForIndexedFile(fileId, [IndexCommandRunner.BuildNullByteIssue(ex)]);
                        WriteProjectRootOnce();
                        txn.Commit();
                        if (!string.IsNullOrWhiteSpace(skippedRecord.Lang))
                            indexedSymbolExtractorLanguages.Add(skippedRecord.Lang);
                        CountFreshInsertedRows();
                        ftsMutated = true;
                    }
                }
                catch (Exception cleanupEx) when (cleanupEx is not McpIndexAuthorizationException)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "record_skipped_binary"));
                }
            }
            catch (FileIndexer.FileTooLargeSkippedException ex)
            {
                try
                {
                    var skippedRecord = indexer.BuildSkippedFileRecord(filePath, target.RelativePath, target.Language);
                    RememberReadableFileSize(filePath, skippedRecord.Size);
                    if (!LoadedCSharpWorkspaceSnapshotMatches(target, skippedRecord))
                    {
                        skipped++;
                    }
                    else
                    {
                        if (skippedRecord.Lang == "csharp")
                            csharpMetadataTargetsNeedRefresh = true;
                        var skippedRecordRequiresTypeScriptAugmentationRefresh = skippedRecord.Lang == "typescript";
                        using var txn = writer.BeginTransaction(requestToken, "mcp index skipped oversized file");
                        if (skippedRecordRequiresTypeScriptAugmentationRefresh)
                            RequireTypeScriptAugmentationRefresh();
                        var referenceIdentityChanged = false;
                        var fileId = startedWithNoIndexedFiles
                            ? writer.InsertNewFile(skippedRecord)
                            : writer.UpsertFile(skippedRecord, out referenceIdentityChanged);
                        if (referenceIdentityChanged)
                            mutualRecursionRefreshNeeded = true;
                        writer.InsertChunks([], requestToken);
                        writer.InsertSymbols([], requestToken);
                        writer.InsertReferencesInAtomicFileScope([], requestToken);
                        InsertIssuesForIndexedFile(fileId,
                        [
                            new FileIssue
                            {
                                Path = ex.RelativePath,
                                Kind = "file_too_large",
                                Line = 0,
                                Message = CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                            }
                        ]);
                        WriteProjectRootOnce();
                        txn.Commit();
                        if (!string.IsNullOrWhiteSpace(skippedRecord.Lang))
                            indexedSymbolExtractorLanguages.Add(skippedRecord.Lang);
                        CountFreshInsertedRows();
                        ftsMutated = true;
                    }
                }
                catch (Exception cleanupEx) when (cleanupEx is not McpIndexAuthorizationException)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "record_skipped_oversized_file"));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();

                if (target.Language == "csharp" && csharpWorkspaceFileSnapshots != null)
                {
                    DeferCSharpMutationsForLoadedSnapshotDrift(target.DisplayRelativePath);
                    skipped++;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var relativePath = FileIndexer.NormalizePathSeparators(
                        FileIndexer.GetRelativePathFromDirectory(projectPath, filePath));
                    if (writer.HasFileAtPath(relativePath))
                    {
                        using var txn = writer.BeginTransaction(requestToken, "mcp index delete missing file");
                        writer.DeleteFileByPath(relativePath);
                        mutualRecursionRefreshNeeded = true;
                        csharpMetadataTargetsNeedRefresh = true;
                        RequireTypeScriptAugmentationRefresh();
                        WriteProjectRootOnce();
                        txn.Commit();
                        ftsMutated = true;
                    }
                }
                catch (Exception cleanupEx)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "delete_missing_file"));
                }
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                if (fileBatchMarked || useFullRunBatchMarker)
                    writer.ClearBatchInProgress();
                throw;
            }
            catch (Exception ex)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();
                errors++;
                failures.Add(BuildIndexFileFailure(projectPath, filePath, ex, "index_file"));
            }
            processed++;
            await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
        }

        if (!deferCSharpMutationsForIncompleteScan && mutualRecursionRefreshNeeded)
        {
            requestToken.ThrowIfCancellationRequested();
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing reference graph.").ConfigureAwait(false);
            writer.RefreshMutualRecursionFlags(requestToken);
        }

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
        var useFreshTargetLanguages = startedWithNoIndexedFiles && !scanHadErrors && errors == 0;
        var hasCSharpFilesAfter = useFreshTargetLanguages
            ? csharpPrepassTargets.Count > 0
            : writer.HasAnyFilesWithLanguage("csharp");
        var hasSqlFilesAfter = useFreshTargetLanguages
            ? hasSqlTargets
            : writer.HasAnyFilesWithLanguage("sql");
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
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing index metadata.").ConfigureAwait(false);
            if (!useFullRunBatchMarker)
                writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(requestToken, "mcp index readiness");
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkIndexReaderContractsReady(symbolsOnlyGraphOmitted: false);
            if (csharpSourceEvidenceComplete && !preservePriorPositiveCSharpSourceNoOp)
                writer.SetCSharpStaticInterfaceSourceEvidence(csharpSourceEvidenceForStamp);
            if (!mutualRecursionRefreshNeeded && referenceIdentityContractMatchedBeforeMutation)
                writer.MarkReferenceIdentityContractReady();
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
            if (typeScriptAugmentationNeedsRefresh
                || typeScriptAugmentationDirtyNames?.RequiresRefresh == true)
            {
                if (startedWithNoIndexedFiles && !hasTypeScriptTargets)
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
                        requestToken);
                    if (startedWithNoIndexedFiles)
                        freshCountReferences += augmentationReferences;
                }
            }
            RestampHotspotFamilyTrust(
                writer,
                reusedHotspotFamilyLanguages,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // FoldReady must reflect reality (#86). Like CLI full-scan, MCP index_project skips
            // unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows keep NULL
            // name_folded / *_folded. Stamp only when every row is backfilled; otherwise readers
            // would silently miss legacy rows on the folded-equality path. Codex #86 review.
            // MCP も incremental で skip される legacy 行が残るため、実検証を通してから stamp。
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent && foldFingerprintMatchesCurrent;
            if (skipped == 0 || canRestampExistingFoldTrust)
            {
                // The stamp transaction performs the only row verification for the common
                // current-metadata path and reports whether NULL or stale values blocked it.
                // current metadata 経路の row 検証は stamp transaction 内の一度だけにまとめ、
                // NULL と stale value のどちらが妨げたかも保持する。
                var foldStampResult = writer.MarkFoldReadyWithResult(
                    stampCurrentSymbolExtractorVersions: skipped == 0,
                    symbolExtractorLanguagesToStamp: skipped == 0 ? indexedSymbolExtractorLanguages : null);
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
                    StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary)));
            writer.MarkIndexComplete();
            writer.ClearLastFailedIndexRunMetadata();
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // Mirrors the CLI full-scan contract (Issue #1508) so MCP-driven re-indexes also
            // refresh `worktree_head_changed`; partial / failed runs leave the prior HEAD
            // untouched and surface staleness until the next clean refresh. Issues #1508 / #1512.
            // CLI full-scan と同じく成功時のみ HEAD を記録する。partial / 失敗は旧 HEAD を残す。
            var currentHeadBranch = GitHelper.TryGetHeadBranch(projectPath, requestToken);
            writer.SetMetaValues(
                (DbContext.IndexedHeadCommitMetaKey, currentHeadCommit),
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
        hotspotAggregateRefresh.Complete(requestToken);
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
                ["skipped"] = skipped,
                ["purged"] = purged,
                ["unknown_extension_file_count"] = scanResult.UnknownExtensionFiles.Count,
                ["errors"] = errors,
                ["failed_count"] = failures.Count,
                ["symbols_dropped_by_kind_filter"] = symbolsDroppedByKindFilter
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
            ["sql_graph_contract_ready"] = sqlGraphContractReadyAfter,
            ["csharp_symbol_name_ready"] = csharpSymbolNameReadyAfter,
            ["csharp_metadata_target_ready"] = csharpMetadataTargetReadyAfter,
            // #86 codex review: AI clients use this to tell whether --exact will use the
            // Unicode fold path or silently fall back to ASCII NOCASE. If false after a clean
            ["fold_ready"] = foldReadyAfter,
            ["fold_ready_reason"] = foldReadyReason
        };
        if (memorySamples != null)
            structured["memory_trace"] = memorySamples;
        if (failures.Count > 0)
        {
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
            GlobalToolLog.Error(
                $"mcp_index_file_failures count={failures.Count} first_path={QuoteMcpIndexFailureLogValue(failures[0].Path)} first_error={QuoteMcpIndexFailureLogValue($"{failures[0].ExceptionType}: {failures[0].Message}")}");
        }
        AddMcpIndexDiagnostics(structured, failures, mcpIndexDiagnostics);
        using var signalReader = new DbReader(writer.Connection);
        AddReferenceGraphCompletenessSignal(structured, signalReader);
        if (!sqlGraphContractReadyAfter)
        {
            AddSqlGraphContractSignal(structured, signalReader.GetSqlGraphContractSignal());
        }
        return CreateToolResult(id,
            errors == 0 && !foldReadyAfter
                ? foldReadyReason switch
                {
                    "stale_fold_key_version" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry an older fold-key version. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "stale_fold_key_fingerprint" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry folded keys generated under an older runtime fingerprint. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "missing_fold_backfill" => "Indexing complete. Note: --exact Unicode fold path not active because legacy rows without name_folded remain. Run backfill_fold to upgrade without reparsing files, or do a full rebuild.",
                    _ => "Indexing complete. Note: --exact Unicode fold path not active."
                }
                : "Indexing complete.",
            structured);
    }

    private static IndexFileFailure BuildIndexFileFailure(string projectPath, string filePath, Exception ex, string stage)
    {
        var relativePath = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectPath, filePath));
        var message = BuildSanitizedIndexFileFailureMessage(stage, ex.GetType().Name, out var messageTruncated);
        return new IndexFileFailure(relativePath, stage, ex.GetType().Name, message, messageTruncated);
    }

    private static IndexFileFailure BuildScanFailure(FileIndexer.ScanError error)
    {
        var message = SanitizeAndCapMcpIndexFailureMessage(error.Message, out var messageTruncated);
        return new IndexFileFailure(
            FileIndexer.NormalizePathSeparators(error.Path),
            "scan",
            nameof(FileIndexer.ScanError),
            message,
            messageTruncated);
    }

    private static McpIndexDiagnostic BuildMcpIndexExceptionDiagnostic(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
    {
        var path = SanitizeMcpIndexDiagnosticPath(projectRoot, filePath);
        var exceptionType = SanitizeMcpIndexFailureToken(ex.GetType().Name, "Exception");
        var message = SanitizeAndCapMcpIndexFailureMessage(
            DiagnosticRedactor.FormatExceptionMessage(ex, MaxMcpIndexFailureMessageLength),
            out var messageTruncated);
        return new McpIndexDiagnostic(code, category, path, stage, exceptionType, message, messageTruncated);
    }

    internal static JsonObject BuildMcpIndexExceptionDiagnosticForTesting(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
        => BuildMcpIndexDiagnosticJson(BuildMcpIndexExceptionDiagnostic(
            code,
            category,
            stage,
            projectRoot,
            filePath,
            ex));

    private static string SanitizeMcpIndexDiagnosticPath(string projectRoot, string path)
    {
        try
        {
            var relative = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
            if (!string.IsNullOrWhiteSpace(relative)
                && relative != "."
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return McpBoundedText.ForDisplay(relative, 256).Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return "<redacted>";
    }

    private static void AddMcpIndexDiagnostics(
        JsonObject structured,
        IReadOnlyList<IndexFileFailure> failures,
        IReadOnlyList<McpIndexDiagnostic> diagnostics)
    {
        var total = failures.Count + diagnostics.Count;
        if (total == 0)
            return;

        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new JsonArray();
        var emitted = 0;
        foreach (var failure in failures)
        {
            var diagnostic = new McpIndexDiagnostic(
                "recoverable_index_error",
                "recoverable_index_error",
                failure.Path,
                failure.Stage,
                failure.ExceptionType,
                failure.Message,
                failure.MessageTruncated);
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        foreach (var diagnostic in diagnostics)
        {
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        var categoryJson = new JsonObject();
        foreach (var entry in categories.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            categoryJson[entry.Key] = entry.Value;

        structured["diagnostics"] = new JsonObject
        {
            ["total_count"] = total,
            ["sample_count"] = emitted,
            ["truncated"] = total > emitted,
            ["categories"] = categoryJson,
            ["items"] = items,
        };
    }

    private static void AddMcpIndexDiagnosticCategory(Dictionary<string, int> categories, string category)
        => categories[category] = categories.TryGetValue(category, out var count) ? count + 1 : 1;

    private static JsonObject BuildMcpIndexDiagnosticJson(McpIndexDiagnostic diagnostic)
        => new()
        {
            ["code"] = diagnostic.Code,
            ["category"] = diagnostic.Category,
            ["path"] = diagnostic.Path,
            ["stage"] = diagnostic.Stage,
            ["exception_type"] = diagnostic.ExceptionType,
            ["message"] = diagnostic.Message,
            ["message_truncated"] = diagnostic.MessageTruncated,
        };

    internal static string BuildSanitizedIndexFileFailureMessageForTesting(string stage, string exceptionType, out bool messageTruncated) =>
        BuildSanitizedIndexFileFailureMessage(stage, exceptionType, out messageTruncated);

    internal static string SanitizeMcpIndexFailureMessageForTesting(string message, out bool messageTruncated) =>
        SanitizeAndCapMcpIndexFailureMessage(message, out messageTruncated);


}
