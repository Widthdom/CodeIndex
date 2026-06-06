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
        CancellationToken cancellationToken)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        var memorySamples = options.MemoryTrace ? new List<IndexMemorySampleJsonResult> { CaptureMemorySample("start", stopwatch) } : [];
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions);
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
                showNextSteps: false,
                cancellationToken);
        }

        if (!options.Json && !options.Quiet)
            Console.WriteLine($"Updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        CancellationTokenSource? updateCts = null;
        var interactiveUpdateSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        int updated = 0, removed = 0, skipped = 0, warnings = 0, errors = 0;
        var errorList = new List<CliJsonMessage>();
        var warningList = new List<CliJsonMessage>();
        var scanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIndexer.FileIdentity>();
        var readinessDemoted = false;
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var ftsMutated = false;
        var purgedRefs = 0;
        var supportedGraphLanguages = ReferenceExtractor.GetSupportedLanguages();
        using var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault();
        var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var currentFoldFingerprint = NameFold.Fingerprint();
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var priorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharp == currentMetadataTargetVersion;
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
                Console.Error.WriteLine(message);
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

        ThrowIfUpdateCancelled();
        WriteIndexJsonLiveness(options, "checking C# workspace contracts...");
        var csharpWorkspaceHeartbeat = StartIndexJsonPhaseHeartbeat(options, "checking C# workspace contracts");
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        try
        {
            csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                writer,
                indexer,
                projectRoot,
                targetPaths,
                cancellationToken: cancellationToken);
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
                foreach (var filePath in indexer.ScanFilesDetailed(cancellationToken: cancellationToken).Files)
                {
                    var detection = FileIndexer.TryDetectLanguage(filePath);
                    if (detection.Status == FileIndexer.FileProbeStatus.Supported
                        && detection.Language == "csharp")
                    {
                        targetPaths.Add(filePath);
                    }
                }

                csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                    writer,
                    indexer,
                    projectRoot,
                    targetPaths,
                    cancellationToken: cancellationToken);
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

            using var purgeTxn = writer.BeginTransaction();
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
        try
        {
            foreach (var relPath in targetPaths)
            {
                ThrowIfUpdateCancelled();
                StartUpdateSpinnerIfNeeded();
                currentUpdatePath = relPath;
                var absPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                var dbPath = FileIndexer.NormalizeIndexPath(relPath);
                var fileBatchMarked = false;
                try
                {
                    if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                    {
                        if (!writer.HasFileAtPath(dbPath))
                        {
                            skipped++;
                            WriteUpdateVerboseStatus($"  [SKIP] {relPath} (not in DB)");
                            continue;
                        }

                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
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

                        if (!writer.HasFileAtPath(dbPath))
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

                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
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

                    var indexability = FileIndexer.GetFileIndexability(absPath);
                    var detection = FileIndexer.TryDetectLanguage(absPath);
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

                        if (writer.HasFileAtPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            using var deleteTxn = writer.BeginTransaction();
                            if (writer.DeleteFileByPath(dbPath))
                            {
                                WriteProjectRootOnce();
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

                    if (indexability == FileIndexer.FileProbeStatus.ProbeFailed || detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                    {
                        DemoteReadinessOnce();

                        errors++;
                        errorList.Add(new CliJsonMessage(relPath, "Could not probe file for indexability/language."));
                        if (!options.Json)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            if (options.Verbose)
                                Console.Error.WriteLine($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            else
                                Console.Error.WriteLine($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                    {
                        if (!writer.HasFileAtPath(dbPath))
                        {
                            using var purgeTxn = writer.BeginTransaction();
                            var purged = projectRootWritten
                                ? writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, dbPath)
                                : 0;
                            if (purged > 0)
                            {
                                DemoteReadinessOnce();
                                WriteProjectRootOnce();
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
                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
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

                        if (!writer.HasFileAtPath(dbPath))
                        {
                            skipped++;
                            continue;
                        }

                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(dbPath))
                        {
                            WriteProjectRootOnce();
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

                    var statReusableLanguage = TryDetectStatReusableLanguage(absPath);
                    var statMatchedId = TryGetUnchangedFileIdFromStat(
                        writer,
                        projectRoot,
                        absPath,
                        statReusableLanguage,
                        allowReuse: symbolKindFilterMatchesPrior
                            && statReusableLanguage is not ("javascript" or "typescript")
                            && (statReusableLanguage != "csharp" || csharpSymbolNameContractMatchesCurrent)
                            && (statReusableLanguage != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                            && (statReusableLanguage != "sql" || sqlGraphContractMatchesCurrent));
                    if (statMatchedId != null)
                    {
                        if (writer.CountSymbolsForFile(statMatchedId.Value) > options.MaxSymbolsPerFile
                            || writer.HasIssueForFile(statMatchedId.Value, "symbol_count_exceeded"))
                        {
                            statMatchedId = null;
                        }
                    }
                    if (statMatchedId != null)
                    {
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} (unchanged)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(absPath, cancellationToken);

                    if (warning != null && !options.Json && !options.Quiet)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        ConsoleUi.PrintWarning(warning);
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }

                    var existingId = writer.GetUnchangedFileId(
                        record.Path,
                        record.Modified,
                        record.Checksum,
                        size: record.Size,
                        lines: record.Lines,
                        language: record.Lang,
                        generated: record.Generated,
                        allowReuse: symbolKindFilterMatchesPrior
                            && record.Lang is not ("javascript" or "typescript")
                            && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                            && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                            && (record.Lang != "sql" || sqlGraphContractMatchesCurrent));
                    if (existingId != null)
                    {
                        if (writer.CountSymbolsForFile(existingId.Value) > options.MaxSymbolsPerFile
                            || writer.HasIssueForFile(existingId.Value, "symbol_count_exceeded"))
                        {
                            existingId = null;
                        }
                    }
                    if (existingId != null)
                    {
                        using var purgeTxn = writer.BeginTransaction();
                        var purged = writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum)
                            + (projectRootWritten
                                ? writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, record.Path)
                                : 0);
                        if (purged > 0)
                        {
                            DemoteReadinessOnce();
                            WriteProjectRootOnce();
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
                    using var txn = writer.BeginTransaction();
                    writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                    if (projectRootWritten)
                        writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, record.Path);
                    WriteProjectRootOnce();
                    var fileId = writer.UpsertFile(record);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "chunking");
                    var chunks = ChunkSplitter.Split(fileId, content);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "symbols");
                    var symbols = ExtractSymbolsWithStallTimeout(
                        fileId,
                        record.Lang,
                        content,
                        absPath,
                        Path.GetFullPath(options.ProjectPath!),
                        currentUpdatePath,
                        cancellationToken);
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        writer.InsertSymbols([]);
                        writer.InsertReferences([]);
                        writer.InsertIssues(fileId, [issue]);
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
                    postExtractionHooks.OnSymbolsExtracted(fileContext, symbols);
                    symbolsDroppedByKindFilter += options.SymbolKindFilter.Apply(symbols);
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        writer.InsertSymbols([]);
                        writer.InsertReferences([]);
                        writer.InsertIssues(fileId, [issue]);
                        writer.ClearBatchInProgress();
                        txn.Commit();
                        fileBatchMarked = false;
                        updated++;
                        ftsMutated = true;
                        WriteUpdateVerboseStatus($"  [SKIP] {relPath} ({issue.Message})");
                        continue;
                    }
                    writer.InsertChunks(chunks);
                    FileIndexer.ValidateSymbolLineRanges(record, symbols);
                    writer.InsertSymbols(symbols);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "references");
                    var references = ReferenceExtractor.Extract(
                        fileId,
                        record.Lang,
                        content,
                        symbols,
                        record.Path,
                        record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                        cancellationToken);
                    postExtractionHooks.OnReferencesExtracted(fileContext, references);
                    writer.InsertReferences(references);
                    // Validate content for encoding issues / エンコーディング問題を検証
                    currentUpdatePath = FormatIndexPhasePath(relPath, "validating");
                    var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                    writer.InsertIssues(fileId, issues);
                    currentUpdatePath = FormatIndexPhasePath(relPath, "committing");
                    writer.ClearBatchInProgress();
                    txn.Commit();

                    updated++;
                    ftsMutated = true;
                    ThrowIfUpdateCancelled();
                    WriteUpdateVerboseStatus($"  [OK  ] {relPath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                }
                catch (IndexExtractionStalledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is FileIndexer.BinaryFileSkippedException)
                    {
                        warnings++;
                        warningList.Add(new CliJsonMessage(relPath, ex.Message));
                        if (!options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(ex.Message);
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }

                        if (writer.HasFileAtPath(dbPath))
                        {
                            DemoteReadinessOnce();
                            using var deleteTxn = writer.BeginTransaction();
                            if (writer.DeleteFileByPath(dbPath))
                            {
                                WriteProjectRootOnce();
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

                    if (ex is FileIndexer.FileTooLargeSkippedException fileTooLarge)
                    {
                        if (fileBatchMarked)
                            writer.ClearBatchInProgress();

                        DemoteReadinessOnce();
                        writer.MarkBatchInProgress();
                        using var txn = writer.BeginTransaction();
                        var skippedRecord = indexer.BuildSkippedFileRecord(absPath);
                        writer.PurgeStaleFilesSharingChecksum(projectRoot, skippedRecord.Path, skippedRecord.Checksum);
                        if (projectRootWritten)
                            writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, skippedRecord.Path);
                        WriteProjectRootOnce();
                        var fileId = writer.UpsertFile(skippedRecord);
                        writer.InsertChunks([]);
                        writer.InsertSymbols([]);
                        writer.InsertReferences([]);
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
                            using var deleteTxn = writer.BeginTransaction();
                            if (writer.DeleteFileByPath(dbPath))
                            {
                                WriteProjectRootOnce();
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
                    GlobalToolLog.Error($"index_update_file_failed path={CollapseLineBreaks(relPath)}\n{GlobalToolLog.FormatExceptionChain(ex)}");

                    errors++;
                    var errorMessage = FormatIndexFileException(ex);
                    errorList.Add(new CliJsonMessage(relPath, errorMessage));
                    if (!options.Json)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        Console.Error.WriteLine(FormatPerFileErrorLine("ERR ", relPath, ex, errorMessage));
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }
                }
            }
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(updateHeartbeat);
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
        var graphTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.GraphReadyFlag) != 0
            : false;
        var issuesTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.IssuesReadyFlag) != 0
            : false;
        var csharpSymbolNameReadyAfter = !writer.HasAnyFilesWithLanguage("csharp")
            || (!readinessDemoted && csharpSymbolNameContractMatchesCurrent);
        var csharpMetadataTargetReadyAfter = !writer.HasAnyFilesWithLanguage("csharp")
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
            using var readinessTxn = writer.BeginTransaction();
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
            if (sqlGraphContractMatchesCurrent || !writer.HasAnyFilesWithLanguage("sql"))
                writer.MarkSqlGraphContractReady();
            if (csharpSymbolNameContractMatchesCurrent || !writer.HasAnyFilesWithLanguage("csharp"))
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
            if (writer.HasAnyFilesWithLanguage("csharp"))
            {
                writer.ResolveCSharpMetadataTargets();
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
            using (var hotspotFamilyTxn = writer.BeginTransaction())
            {
                writer.RebuildTypeScriptAugmentationReferences(projectRoot);
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
            StampIndexedHeadMetadata(writer, projectRoot, cancellationToken);
            StampCommitScopedFreshHeadMetadata(writer, options, projectRoot, currentHeadCommit);
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(memorySamples);
            StampLastIndexRunMetadata(
                writer,
                "update",
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                updated + removed + skipped,
                skipped,
                errors,
                SumReadableFileBytes(targetPaths.Select(path => Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)))),
                updated,
                removed,
                memoryTimelineForStamp);
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
        warnings += AddPostExtractionHookWarnings(postExtractionHooks, warningList);
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
