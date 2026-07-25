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
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentHotspotFamilyMarkerFingerprints,
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
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            options.SymbolKindFilter.Signature,
            StringComparison.Ordinal);
        var priorFilterRetainedCSharpContractMembers =
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(
                priorSymbolKindFilterSignature);

        // Detect HEAD divergence on the default incremental path (no `--rebuild`). `--rebuild`
        // already wipes the DB, so the prior captured HEAD is irrelevant there. We only signal
        // when both sides are known so legacy DBs / non-git workspaces never spuriously trigger.
        // Issue #1508.
        // 既定の incremental 経路で HEAD 差分を検出する。`--rebuild` は DB を消すので比較不要。
        // 双方の HEAD が分かるときのみ警告し、legacy DB / 非 git workspace では誤検知させない。
        var headChangeDetected = !options.Rebuild
            && !string.IsNullOrWhiteSpace(priorIndexedHeadCommit)
            && !string.IsNullOrWhiteSpace(currentHeadCommit)
            && !string.Equals(priorIndexedHeadCommit, currentHeadCommit, StringComparison.Ordinal);
        string? headChangeNotice = null;
        if (headChangeDetected)
        {
            headChangeNotice =
                $"Indexed HEAD changed since the last full scan (was {priorIndexedHeadCommit}, now {currentHeadCommit}). " +
                $"Incremental indexing only refreshes files it can scan in the current worktree, so rows for files that exist only on the previously indexed branch may remain. " +
                $"Run `cdidx index {QuoteCommandArgument(projectRoot)} --rebuild` to fully refresh the index.";
            if (!options.Json && !options.Quiet)
                ConsoleUi.PrintWarning(headChangeNotice);
        }

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
        var scanHadErrors = scanResult.HadErrors;
        var files = discovery.Files;
        var fileTargets = new FullScanFileTarget[files.Count];
        var languageCounts = scanResult.LanguageCounts;
        var csharpPrepassCapacity = languageCounts.TryGetValue("csharp", out var csharpFileCount) ? csharpFileCount : 0;
        var csharpPrepassTargets = new List<CSharpStaticInterfacePrepass.FileTarget>(
            options.SymbolsOnly ? 0 : csharpPrepassCapacity);
        var hasGeneratedCodeExtractionSuppressionPatterns = indexer.HasGeneratedCodeExtractionSuppressionPatterns;
        for (var i = 0; i < files.Count; i++)
        {
            var filePath = files[i];
            var language = FileIndexer.GetReusableDetectedLanguage(filePath, scanResult.FileLanguages);
            var target = FullScanFileTarget.Create(projectRoot, filePath, language);
            fileTargets[i] = hasGeneratedCodeExtractionSuppressionPatterns
                ? target with { GeneratedExtractionSuppressed = indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath) }
                : target;
            if (!options.SymbolsOnly && language == "csharp")
            {
                var indexedTarget = fileTargets[i];
                csharpPrepassTargets.Add(new CSharpStaticInterfacePrepass.FileTarget(
                    indexedTarget.FilePath,
                    indexedTarget.RelativePath,
                    indexedTarget.DisplayRelativePath,
                    indexedTarget.IndexPath,
                    indexedTarget.Language,
                    indexedTarget.GeneratedExtractionSuppressed));
            }
        }
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
        var purged = 0;
        var staleFilePurgePlan = FilePurgePlan.Empty;
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
        var retainedPaths = startedWithNoIndexedFiles
            ? null
            : new HashSet<string>(fileTargets.Length, StringComparer.Ordinal);
        IReadOnlyList<string> indexedJavaScriptTypeScriptConfigPathsBeforePurge = [];
        if (!startedWithNoIndexedFiles)
        {
            foreach (var target in fileTargets)
                retainedPaths!.Add(target.IndexPath);
            indexedJavaScriptTypeScriptConfigPathsBeforePurge = writer.GetIndexedJavaScriptTypeScriptConfigPaths();
        }
        if (scanHadErrors)
        {
            if (!startedWithNoIndexedFiles)
            {
                retainedPaths!.UnionWith(scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));
                var authoritativeDirectories = scanResult.ListedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                attributePrunedDirectories.UnionWith(scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
                var explicitlyRemovedPaths = scanResult.NonIndexablePaths
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                staleFilePurgePlan = writer.PlanFilesOutsideRetainedSetWithinListedDirectories(
                    retainedPaths!,
                    authoritativeDirectories,
                    attributePrunedDirectories,
                    explicitlyRemovedPaths,
                    cancellationToken);
                purged = staleFilePurgePlan.Count;
            }
        }
        else
        {
            if (!startedWithNoIndexedFiles)
                staleFilePurgePlan = writer.PlanFilesOutsideRetainedSet(retainedPaths!, cancellationToken);
            purged = staleFilePurgePlan.Count;
        }
        if (deferCSharpMutationsForIncompleteScan && staleFilePurgePlan.Count > 0)
        {
            // A fatal discovery gap makes the C# workspace non-authoritative. Keep every
            // prior row (including stale candidates) until a clean scan can rebuild implicit
            // implementation references from one complete source snapshot.
            // fatal discovery gap中はC# workspaceが不完全なため、clean scanまで既存rowを保持する。
            staleFilePurgePlan = FilePurgePlan.Empty;
            purged = 0;
        }
        var hadCSharpStaticInterfaceContractsBeforePurge = !options.SymbolsOnly
            && !startedWithNoIndexedFiles
            && staleFilePurgePlan.Count > 0
            && writer.HasCSharpFilesInFileIds(staleFilePurgePlan.FileIds, cancellationToken)
            && (priorCSharpStaticInterfaceSourceEvidence == true
                || writer.HasCSharpStaticInterfaceContractMembersInFileIds(
                    staleFilePurgePlan.FileIds,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        priorCSharpStaticInterfaceSourceEvidence == null
                        || !priorFilterRetainedCSharpContractMembers,
                    cancellationToken));
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

        var redirectedIndexingMessagePrinted = false;
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
        var lastJsonProgressAt = Stopwatch.GetTimestamp();
        string? currentJsonIndexFile = null;
        ActiveExtractionPhase?[] activeExtractionPhases = [];
        CancellationTokenSource? jsonHeartbeatCts = null;
        Task? jsonHeartbeatTask = null;
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

        void EnsureIndexingActivityVisible()
        {
            if (options.Json || options.Quiet)
                return;

            if (indexProgressVisible)
                return;

            if (indexProgress.Interactive)
            {
                indexProgress.Start();
                return;
            }

            if (redirectedIndexingMessagePrinted)
                return;

            CommandOutputWriter.WriteLine("Indexing...");
            redirectedIndexingMessagePrinted = true;
        }

        void ReportJsonIndexProgressIfNeeded()
        {
            if (!options.Json || options.Quiet || files.Count == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (processed == 0
                || processed == files.Count
                || processed % 100 == 0
                || Stopwatch.GetElapsedTime(lastJsonProgressAt, now) >= TimeSpan.FromSeconds(5))
            {
                ConsoleUi.TryWriteErrorLine($"cdidx: indexed {processed:N0}/{files.Count:N0} file(s)...");
                lastJsonProgressAt = now;
            }
        }

        void StartJsonHeartbeatIfNeeded()
        {
            if (!options.Json || options.Quiet || files.Count == 0 || jsonHeartbeatCts != null)
                return;

            jsonHeartbeatCts = new CancellationTokenSource();
            var token = jsonHeartbeatCts.Token;
            jsonHeartbeatTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested)
                        break;

                    var file = GetJsonIndexHeartbeatPath(
                        currentJsonIndexFile,
                        FormatActiveExtractionPhases(activeExtractionPhases));
                    var fileSuffix = string.IsNullOrEmpty(file) ? string.Empty : $": {file}";
                    ConsoleUi.TryWriteErrorLine($"cdidx: still indexing {processed:N0}/{files.Count:N0} file(s){fileSuffix}...");
                }
            }, token);
        }

        void StopJsonHeartbeat()
        {
            if (jsonHeartbeatCts == null)
                return;

            jsonHeartbeatCts.Cancel();
            try
            {
                jsonHeartbeatTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
            jsonHeartbeatCts.Dispose();
            jsonHeartbeatCts = null;
            jsonHeartbeatTask = null;
        }

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

        HashSet<string>? retainedPathsForReuse = null;
        if (!options.Rebuild
            && !startedWithNoIndexedFiles
            && staleFilePurgePlan.RemainingFileCount - fileTargets.LongLength > fileTargets.LongLength)
        {
            retainedPathsForReuse = new HashSet<string>(fileTargets.Length, StringComparer.Ordinal);
            foreach (var target in fileTargets)
                retainedPathsForReuse.Add(target.IndexPath);
        }
        var csharpPositiveNoOpPolicyCandidate = !options.SymbolsOnly
            && priorCSharpStaticInterfaceSourceEvidence is not null
            && priorIndexComplete
            && (priorReadiness & DbContext.GraphReadyFlag) != 0
            && !scanHadErrors
            && !hadCSharpStaticInterfaceContractsBeforePurge
            && !forceExtractorRefresh
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

        var reusableIndexedFileStats = !options.Rebuild && !startedWithNoIndexedFiles
            ? writer.LoadReusableIndexedFileStats(
                options.MaxSymbolsPerFile,
                options.MaxReferencesPerFile,
                cancellationToken,
                fileTargets.Length,
                retainedPathsForReuse,
                staleFilePurgePlan.FileIds,
                csharpPositiveNoOpPolicyCandidate
                    ? ObservePersistedCSharpPath
                    : null)
            : null;
        Dictionary<string, IndexedFileStatReuseResult?>? csharpPrepassStatReuse = null;
        var priorPositiveCSharpSourceNoOpCandidate = false;
        var allCSharpPrepassTargetsReusable = false;

        bool CanReuseCSharpPrepassTargetWithoutRead(CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (forceExtractorRefresh
                || options.Rebuild
                || startedWithNoIndexedFiles
                || !projectRootWritten
                || (requiresConservativeCSharpSourceRefresh
                    && !priorPositiveCSharpSourceNoOpCandidate)
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
                target.GeneratedExtractionSuppressed);
            if (existingFile == null)
            {
                allCSharpPrepassTargetsReusable = false;
                (csharpPrepassStatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                    csharpPrepassCapacity,
                    StringComparer.Ordinal))[target.IndexPath] = null;
                return false;
            }

            (csharpPrepassStatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                csharpPrepassCapacity,
                StringComparer.Ordinal))[target.IndexPath] = existingFile.Value;
            return true;
        }

        bool IsExistingCSharpSymbolPathNowNonCSharp(string indexPath)
        {
            var currentPath = Path.Combine(
                projectRoot,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(indexPath));
            return scanResult.FileLanguages.TryGetValue(currentPath, out var currentLanguage)
                && currentLanguage != "csharp";
        }

        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? csharpWorkspaceFileSnapshots = null;

        string FormatCSharpWorkspaceSnapshotPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "<csharp_workspace>")
                return "<csharp_workspace>";
            if (!Path.IsPathRooted(path))
                return FileIndexer.NormalizePathSeparators(path);
            try
            {
                return FileIndexer.NormalizePathSeparators(
                    FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                return "<csharp_workspace>";
            }
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
                cancellationToken);
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

            FullScanCSharpPrepassForTesting?.Invoke();
            var workspace = buildWorkspace();
            var stableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                csharpPrepassTargets,
                fileSnapshots,
                out var changedFilePath,
                cancellationToken);
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
                csharpPrepassCapacity,
                StringComparer.Ordinal);
            foreach (var target in csharpPrepassTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existingFile = IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    reusableIndexedFileStats!,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    target.GeneratedExtractionSuppressed);
                csharpPrepassStatReuse[target.IndexPath] = existingFile;
                allCSharpPrepassTargetsReusable &= existingFile != null;
            }
        }

        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        var forceFullCSharpRefreshFromInvalidatedNoOp = false;
        if (options.SymbolsOnly || deferCSharpMutationsForIncompleteScan)
        {
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            WriteFullScanJsonLiveness(options, "preparing C# workspace symbols...");
            var activeCSharpWorkspaceFiles = new string?[csharpPrepassTargets.Count];
            var csharpWorkspaceHeartbeat = StartFullScanJsonPhaseHeartbeat(
                options,
                "preparing C# workspace symbols",
                () => GetActiveCSharpPrepassPath(activeCSharpWorkspaceFiles));
            try
            {
                if (csharpPrepassTargets.Count == 0)
                {
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
                }
                else if (priorPositiveCSharpSourceNoOpCandidate
                         && allCSharpPrepassTargetsReusable)
                {
                    // A strict positive no-op needs neither persisted C# symbols nor a
                    // workspace lookup: every existing reference row is retained unchanged.
                    // positive完全no-opではDB symbol/lookupを一切materializeしない。
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
                }
                else
                {
                    csharpWorkspace = BuildStableCSharpWorkspace(() =>
                        CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                            writer,
                            indexer,
                            csharpPrepassTargets,
                            includeExistingSymbols: csharpIndexedProjectRootCompatible && !options.Rebuild && !startedWithNoIndexedFiles,
                            canReuseExistingSymbolsWithoutRead:
                                priorPositiveCSharpSourceNoOpCandidate
                                    ? null
                                    : CanReuseCSharpPrepassTargetWithoutRead,
                            reportCandidateFile: (candidateIndex, path) => SetActiveCSharpPrepassPath(activeCSharpWorkspaceFiles, candidateIndex, path),
                            parallelism: extractionParallelism,
                            excludedExistingFileIds: staleFilePurgePlan.FileIds,
                            isExistingSymbolPathExcluded: IsExistingCSharpSymbolPathNowNonCSharp,
                            cancellationToken: cancellationToken));
                    forceFullCSharpRefreshFromInvalidatedNoOp =
                        priorCSharpStaticInterfaceSourceEvidence == true
                        || csharpWorkspace.HasStaticInterfaceContracts;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new IndexInterruptedException(0, files.Count, actualMode);
            }
            finally
            {
                Array.Clear(activeCSharpWorkspaceFiles);
                StopFullScanJsonPhaseHeartbeat(csharpWorkspaceHeartbeat);
            }
        }
        if (!options.SymbolsOnly && !csharpWorkspace.SourceContractEvidenceComplete)
        {
            var incompleteSourcePaths = csharpWorkspace.IncompleteSourcePaths;
            DeferCSharpMutationsForIncompleteWorkspace(csharpWorkspace);
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                false,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: incompleteSourcePaths);
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
        if (!options.SymbolsOnly
            && !deferCSharpMutationsForIncompleteScan
            && !preservePriorPositiveCSharpSourceNoOp
            && (forceFullCSharpRefreshFromInvalidatedNoOp
                || requiresConservativeCSharpSourceRefresh
                || !csharpSourceEvidenceComplete
                || (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)))
        {
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
        }

        void DeferCSharpMutationsForLoadedSnapshotDrift(string path)
        {
            path = FormatCSharpWorkspaceSnapshotPath(path);
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

        ThrowIfFullScanCancelled(processed, files.Count);
        List<int>? extractionFileIndexes = null;
        int extractionWorkItemCount;
        if (canSkipFullScanTargetsBeforeContentLoad)
        {
            var statPreflightMatched = new bool[fileTargets.Length];
            var csharpNoOpHasInterveningWork = staleFilePurgePlan.Count > 0;
            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (deferCSharpMutationsForIncompleteScan
                    && fileTargets[fileIndex].Language == "csharp")
                {
                    continue;
                }
                statPreflightMatched[fileIndex] = GetFullScanTargetStatMatch(
                    fileIndex,
                    allowCSharpPrepassCache: true) != null;
                if (!statPreflightMatched[fileIndex])
                    csharpNoOpHasInterveningWork = true;
            }

            var revalidatedMatches = new IndexedFileStatReuseResult?[fileTargets.Length];
            var preservedCSharpNoOpInvalidated = false;
            // Revalidate non-C# targets first. If the whole run is still a pure stat no-op,
            // retain the C# candidate cache until the final readiness-boundary stat instead
            // of issuing two back-to-back full C# stat passes.
            // non-C#を先に再確認し、純粋no-opならC#はreadiness直前の最終statへ統合する。
            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (fileTargets[fileIndex].Language == "csharp")
                    continue;

                var revalidated = statPreflightMatched[fileIndex]
                    ? GetFullScanTargetStatMatch(fileIndex, allowCSharpPrepassCache: false)
                    : null;
                revalidatedMatches[fileIndex] = revalidated;
                if (revalidated == null)
                    csharpNoOpHasInterveningWork = true;
            }

            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (fileTargets[fileIndex].Language != "csharp"
                    || deferCSharpMutationsForIncompleteScan)
                {
                    continue;
                }

                var revalidated = statPreflightMatched[fileIndex]
                    ? GetFullScanTargetStatMatch(
                        fileIndex,
                        allowCSharpPrepassCache: preservePriorPositiveCSharpSourceNoOp
                                                     && !csharpNoOpHasInterveningWork)
                    : null;
                revalidatedMatches[fileIndex] = revalidated;
                if (preservePriorPositiveCSharpSourceNoOp && revalidated == null)
                    preservedCSharpNoOpInvalidated = true;
            }

            if (preservedCSharpNoOpInvalidated)
            {
                // The final target-level stat pass is the last boundary before file writes.
                // If it invalidates the empty-workspace shortcut, rebuild raw C# evidence and
                // make every C# target dirty before any stale row can be retained or rewritten.
                // 最終target statでno-opが崩れた場合、write前に全C# raw prepassへ戻す。
                csharpWorkspace = BuildStableCSharpWorkspace(() =>
                    CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        includeExistingSymbols: csharpIndexedProjectRootCompatible && !options.Rebuild && !startedWithNoIndexedFiles,
                        canReuseExistingSymbolsWithoutRead: null,
                        parallelism: extractionParallelism,
                        excludedExistingFileIds: staleFilePurgePlan.FileIds,
                        isExistingSymbolPathExcluded: IsExistingCSharpSymbolPathNowNonCSharp,
                        cancellationToken: cancellationToken));
                preservePriorPositiveCSharpSourceNoOp = false;
                if (!csharpWorkspace.SourceContractEvidenceComplete)
                {
                    var incompleteSourcePaths = csharpWorkspace.IncompleteSourcePaths;
                    DeferCSharpMutationsForIncompleteWorkspace(csharpWorkspace);
                    csharpSourceEvidenceForStamp = false;
                    csharpSourceEvidenceComplete = false;
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: incompleteSourcePaths);
                }
                else
                {
                    var requiresFullCSharpRefresh =
                        priorCSharpStaticInterfaceSourceEvidence == true
                        || csharpWorkspace.HasStaticInterfaceContracts;
                    forceFullCSharpRefreshFromInvalidatedNoOp = requiresFullCSharpRefresh;
                    csharpSourceEvidenceForStamp = csharpWorkspace.HasSourceStaticInterfaceContracts;
                    csharpSourceEvidenceComplete = true;
                    if (requiresFullCSharpRefresh)
                    {
                        csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
                        for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
                        {
                            if (fileTargets[fileIndex].Language == "csharp")
                                revalidatedMatches[fileIndex] = null;
                        }
                    }
                }
            }

            extractionFileIndexes = new List<int>(fileTargets.Length);
            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (deferCSharpMutationsForIncompleteScan
                    && fileTargets[fileIndex].Language == "csharp")
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
                    continue;
                }

                var revalidated = revalidatedMatches[fileIndex];
                if (revalidated != null)
                    RecordFullScanTargetStatSkip(fileIndex, revalidated.Value);
                else
                    extractionFileIndexes.Add(fileIndex);
            }
            extractionWorkItemCount = extractionFileIndexes.Count;
        }
        else
        {
            if (deferCSharpMutationsForIncompleteScan)
            {
                extractionFileIndexes = new List<int>(fileTargets.Length);
                for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
                {
                    if (fileTargets[fileIndex].Language != "csharp")
                    {
                        extractionFileIndexes.Add(fileIndex);
                        continue;
                    }

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
                extractionWorkItemCount = extractionFileIndexes.Count;
            }
            else
            {
                extractionWorkItemCount = fileTargets.Length;
            }
        }

        var useFtsBulkLoad = options.Rebuild || startedWithNoIndexedFiles;
        if (!useFtsBulkLoad && (extractionWorkItemCount > 0 || staleFilePurgePlan.Count > 0))
        {
            var dirtyBytes = staleFilePurgePlan.DeletedBytes;
            var persistedSizeExcessBytes = 0L;
            var byteEstimateComplete = !scanHadErrors
                && staleFilePurgePlan.ByteEstimateComplete
                && readableFileBytes.EstimateComplete;

            void AddDirtyFileBytes(int fileIndex)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                try
                {
                    var info = new FileInfo(fileTargets[fileIndex].FilePath);
                    if (!info.Exists || info.Length < 0)
                    {
                        byteEstimateComplete = false;
                        return;
                    }

                    readableFileBytes.Remember(fileIndex, info.Length);
                    var persistedSize = reusableIndexedFileStats!.GetPersistedSize(fileTargets[fileIndex].IndexPath);
                    if (!FtsBulkLoadTriggerGuard.TryAccumulateDirtyFileBytes(
                            dirtyBytes,
                            persistedSizeExcessBytes,
                            info.Length,
                            persistedSize,
                            out dirtyBytes,
                            out persistedSizeExcessBytes))
                    {
                        byteEstimateComplete = false;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    byteEstimateComplete = false;
                }
            }

            if (extractionFileIndexes != null)
            {
                foreach (var fileIndex in extractionFileIndexes)
                    AddDirtyFileBytes(fileIndex);
            }
            else
            {
                for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
                    AddDirtyFileBytes(fileIndex);
            }

            byteEstimateComplete &= readableFileBytes.EstimateComplete;
            var totalBytes = readableFileBytes.KnownBytes;
            if (!readableFileBytes.EstimateComplete
                || totalBytes > long.MaxValue - staleFilePurgePlan.DeletedBytes)
                byteEstimateComplete = false;
            else
                totalBytes += staleFilePurgePlan.DeletedBytes;
            if (totalBytes > long.MaxValue - persistedSizeExcessBytes)
                byteEstimateComplete = false;
            else
                totalBytes += persistedSizeExcessBytes;

            useFtsBulkLoad = byteEstimateComplete
                && FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(dirtyBytes, totalBytes);
        }

        if (preservePriorPositiveCSharpSourceNoOp
            && (extractionWorkItemCount > 0 || staleFilePurgePlan.Count > 0))
        {
            // The dirty-byte pass can be long on a mixed-language monorepo. Revalidate C#
            // once more at the final read-only boundary, then undo the tentative stat skips
            // and promote every C# target if any source changed.
            // mixed-language dirty-byte pass後の最終read-only境界でC#を再statする。
            FullScanCSharpFinalStatRevalidationForTesting?.Invoke();
            var invalidatedCSharpFileIndexes = new List<int>();
            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                var target = fileTargets[fileIndex];
                if (target.Language != "csharp")
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                if (IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        target.Language,
                        target.GeneratedExtractionSuppressed) == null)
                {
                    invalidatedCSharpFileIndexes.Add(fileIndex);
                }
            }

            if (invalidatedCSharpFileIndexes.Count > 0)
            {
                csharpWorkspace = BuildStableCSharpWorkspace(() =>
                    CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        includeExistingSymbols: csharpIndexedProjectRootCompatible && !options.Rebuild && !startedWithNoIndexedFiles,
                        canReuseExistingSymbolsWithoutRead: null,
                        parallelism: extractionParallelism,
                        excludedExistingFileIds: staleFilePurgePlan.FileIds,
                        isExistingSymbolPathExcluded: IsExistingCSharpSymbolPathNowNonCSharp,
                        cancellationToken: cancellationToken));
                preservePriorPositiveCSharpSourceNoOp = false;
                if (!csharpWorkspace.SourceContractEvidenceComplete)
                {
                    var incompleteSourcePaths = csharpWorkspace.IncompleteSourcePaths;
                    DeferCSharpMutationsForIncompleteWorkspace(csharpWorkspace);
                    csharpSourceEvidenceForStamp = false;
                    csharpSourceEvidenceComplete = false;
                    csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: incompleteSourcePaths);
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
                    IReadOnlyList<int> csharpFileIndexesToRefresh;
                    if (requiresFullCSharpRefresh)
                    {
                        csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
                        var allCSharpFileIndexes = new List<int>(csharpPrepassTargets.Count);
                        for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
                        {
                            if (fileTargets[fileIndex].Language == "csharp")
                                allCSharpFileIndexes.Add(fileIndex);
                        }
                        csharpFileIndexesToRefresh = allCSharpFileIndexes;
                    }
                    else
                    {
                        // A previously authoritative negative workspace only needs the
                        // stat-invalidated files when the raw fallback is still negative.
                        // prior negative のraw fallbackもnegativeなら変更fileだけを更新する。
                        csharpFileIndexesToRefresh = invalidatedCSharpFileIndexes;
                    }

                    skipped -= csharpFileIndexesToRefresh.Count;
                    processed -= csharpFileIndexesToRefresh.Count;
                    if (csharpFileIndexesToRefresh.Count == csharpPrepassTargets.Count)
                    {
                        skippedSymbolExtractorLanguages?.Remove("csharp");
                        reusedHotspotFamilyLanguages?.Remove("csharp");
                    }
                    foreach (var fileIndex in csharpFileIndexesToRefresh)
                        extractionFileIndexes!.Add(fileIndex);
                    extractionFileIndexes!.Sort();
                    extractionWorkItemCount = extractionFileIndexes.Count;
                    useFtsBulkLoad = false;
                }
            }
        }

        int ReturnBeforeWriteSnapshotFailure(string changedPath)
        {
            var formattedPath = FormatCSharpWorkspaceSnapshotPath(changedPath);
            var exception = new IOException(
                "Directory entries or scan configuration changed after source discovery; rerun indexing from a stable workspace snapshot.");
            errors++;
            errorList.Add(new CliJsonMessage(formattedPath, FormatIndexFileException(exception)));
            if (fileErrorList.Count < PartialIndexFileErrorLimit)
                fileErrorList.Add(BuildIndexFileError(formattedPath, "csharp_workspace_validation", exception));

            stopwatch.Stop();
            var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
            var graphTableAvailable = (priorReadiness & DbContext.GraphReadyFlag) != 0;
            var issuesTableAvailable = (priorReadiness & DbContext.IssuesReadyFlag) != 0;
            var referenceExtractionCapHits = writer.GetReferenceExtractionCapHits(issuesTableAvailable);
            // The connection is writable, but failure diagnostics must not trigger the
            // DbReader constructor's interrupted-FTS recovery before the write barrier.
            using var signalReader = new DbReader(writer.Connection, isReadOnly: true);
            var discoveredCSharpFiles = languageCounts.ContainsKey("csharp");
            var discoveredSqlFiles = languageCounts.ContainsKey("sql");
            var persistedCSharpFiles = writer.HasAnyFilesWithLanguage("csharp");
            var persistedSqlFiles = writer.HasAnyFilesWithLanguage("sql");
            var hasCSharpFiles = discoveredCSharpFiles || persistedCSharpFiles;
            var hasSqlFiles = discoveredSqlFiles || persistedSqlFiles;
            var sqlGraphContractSignal = signalReader.GetSqlGraphContractSignal(lang: null);
            if (!hasSqlFiles)
            {
                sqlGraphContractSignal = new SqlGraphContractSignal(
                    Ready: true,
                    Relevant: false,
                    DegradedReason: null);
            }
            else if (!sqlGraphContractSignal.Relevant)
            {
                // The write barrier can fail after discovery but before the first target is
                // persisted. Keep positive language evidence in the immediate response.
                // write 前 barrier failure でも発見済み language の degraded signal を保持する。
                sqlGraphContractSignal = new SqlGraphContractSignal(
                    Ready: false,
                    Relevant: true,
                    DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason());
            }
            var hotspotFamilySignal = signalReader.GetHotspotFamilySignal(lang: null);
            var csharpSymbolNameReady = !hasCSharpFiles
                || (persistedCSharpFiles && csharpSymbolNameContractMatchesCurrent);
            var csharpMetadataTargetReady = !hasCSharpFiles
                || (persistedCSharpFiles && priorMetadataTargetCsharpMatchesCurrent);
            var foldReady = (priorReadiness & DbContext.FoldReadyFlag) != 0;
            var memoryTimeline = BuildMemoryTimeline(memorySamples);

            if (options.Json)
            {
                CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
                {
                    Status = "partial",
                    Mode = options.Rebuild ? "rebuild" : "incremental",
                    Summary = new IndexFullScanSummaryJsonResult
                    {
                        FilesTotal = totalFiles,
                        ChunksTotal = totalChunks,
                        SymbolsTotal = totalSymbols,
                        ReferencesTotal = totalReferences,
                        FilesScanned = files.Count,
                        FilesSkipped = skipped,
                        FilesPurged = 0,
                        DanglingSymlinksSkipped = scanResult.DanglingSymlinks.Count,
                        Warnings = warnings,
                        Errors = errors,
                        SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                    },
                    SymbolKindFilter = options.SymbolKindFilter.ToJsonResult(),
                    GraphTableAvailable = graphTableAvailable,
                    GraphDataCurrent = false,
                    IndexComplete = false,
                    ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
                    ReferenceGraphComplete = signalReader.IsReferenceGraphComplete(
                        referenceExtractionCapHits),
                    ReferenceExtractionCapHits = referenceExtractionCapHits,
                    ErrorCode = CommandErrorCodes.IndexPartial,
                    IssuesTableAvailable = issuesTableAvailable,
                    SqlGraphContractReady = sqlGraphContractSignal.Ready,
                    SqlGraphContractDegradedReason = sqlGraphContractSignal.DegradedReason,
                    HotspotFamilyReady = hotspotFamilySignal.Ready,
                    HotspotFamilyDegradedReason = hotspotFamilySignal.DegradedReason,
                    CSharpSymbolNameReady = csharpSymbolNameReady,
                    CSharpMetadataTargetReady = csharpMetadataTargetReady,
                    FoldReady = foldReady,
                    FoldReadyReason = foldReady ? null : GetFoldReadyReason(
                        backfillReady: false,
                        priorFoldVersion == NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        priorFoldFingerprint == NameFold.Fingerprint()),
                    Errors = errorList,
                    FileErrors = fileErrorList,
                    Warnings = warningList.Count > 0 ? warningList : null,
                    MemoryTimeline = memoryTimeline,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                }, jsonContext.IndexFullScanJsonResult));
            }
            else if (!options.Quiet)
            {
                ConsoleUi.TryWriteErrorLine(
                    $"Indexing stopped before index-data mutation because the scan snapshot changed: {formattedPath}");
            }

            return options.AllowPartial
                ? CommandExitCodes.Success
                : CommandExitCodes.PartialResult;
        }

        if (discovery.InputSnapshot != null)
        {
            FullScanInputSnapshotBarrierForTesting?.Invoke("before_write");
            if (!indexer.TryValidateScanInputSnapshot(
                    discovery.InputSnapshot,
                    out var changedScanInputPath,
                    cancellationToken))
            {
                return ReturnBeforeWriteSnapshotFailure(changedScanInputPath);
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
                var driftPath = FormatCSharpWorkspaceSnapshotPath(changedFilePath);
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
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh();
        using var fullScanTxn = writer.BeginTransaction(cancellationToken, "full scan write phase");
        fullScanWritePhaseStarted = true;
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

        ReportJsonIndexProgressIfNeeded();

        PostExtractionHookRunner? postExtractionHooks = null;
        if (extractionWorkItemCount == 0)
        {
            FullScanExtractionSchedulingForTesting?.Invoke(false, null);
        }
        else
        {
            postExtractionHooks = PostExtractionHookRunner.DiscoverDefault(
                options.MaxFileSizeBytes,
                maxSymbolCount: options.MaxSymbolsPerFile + 1,
                maxReferenceCount: options.MaxReferencesPerFile + 1);
            var hasPostExtractionHooks = postExtractionHooks.Hooks.Count > 0;
            var parallelizeExtraction = !options.SymbolKindFilter.IsActive
                && !hasPostExtractionHooks;
            var parallelizeExtractionReason = parallelizeExtraction
                ? options.Rebuild
                    ? "rebuild"
                    : startedWithNoIndexedFiles
                        ? "empty_index"
                        : "incremental_changes"
                : null;
            FullScanExtractionSchedulingForTesting?.Invoke(
                parallelizeExtraction,
                parallelizeExtractionReason);

            EnsureIndexingActivityVisible();
            StartJsonHeartbeatIfNeeded();

            try
            {
                if (!options.Json && !options.Quiet)
                {
                    indexProgress.Pause();
                    indexProgressVisible = true;
                    ConsoleUi.PrintProgress(0, files.Count);
                }

                FullScanExtractionWorkStartedForTesting?.Invoke();
                var extractionWorkerCount = Math.Min(extractionParallelism, extractionWorkItemCount);
                activeExtractionPhases = new ActiveExtractionPhase?[extractionWorkerCount];
                var extractionQueueCapacity = parallelizeExtraction
                    ? Math.Max(1, extractionWorkerCount * 2)
                    : 1;
                FullScanExtractionQueueCapacityForTesting?.Invoke(extractionQueueCapacity);
                using var extractionResults = new BlockingCollection<FullScanFileWorkItem>(extractionQueueCapacity);
                using var extractionStallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var mainSymbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(
                    () => new SymbolExtractionWorkerClient(options.MaxFileSizeBytes));
                var extractionCancellationToken = extractionStallCts.Token;
                var nextExtractionIndex = -1;
                var workers = Enumerable.Range(0, extractionWorkerCount)
                    .Select(workerIndex => Task.Factory.StartNew(() =>
                    {
                        using var workerSymbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(
                            () => new SymbolExtractionWorkerClient(options.MaxFileSizeBytes));
                        while (true)
                        {
                            extractionCancellationToken.ThrowIfCancellationRequested();
                            var extractionIndex = Interlocked.Increment(ref nextExtractionIndex);
                            if (extractionIndex >= extractionWorkItemCount)
                                break;

                            var fileIndex = extractionFileIndexes == null
                                ? extractionIndex
                                : extractionFileIndexes[extractionIndex];
                            var target = fileTargets[fileIndex];
                            var filePath = target.FilePath;
                            var relativeFilePath = target.RelativePath;
                            var displayRelativePath = target.DisplayRelativePath;
                            try
                            {
                                Volatile.Write(ref activeExtractionPhases[workerIndex], new(displayRelativePath, "reading"));
                                FullScanFileContentLoadForTesting?.Invoke(displayRelativePath);
                                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                                    filePath,
                                    relativeFilePath,
                                    target.Language,
                                    extractionCancellationToken);
                                var record = loaded.Record;
                                var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                                if (target.Language == "csharp"
                                    && workspaceFileSnapshots != null
                                    && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                        target.FilePath,
                                        target.IndexPath,
                                        target.DisplayRelativePath,
                                        record.Size,
                                        record.Modified,
                                        workspaceFileSnapshots,
                                        out var changedPath,
                                        extractionCancellationToken))
                                {
                                    extractionResults.Add(
                                        FullScanFileWorkItem.Failure(
                                            fileIndex,
                                            filePath,
                                            displayRelativePath,
                                            "csharp_workspace_validation",
                                            new CSharpWorkspaceSnapshotDriftException(
                                                FormatCSharpWorkspaceSnapshotPath(changedPath))),
                                        extractionCancellationToken);
                                    continue;
                                }
                                var content = loaded.Content;
                                var rawBytes = loaded.RawBytes;
                                var warning = loaded.Warning;
                                var hasOversizeLine = loaded.HasOversizeLine;
                                IReadOnlyList<ChunkRecord>? chunks = null;
                                IReadOnlyList<SymbolRecord>? symbols = null;
                                IReadOnlyList<ReferenceRecord>? references = null;
                                IReadOnlyList<FileIssue>? issues = null;
                                var generatedSuppressionIssue = target.GeneratedExtractionSuppressed
                                    ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                                    : null;
                                if (parallelizeExtraction)
                                {
                                    Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "chunking"));
                                    chunks = ChunkSplitter.SplitNormalized(0, content, hasOversizeLine, record.Lines);
                                    if (generatedSuppressionIssue != null)
                                    {
                                        symbols = [];
                                        references = [];
                                        Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                                        issues = AppendIssueIfMissing(
                                            FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, hasOversizeLine, loaded.ConflictMarkerLine),
                                            generatedSuppressionIssue);
                                        extractionResults.Add(
                                            FullScanFileWorkItem.Precomputed(
                                                fileIndex,
                                                filePath,
                                                displayRelativePath,
                                                record,
                                                warning,
                                                chunks,
                                                symbols,
                                                references,
                                                issues,
                                                generatedSuppressionIssue,
                                                generatedSuppressionChecked: true),
                                            extractionCancellationToken);
                                        continue;
                                    }
                                    Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "symbols"));
                                    FullScanFilePhaseForTesting?.Invoke(record.Path, "symbols");
                                    var symbolExtraction = ExtractSymbolsWithStallTimeout(
                                        0,
                                        record.Lang,
                                        content,
                                        filePath,
                                        projectRoot,
                                        record.Path,
                                        Volatile.Read(ref activeExtractionPhases[workerIndex])!.Format(),
                                        true,
                                        hasOversizeLine,
                                        loaded.ConflictMarkerLine,
                                        workerSymbolExtractionWorker.Value,
                                        extractionCancellationToken);
                                    symbols = symbolExtraction.Symbols;
                                    var symbolRegexTimeoutIssue = symbolExtraction.RegexTimeoutIssue;
                                    if (string.Equals(record.Lang, "csharp", StringComparison.Ordinal))
                                    {
                                        var sourceFileContext = new FileContext(
                                            projectRoot,
                                            record.Path,
                                            filePath,
                                            record.Lang);
                                        postExtractionHooks.ObserveCSharpStaticInterfaceSourceSymbols(
                                            sourceFileContext,
                                            symbols);
                                    }
                                    if (symbols.Count > options.MaxSymbolsPerFile)
                                    {
                                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                                        IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                                            ? [issue]
                                            : AppendIssue([symbolRegexTimeoutIssue], issue);
                                        extractionResults.Add(
                                            FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, issue.Message, [], [], [], capIssues),
                                            extractionCancellationToken);
                                        continue;
                                    }
                                    SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                                    FileIssue? referenceRegexTimeoutIssue = null;
                                    ReferenceExtractionResult? referenceExtraction = null;
                                    if (options.SymbolsOnly)
                                    {
                                        references = [];
                                    }
                                    else
                                    {
                                        Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "references"));
                                        FullScanFilePhaseForTesting?.Invoke(record.Path, "references");
                                        using var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction");
                                        referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                                            0,
                                            record.Lang,
                                            content,
                                            hasOversizeLine,
                                            symbols,
                                            record.Path,
                                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                                            extractionCancellationToken,
                                            maxReferenceCount: options.MaxReferencesPerFile + 1,
                                            conflictMarkerLine: loaded.ConflictMarkerLine,
                                            workspaceRoot: projectRoot,
                                            csharpStaticInterfaceMemberLookups: csharpWorkspace.StaticInterfaceMemberLookups);
                                        references = referenceExtraction.References;
                                        referenceRegexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                                    }
                                    Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                                    issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, hasOversizeLine, loaded.ConflictMarkerLine);
                                    if (symbolRegexTimeoutIssue != null)
                                        issues = AppendIssue(issues, symbolRegexTimeoutIssue);
                                    if (referenceRegexTimeoutIssue != null)
                                        issues = AppendIssue(issues, referenceRegexTimeoutIssue);
                                    if (referenceExtraction != null)
                                        issues = AppendReferenceExtractionDiagnosticIssues(issues, record.Path, referenceExtraction.Diagnostics);
                                    if (references.Count > options.MaxReferencesPerFile)
                                    {
                                        var issue = BuildReferenceCountExceededIssue(record.Path, references.Count, options.MaxReferencesPerFile);
                                        references = [];
                                        issues = AppendIssue(issues, issue);
                                    }
                                }
                                else
                                {
                                    Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                                    issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, hasOversizeLine, loaded.ConflictMarkerLine);
                                }
                                extractionResults.Add(
                                    parallelizeExtraction
                                        ? FullScanFileWorkItem.Precomputed(
                                            fileIndex,
                                            filePath,
                                            displayRelativePath,
                                            record,
                                            warning,
                                            chunks!,
                                            symbols!,
                                            references!,
                                            issues!,
                                            generatedSuppressionIssue,
                                            generatedSuppressionChecked: true)
                                        : FullScanFileWorkItem.Success(
                                            fileIndex,
                                            filePath,
                                            displayRelativePath,
                                            record,
                                            content,
                                            hasOversizeLine,
                                            loaded.ConflictMarkerLine,
                                            warning,
                                            chunks,
                                            symbols,
                                            references,
                                            issues,
                                            generatedSuppressionIssue,
                                            generatedSuppressionChecked: true),
                                    extractionCancellationToken);
                            }
                            catch (OperationCanceledException) when (extractionCancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (FileIndexer.BinaryFileSkippedException ex)
                            {
                                var record = indexer.BuildSkippedFileRecord(filePath, relativeFilePath, target.Language);
                                var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                                if (target.Language == "csharp"
                                    && workspaceFileSnapshots != null
                                    && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                        target.FilePath,
                                        target.IndexPath,
                                        target.DisplayRelativePath,
                                        record.Size,
                                        record.Modified,
                                        workspaceFileSnapshots,
                                        out var changedPath,
                                        extractionCancellationToken))
                                {
                                    extractionResults.Add(
                                        FullScanFileWorkItem.Failure(
                                            fileIndex,
                                            filePath,
                                            displayRelativePath,
                                            "csharp_workspace_validation",
                                            new CSharpWorkspaceSnapshotDriftException(
                                                FormatCSharpWorkspaceSnapshotPath(changedPath))),
                                        extractionCancellationToken);
                                    continue;
                                }
                                var issue = BuildNullByteIssue(ex);
                                var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                                extractionResults.Add(
                                    FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, sanitizedMessage, [], [], [], [issue]),
                                    extractionCancellationToken);
                            }
                            catch (FileIndexer.FileTooLargeSkippedException ex)
                            {
                                var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                                var record = indexer.BuildSkippedFileRecord(filePath, relativeFilePath, target.Language);
                                var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                                if (target.Language == "csharp"
                                    && workspaceFileSnapshots != null
                                    && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                        target.FilePath,
                                        target.IndexPath,
                                        target.DisplayRelativePath,
                                        record.Size,
                                        record.Modified,
                                        workspaceFileSnapshots,
                                        out var changedPath,
                                        extractionCancellationToken))
                                {
                                    extractionResults.Add(
                                        FullScanFileWorkItem.Failure(
                                            fileIndex,
                                            filePath,
                                            displayRelativePath,
                                            "csharp_workspace_validation",
                                            new CSharpWorkspaceSnapshotDriftException(
                                                FormatCSharpWorkspaceSnapshotPath(changedPath))),
                                        extractionCancellationToken);
                                    continue;
                                }
                                var issue = new FileIssue
                                {
                                    Path = ex.RelativePath,
                                    Kind = "file_too_large",
                                    Line = 0,
                                    Message = sanitizedMessage,
                                };
                                extractionResults.Add(
                                    FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, sanitizedMessage, [], [], [], [issue]),
                                    extractionCancellationToken);
                            }
                            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                            {
                                var item = target.Language == "csharp" && csharpWorkspaceFileSnapshots != null
                                    ? FullScanFileWorkItem.Failure(
                                        fileIndex,
                                        filePath,
                                        displayRelativePath,
                                        "csharp_workspace_validation",
                                        new CSharpWorkspaceSnapshotDriftException(target.DisplayRelativePath))
                                    : FullScanFileWorkItem.Skipped(
                                        fileIndex,
                                        filePath,
                                        displayRelativePath,
                                        $"{displayRelativePath}: skipped because it was deleted during indexing.");
                                extractionResults.Add(item, extractionCancellationToken);
                            }
                            catch (Exception ex)
                            {
                                var failedPhase = Volatile.Read(ref activeExtractionPhases[workerIndex])?.Phase ?? "unknown";
                                extractionResults.Add(FullScanFileWorkItem.Failure(fileIndex, filePath, displayRelativePath, failedPhase, ex), extractionCancellationToken);
                            }
                            finally
                            {
                                Volatile.Write(ref activeExtractionPhases[workerIndex], null);
                            }
                        }
                    }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                    .ToArray();

                _ = Task.WhenAll(workers).ContinueWith(
                    task =>
                    {
                        extractionResults.CompleteAdding();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var extractionStallTimeout = IndexExtractionStallTimeoutForTesting?.Invoke() ?? IndexExtractionStallTimeout;
                var lastExtractionProgressAt = Stopwatch.GetTimestamp();
                while (!extractionResults.IsCompleted)
                {
                    ThrowIfFullScanCancelled(processed, files.Count);
                    if (!extractionResults.TryTake(out var item, millisecondsTimeout: 100))
                    {
                        ThrowIfFullScanExtractionStalled(
                            processed,
                            files.Count,
                            extractionStallTimeout,
                            lastExtractionProgressAt,
                            currentJsonIndexFile,
                            activeExtractionPhases,
                            extractionStallCts.Cancel);
                        continue;
                    }

                    lastExtractionProgressAt = Stopwatch.GetTimestamp();
                    currentJsonIndexFile = item.RelativePath;
                    var indexFilePhase = item.FailurePhase ?? "preparing";
                    var itemFileExtracted = item.Record == null ? 0L : 1L;
                    var itemChunksExtracted = item.Chunks?.Count ?? 0L;
                    var itemSymbolsExtracted = item.Symbols?.Count ?? 0L;
                    var itemReferencesExtracted = item.References?.Count ?? 0L;
                    EnsureIndexingActivityVisible();
                    if (item.Exception is IndexExtractionStalledException stalledException)
                        RethrowPreservingStackTrace(stalledException);

                    try
                    {
                        var itemTargetsCSharp = item.FileIndex >= 0
                            && fileTargets[item.FileIndex].Language == "csharp";
                        if (itemTargetsCSharp)
                        {
                            var deferCurrentItem = deferCSharpMutationsForIncompleteScan;
                            if (!deferCurrentItem
                                && item.Exception is CSharpWorkspaceSnapshotDriftException driftException)
                            {
                                DeferCSharpMutationsForLoadedSnapshotDrift(driftException.Path);
                                deferCurrentItem = true;
                            }
                            else if (!deferCurrentItem
                                     && item.Record != null
                                     && csharpWorkspaceFileSnapshots is { } workspaceFileSnapshots
                                     && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                         item.FilePath,
                                         fileTargets[item.FileIndex].IndexPath,
                                         fileTargets[item.FileIndex].DisplayRelativePath,
                                         item.Record.Size,
                                         item.Record.Modified,
                                         workspaceFileSnapshots,
                                         out var changedPath,
                                         cancellationToken))
                            {
                                DeferCSharpMutationsForLoadedSnapshotDrift(
                                    changedPath ?? fileTargets[item.FileIndex].DisplayRelativePath);
                                deferCurrentItem = true;
                            }

                            if (deferCurrentItem)
                            {
                                skipped++;
                                processed++;
                                currentJsonIndexFile = null;
                                ThrowIfFullScanCancelled(processed, files.Count);
                                ReportJsonIndexProgressIfNeeded();
                                if (!options.Json && !options.Quiet)
                                {
                                    indexProgress.Pause();
                                    ConsoleUi.PrintProgress(processed, files.Count);
                                    indexProgress.Resume();
                                }
                                continue;
                            }
                        }

                        if (item.Exception != null)
                            RethrowPreservingStackTrace(item.Exception);

                        if (item.Record == null)
                        {
                            warnings++;
                            warningList.Add(new CliJsonMessage(currentJsonIndexFile, item.Warning ?? "File skipped"));
                            if (!options.Json && !options.Quiet && item.Warning != null)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintWarning(item.Warning);
                                indexProgress.Resume();
                            }

                            if (writer.HasFileAtPath(currentJsonIndexFile))
                            {
                                using var deleteTxn = writer.BeginTransaction(cancellationToken, "full scan delete skipped file");
                                if (writer.DeleteFileByPath(currentJsonIndexFile))
                                {
                                    csharpMetadataTargetsNeedRefresh = true;
                                    RequireTypeScriptAugmentationRefresh();
                                    WriteProjectRootOnce();
                                    deleteTxn.Commit();
                                    ftsMutated = true;
                                }
                            }
                            else
                            {
                                skipped++;
                            }
                            processed++;
                            currentJsonIndexFile = null;
                            ThrowIfFullScanCancelled(processed, files.Count);
                            ReportJsonIndexProgressIfNeeded();
                            if (!options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintProgress(processed, files.Count);
                                indexProgress.Resume();
                            }
                            continue;
                        }

                        var record = item.Record!;
                        readableFileBytes.Remember(item.FileIndex, record.Size);
                        if (item.Warning != null && !options.Json && !options.Quiet)
                        {
                            indexProgress.Pause();
                            ConsoleUi.PrintWarning(item.Warning);
                            indexProgress.Resume();
                        }

                        var generatedSuppressionIssue = item.GeneratedSuppressionChecked
                            ? item.GeneratedSuppressionIssue
                            : indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path);
                        long? existingId = null;
                        if (!forceExtractorRefresh && !options.Rebuild && !startedWithNoIndexedFiles && !options.SymbolsOnly)
                        {
                            var targetRequiresRefresh = TargetRequiresJavaScriptTypeScriptRefresh(record.Lang, record.Path);
                            existingId = writer.GetReusableUnchangedFileId(
                                record.Path,
                                record.Modified,
                                record.Checksum,
                                size: record.Size,
                                lines: record.Lines,
                                language: record.Lang,
                                generated: record.Generated,
                                maxSymbolsPerFile: options.MaxSymbolsPerFile,
                                maxReferencesPerFile: options.MaxReferencesPerFile,
                                generatedExtractionSuppressed: generatedSuppressionIssue != null,
                                allowReuse: symbolKindFilterMatchesPrior
                                    && !targetRequiresRefresh
                                    && !priorSymbolsOnlyGraphOmitted
                                    && (record.Lang != "csharp" || csharpIndexedProjectRootCompatible)
                                    && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                                    && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                                    && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                                    && (record.Lang is not ("verilog" or "systemverilog" or "vhdl") || hdlGraphContractMatchesCurrent)
                                    && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                        }
                        if (existingId != null)
                        {
                            var stalePurged = deferCSharpMutationsForIncompleteScan
                                ? 0
                                : writer.PurgeStaleFilesSharingChecksum(
                                    projectRoot,
                                    record.Path,
                                    record.Checksum);
                            if (stalePurged > 0)
                            {
                                ftsMutated = true;
                                csharpMetadataTargetsNeedRefresh = true;
                                RequireTypeScriptAugmentationRefresh();
                                if (!options.SymbolsOnly)
                                    mutualRecursionRefreshNeeded = true;
                            }
                            skipped++;
                            processed++;
                            if (!string.IsNullOrWhiteSpace(record.Lang))
                            {
                                skippedSymbolExtractorLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                                skippedSymbolExtractorLanguages.Add(record.Lang);
                            }
                            if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang) && record.Lang != null)
                            {
                                reusedHotspotFamilyLanguages ??= new HashSet<string>(StringComparer.Ordinal);
                                reusedHotspotFamilyLanguages.Add(record.Lang);
                            }
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.ClearProgressLine();
                                CommandOutputWriter.WriteLine($"  [SKIP] {record.Path}");
                                indexProgress.Resume();
                            }
                            if (!options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintProgress(processed, files.Count);
                                indexProgress.Resume();
                            }
                            ReportJsonIndexProgressIfNeeded();
                            currentJsonIndexFile = null;
                            continue;
                        }

                        if (record.Lang == "csharp")
                            csharpMetadataTargetsNeedRefresh = true;
                        if (record.Lang == "typescript")
                            RequireTypeScriptAugmentationRefresh();

                        var fileFtsMutated = false;
                        using var txn = writer.BeginTransaction(cancellationToken, "full scan file");
                        if (!startedWithNoIndexedFiles)
                        {
                            var stalePurged = deferCSharpMutationsForIncompleteScan
                                ? 0
                                : writer.PurgeStaleFilesSharingChecksum(
                                    projectRoot,
                                    record.Path,
                                    record.Checksum);
                            if (stalePurged > 0)
                            {
                                fileFtsMutated = true;
                                csharpMetadataTargetsNeedRefresh = true;
                                if (!options.SymbolsOnly)
                                    mutualRecursionRefreshNeeded = true;
                            }
                        }
                        var referenceIdentityChanged = false;
                        var fileId = startedWithNoIndexedFiles
                            ? writer.InsertNewFile(record)
                            : writer.UpsertFile(record, out referenceIdentityChanged);
                        if (!options.SymbolsOnly && referenceIdentityChanged)
                            mutualRecursionRefreshNeeded = true;
                        fileFtsMutated = true;
                        currentJsonIndexFile = FormatIndexPhasePath(record.Path, "chunking");
                        indexFilePhase = "chunking";
                        var chunks = item.Chunks == null
                            ? ChunkSplitter.SplitNormalized(
                                fileId,
                                item.Content!,
                                item.HasOversizeLine ?? ChunkSplitter.HasOversizeLine(item.Content!),
                                record.Lines)
                            : ReassignChunkFileIds(item.Chunks, fileId);
                        itemChunksExtracted = chunks.Count;
                        if (generatedSuppressionIssue != null)
                        {
                            writer.InsertChunks(chunks, cancellationToken);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
                            var generatedIssues = AppendIssueIfMissing(
                                RequireWorkItemIssues(item),
                                generatedSuppressionIssue);
                            InsertIssuesForIndexedFile(fileId, generatedIssues);
                            if (options.Verbose)
                                indexProgress.WriteVerbose($"  [OK  ] {record.Path} ({chunks.Count} chunks, generated-code extraction skipped)");
                            currentJsonIndexFile = FormatIndexPhasePath(record.Path, "committing");
                            WriteProjectRootOnce();
                            txn.Commit();
                            ftsMutated |= fileFtsMutated;
                            if (!string.IsNullOrWhiteSpace(record.Lang))
                                indexedSymbolExtractorLanguages.Add(record.Lang);
                            CountFreshInsertedRows(chunkCount: chunks.Count);

                            processed++;
                            if (!options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintProgress(processed, files.Count);
                                indexProgress.Resume();
                            }
                            ReportJsonIndexProgressIfNeeded();
                            currentJsonIndexFile = null;
                            continue;
                        }
                        currentJsonIndexFile = FormatIndexPhasePath(record.Path, "symbols");
                        indexFilePhase = "symbols";
                        FullScanFilePhaseForTesting?.Invoke(record.Path, "symbols");
                        SymbolExtractionResult? symbolExtraction = null;
                        var symbols = item.Symbols == null
                            ? (symbolExtraction = ExtractSymbolsWithStallTimeout(
                                fileId,
                                record.Lang,
                                item.Content!,
                                item.FilePath,
                                projectRoot,
                                record.Path,
                                currentJsonIndexFile,
                                true,
                                item.HasOversizeLine,
                                item.ConflictMarkerLine,
                                mainSymbolExtractionWorker.Value,
                                cancellationToken)).Symbols
                            : ReassignSymbolFileIds(item.Symbols, fileId);
                        itemSymbolsExtracted = symbols.Count;
                        var symbolRegexTimeoutIssue = symbolExtraction?.RegexTimeoutIssue;
                        var fileContext = new FileContext(projectRoot, record.Path, item.FilePath, record.Lang);
                        postExtractionHooks.ObserveCSharpStaticInterfaceSourceSymbols(fileContext, symbols);
                        if (symbols.Count > options.MaxSymbolsPerFile)
                        {
                            var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                            IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                                ? [issue]
                                : AppendIssue([symbolRegexTimeoutIssue], issue);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
                            InsertIssuesForIndexedFile(fileId, capIssues);
                            if (options.Verbose)
                                indexProgress.WriteVerbose($"  [SKIP] {record.Path} ({issue.Message})");
                            txn.Commit();
                            ftsMutated |= fileFtsMutated;
                            CountFreshInsertedRows();
                            processed++;
                            if (!options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintProgress(processed, files.Count);
                                indexProgress.Resume();
                            }
                            ReportJsonIndexProgressIfNeeded();
                            currentJsonIndexFile = null;
                            continue;
                        }
                        if (item.Symbols == null)
                            SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(item.FilePath, record.Lang));
                        var mutableSymbols = symbols as IList<SymbolRecord> ?? symbols.ToList();
                        postExtractionHooks.OnSymbolsExtractedAfterSourceObservation(fileContext, mutableSymbols);
                        symbolsDroppedByKindFilter += options.SymbolKindFilter.Apply(mutableSymbols);
                        symbols = (IReadOnlyList<SymbolRecord>)mutableSymbols;
                        if (symbols.Count > options.MaxSymbolsPerFile)
                        {
                            var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                            IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                                ? [issue]
                                : AppendIssue([symbolRegexTimeoutIssue], issue);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
                            writer.InsertIssues(fileId, capIssues);
                            if (options.Verbose)
                                indexProgress.WriteVerbose($"  [SKIP] {record.Path} ({issue.Message})");
                            txn.Commit();
                            ftsMutated |= fileFtsMutated;
                            CountFreshInsertedRows();
                            processed++;
                            if (!options.Json && !options.Quiet)
                            {
                                indexProgress.Pause();
                                ConsoleUi.PrintProgress(processed, files.Count);
                                indexProgress.Resume();
                            }
                            ReportJsonIndexProgressIfNeeded();
                            currentJsonIndexFile = null;
                            continue;
                        }
                        writer.InsertChunks(chunks, cancellationToken);
                        FileIndexer.ValidateSymbolLineRanges(record, symbols);
                        writer.InsertSymbols(symbols, cancellationToken);
                        if (symbolRegexTimeoutIssue != null)
                        {
                            var baseIssues = RequireWorkItemIssues(item);
                            item = item with { Issues = AppendIssue(baseIssues, symbolRegexTimeoutIssue) };
                        }
                        currentJsonIndexFile = FormatIndexPhasePath(record.Path, "references");
                        indexFilePhase = "references";
                        FullScanFilePhaseForTesting?.Invoke(record.Path, "references");
                        IReadOnlyList<ReferenceRecord> references;
                        if (options.SymbolsOnly)
                        {
                            references = [];
                        }
                        else
                        {
                            FileIssue? regexTimeoutIssue = null;
                            ReferenceExtractionResult? referenceExtraction = null;
                            if (item.References == null)
                            {
                                using var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction");
                                referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                                    fileId,
                                    record.Lang,
                                    item.Content!,
                                    item.HasOversizeLine ?? ChunkSplitter.HasOversizeLine(item.Content!),
                                    symbols,
                                    record.Path,
                                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                                    cancellationToken,
                                    maxReferenceCount: options.MaxReferencesPerFile + 1,
                                    conflictMarkerLine: item.ConflictMarkerLine,
                                    workspaceRoot: projectRoot,
                                    csharpStaticInterfaceMemberLookups: csharpWorkspace.StaticInterfaceMemberLookups);
                                references = referenceExtraction.References;
                                regexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                            }
                            else
                            {
                                references = ReassignReferenceFileIds(item.References, fileId);
                            }
                            itemReferencesExtracted = references.Count;
                            postExtractionHooks.OnReferencesExtracted(fileContext, AsMutableList(references));
                            if (regexTimeoutIssue != null)
                            {
                                var baseIssues = RequireWorkItemIssues(item);
                                item = item with { Issues = AppendIssue(baseIssues, regexTimeoutIssue) };
                            }
                            if (referenceExtraction != null)
                            {
                                var baseIssues = RequireWorkItemIssues(item);
                                item = item with
                                {
                                    Issues = AppendReferenceExtractionDiagnosticIssues(baseIssues, record.Path, referenceExtraction.Diagnostics),
                                };
                            }
                            if (references.Count > options.MaxReferencesPerFile)
                            {
                                var issue = BuildReferenceCountExceededIssue(record.Path, references.Count, options.MaxReferencesPerFile);
                                references = [];
                                var baseIssues = RequireWorkItemIssues(item);
                                item = item with { Issues = AppendIssue(baseIssues, issue) };
                            }
                        }
                        if (startedWithNoIndexedFiles)
                            writer.InsertReferencesForNewFilesInAtomicFileScope(references, refreshMutualRecursionFlags: false, cancellationToken);
                        else
                            writer.InsertReferencesInAtomicFileScope(references, refreshMutualRecursionFlags: false, cancellationToken);
                        if (!options.SymbolsOnly && (symbols.Count > 0 || references.Count > 0))
                            mutualRecursionRefreshNeeded = true;
                        currentJsonIndexFile = FormatIndexPhasePath(record.Path, "validating");
                        indexFilePhase = "validating";
                        var issues = RequireWorkItemIssues(item);
                        InsertIssuesForIndexedFile(fileId, issues);
                        currentJsonIndexFile = FormatIndexPhasePath(record.Path, "committing");
                        indexFilePhase = "committing";
                        WriteProjectRootOnce();
                        txn.Commit();
                        ftsMutated |= fileFtsMutated;
                        if (!string.IsNullOrWhiteSpace(record.Lang))
                            indexedSymbolExtractorLanguages.Add(record.Lang);
                        CountFreshInsertedRows(chunks.Count, symbols.Count, references.Count);

                        indexProgress.WriteVerbose($"  [OK  ] {record.Path} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                    }
                    catch (IndexExtractionStalledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogIndexFileFailure("index_file_failed", item.FilePath, indexFilePhase, ex);
                        errors++;
                        var errorMessage = FormatIndexFileException(ex);
                        errorList.Add(new CliJsonMessage(item.FilePath, errorMessage));
                        if (fileErrorList.Count < PartialIndexFileErrorLimit)
                            fileErrorList.Add(BuildIndexFileError(item.RelativePath, indexFilePhase, ex));
                        if (!options.Json)
                        {
                            indexProgress.Pause();
                            ConsoleUi.ClearProgressLine();
                            ConsoleUi.TryWriteErrorLine(FormatPerFileErrorLine("ERR ", item.FilePath, ex, errorMessage));
                            indexProgress.Resume();
                        }
                    }
                    finally
                    {
                        extractedFiles += itemFileExtracted;
                        extractedChunks += itemChunksExtracted;
                        extractedSymbols += itemSymbolsExtracted;
                        extractedReferences += itemReferencesExtracted;
                    }

                    processed++;
                    currentJsonIndexFile = null;
                    ThrowIfFullScanCancelled(processed, files.Count);
                    ReportJsonIndexProgressIfNeeded();
                    if (!options.Json && !options.Quiet)
                    {
                        indexProgress.Pause();
                        ConsoleUi.PrintProgress(processed, files.Count);
                        indexProgress.Resume();
                    }
                }
                Task.WaitAll(workers, cancellationToken);
            }
            finally
            {
                currentJsonIndexFile = null;
                StopJsonHeartbeat();
                postExtractionHooks?.Dispose();
            }
        }

        indexProgress.Pause();

        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));

        ThrowIfFullScanCancelled(processed, files.Count);
        if (!deferCSharpMutationsForIncompleteScan && mutualRecursionRefreshNeeded)
        {
            WriteFullScanJsonLiveness(options, "finalizing reference graph...");
            var referenceGraphHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "finalizing reference graph");
            try
            {
                writer.RefreshMutualRecursionFlags(cancellationToken);
            }
            finally
            {
                StopFullScanJsonPhaseHeartbeat(referenceGraphHeartbeat);
            }
        }
        if (options.MemoryTrace)
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
        if (errors > 0)
        {
            if (!options.SymbolsOnly)
            {
                // Keep successfully committed graph generations queryable while the separate
                // completeness/currentness signals remain false for the failed-file coverage.
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            writer.MarkIndexIncomplete(["file_index_error"]);
            writer.SetMetaValues(
                (DbContext.LastFailedIndexRunStatusMetaKey, "partial"),
                (DbContext.LastFailedIndexRunModeMetaKey, options.Rebuild ? "rebuild" : "incremental"),
                (DbContext.LastFailedIndexRunStartedAtMetaKey, runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunDurationMsMetaKey, stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesProcessedMetaKey, processed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesTotalMetaKey, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunErrorCodeMetaKey, CommandErrorCodes.IndexPartial),
                (DbContext.LastFailedIndexRunReasonMetaKey, "file_index_error"),
                (DbContext.LastFailedIndexRunProgressPersistedMetaKey, true.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunRecoveryHintMetaKey, "Fix the reported file/extractor error, then rerun the same index command. Successful files and graph edges remain persisted; a rebuild is not required."),
                (DbContext.LastFailedIndexRunFileErrorsMetaKey, JsonSerializer.Serialize(fileErrorList, StatusMetadataJsonContext.Default.ListStatusIndexFileError)));
        }
        if (errors == 0)
        {
            // Full-scan covers the whole repo, so it may always stamp Graph / Issues on
            // success regardless of what the DB carried before. Fold still gates on the
            // backfill verification below because incremental-by-default full scans skip
            // unchanged legacy files whose folded columns remain NULL.
            // full-scan は全repo をカバーするため、Graph / Issues は常に stamp。Fold のみ条件付き。
            writer.MarkIssuesReady();
            if (!options.SymbolsOnly)
            {
                writer.MarkGraphReady();
                writer.MarkHdlGraphContractReady();
            }
            writer.MarkIndexReaderContractsReady(options.SymbolsOnly);
            if (!options.SymbolsOnly
                && !scanHadErrors
                && csharpSourceEvidenceComplete
                && !preservePriorPositiveCSharpSourceNoOp)
                writer.SetCSharpStaticInterfaceSourceEvidence(csharpSourceEvidenceForStamp);
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    FullScanCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets(cancellationToken);
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            graphTableAvailableAfter = !options.SymbolsOnly;
            issuesTableAvailableAfter = true;
            csharpSymbolNameReadyAfter = true;
            if (!options.SymbolsOnly)
            {
                if (typeScriptAugmentationNeedsRefresh
                    || typeScriptAugmentationDirtyNames?.RequiresRefresh == true)
                {
                    if (startedWithNoIndexedFiles && !languageCounts.ContainsKey("typescript"))
                    {
                        writer.MarkTypeScriptAugmentationReady();
                    }
                    else
                    {
                        FullScanTypeScriptAugmentationRebuildForTesting?.Invoke();
                        var augmentationReferences = writer.RebuildTypeScriptAugmentationReferences(
                            projectRoot,
                            useScopedTypeScriptAugmentationRefresh
                                ? typeScriptAugmentationDirtyNames?.DirtyNames
                                : null,
                            cancellationToken);
                        if (startedWithNoIndexedFiles)
                            freshCountReferences += augmentationReferences;
                    }
                }
            }
            RestampHotspotFamilyTrustForFullScan(
                writer,
                reusedHotspotFamilyLanguages,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // Extractor versions describe rows regenerated during this successful run and
            // must not depend on whether the independent fold-key contract can be restamped.
            // extractor version は今回再生成した row の契約であり、独立した fold-key
            // 契約を restamp できるかどうかに依存させない。
            writer.StampSymbolExtractorVersions(indexedSymbolExtractorLanguages);
            writer.StampDynamicReferenceGraphContracts(indexedSymbolExtractorLanguages);
            // FoldReady must reflect reality (#86). Full-scan is INCREMENTAL by default — it
            // skips unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows
            // keep NULL name_folded / *_folded values. Stamping FoldReady anyway would flip
            // readers onto the folded-equality path and silently miss those rows. Verify
            // every existing row has its folded column populated before stamping, and tell
            // the user how to upgrade when not (only --rebuild / a truly-fresh index can
            // guarantee 100% backfill on a legacy DB).
            // fold は実検証が通ったときだけ stamp。legacy DB で skip された行は NULL のため、
            // 黙って stamp すると reader が fold 経路で legacy 行を見逃す。codex #86 レビュー。
            IReadOnlyCollection<string> skippedSymbolExtractorLanguageSet = skippedSymbolExtractorLanguages is null
                ? Array.Empty<string>()
                : skippedSymbolExtractorLanguages;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent
                && foldFingerprintMatchesCurrent
                && writer.SymbolExtractorVersionsMatchCurrent(skippedSymbolExtractorLanguageSet);
            // A normal `index .` run still skips unchanged files. If the prior fold metadata
            // is stale, those skipped rows keep the old physical folded keys, so stamping the
            // NEW metadata for the whole DB would silently misadvertise trust. Only stamp when
            // every row was regenerated this run (skipped==0) or when the carried metadata is
            // already known-good for the current runtime, even if user_version was cleared by
            // an interrupted refresh before MarkFoldReady ran. Issue #97 codex review.
            // 通常の `index .` は unchanged 行を skip するため、事前 metadata が stale なら
            // skipped 行は旧 key のまま残る。全件再生成済み（skipped==0）か、事前 metadata が
            // current と一致しているときだけ FoldReady を stamp する。途中中断で
            // user_version だけ落ちた current DB もここで回復させる。
            if (skipped == 0 || canRestampExistingFoldTrust)
            {
                // Validate once inside BEGIN IMMEDIATE and retain the precise failure category.
                // This avoids scanning every folded value before MarkFoldReady repeats the same
                // work, while preserving the concurrent-writer safety from Issue #1535.
                // BEGIN IMMEDIATE 内で一度だけ検証し、Issue #1535 の concurrent-writer safety と
                // 失敗理由を維持しながら、stamp 前後の重複した全 folded-value scan を避ける。
                var foldStampResult = writer.MarkFoldReadyWithResult(
                    stampCurrentSymbolExtractorVersions: skipped == 0,
                    symbolExtractorLanguagesToStamp: skipped == 0 ? indexedSymbolExtractorLanguages : null);
                foldReadyAfter = foldStampResult == FoldReadyStampResult.Ready;
                if (foldStampResult == FoldReadyStampResult.MissingBackfill)
                {
                    foldReadyReasonAfter = GetFoldReadyReason(false, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);
                }
                else if (foldStampResult == FoldReadyStampResult.NonCurrentFoldValues)
                {
                    foldReadyReasonAfter = DegradationReasonCodes.FoldRowsNotRestamped;
                }
            }
            else
            {
                var backfillReady = writer.AllFoldedColumnsBackfilled(skippedSymbolExtractorLanguageSet);
                foldReadyReasonAfter = GetFoldReadyReason(backfillReady, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);
            }

            StampWriterVersionAndSymbolKindFilter(writer, ConsoleUi.LoadVersion(), options.SymbolKindFilter.Signature);

            // Successful no-op full scans should repair stale / missing explicit-DB roots
            // only after readiness stamps succeed, so an interruption cannot rewrite trust
            // metadata ahead of the success markers.
            // no-op full-scan の explicit DB root backfill は readiness stamp 後に限定する。
            WriteProjectRootOnce();
            writer.WriteUnknownExtensionFileMetadata(scanResult.UnknownExtensionFiles);
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // We deliberately only stamp on full scans (rebuild or default incremental). Update
            // mode (`--commits` / `--files`) leaves the captured HEAD untouched so the next
            // default scan can still detect "branch moved since the last full scan." A
            // best-effort `null` from a non-git workspace simply clears the field. Issue #1508.
            // フル成功時のみ HEAD を記録する。partial update は HEAD を触らないので、後続の
            // full scan が「直近 full scan からブランチが動いた」をきちんと検知できる。
            // 非 git workspace で null になった場合はキーごとクリアされる。Issue #1508。
            var currentHeadBranch = GitHelper.TryGetHeadBranch(projectRoot, cancellationToken);
            var lastFullScanElapsedMs = stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.SetMetaValues(
                (DbContext.IndexedHeadCommitMetaKey, currentHeadCommit),
                (DbContext.IndexedHeadCommitBranchMetaKey, currentHeadBranch),
                (DbContext.LastFullScanElapsedMsMetaKey, lastFullScanElapsedMs));
            // #1509: also stamp the always-updated "last indexed HEAD" triple (SHA + branch +
            // timestamp). Unlike #1508's IndexedHeadCommitMetaKey which only fires here on
            // full scans, this triple is also stamped at the end of incremental update runs
            // (see RunUpdateMode) so cross-session `commits_ahead_of_indexed_head` always
            // reflects the true HEAD at the time of the most recent successful index.
            // #1509: あらゆる成功 index の終端で更新する HEAD トリプル (SHA + branch + 時刻) も
            // ここで stamp する。full scan / partial update を問わず最新の HEAD を保存する。
            TryStampIndexedHeadMetadata(writer, currentHeadCommit, currentHeadBranch, indexRunDiagnostics);
            StampWorkspacePathCaseSensitivity(writer, projectRoot, indexRunDiagnostics, cancellationToken);
            StampIndexedSymlinkPolicy(writer, options.SymlinkPolicy, indexRunDiagnostics);
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(memorySamples);
            var bytesRead = readableFileBytes.MeasureRemaining();
            StampLastIndexRunMetadata(
                writer,
                options.Rebuild ? "rebuild" : "incremental",
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                files.Count,
                skipped,
                errors,
                bytesRead.BytesRead,
                bytesRead.SkippedFileCount,
                processed,
                purged,
                memoryTimelineForStamp,
                indexRunDiagnostics,
                writer.GetReferenceExtractionCapHits(issuesTableAvailableAfter));
        }
        hotspotAggregateRefresh.Complete(cancellationToken);
        writer.ClearBatchInProgress();
        fullScanTxn.Commit();
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("commit", stopwatch));
        stopwatch.Stop();
        var memoryTimeline = BuildMemoryTimeline(memorySamples);
        WarnIfMemoryThresholdExceeded(memoryTimeline);
        // Detect cwd drift between option-parsing and finalize. See RunUpdateMode for the
        // rationale; the warning is informational because we already absolutized paths.
        // Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(initialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            warningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            warnings++;
        }
        warnings += AddPostExtractionHookWarnings(postExtractionHooks, warningList);
        var (totalFiles, totalChunks, totalSymbols, totalReferences) =
            startedWithNoIndexedFiles && !scanHadErrors && errors == 0
                ? (freshCountFiles, freshCountChunks, freshCountSymbols, freshCountReferences)
                : writer.GetCounts();
        var signalReader = new DbReader(writer.Connection);
        var referenceExtractionCapHitsAfter = signalReader.GetReferenceExtractionCapHits();
        var referenceGraphCompleteAfter = signalReader.IsReferenceGraphComplete(
            referenceExtractionCapHitsAfter);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        if (!hasSqlFilesAfter)
        {
            sqlGraphContractSignalAfter = new SqlGraphContractSignal(
                Ready: true,
                Relevant: false,
                DegradedReason: null);
        }
        else if (!sqlGraphContractSignalAfter.Relevant)
        {
            // A failed first SQL target leaves no persisted row for DbReader to classify.
            // Preserve the discovered-language contract in this immediate index response.
            // 最初の SQL target failure で row が無くても index response は degraded を返す。
            sqlGraphContractSignalAfter = new SqlGraphContractSignal(
                Ready: false,
                Relevant: true,
                DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason());
        }
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
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
            {
                Status = errors > 0 ? "partial" : "success",
                Mode = options.Rebuild ? "rebuild" : "incremental",
                Summary = new IndexFullScanSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    FilesExtracted = extractedFiles,
                    FilesPersisted = persistedFiles,
                    ChunksExtracted = extractedChunks,
                    ChunksPersisted = persistedChunks,
                    SymbolsExtracted = extractedSymbols,
                    SymbolsPersisted = persistedSymbols,
                    ReferencesExtracted = extractedReferences,
                    ReferencesPersisted = persistedReferences,
                    FilesScanned = files.Count,
                    FilesSkipped = skipped,
                    FilesPurged = purged,
                    DanglingSymlinksSkipped = scanResult.DanglingSymlinks.Count,
                    Warnings = warnings,
                    Errors = errors,
                    SymbolsDroppedByKindFilter = symbolsDroppedByKindFilter,
                },
                SymbolKindFilter = options.SymbolKindFilter.ToJsonResult(),
                GraphTableAvailable = graphTableAvailableAfter,
                GraphDataCurrent = errors == 0 && graphTableAvailableAfter && referenceGraphCompleteAfter,
                IndexComplete = errors == 0,
                ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
                ReferenceGraphComplete = referenceGraphCompleteAfter,
                ReferenceExtractionCapHits = referenceExtractionCapHitsAfter,
                ErrorCode = errors > 0 ? CommandErrorCodes.IndexPartial : null,
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
                HeadChanged = headChangeDetected,
                PriorIndexedHeadCommit = priorIndexedHeadCommit,
                CurrentHeadCommit = currentHeadCommit,
                HeadChangeNotice = headChangeNotice,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = initialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = errorList.Count > 0 ? errorList : null,
                FileErrors = fileErrorList.Count > 0 ? fileErrorList : null,
                Warnings = warningList.Count > 0 ? warningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, jsonContext.IndexFullScanJsonResult));
        }
        else if (!options.Quiet)
        {
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine("Done.");
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Files", ConsoleUi.FormatNumber(totalFiles), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", ConsoleUi.FormatNumber(totalChunks), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", ConsoleUi.FormatNumber(totalSymbols), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Refs", ConsoleUi.FormatNumber(totalReferences), indent: "  "));
            if (skipped > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", $"{ConsoleUi.FormatNumber(skipped)} (unchanged)", indent: "  "));
            if (scanResult.DanglingSymlinks.Count > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Dangling symlinks", $"{ConsoleUi.FormatNumber(scanResult.DanglingSymlinks.Count)} skipped", indent: "  "));
            if (options.Verbose && scanResult.UnknownExtensionFiles.Count > 0)
            {
                CommandOutputWriter.WriteLine($"  Unknown extension files: {ConsoleUi.FormatNumber(scanResult.UnknownExtensionFiles.Count)}");
                foreach (var relPath in scanResult.UnknownExtensionFiles.Take(5))
                    CommandOutputWriter.WriteLine($"    {relPath}");
                if (scanResult.UnknownExtensionFiles.Count > 5)
                    CommandOutputWriter.WriteLine($"    ... {ConsoleUi.FormatNumber(scanResult.UnknownExtensionFiles.Count - 5)} more");
            }
            if (warnings > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", ConsoleUi.FormatNumber(warnings), indent: "  "));
            if (errors > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Errors", ConsoleUi.FormatNumber(errors), indent: "  "));
            if (symbolsDroppedByKindFilter > 0) CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Filtered symbols", ConsoleUi.FormatNumber(symbolsDroppedByKindFilter), indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Graph", graphTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Issues", issuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# names", csharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", csharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Fold", foldReadyAfter ? "ready" : "degraded", indent: "  "));
            CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed, options.DurationFormat), indent: "  "));
            CommandOutputWriter.WriteLine();
            if (errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to index. Fix the reported files or permissions, then rerun `cdidx index \"{projectRoot}\"` to restore a fully ready index.");
            if (!graphTableAvailableAfter || !issuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !csharpSymbolNameReadyAfter || !csharpMetadataTargetReadyAfter || !foldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(graphTableAvailableAfter, issuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, csharpSymbolNameReadyAfter, csharpMetadataTargetReadyAfter, foldReadyAfter, foldReadyReasonAfter, projectRoot, resolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
            if (errors == 0 && showNextSteps)
                ConsoleUi.PrintIndexCompleteSummary(projectRoot, resolvedDbPath, incremental: !options.Rebuild, files.Count, languageCounts);
        }

        if (!options.Json && !options.Quiet && stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            ConsoleUi.EmitCompletionNotification(
                options.NotifyMode,
                $"cdidx index complete ({ConsoleUi.Counted(files.Count, "file", format: "N0")})");

        return errors > 0 && !options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }


}
