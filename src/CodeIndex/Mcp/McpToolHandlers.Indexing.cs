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
    private async Task<JsonNode> ExecuteIndexAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
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
        var projectPath = Path.GetFullPath(indexOptions.Path);
        var runStartedAtUtc = GetUtcNow();
        var runStopwatch = Stopwatch.StartNew();
        var memorySamples = memoryTrace
            ? new JsonArray { CaptureMcpIndexMemorySample("start", runStopwatch) }
            : null;

        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!McpPathBoundary.IsPathWithinDirectory(cwd, projectPath))
            return CreateToolErrorResponse(id, "Path must be within the current working directory");
        await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        if (!IsPathWithinClientRoots(projectPath))
            return CreateToolErrorResponse(id, "Path must be within an MCP client root");

        if (!Directory.Exists(projectPath))
            return CreateToolErrorResponse(id, "Directory not found");

        var unsupportedModesJson = BuildMcpIndexUnsupportedModesJson(unsupportedModes);
        if (dryRun)
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, _currentRequestToken.Value);
            var dryRunIndexer = new FileIndexer(
                projectPath,
                ignoreCase,
                GitHelper.TryGetRepositoryRoot(projectPath, _currentRequestToken.Value) ?? Path.GetFullPath(projectPath),
                maxFileBytes,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: symlinkPolicy,
                generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment());
            var scan = dryRunIndexer.ScanFilesDetailed(cancellationToken: _currentRequestToken.Value);
            if (memorySamples != null)
                memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
            var dryRunFatalScanErrors = scan.Errors.Where(error => error.IsFatal).ToList();
            var dryRunPayload = new JsonObject
            {
                ["path"] = projectPath,
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

        // Reuse the per-session DbContext (issue #1494) instead of opening a fresh
        // connection on every index call. InitializeSchema below is idempotent so the
        // shared connection still picks up legacy-DB migrations on demand.
        // index 呼び出しごとに新しい接続を開かず、セッション共有 DbContext を再利用する（#1494）。
        // 後段の InitializeSchema は冪等なので共有接続でもレガシー DB の移行は正しく走る。
        var db = GetOrOpenSharedDb();
        var csharpMetadataTargetVersionMetaKey = DbContext.GetMetadataTargetVersionMetaKey("csharp");
        var priorMeta = db.GetMetaStrings(
        [
            "fold_key_version",
            "fold_key_fingerprint",
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            csharpMetadataTargetVersionMetaKey,
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.IndexedProjectRootMetaKey,
            IndexCommandRunner.SymbolKindFilterMetaKey,
        ]);
        var priorFoldVersion = priorMeta["fold_key_version"];
        var priorFoldFingerprint = priorMeta["fold_key_fingerprint"];
        var priorCSharpSymbolNameContractVersion = priorMeta[DbContext.CSharpSymbolNameContractVersionMetaKey];
        var priorMetadataTargetCsharp = priorMeta[csharpMetadataTargetVersionMetaKey];
        var priorSqlGraphContractVersion = priorMeta[DbContext.SqlGraphContractVersionMetaKey];
        var priorHotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyVersionMetaKey);
        var priorHotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey);
        var priorIndexedProjectRoot = priorMeta[DbContext.IndexedProjectRootMetaKey];
        var priorSymbolKindFilterSignature = priorMeta[IndexCommandRunner.SymbolKindFilterMetaKey];
        var requestToken = _currentRequestToken.Value;
        requestToken.ThrowIfCancellationRequested();
        // Capture git HEAD so subsequent queries can detect a worktree branch / HEAD switch
        // (`git switch other-branch` inside the worktree) without a `--check` workspace scan.
        // Like the CLI full-scan path, the value is only persisted at the end of a successful
        // run (errors == 0) so a crashed / partial index keeps the previous HEAD and surfaces
        // staleness until the next clean refresh. Issues #1508 and #1512.
        // worktree 内の HEAD 切替検出のため HEAD を捕捉。CLI full-scan と同じく成功時のみ
        // 書き込み、partial 失敗は旧 HEAD を残して次回 full scan で更新する。
        var currentHeadCommit = GitHelper.TryGetHeadCommit(projectPath, requestToken);

        // On --rebuild, clear readiness before DropAll so a crash during the window
        // (empty tables recreated, MarkReady not yet run) cannot leave old trust bits
        // blessing the freshly-empty tables. On non-rebuild runs, readiness is cleared
        // just before the first write below so a scan failure does not downgrade a
        // previously-healthy index.
        // --rebuild は DropAll 前に clear。通常は実書き込み直前で clear。
        if (rebuild)
        {
            db.ClearReadyFlags();
            var rebuildWriter = new DbWriter(db);
            rebuildWriter.ClearHotspotFamilyReady();
            rebuildWriter.ClearMetadataTargetReady();
            db.DropAll();
        }

        db.InitializeSchema();
        MarkSharedDbMigrated();

        var writer = new DbWriter(db);
        writer.RecoverInterruptedFtsBulkLoadIfNeeded();
        var indexer = new FileIndexer(
            projectPath,
            GitHelper.ResolveIgnoreCase(projectPath, requestToken),
            GitHelper.TryGetRepositoryRoot(projectPath, requestToken) ?? Path.GetFullPath(projectPath),
            maxFileBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: symlinkPolicy,
            generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment());
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
        var symbolKindFilterMetaMarkedIncomplete = symbolKindFilterMatchesPrior;
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectPath);
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = !projectRootWritten
            || !typeScriptAugmentationVersionMatchesCurrent;
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var ftsMutated = false;
        var startedWithNoIndexedFiles = !writer.HasAnyIndexedFiles();

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
            IReadOnlyDictionary<string, long>? knownFileSizes = null)
        {
            long total = 0;
            long skipped = 0;
            foreach (var filePath in paths)
            {
                if (knownFileSizes != null && knownFileSizes.TryGetValue(filePath, out var knownSize))
                {
                    total += knownSize;
                    continue;
                }

                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Exists)
                        total += info.Length;
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

        // First mutation point — demote readiness just before any write.
        // 実書き込み直前で readiness をクリア。
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();
        var useFullRunBatchMarker = rebuild || startedWithNoIndexedFiles;
        if (useFullRunBatchMarker)
            writer.MarkBatchInProgress();

        var hadCSharpStaticInterfaceContractsBeforePurge = !startedWithNoIndexedFiles
            && writer.HasCSharpStaticInterfaceContractSymbols(requestToken);

        // Purge stale files / 古いファイルをパージ
        var purged = startedWithNoIndexedFiles
            ? 0
            : writer.PurgeStaleFiles(projectPath, beforeCommit: RequireTypeScriptAugmentationRefresh);
        if (purged > 0)
        {
            csharpMetadataTargetsNeedRefresh = true;
            ftsMutated = true;
            WriteProjectRootOnce();
        }

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        if (!startedWithNoIndexedFiles)
            writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());

        // Scan and index / スキャン・インデックス
        var scanResult = indexer.ScanFilesDetailed(cancellationToken: requestToken);
        var scanHadErrors = scanResult.HadErrors;
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
        var knownReadableFileSizes = new Dictionary<string, long>(files.Count, StringComparer.Ordinal);
        long knownReadableBytesRead = 0;
        void RememberReadableFileSize(string path, long size)
        {
            if (knownReadableFileSizes.TryGetValue(path, out var priorSize))
                knownReadableBytesRead += size - priorSize;
            else
                knownReadableBytesRead += size;
            knownReadableFileSizes[path] = size;
        }
        await EmitProgressNotificationAsync(progressToken, 0, files.Count, "Index scan complete; indexing files.").ConfigureAwait(false);
        var reusableIndexedFileStats = !rebuild && !startedWithNoIndexedFiles
            ? writer.LoadReusableIndexedFileStats(
                maxSymbolsPerFile,
                maxReferencesPerFile,
                _currentRequestToken.Value)
            : null;
        Dictionary<string, IndexedFileStatReuseResult?>? csharpPrepassStatReuse = null;
        bool IsGeneratedExtractionSuppressed(CSharpStaticInterfacePrepass.FileTarget target)
            => target.GeneratedExtractionSuppressed == true;

        bool CanReuseCSharpPrepassTargetWithoutRead(CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (rebuild
                || startedWithNoIndexedFiles
                || !symbolKindFilterMatchesPrior
                || !csharpSymbolNameContractMatchesCurrent)
                return false;
            if (target.Language != "csharp")
                return false;

            var existingFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                reusableIndexedFileStats!,
                target.FilePath,
                target.IndexPath,
                target.Language,
                IsGeneratedExtractionSuppressed(target));
            if (existingFile == null)
            {
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

        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        if (csharpPrepassTargets.Count == 0)
        {
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            McpIndexCSharpPrepassForTesting?.Invoke();
            csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                writer,
                indexer,
                csharpPrepassTargets,
                includeExistingSymbols: !rebuild && !startedWithNoIndexedFiles,
                canReuseExistingSymbolsWithoutRead: CanReuseCSharpPrepassTargetWithoutRead,
                isGeneratedCodeExtractionSuppressed: IsGeneratedExtractionSuppressed,
                parallelism: 1,
                cancellationToken: requestToken);
        }
        if (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
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
        HashSet<string>? reusedHotspotFamilyLanguages = null;
        var indexedSymbolExtractorLanguages = new HashSet<string>(languageCounts.Count, StringComparer.Ordinal);
        var symbolsDroppedByKindFilter = 0;
        var mutualRecursionRefreshNeeded = false;
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
        using var ftsBulkLoad = FtsBulkLoadTriggerGuard.Start(writer, rebuild || startedWithNoIndexedFiles, () => ftsMutated);

        foreach (var target in fileTargets)
        {
            var filePath = target.FilePath;
            var fileBatchMarked = false;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                var allowStatReuse = !rebuild
                    && !startedWithNoIndexedFiles
                    && symbolKindFilterMatchesPrior
                    && (target.Language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                    && (target.Language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                    && (target.Language != "sql" || sqlGraphContractMatchesCurrent)
                    && AllowReuseWithCurrentHotspotFamilyTrust(target.Language, hotspotFamilyTrustMatchesCurrent);
                var statMatchedFile = !allowStatReuse
                    ? null
                    : target.Language == "csharp"
                      && csharpPrepassStatReuse != null
                      && csharpPrepassStatReuse.TryGetValue(target.IndexPath, out var cachedCSharpPrepassReuse)
                        ? cachedCSharpPrepassReuse
                        : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                            reusableIndexedFileStats!,
                            filePath,
                            target.IndexPath,
                            target.Language,
                            IsGeneratedExtractionSuppressed(target));
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
                var fileId = startedWithNoIndexedFiles
                    ? writer.InsertNewFile(record)
                    : writer.UpsertFile(record);
                var chunks = ChunkSplitter.SplitNormalized(fileId, content, loaded.HasOversizeLine, record.Lines);
                if (generatedSuppressionIssue != null)
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferences([], requestToken);
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
                    writer.InsertReferences([], requestToken);
                    InsertIssuesForIndexedFile(fileId, capIssues);
                }
                else
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols(symbols, requestToken);
                    List<ReferenceRecord> references;
                    FileIssue? regexTimeoutIssue;
                    using (var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction"))
                    {
                        references = ReferenceExtractor.ExtractNormalized(
                            fileId,
                            record.Lang,
                            content,
                            loaded.HasOversizeLine,
                            symbols,
                            record.Path,
                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                            requestToken,
                            maxReferenceCount: maxReferencesPerFile + 1,
                            conflictMarkerLine: loaded.ConflictMarkerLine);
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
                        writer.InsertReferencesForNewFiles(references, refreshMutualRecursionFlags: false, requestToken);
                    else
                        writer.InsertReferences(references, refreshMutualRecursionFlags: false, requestToken);
                    if (references.Count > 0)
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
            catch (FileIndexer.BinaryFileSkippedException ex)
            {
                try
                {
                    var skippedRecord = indexer.BuildSkippedFileRecord(filePath, target.RelativePath, target.Language);
                    RememberReadableFileSize(filePath, skippedRecord.Size);
                    if (skippedRecord.Lang == "csharp")
                        csharpMetadataTargetsNeedRefresh = true;
                    var skippedRecordRequiresTypeScriptAugmentationRefresh = skippedRecord.Lang == "typescript";
                    using var txn = writer.BeginTransaction(requestToken, "mcp index skipped binary");
                    if (skippedRecordRequiresTypeScriptAugmentationRefresh)
                        RequireTypeScriptAugmentationRefresh();
                    var fileId = startedWithNoIndexedFiles
                        ? writer.InsertNewFile(skippedRecord)
                        : writer.UpsertFile(skippedRecord);
                    writer.InsertChunks([], requestToken);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferences([], requestToken);
                    InsertIssuesForIndexedFile(fileId, [IndexCommandRunner.BuildNullByteIssue(ex)]);
                    WriteProjectRootOnce();
                    txn.Commit();
                    if (!string.IsNullOrWhiteSpace(skippedRecord.Lang))
                        indexedSymbolExtractorLanguages.Add(skippedRecord.Lang);
                    CountFreshInsertedRows();
                    ftsMutated = true;
                }
                catch (Exception cleanupEx)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "record_skipped_binary"));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();

                try
                {
                    var relativePath = FileIndexer.NormalizePathSeparators(
                        FileIndexer.GetRelativePathFromDirectory(projectPath, filePath));
                    if (writer.HasFileAtPath(relativePath))
                    {
                        using var txn = writer.BeginTransaction(requestToken, "mcp index delete missing file");
                        writer.DeleteFileByPath(relativePath);
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

        if (mutualRecursionRefreshNeeded)
        {
            requestToken.ThrowIfCancellationRequested();
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing reference graph.").ConfigureAwait(false);
            writer.RefreshMutualRecursionFlags(requestToken);
        }

        if (ftsBulkLoad != null)
        {
            ftsBulkLoad.Complete(ftsMutated, McpIndexFtsOptimizeForTesting);
        }
        else if (ftsMutated)
        {
            McpIndexFtsOptimizeForTesting?.Invoke();
            writer.OptimizeFts();
        }
        // MCP index now runs ValidateContent + InsertIssues per file (bdbb2bd) on par with CLI
        // index, so stamp both graph-ready and issues-ready on clean runs — the old "graph only"
        // path is no longer accurate. Bits are only stamped when every file committed without
        // throwing, so a partial failure leaves trust degraded and `validate` still surfaces it.
        // MCP index は CLI と同等に file_issues を永続化するため、成功時は graph / issues の両方を stamp する。
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
        if (!scanHadErrors && errors == 0)
        {
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing index metadata.").ConfigureAwait(false);
            if (!useFullRunBatchMarker)
                writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(requestToken, "mcp index readiness");
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkIndexReaderContractsReady(symbolsOnlyGraphOmitted: false);
            csharpSymbolNameReadyAfter = true;
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    McpIndexCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets();
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            sqlGraphContractReadyAfter = true;
            if (typeScriptAugmentationNeedsRefresh)
            {
                if (startedWithNoIndexedFiles && !hasTypeScriptTargets)
                {
                    writer.MarkTypeScriptAugmentationReady();
                }
                else
                {
                    McpIndexTypeScriptAugmentationRebuildForTesting?.Invoke();
                    var augmentationReferences = writer.RebuildTypeScriptAugmentationReferences(projectPath);
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
                ? (BytesRead: knownReadableBytesRead, SkippedFileCount: 0)
                : SumReadableFileBytes(files, projectPath, indexRunDiagnostics, mcpIndexDiagnostics, knownReadableFileSizes);
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
                (DbContext.LastIndexRunRowsDeletedMetaKey, purged.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
        if (!scanResult.HadErrors && errors == 0)
        {
            var plannerMaintenanceFailure = db.RunPlannerStatisticsMaintenance(forceAnalyze: false);
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
        if (!sqlGraphContractReadyAfter)
        {
            using var signalReader = new DbReader(writer.Connection);
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
