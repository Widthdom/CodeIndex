using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static int RunUpdateMode(
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
        bool priorSymbolsOnlyGraphOmitted,
        string? priorFoldVersion,
        string? priorFoldFingerprint,
        bool priorSymbolExtractorVersionsMatchCurrent,
        string? priorCSharpSymbolNameContractVersion,
        string? priorMetadataTargetCsharp,
        string? priorSqlGraphContractVersion,
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

        var resolveTargetsExitCode = TryResolveUpdateTargets(
            projectRoot,
            options,
            spinnerFrames,
            jsonOptions,
            cancellationToken,
            out var targetPaths,
            out var relevantIgnoreFileChanged);
        if (resolveTargetsExitCode != null)
            return resolveTargetsExitCode.Value;

        var typeScriptJavaScriptConfigChanged = ContainsJavaScriptTypeScriptConfigPath(targetPaths);
        if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(targetPaths) || typeScriptJavaScriptConfigChanged)
        {
            if (!options.Json && !options.Quiet)
            {
                var reason = typeScriptJavaScriptConfigChanged
                    ? "JavaScript/TypeScript config changes"
                    : "ignore-file changes";
                Console.WriteLine($"  Detected {reason}; falling back to a full scan to keep the index aligned.");
                Console.WriteLine();
            }

            return RunFullScan(
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
                priorSymbolsOnlyGraphOmitted,
                priorFoldVersion,
                priorFoldFingerprint,
                priorSymbolExtractorVersionsMatchCurrent,
                priorCSharpSymbolNameContractVersion,
                priorMetadataTargetCsharp,
                priorSqlGraphContractVersion,
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
                cancellationToken,
                forceJavaScriptTypeScriptRefresh: typeScriptJavaScriptConfigChanged);
        }

        if (!options.Json && !options.Quiet)
            Console.WriteLine($"Updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        CancellationTokenSource? updateCts = null;
        var interactiveUpdateSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        int updated = 0, removed = 0, skipped = 0, warnings = 0, errors = 0;
        var errorList = new List<CliJsonMessage>();
        var warningList = new List<CliJsonMessage>();
        var knownReadableFileSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        warnings += AddProjectMarkerFingerprintWarnings(currentHotspotFamilyMarkerFingerprints, warningList, options);
        var scanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIndexer.FileIdentity>();
        var readinessDemoted = false;
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = !options.SymbolsOnly
            && (!projectRootWritten || !typeScriptAugmentationVersionMatchesCurrent);
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var ftsMutated = false;
        var purgedRefs = 0;
        var supportedGraphLanguages = ReferenceExtractor.GetSupportedLanguages();
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

        void RecordScanErrors(IEnumerable<FileIndexer.ScanError> scanErrors)
        {
            foreach (var scanError in scanErrors)
            {
                var key = $"{scanError.Severity}\n{scanError.Path}\n{scanError.Message}";
                if (!scanErrorKeys.Add(key))
                    continue;

                if (scanError.IsFatal)
                {
                    DemoteReadinessOnce();
                    errors++;
                    errorList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }
                else
                {
                    warnings++;
                    warningList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }

                if (!options.Json)
                {
                    PauseUpdateSpinnerForConsoleWrite();
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
                    ResumeUpdateSpinnerAfterConsoleWrite();
                }
            }
        }

        void StartUpdateSpinnerIfNeeded()
        {
            if (!interactiveUpdateSpinner || updateCts != null)
                return;

            updateCts = ConsoleUi.StartSpinner("Updating...", spinnerFrames);
        }

        void PauseUpdateSpinnerForConsoleWrite()
        {
            if (updateCts == null)
                return;

            ConsoleUi.StopSpinner(updateCts);
            updateCts = null;
        }

        void ResumeUpdateSpinnerAfterConsoleWrite()
        {
            if (!interactiveUpdateSpinner)
                return;

            StartUpdateSpinnerIfNeeded();
        }

        void WriteUpdateVerboseStatus(string message)
        {
            if (!options.Verbose || options.Quiet)
                return;

            if (options.Json)
            {
                CommandErrorWriter.WriteStderr(message);
                return;
            }

            PauseUpdateSpinnerForConsoleWrite();
            Console.WriteLine(message);
            ResumeUpdateSpinnerAfterConsoleWrite();
        }

        void ThrowIfUpdateCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            PauseUpdateSpinnerForConsoleWrite();
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }

        string ToUpdateAbsolutePath(string path)
            => Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        string ToUpdateRelativePath(string path)
            => Path.IsPathRooted(path)
                ? Path.GetRelativePath(projectRoot, path)
                : path;

        List<CSharpStaticInterfacePrepass.FileTarget> BuildCSharpPrepassTargets(
            IReadOnlyDictionary<string, string>? scannedLanguages)
        {
            var targets = new List<CSharpStaticInterfacePrepass.FileTarget>();
            foreach (var targetPath in targetPaths)
            {
                var absPath = ToUpdateAbsolutePath(targetPath);
                string? language = null;
                if (scannedLanguages != null && scannedLanguages.TryGetValue(absPath, out var scannedLanguage))
                {
                    if (scannedLanguage != "csharp")
                        continue;

                    language = scannedLanguage;
                }
                else
                {
                    var detection = FileIndexer.TryDetectLanguage(absPath);
                    if (detection.Status != FileIndexer.FileProbeStatus.Supported || detection.Language != "csharp")
                        continue;

                    language = detection.Language;
                }

                var target = CSharpStaticInterfacePrepass.FileTarget.Create(projectRoot, absPath, language);
                targets.Add(target with
                {
                    GeneratedExtractionSuppressed = indexer.HasGeneratedCodeExtractionSuppressionPatterns
                        && indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath)
                });
            }

            return targets;
        }

        IReadOnlyDictionary<string, string>? scannedUpdateLanguages = null;
        ThrowIfUpdateCancelled();
        WriteIndexJsonLiveness(options, "checking C# workspace contracts...");
        var csharpWorkspaceHeartbeat = StartIndexJsonPhaseHeartbeat(options, "checking C# workspace contracts");
        var csharpPrepassTargets = BuildCSharpPrepassTargets(scannedUpdateLanguages);
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        try
        {
            if (csharpPrepassTargets.Count == 0)
            {
                csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
            }
            else
            {
                UpdateCSharpPrepassForTesting?.Invoke();
                csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    writer,
                    indexer,
                    csharpPrepassTargets,
                    cancellationToken: cancellationToken);
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
                var scanResult = indexer.ScanFilesDetailed(cancellationToken: cancellationToken);
                scannedUpdateLanguages = scanResult.FileLanguages;
                foreach (var filePath in scanResult.Files)
                {
                    if (scanResult.FileLanguages.TryGetValue(filePath, out var language)
                        && language == "csharp")
                        targetPaths.Add(filePath);
                }

                csharpPrepassTargets = BuildCSharpPrepassTargets(scannedUpdateLanguages);
                if (csharpPrepassTargets.Count == 0)
                {
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
                }
                else
                {
                    UpdateCSharpPrepassForTesting?.Invoke();
                    csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        cancellationToken: cancellationToken);
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

        if (writer.CountUnsupportedReferences(supportedGraphLanguages) > 0)
        {
            DemoteReadinessOnce();

            using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported references");
            purgedRefs = writer.PurgeUnsupportedReferences(supportedGraphLanguages);
            if (purgedRefs > 0)
                purgeTxn.Commit();
        }

        StartUpdateSpinnerIfNeeded();

        WriteIndexJsonLiveness(options, $"updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        string? currentUpdatePath = null;
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
            foreach (var targetPath in targetPaths)
            {
                ThrowIfUpdateCancelled();
                StartUpdateSpinnerIfNeeded();
                var relPath = ToUpdateRelativePath(targetPath);
                currentUpdatePath = relPath;
                var absPath = ToUpdateAbsolutePath(targetPath);
                var dbPath = FileIndexer.NormalizeIndexPath(relPath);
                var fileBatchMarked = false;
                string? knownLanguage = null;
                try
                {
                    if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                    {
                        using var deleteTxn = writer.BeginTransaction(cancellationToken, "update delete missing target");
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            WriteUpdateVerboseStatus($"  [DEL ] {relPath}");
                        }
                        else
                        {
                            skipped++;
                            WriteUpdateVerboseStatus($"  [SKIP] {relPath} (not in DB)");
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
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                ResumeUpdateSpinnerAfterConsoleWrite();
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
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [DEL ] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        continue;
                    }

                    var indexability = indexer.GetFileIndexabilityForIndexing(absPath);
                    var detection = indexer.TryDetectLanguageForIndexing(absPath, knownIndexability: indexability);
                    if (indexability == FileIndexer.FileProbeStatus.Missing || detection.Status == FileIndexer.FileProbeStatus.Missing)
                    {
                        var message = $"{relPath}: skipped because it was deleted during indexing.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(message);
                            ResumeUpdateSpinnerAfterConsoleWrite();
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
                        if (!options.Json)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            if (options.Verbose)
                                CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            else
                                CommandErrorWriter.WriteStderr($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                    {
                        if (!writer.HasFileAtPath(dbPath))
                        {
                            using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unsupported renamed target");
                            var purged = projectRootWritten
                                ? writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, dbPath)
                                : 0;
                            if (purged > 0)
                            {
                                DemoteReadinessOnce();
                                WriteProjectRootOnce();
                                RequireTypeScriptAugmentationRefresh();
                                purgeTxn.Commit();
                                removed += purged;
                                ftsMutated = true;
                                if (options.Verbose && !options.Json && !options.Quiet)
                                {
                                    PauseUpdateSpinnerForConsoleWrite();
                                    Console.WriteLine($"  [DEL ] {relPath} (unsupported renamed target)");
                                    ResumeUpdateSpinnerAfterConsoleWrite();
                                }
                            }
                            else
                            {
                                skipped++;
                                if (options.Verbose && !options.Json && !options.Quiet)
                                {
                                    PauseUpdateSpinnerForConsoleWrite();
                                    Console.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                                    ResumeUpdateSpinnerAfterConsoleWrite();
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
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [DEL ] {relPath} (no longer indexable)");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        continue;
                    }

                    if (FileIndexer.TryGetFileIdentity(absPath, out var identity) && !visitedFileIdentities.Add(identity))
                    {
                        var message = "Skipped hardlinked file because the same file content was already indexed from another path.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning($"{relPath}: {message}");
                            ResumeUpdateSpinnerAfterConsoleWrite();
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
                            && (statReusableLanguage != "sql" || sqlGraphContractMatchesCurrent));
                    if (statMatchedFile != null)
                    {
                        skipped++;
                        knownReadableFileSizes[absPath] = statMatchedFile.Value.Size;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} (unchanged)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    knownLanguage = scannedUpdateLanguages == null
                        ? statReusableLanguage
                        : FileIndexer.GetReusableDetectedLanguage(absPath, scannedUpdateLanguages);

                    var loaded = indexer.BuildLoadedRecordWithRawBytes(
                        absPath,
                        relPath,
                        knownLanguage,
                        cancellationToken);
                    var record = loaded.Record;
                    knownReadableFileSizes[absPath] = record.Size;
                    var content = loaded.Content;
                    var rawBytes = loaded.RawBytes;
                    var warning = loaded.Warning;
                    var generatedSuppressionIssue = generatedExtractionSuppressed
                        ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                        : null;

                    if (warning != null && !options.Json && !options.Quiet)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        ConsoleUi.PrintWarning(warning);
                        ResumeUpdateSpinnerAfterConsoleWrite();
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
                            && (record.Lang != "sql" || sqlGraphContractMatchesCurrent));
                    if (existingId != null)
                    {
                        using var purgeTxn = writer.BeginTransaction(cancellationToken, "update purge unchanged stale paths");
                        var purged = writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum)
                            + (projectRootWritten
                                ? writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, record.Path)
                                : 0);
                        if (purged > 0)
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
                            RequireTypeScriptAugmentationRefresh();
                            purgeTxn.Commit();
                            removed += purged;
                            ftsMutated = true;
                        }
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine(purged > 0
                                ? $"  [SKIP] {relPath} (unchanged; purged {purged:N0} stale renamed path(s))"
                                : $"  [SKIP] {relPath} (unchanged)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    DemoteReadinessOnce();
                    writer.MarkBatchInProgress();
                    fileBatchMarked = true;
                    if (record.Lang == "csharp")
                        csharpMetadataTargetsNeedRefresh = true;
                    var recordRequiresTypeScriptAugmentationRefresh = record.Lang == "typescript";
                    using var txn = writer.BeginTransaction(cancellationToken, "update file");
                    if (recordRequiresTypeScriptAugmentationRefresh)
                        RequireTypeScriptAugmentationRefresh();
                    var stalePurged = writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                    if (projectRootWritten)
                        stalePurged += writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, record.Path);
                    if (stalePurged > 0)
                        RequireTypeScriptAugmentationRefresh();
                    WriteProjectRootOnce();
                    var fileId = writer.UpsertFile(record);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "chunking");
                    var chunks = ChunkSplitter.SplitNormalized(fileId, content, loaded.HasOversizeLine, record.Lines);
                    if (generatedSuppressionIssue != null)
                    {
                        writer.InsertChunks(chunks, cancellationToken);
                        writer.InsertSymbols([], cancellationToken);
                        writer.InsertReferences([], cancellationToken);
                        currentUpdatePath = FormatIndexPhasePath(relPath, "validating");
                        var generatedIssues = AppendIssueIfMissing(
                            FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine, loaded.ConflictMarkerLine),
                            generatedSuppressionIssue);
                        writer.InsertIssues(fileId, generatedIssues);
                        currentUpdatePath = FormatIndexPhasePath(relPath, "committing");
                        writer.ClearBatchInProgress();
                        txn.Commit();
                        fileBatchMarked = false;
                        updated++;
                        ftsMutated = true;
                        WriteUpdateVerboseStatus($"  [OK  ] {relPath} ({chunks.Count} chunks, generated-code extraction skipped)");
                        continue;
                    }
                    currentUpdatePath = FormatIndexPhasePath(relPath, "symbols");
                    var symbolExtraction = ExtractSymbolsWithStallTimeout(
                        fileId,
                        record.Lang,
                        content,
                        absPath,
                        Path.GetFullPath(options.ProjectPath!),
                        record.Path,
                        currentUpdatePath,
                        true,
                        loaded.HasOversizeLine,
                        loaded.ConflictMarkerLine,
                        symbolExtractionWorker.Value,
                        cancellationToken);
                    var symbols = symbolExtraction.Symbols;
                    var symbolRegexTimeoutIssue = symbolExtraction.RegexTimeoutIssue;
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                            ? [issue]
                            : AppendIssue([symbolRegexTimeoutIssue], issue);
                        writer.InsertSymbols([], cancellationToken);
                        writer.InsertReferences([], cancellationToken);
                        writer.InsertIssues(fileId, capIssues);
                        writer.ClearBatchInProgress();
                        txn.Commit();
                        fileBatchMarked = false;
                        updated++;
                        ftsMutated = true;
                        WriteUpdateVerboseStatus($"  [SKIP] {relPath} ({issue.Message})");
                        continue;
                    }
                    SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(absPath, record.Lang));
                    var fileContext = new FileContext(projectRoot, record.Path, absPath, record.Lang);
                    postExtractionHooks.Value.OnSymbolsExtracted(fileContext, symbols);
                    symbolsDroppedByKindFilter += options.SymbolKindFilter.Apply(symbols);
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                            ? [issue]
                            : AppendIssue([symbolRegexTimeoutIssue], issue);
                        writer.InsertSymbols([], cancellationToken);
                        writer.InsertReferences([], cancellationToken);
                        writer.InsertIssues(fileId, capIssues);
                        writer.ClearBatchInProgress();
                        txn.Commit();
                        fileBatchMarked = false;
                        updated++;
                        ftsMutated = true;
                        WriteUpdateVerboseStatus($"  [SKIP] {relPath} ({issue.Message})");
                        continue;
                    }
                    writer.InsertChunks(chunks, cancellationToken);
                    FileIndexer.ValidateSymbolLineRanges(record, symbols);
                    writer.InsertSymbols(symbols, cancellationToken);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "references");
                    List<ReferenceRecord> references;
                    FileIssue? referenceRegexTimeoutIssue;
                    ReferenceExtractionResult referenceExtraction;
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
                            cancellationToken,
                            maxReferenceCount: options.MaxReferencesPerFile + 1,
                            conflictMarkerLine: loaded.ConflictMarkerLine);
                        references = referenceExtraction.References;
                        referenceRegexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                    }
                    postExtractionHooks.Value.OnReferencesExtracted(fileContext, references);
                    FileIssue? referenceCapIssue = null;
                    if (references.Count > options.MaxReferencesPerFile)
                    {
                        referenceCapIssue = BuildReferenceCountExceededIssue(record.Path, references.Count, options.MaxReferencesPerFile);
                        references = [];
                    }
                    writer.InsertReferences(references, cancellationToken);
                    // Validate content for encoding issues / エンコーディング問題を検証
                    currentUpdatePath = FormatIndexPhasePath(relPath, "validating");
                    IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine, loaded.ConflictMarkerLine);
                    if (symbolRegexTimeoutIssue != null)
                        issues = AppendIssue(issues, symbolRegexTimeoutIssue);
                    if (referenceRegexTimeoutIssue != null)
                        issues = AppendIssue(issues, referenceRegexTimeoutIssue);
                    issues = AppendReferenceExtractionDiagnosticIssues(issues, record.Path, referenceExtraction.Diagnostics);
                    if (referenceCapIssue != null)
                        issues = AppendIssue(issues, referenceCapIssue);
                    writer.InsertIssues(fileId, issues);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "committing");
                    writer.ClearBatchInProgress();
                    txn.Commit();

                    updated++;
                    ftsMutated = true;
                    UpdateFileCommittedForTesting?.Invoke(updated + removed, targetPaths.Count);
                    ThrowIfUpdateCancelled();
                    WriteUpdateVerboseStatus($"  [OK  ] {relPath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                }
                catch (IndexExtractionStalledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is FileIndexer.BinaryFileSkippedException binaryFile)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        warnings++;
                        var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                        warningList.Add(new CliJsonMessage(relPath, sanitizedMessage));
                        if (!options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(sanitizedMessage);
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }

                        DemoteReadinessOnce();
                        writer.MarkBatchInProgress();
                        using var txn = writer.BeginTransaction(cancellationToken, "update skipped binary");
                        var skippedRecord = indexer.BuildSkippedFileRecord(absPath, relPath, knownLanguage);
                        knownReadableFileSizes[absPath] = skippedRecord.Size;
                        var stalePurged = writer.PurgeStaleFilesSharingChecksum(projectRoot, skippedRecord.Path, skippedRecord.Checksum);
                        if (projectRootWritten)
                            stalePurged += writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, skippedRecord.Path);
                        if (skippedRecord.Lang == "typescript" || stalePurged > 0)
                            RequireTypeScriptAugmentationRefresh();
                        WriteProjectRootOnce();
                        var fileId = writer.UpsertFile(skippedRecord);
                        writer.InsertChunks([], cancellationToken);
                        writer.InsertSymbols([], cancellationToken);
                        writer.InsertReferences([], cancellationToken);
                        writer.InsertIssues(fileId, [BuildNullByteIssue(binaryFile)]);
                        writer.ClearBatchInProgress();
                        txn.Commit();

                        updated++;
                        ftsMutated = true;
                        continue;
                    }

                    if (ex is FileIndexer.FileTooLargeSkippedException fileTooLarge)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        DemoteReadinessOnce();
                        writer.MarkBatchInProgress();
                        using var txn = writer.BeginTransaction(cancellationToken, "update skipped oversized file");
                        var skippedRecord = indexer.BuildSkippedFileRecord(absPath, relPath, knownLanguage);
                        knownReadableFileSizes[absPath] = skippedRecord.Size;
                        var stalePurged = writer.PurgeStaleFilesSharingChecksum(projectRoot, skippedRecord.Path, skippedRecord.Checksum);
                        if (projectRootWritten)
                            stalePurged += writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, skippedRecord.Path);
                        if (skippedRecord.Lang == "typescript" || stalePurged > 0)
                            RequireTypeScriptAugmentationRefresh();
                        WriteProjectRootOnce();
                        var fileId = writer.UpsertFile(skippedRecord);
                        writer.InsertChunks([], cancellationToken);
                        writer.InsertSymbols([], cancellationToken);
                        writer.InsertReferences([], cancellationToken);
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

                        updated++;
                        ftsMutated = true;
                        continue;
                    }

                    if (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        var message = $"{relPath}: skipped because it was deleted during indexing.";
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, message));
                        if (!options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(message);
                            ResumeUpdateSpinnerAfterConsoleWrite();
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
                            }
                        }
                        else
                        {
                            skipped++;
                        }
                        continue;
                    }

                    DemoteReadinessOnce();
                    if (fileBatchMarked)
                        writer.ClearBatchInProgress();
                    LogIndexFileFailure("index_update_file_failed", relPath, ex);

                    errors++;
                    var errorMessage = FormatIndexFileException(ex);
                    errorList.Add(new CliJsonMessage(relPath, errorMessage));
                    if (!options.Json)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        CommandErrorWriter.WriteStderr(FormatPerFileErrorLine("ERR ", relPath, ex, errorMessage));
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }
                }
            }
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(updateHeartbeat);
        }

        if (options.ChangedBetweenSpecified)
        {
            ThrowIfUpdateCancelled();

            var skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectRoot, cancellationToken);
            if (skipWorktreePaths != null)
            {
                var purgedMissing = writer.PurgeStaleFiles(
                    projectRoot,
                    beforeCommit: () =>
                    {
                        DemoteReadinessOnce();
                        WriteProjectRootOnce();
                        RequireTypeScriptAugmentationRefresh();
                    },
                    preservedMissingPaths: skipWorktreePaths);
                if (purgedMissing > 0)
                {
                    removed += purgedMissing;
                    ftsMutated = true;
                    WriteUpdateVerboseStatus($"  [DEL ] purged {purgedMissing:N0} missing indexed path(s) after --changed-between");
                }
            }
        }

        ThrowIfUpdateCancelled();
        PauseUpdateSpinnerForConsoleWrite();

        if (purgedRefs > 0 && !options.Json && !options.Quiet)
            Console.WriteLine($"  Purged {purgedRefs:N0} stale references (unsupported language)");

        var ftsOptimizeRan = false;
        if (ftsMutated)
        {
            writer.RecordFtsIncrementalWrite();
            ftsOptimizeRan = writer.OptimizeFtsIfIncrementalWriteThresholdReached();
        }
        ThrowIfUpdateCancelled();
        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // readiness was demoted before the first committed mutation.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        var hasCSharpFilesAfter = writer.HasAnyFilesWithLanguage("csharp");
        var hasSqlFilesAfter = writer.HasAnyFilesWithLanguage("sql");
        var graphTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.GraphReadyFlag) != 0
            : false;
        var issuesTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.IssuesReadyFlag) != 0
            : false;
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter
            || (!readinessDemoted && csharpSymbolNameContractMatchesCurrent);
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter
            || (!readinessDemoted && priorMetadataTargetCsharpMatchesCurrent);
        var foldReadyAfter = !readinessDemoted
            && (priorReadiness & DbContext.FoldReadyFlag) != 0
            && priorFoldVersion == currentFoldVersion
            && priorFoldFingerprint == currentFoldFingerprint
            && priorSymbolExtractorVersionsMatchCurrent;
        string? foldReadyReasonAfter = foldReadyAfter
            ? null
            : GetFoldReadyReason(
                (priorReadiness & DbContext.FoldReadyFlag) != 0,
                priorFoldVersion == currentFoldVersion,
                priorFoldFingerprint == currentFoldFingerprint);
        if (readinessDemoted && errors == 0)
        {
            writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(cancellationToken, "update readiness restamp");
            // Restore each readiness bit independently based on what the DB carried BEFORE
            // ClearReadyFlags wiped them. A pre-#86 DB (user_version=3, i.e. Graph+Issues but
            // no Fold) must keep Graph+Issues after a successful partial update, even though
            // FoldReady can't be restamped. Codex #86 second-pass review: the old single-flag
            // `wasFullyReady` gate silently dropped Graph/Issues for the whole workspace on
            // such DBs, breaking references/callers/callees/impact.
            // Fold is the only bit that needs the runtime verify: the other two only require
            // that the DB previously reached end-of-run for those subsystems. Fold also
            // requires name_folded to be populated for every row, but the invariant holds
            // when the prior bit was set AND this update rewrote its touched rows with
            // name_folded populated, so no extra scan is needed here.
            // update mode は事前 bit を個別に復元。Graph/Issues は prior bit があれば復元、
            // Fold も prior bit があれば invariant を信じて restamp（codex 2nd review 対応）。
            // unreadable ignore file の true no-op skip は ClearReadyFlags 自体を避けるので、
            // ここでは通常どおり errors==0 の成功 run だけを復元対象にする。
            if ((priorReadiness & DbContext.GraphReadyFlag) != 0)
            {
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            if ((priorReadiness & DbContext.IssuesReadyFlag) != 0)
            {
                writer.MarkIssuesReady();
                issuesTableAvailableAfter = true;
            }
            if (sqlGraphContractMatchesCurrent || !hasSqlFilesAfter)
                writer.MarkSqlGraphContractReady();
            if (csharpSymbolNameContractMatchesCurrent || !hasCSharpFilesAfter)
            {
                writer.MarkCSharpSymbolNameContractReady();
                csharpSymbolNameReadyAfter = true;
            }
            // Issue #435: run the metadata-target resolver across all currently-indexed C#
            // class rows. This is always safe because the resolver rewrites every row, so
            // legacy NULL rows from a pre-#435 DB and untouched rows from this partial
            // update both end up authoritative. Only stamp readiness when the resolver
            // actually ran (i.e. there are C# files to resolve).
            // Issue #435: 成功 update の末尾で全 csharp class 行を resolver で再分類する。
            // resolver は全行を書き直すので pre-#435 DB の NULL 行と未更新行の両方が
            // authoritative になる。csharp ファイルがある場合のみ readiness も立てる。
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    UpdateCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets();
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            // Keep hotspot-family maintenance rewrites and readiness restamps in one rollback
            // boundary. If the process dies after SetMeta but before commit, SQLite rolls back
            // the version stamp along with any maintenance rows, so readers never see a partial
            // family_key/container_qualified_name state as authoritative (#1488).
            using (var hotspotFamilyTxn = writer.BeginTransaction(cancellationToken, "update hotspot-family restamp"))
            {
                if (typeScriptAugmentationNeedsRefresh)
                {
                    UpdateTypeScriptAugmentationRebuildForTesting?.Invoke();
                    writer.RebuildTypeScriptAugmentationReferences(projectRoot);
                }
                RestampHotspotFamilyTrustForUpdate(
                    writer,
                    priorHotspotFamilyVersions,
                    priorHotspotFamilyMarkerFingerprints,
                    currentHotspotFamilyMarkerFingerprints);
                HotspotFamilyUpdateRestampReadyForCommitForTesting?.Invoke();
                hotspotFamilyTxn.Commit();
            }
            // FoldReady restamp requires both the prior stored version and fingerprint to
            // match the current binary/runtime. Otherwise untouched rows still carry keys
            // from an older fold implementation or runtime table set, and advertising
            // FoldReady would silently mismatch on --exact. Only full rebuild can re-fold all rows.
            // fold は version / fingerprint の両一致時のみ restamp。ズレた DB は rebuild まで
            // fold_ready=false のまま残す。
            if ((priorReadiness & DbContext.FoldReadyFlag) != 0
                && priorFoldVersion == currentFoldVersion
                && priorFoldFingerprint == currentFoldFingerprint
                && priorSymbolExtractorVersionsMatchCurrent)
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; a concurrent NULL-folded
                // insert during this restamp window leaves foldReadyAfter=false. Issue #1535.
                // MarkFoldReady は BEGIN IMMEDIATE 内で再検証する。restamp 窓の concurrent
                // 書き込みで NULL 行が残った場合は foldReadyAfter=false のまま。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady();
            }
            writer.WriteCdidxWriterVersion(ConsoleUi.LoadVersion());
            writer.SetMeta(SymbolKindFilterMetaKey, options.SymbolKindFilter.Signature);
            writer.ClearBatchInProgress();
            readinessTxn.Commit();
        }
        if (errors == 0)
        {
            StampIndexedHeadMetadata(writer, projectRoot, indexRunDiagnostics, cancellationToken);
            StampCommitScopedFreshHeadMetadata(writer, options, projectRoot, currentHeadCommit, indexRunDiagnostics, cancellationToken);
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(memorySamples);
            var bytesRead = MeasureReadableFileBytes(
                targetPaths,
                ToUpdateAbsolutePath,
                projectRoot,
                indexRunDiagnostics,
                knownReadableFileSizes);
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
                indexRunDiagnostics);
        }
        stopwatch.Stop();
        var memoryTimeline = BuildMemoryTimeline(memorySamples);
        WarnIfMemoryThresholdExceeded(memoryTimeline);
        // Detect cwd drift between option-parsing and finalize. Paths used in this run are
        // already absolute, but a drifted cwd is a strong signal that an embedded host or
        // signal handler mutated process state -- surface it so the operator can correct
        // their hosting code. Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(initialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            warningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            warnings++;
        }
        warnings += AddPostExtractionHookWarnings(postExtractionHooks.ValueIfCreated, warningList);
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        var signalReader = new DbReader(writer.Connection);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignalAfter = signalReader.GetHotspotFamilySignal(lang: null);
        var sqlGraphContractReadyAfter = sqlGraphContractSignalAfter.Ready;
        var sqlGraphContractDegradedReasonAfter = sqlGraphContractSignalAfter.DegradedReason;
        var hotspotFamilyReadyAfter = hotspotFamilySignalAfter.Ready;
        var hotspotFamilyDegradedReasonAfter = hotspotFamilySignalAfter.DegradedReason;

        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailableAfter,
            issuesTableAvailableAfter,
            sqlGraphContractReadyAfter,
            hotspotFamilyReadyAfter,
            csharpSymbolNameReadyAfter,
            csharpMetadataTargetReadyAfter,
            foldReadyAfter,
            foldReadyReasonAfter,
            projectRoot,
            resolvedDbPath);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new IndexUpdateJsonResult
            {
                Status = errors > 0 ? "partial" : "success",
                Mode = "update",
                Summary = new IndexUpdateSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    Updated = updated,
                    Removed = removed,
                    Skipped = skipped,
                    Warnings = warnings,
                    Errors = errors,
                    SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                    FtsOptimizeRan = ftsOptimizeRan,
                },
                SymbolKindFilter = options.SymbolKindFilter.ToJsonResult(),
                GraphTableAvailable = graphTableAvailableAfter,
                IssuesTableAvailable = issuesTableAvailableAfter,
                SqlGraphContractReady = sqlGraphContractReadyAfter,
                SqlGraphContractDegradedReason = sqlGraphContractDegradedReasonAfter,
                HotspotFamilyReady = hotspotFamilyReadyAfter,
                HotspotFamilyDegradedReason = hotspotFamilyDegradedReasonAfter,
                CSharpSymbolNameReady = csharpSymbolNameReadyAfter,
                CSharpMetadataTargetReady = csharpMetadataTargetReadyAfter,
                // #86 codex review: expose fold-readiness so AI clients can decide whether
                // `--exact` will use the Unicode fold path or fall back to ASCII NOCASE.
                // #86 codex: AI クライアントが --exact の経路を判断できるよう fold_ready を返す。
                FoldReady = foldReadyAfter,
                FoldReadyReason = foldReadyAfter ? null : foldReadyReasonAfter,
                DegradedReason = foldOnlyRemediation?.DegradedReason,
                RecommendedAction = foldOnlyRemediation?.RecommendedAction,
                AlternativeAction = foldOnlyRemediation?.AlternativeAction,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = initialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = errorList.Count > 0 ? errorList : null,
                Warnings = warningList.Count > 0 ? warningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, jsonContext.IndexUpdateJsonResult));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine();
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{ConsoleUi.FormatNumber(totalFiles)} (total in DB)", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", ConsoleUi.FormatNumber(totalChunks), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", ConsoleUi.FormatNumber(totalSymbols), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", ConsoleUi.FormatNumber(totalReferences), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Updated", ConsoleUi.FormatNumber(updated), indent: "  "));
            if (removed > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Removed", ConsoleUi.FormatNumber(removed), indent: "  "));
            if (skipped > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", ConsoleUi.FormatNumber(skipped), indent: "  "));
            if (warnings > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", ConsoleUi.FormatNumber(warnings), indent: "  "));
            if (errors > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Errors", ConsoleUi.FormatNumber(errors), indent: "  "));
            if (symbolsDroppedByKindFilter > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Filtered symbols", ConsoleUi.FormatNumber(symbolsDroppedByKindFilter), indent: "  "));
            if (ftsOptimizeRan) Console.WriteLine(ConsoleUi.FormatSummaryLine("FTS optimize", "completed", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", graphTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Issues", issuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# names", csharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", csharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Fold", foldReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed, options.DurationFormat), indent: "  "));
            Console.WriteLine();
            if (errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to update. Fix the reported files or permissions, then rerun `cdidx index \"{projectRoot}\"` to restore a fully ready index.");
            if (!graphTableAvailableAfter || !issuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !csharpSymbolNameReadyAfter || !csharpMetadataTargetReadyAfter || !foldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(graphTableAvailableAfter, issuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, csharpSymbolNameReadyAfter, csharpMetadataTargetReadyAfter, foldReadyAfter, foldReadyReasonAfter, projectRoot, resolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
        }

        if (!options.Json && !options.Quiet && stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            ConsoleUi.EmitCompletionNotification(
                options.NotifyMode,
                $"cdidx index update complete ({ConsoleUi.Counted(updated + removed + skipped, "file", format: "N0")})");

        return CommandExitCodes.Success;
    }

}
