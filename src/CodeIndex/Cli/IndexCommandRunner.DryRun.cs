using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal const int DryRunFileSampleLimit = 100;
    internal const int DryRunLanguageDetectionLimit = 100;
    internal const int DryRunWarningSampleLimit = 100;
    internal const int DryRunErrorSampleLimit = 100;
    internal const int DryRunParseEstimateFileLimit = 100;
    internal const int DefaultDryRunPathLimit = 100_000;
    internal const int MaxDryRunPathLimit = 1_000_000;
    private const int DryRunScanErrorKeyLimit = 2048;

    private static int RunDryRun(
        IndexCommandOptions options,
        bool ignoreCase,
        string ignoreRuleRoot,
        JsonSerializerOptions jsonOptions,
        CliJsonSerializerContext jsonContext,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var memorySamples = options.MemoryTrace
            ? new List<IndexMemorySampleJsonResult> { CaptureMemorySample("start", stopwatch) }
            : [];
        var projectPath = options.ProjectPath!;
        var resolvedDbPath = DbPathResolver.NormalizeDbPath(DbPathResolver.ResolveForIndex(projectPath, options.DbPath, options.DataDir).DbPath);
        var dryIndexer = new FileIndexer(
            projectPath,
            ignoreCase,
            ignoreRuleRoot,
            options.MaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: options.SymlinkPolicy,
            generatedCodePatterns: options.GeneratedCodePatterns,
            internalIndexDatabasePath: resolvedDbPath);
        IEnumerable<string> dryCandidates;
        IEnumerable<string> dryDeleteCandidates;
        bool authoritativeFullScan;
        var errorSamples = new List<CliJsonMessage>();
        var errorCount = 0;
        var warningSamples = options.OptionWarnings
            .Take(DryRunWarningSampleLimit)
            .ToList();
        var warningCount = options.OptionWarnings.Count;
        var dryScanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        DryRunScanMetadata dryScanMetadata;
        DryRunDbSnapshot dbSnapshot;
        try
        {
            dbSnapshot = ReadDryRunDbSnapshot(resolvedDbPath, options, cancellationToken);
            if (options.ExplicitFileInputs.Count > 0)
            {
                var indexedPaths = CreateExplicitFilesIndexedPathSnapshot(
                    dbSnapshot.Files.Keys,
                    dbSnapshot.ReadFailed);
                var explicitFilesPreflightExitCode = RunExplicitFilesPreflight(
                    options,
                    resolvedDbPath,
                    ignoreCase,
                    ignoreRuleRoot,
                    jsonOptions,
                    cancellationToken,
                    writerLockHeld: false,
                    providedIndexedPaths: indexedPaths);
                if (explicitFilesPreflightExitCode is { } preflightExitCode)
                    return preflightExitCode;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WriteDryRunInterrupted(options, jsonOptions);
        }
        var scopedUpdateSymbolKindFilterMatchesPrior = string.Equals(
                dbSnapshot.SymbolKindFilterSignature,
                options.SymbolKindFilter.Signature,
                StringComparison.Ordinal)
            || (dbSnapshot.SymbolKindFilterSignature == null
                && !options.SymbolKindFilter.IsActive);
        if (IsUpdateMode(options)
            && !scopedUpdateSymbolKindFilterMatchesPrior)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "symbol-kind filter policy cannot change during a scoped update because existing files would keep symbols from the prior index policy",
                CommandExitCodes.UsageError,
                "Run a full index refresh without --files, --commits, or --changed-between when changing --include-symbol-kind or --exclude-symbol-kind.",
                CommandErrorCodes.UsageError);
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("snapshot", stopwatch));
        var normalizedProjectRoot = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(dbSnapshot.IndexedProjectRoot)
            ? null
            : Path.GetFullPath(dbSnapshot.IndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var retainedRelativePaths = new HashSet<string>(StringComparer.Ordinal);
        var projectedDeletePaths = new HashSet<string>(StringComparer.Ordinal);
        var projectedPurgePaths = new HashSet<string>(StringComparer.Ordinal);
        var mutationEstimates = new DryRunMutationEstimateAccumulator();
        if (dbSnapshot.ReadFailed)
            mutationEstimates.MarkAllUnknown("index_snapshot_unavailable");
        var estimatedSymbolsDroppedByKindFilter = 0L;
        var projectedFileUpdates = 0;
        var projectedFileSkips = 0;
        var projectedPolicySkips = 0;
        var projectedSymbolCapHits = 0;
        var projectedReferenceCapHits = 0;
        var parseEstimateFilesProcessed = 0;
        var parseEstimateFilesTruncated = false;
        var projectedCSharpSkips = new List<DryRunProjectedCSharpSkip>();
        var csharpWorkspaceContractDetected = false;
        var csharpWorkspaceEstimateUnavailable = false;
        var unsupportedTotal = 0;
        var unknownExtensionTotal = 0;
        var unknownExtensionPaths = new List<string>();
        using var symbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(
            () => new SymbolExtractionWorkerClient(options.MaxFileSizeBytes));
        var normalizedUpdatePaths = options.UpdateFiles.Count > 0
            ? NormalizeUpdateFileTargets(projectPath, options.UpdateFiles, options.Json)
            : [];

        void RecordDryRunError(string file, string message)
        {
            errorCount++;
            if (errorSamples.Count < DryRunErrorSampleLimit)
                errorSamples.Add(new CliJsonMessage(file, message));
        }

        void RecordDryRunScanErrors(IEnumerable<FileIndexer.ScanError> scanErrors)
        {
            foreach (var scanError in scanErrors)
            {
                var key = $"{scanError.Path}\n{scanError.Message}";
                if (dryScanErrorKeys.Count < DryRunScanErrorKeyLimit)
                {
                    if (!dryScanErrorKeys.Add(key))
                        continue;
                }

                if (scanError.Severity == FileIndexer.ScanIssueSeverity.Warning)
                {
                    warningCount++;
                    if (warningSamples.Count < DryRunWarningSampleLimit)
                        warningSamples.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }
                else
                {
                    RecordDryRunError(scanError.Path, scanError.Message);
                }
                if (!options.Json)
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
            }
        }

        var priorHotspotFamilyVersions = FileIndexer
            .GetHotspotFamilyMarkerLanguages()
            .ToDictionary(
                static language => language,
                language => dbSnapshot.GetMeta(
                    DbContext.GetHotspotFamilyVersionMetaKey(language)),
                StringComparer.Ordinal);
        var priorHotspotFamilyMarkerFingerprints = FileIndexer
            .GetHotspotFamilyMarkerLanguages()
            .ToDictionary(
                static language => language,
                language => dbSnapshot.GetMeta(
                    DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(language)),
                StringComparer.Ordinal);
        if (!TryResolveDryRunCandidates(
            options,
            dryIndexer,
            projectPath,
            normalizedUpdatePaths,
            jsonOptions,
            cancellationToken,
            RecordDryRunScanErrors,
            out dryCandidates,
            out dryDeleteCandidates,
            out authoritativeFullScan,
            out dryScanMetadata,
            out var forceExtractorRefresh,
            out var forceJavaScriptTypeScriptRefresh,
            out var exitCode))
        {
            return exitCode;
        }

        var currentHotspotFamilyMarkerFingerprints = authoritativeFullScan
            ? dryScanMetadata.ProjectMarkerFingerprints
            : GetHotspotFamilyMarkerFingerprints(dryIndexer, cancellationToken);
        var hotspotFamilyTrustMatchesCurrent =
            GetHotspotFamilyTrustMatchesCurrent(
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);

        var dryFileSamples = new List<string>();
        var dryFileCount = 0;
        var candidatePathsProcessed = 0;
        var candidatePathsTruncated = false;
        var dryRunPathLimit = options.DryRunPathLimit;
        var langCounts = new Dictionary<string, int>();
        var languageDetectionSamples = new List<IndexLanguageDetectionJsonResult>();
        var languageDetectionTotal = 0;
        if (authoritativeFullScan)
        {
            unknownExtensionTotal = dryScanMetadata.UnknownExtensionFiles.Count;
            unknownExtensionPaths.AddRange(dryScanMetadata.UnknownExtensionFiles);
            unsupportedTotal = CountUnsupportedNonIndexablePaths(dryScanMetadata);
        }

        foreach (var f in dryCandidates)
        {
            if (candidatePathsProcessed >= dryRunPathLimit)
            {
                candidatePathsTruncated = true;
                break;
            }

            candidatePathsProcessed++;
            var displayRelativePath = FileIndexer.NormalizePathSeparators(
                FileIndexer.GetRelativePathFromDirectory(projectPath, f));
            var dbRelativePath = FileIndexer.NormalizeIndexPath(displayRelativePath);
            var pathFilter = dryIndexer.EvaluatePathFilter(f);
            RecordDryRunScanErrors(pathFilter.Errors);
            if (pathFilter.ShouldSkip)
            {
                if (pathFilter.ShouldDeleteExisting && dbSnapshot.Files.ContainsKey(dbRelativePath))
                    projectedDeletePaths.Add(dbRelativePath);
                continue;
            }

            var knownLanguage = authoritativeFullScan
                ? FileIndexer.GetReusableDetectedLanguage(f, dryScanMetadata.FileLanguages)
                : null;
            var probe = ProbeDryRunFile(
                dryIndexer,
                f,
                displayRelativePath,
                knownLanguage,
                cancellationToken);
            if (probe.PolicySkipped)
            {
                dryFileCount++;
                retainedRelativePaths.Add(dbRelativePath);
                if (dryFileSamples.Count < DryRunFileSampleLimit)
                    dryFileSamples.Add(displayRelativePath);
                langCounts[probe.Language] = langCounts.GetValueOrDefault(probe.Language) + 1;
                var projectedBinarySkip = probe.PolicySkipKind == DryRunPolicySkipKind.Binary
                    && IsDryRunStatReusable(
                        options,
                        dbSnapshot,
                        dbRelativePath,
                        probe.Language,
                        probe.Size,
                        probe.Modified,
                        dryIndexer.IsGeneratedCodeExtractionSuppressed(dbRelativePath),
                        authoritativeFullScan,
                        normalizedProjectRoot,
                        forceExtractorRefresh,
                        forceJavaScriptTypeScriptRefresh,
                        hotspotFamilyTrustMatchesCurrent);
                if (projectedBinarySkip)
                {
                    projectedFileSkips++;
                    if (probe.Language == "csharp")
                    {
                        projectedCSharpSkips.Add(
                            new DryRunProjectedCSharpSkip(
                                dbRelativePath,
                                PolicySkipped: true));
                    }
                    continue;
                }

                projectedFileUpdates++;
                projectedPolicySkips++;
                AddEstimatedExistingUpdateMutations(
                    mutationEstimates,
                    dbSnapshot,
                    dbRelativePath);
                mutationEstimates.AddParsedEstimate(new DryRunParsedMutationEstimate(
                    0,
                    0,
                    0,
                    0,
                    1,
                    0,
                    SymbolCapHit: false,
                    ReferenceCapHit: false));
                if (probe.Error != null)
                {
                    RecordDryRunError(displayRelativePath, probe.Error);
                    if (!options.Json && !options.Quiet)
                        ConsoleUi.PrintWarning($"{displayRelativePath}: {probe.Error}");
                }
                continue;
            }
            if (!probe.Supported)
            {
                if (probe.UnknownExtension)
                {
                    unknownExtensionTotal++;
                    if (!authoritativeFullScan)
                        unknownExtensionPaths.Add(displayRelativePath);
                }
                else if (probe.Unsupported)
                    unsupportedTotal++;

                if (dbSnapshot.Files.ContainsKey(dbRelativePath))
                {
                    projectedDeletePaths.Add(dbRelativePath);
                }
                else if (!authoritativeFullScan && projectRootWritten && probe.Error == null)
                {
                    AddProjectedPartialStalePurges(
                        projectedPurgePaths,
                        dbSnapshot,
                        projectPath,
                        dbRelativePath);
                }

                if (probe.Error != null)
                {
                    RecordDryRunError(displayRelativePath, probe.Error);
                    if (!options.Json && !options.Quiet)
                        ConsoleUi.PrintWarning($"{displayRelativePath}: {probe.Error}");
                }
                continue;
            }

            dryFileCount++;
            if (probe.DetectionSource is { } detectionSource && probe.DetectionConfidence is { } detectionConfidence)
            {
                languageDetectionTotal++;
                if (languageDetectionSamples.Count < DryRunLanguageDetectionLimit)
                {
                    languageDetectionSamples.Add(new IndexLanguageDetectionJsonResult(
                        displayRelativePath,
                        probe.Language,
                        detectionSource,
                        FileIndexer.GetLanguageDetectionConfidenceCode(detectionConfidence)));
                }
            }
            retainedRelativePaths.Add(dbRelativePath);
            if (!authoritativeFullScan)
            {
                AddProjectedPartialChecksumPurges(
                    projectedPurgePaths,
                    dbSnapshot,
                    projectPath,
                    dbRelativePath,
                    probe.Checksum);
                if (projectRootWritten)
                {
                    AddProjectedPartialStalePurges(
                        projectedPurgePaths,
                        dbSnapshot,
                        projectPath,
                        dbRelativePath);
                }
            }
            var projectedSkip = IsDryRunLoadedFileReusable(
                options,
                dbSnapshot,
                dbRelativePath,
                probe.Loaded!.Value.Record,
                dryIndexer.IsGeneratedCodeExtractionSuppressed(dbRelativePath),
                authoritativeFullScan,
                normalizedProjectRoot,
                forceExtractorRefresh,
                forceJavaScriptTypeScriptRefresh,
                hotspotFamilyTrustMatchesCurrent);
            if (projectedSkip)
            {
                projectedFileSkips++;
                if (probe.Language == "csharp")
                {
                    projectedCSharpSkips.Add(
                        new DryRunProjectedCSharpSkip(
                            dbRelativePath,
                            PolicySkipped: false));
                }
            }
            else
            {
                projectedFileUpdates++;
                AddEstimatedExistingUpdateMutations(
                    mutationEstimates,
                    dbSnapshot,
                    dbRelativePath);
                if (parseEstimateFilesProcessed >= DryRunParseEstimateFileLimit)
                {
                    parseEstimateFilesTruncated = true;
                    mutationEstimates.MarkParseUnknown("parse_estimate_file_limit_reached");
                    if (probe.Language == "csharp")
                        csharpWorkspaceEstimateUnavailable = true;
                }
                else
                {
                    parseEstimateFilesProcessed++;
                    try
                    {
                        var injectedFailure = DryRunParseEstimateFailureForTesting?.Invoke(displayRelativePath);
                        if (injectedFailure != null)
                            throw injectedFailure;
                        var parsedEstimate = BuildDryRunParsedMutationEstimate(
                            options,
                            dryIndexer,
                            probe.Loaded!.Value,
                            f,
                            projectPath,
                            symbolExtractionWorker.Value,
                            cancellationToken);
                        mutationEstimates.AddParsedEstimate(parsedEstimate);
                        estimatedSymbolsDroppedByKindFilter += parsedEstimate.SymbolsDroppedByKindFilter;
                        if (parsedEstimate.SymbolCapHit)
                            projectedSymbolCapHits++;
                        if (parsedEstimate.ReferenceCapHit)
                            projectedReferenceCapHits++;
                        csharpWorkspaceContractDetected |=
                            parsedEstimate.CSharpStaticInterfaceContract;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return WriteDryRunInterrupted(options, jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        mutationEstimates.MarkParseUnknown("parse_estimation_failed");
                        if (probe.Language == "csharp")
                            csharpWorkspaceEstimateUnavailable = true;
                        RecordDryRunError(
                            displayRelativePath,
                            $"Parse-only mutation estimate unavailable: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
                    }
                }
            }
            if (dryFileSamples.Count < DryRunFileSampleLimit)
                dryFileSamples.Add(displayRelativePath);
            langCounts[probe.Language] = langCounts.GetValueOrDefault(probe.Language) + 1;
        }

        if (authoritativeFullScan
            && projectedCSharpSkips.Count > 0
            && (csharpWorkspaceContractDetected
                || csharpWorkspaceEstimateUnavailable))
        {
            projectedFileSkips -= projectedCSharpSkips.Count;
            projectedFileUpdates += projectedCSharpSkips.Count;
            projectedPolicySkips += projectedCSharpSkips.Count(
                static skip => skip.PolicySkipped);
            foreach (var skip in projectedCSharpSkips)
            {
                AddEstimatedExistingUpdateMutations(
                    mutationEstimates,
                    dbSnapshot,
                    skip.RelativePath);
            }
            mutationEstimates.MarkParseUnknown(
                csharpWorkspaceContractDetected
                    ? "csharp_workspace_augmentation_required"
                    : "csharp_workspace_preflight_unavailable");
        }

        foreach (var relativePath in dryDeleteCandidates)
        {
            if (candidatePathsProcessed >= dryRunPathLimit)
            {
                candidatePathsTruncated = true;
                break;
            }

            candidatePathsProcessed++;
            var dbRelativePath = FileIndexer.NormalizeIndexPath(relativePath);
            if (dbSnapshot.Files.ContainsKey(dbRelativePath))
                projectedDeletePaths.Add(dbRelativePath);
        }

        if (authoritativeFullScan && dbSnapshot.Files.Count > 0 && !candidatePathsTruncated)
        {
            AddProjectedFullScanPurges(
                projectedPurgePaths,
                dbSnapshot,
                retainedRelativePaths,
                dryScanMetadata);
        }

        projectedPurgePaths.ExceptWith(projectedDeletePaths);

        var projectedDeletes = projectedDeletePaths.Count;
        var projectedPurges = projectedPurgePaths.Count;

        foreach (var relativePath in projectedDeletePaths)
            AddEstimatedDeleteMutation(mutationEstimates, dbSnapshot, relativePath);
        foreach (var relativePath in projectedPurgePaths)
            AddEstimatedDeleteMutation(mutationEstimates, dbSnapshot, relativePath);

        if (candidatePathsTruncated)
            mutationEstimates.MarkAllUnknown("candidate_path_limit_reached");

        var estimatedTableMutations = mutationEstimates.BuildValues();
        var estimatedTableMutationDetails = mutationEstimates.BuildDetails();
        var unknownExtensionClassification = UnknownExtensionClassifier.Classify(unknownExtensionPaths);
        var unknownExtensionGroups = unknownExtensionClassification.Groups
            .Take(UnknownExtensionClassifier.MaxCompletionGroups)
            .ToList();
        var unknownExtensionGroupOmittedCount = Math.Max(
            0,
            unknownExtensionClassification.GroupCount - unknownExtensionGroups.Count);
        var unknownExtensionFileCountLowerBound = candidatePathsTruncated
            || (authoritativeFullScan && dryScanMetadata.HadErrors);
        var unknownExtensionWarning = unknownExtensionClassification.ActionableFileCount > 0
            ? $"{unknownExtensionClassification.ActionableFileCount} file(s) were excluded because no language mapping or extractor was available. {UnknownExtensionClassifier.GetGuidance(unknownExtensionClassification)}"
            : null;
        if (unknownExtensionWarning != null)
        {
            warningCount++;
            if (warningSamples.Count < DryRunWarningSampleLimit)
                warningSamples.Add(new CliJsonMessage("<unknown_extensions>", unknownExtensionWarning));
        }

        if (options.MemoryTrace)
        {
            memorySamples.Add(CaptureMemorySample("scan", stopwatch));
            memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
        }
        var memoryTimeline = BuildMemoryTimeline(memorySamples);
        WarnIfMemoryThresholdExceeded(memoryTimeline);

        if (options.Json)
        {
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new IndexDryRunJsonResult
            {
                Status = "dry_run",
                FilesTotal = dryFileCount,
                Estimates = true,
                ProjectedFileUpdates = projectedFileUpdates,
                ProjectedFileSkips = projectedFileSkips,
                ProjectedPolicySkips = projectedPolicySkips,
                ProjectedFileDeletes = projectedDeletes,
                ProjectedFilePurges = projectedPurges,
                ProjectedSymbolCapHits = projectedSymbolCapHits,
                ProjectedReferenceCapHits = projectedReferenceCapHits,
                UnsupportedTotal = unsupportedTotal,
                UnknownExtensionTotal = unknownExtensionTotal,
                UnknownExtensionFileCount = unknownExtensionTotal,
                UnknownExtensionGroups = unknownExtensionGroups.Count > 0 ? unknownExtensionGroups : null,
                UnknownExtensionGroupCount = unknownExtensionClassification.GroupCount,
                UnknownExtensionGroupsTruncated = unknownExtensionGroupOmittedCount > 0,
                UnknownExtensionGroupLimit = UnknownExtensionClassifier.MaxCompletionGroups,
                UnknownExtensionGroupOmittedCount = unknownExtensionGroupOmittedCount,
                UnknownExtensionDiagnosticsScope = authoritativeFullScan ? "workspace" : "candidate_scope",
                UnknownExtensionFileCountLowerBound = unknownExtensionFileCountLowerBound,
                UnknownExtensionGuidance = unknownExtensionTotal > 0
                    ? UnknownExtensionClassifier.GetGuidance(unknownExtensionClassification)
                    : null,
                CandidatePathLimit = dryRunPathLimit,
                CandidatePathsProcessed = candidatePathsProcessed,
                CandidatePathsTruncated = candidatePathsTruncated,
                TotalsLowerBound = candidatePathsTruncated,
                ParseEstimateFileLimit = DryRunParseEstimateFileLimit,
                ParseEstimateFilesProcessed = parseEstimateFilesProcessed,
                ParseEstimateFilesTruncated = parseEstimateFilesTruncated,
                EstimatedTableMutations = estimatedTableMutations,
                EstimatedTableMutationDetails = estimatedTableMutationDetails,
                SymbolsDroppedByKindFilter = estimatedSymbolsDroppedByKindFilter,
                SymbolKindFilter = options.SymbolKindFilter.ToJsonResult(),
                FileSamples = dryFileSamples.Count > 0 ? dryFileSamples : null,
                FileSamplesTruncated = candidatePathsTruncated || dryFileCount > dryFileSamples.Count,
                FileSampleLimit = DryRunFileSampleLimit,
                Languages = langCounts,
                LanguageDetectionsTotal = languageDetectionTotal,
                LanguageDetections = languageDetectionSamples.Count > 0 ? languageDetectionSamples : null,
                LanguageDetectionsTruncated = languageDetectionTotal > languageDetectionSamples.Count,
                LanguageDetectionLimit = DryRunLanguageDetectionLimit,
                WarningsTotal = warningCount,
                Warnings = warningSamples.Count > 0 ? warningSamples : null,
                WarningsTruncated = warningCount > warningSamples.Count,
                WarningLimit = DryRunWarningSampleLimit,
                ErrorsTotal = errorCount,
                Errors = errorSamples.Count > 0 ? errorSamples : null,
                ErrorsTruncated = errorCount > errorSamples.Count,
                ErrorLimit = DryRunErrorSampleLimit,
                MemoryTimeline = memoryTimeline,
            }, jsonContext.IndexDryRunJsonResult));
        }
        else
        {
            var lowerBound = candidatePathsTruncated ? " (truncated; totals are lower bounds)" : string.Empty;
            CommandOutputWriter.WriteLine($"Dry run: {dryFileCount} indexable files inspected{lowerBound}");
            if (unknownExtensionTotal > 0)
            {
                CommandOutputWriter.WriteLine($"  unknown extensions {unknownExtensionTotal,6}{(unknownExtensionFileCountLowerBound ? " (lower bound)" : string.Empty)}");
                foreach (var group in unknownExtensionGroups)
                    CommandOutputWriter.WriteLine($"    {group.Extension}: {ConsoleUi.FormatNumber(group.Count)} ({group.RecommendedAction})");
                if (unknownExtensionGroupOmittedCount > 0)
                    CommandOutputWriter.WriteLine($"    ... {ConsoleUi.FormatNumber(unknownExtensionGroupOmittedCount)} more extension groups");
            }
            if (candidatePathsTruncated)
                CommandOutputWriter.WriteLine($"  candidate paths processed {candidatePathsProcessed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of limit {dryRunPathLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
            CommandOutputWriter.WriteLine($"  projected updates {projectedFileUpdates,6}");
            CommandOutputWriter.WriteLine($"  projected skips   {projectedFileSkips,6}");
            CommandOutputWriter.WriteLine($"  projected policy skips {projectedPolicySkips,6}");
            CommandOutputWriter.WriteLine($"  projected deletes {projectedDeletes,6}");
            CommandOutputWriter.WriteLine($"  projected purges  {projectedPurges,6}");
            CommandOutputWriter.WriteLine($"  projected symbol cap hits    {projectedSymbolCapHits,6}");
            CommandOutputWriter.WriteLine($"  projected reference cap hits {projectedReferenceCapHits,6}");
            foreach (var metric in DryRunMutationEstimateAccumulator.MetricNames)
            {
                var estimate = estimatedTableMutationDetails[metric];
                var value = estimate.Value?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                var reasons = estimate.UnknownReasons.Count == 0
                    ? string.Empty
                    : $"; reason {string.Join(",", estimate.UnknownReasons)}";
                CommandOutputWriter.WriteLine(
                    $"  estimated {metric,-17} {value,10} ({estimate.Source}, {estimate.Confidence}{reasons})");
            }
            if (parseEstimateFilesTruncated)
                CommandOutputWriter.WriteLine($"  parse estimates capped at {DryRunParseEstimateFileLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} update files");
            foreach (var (lang, count) in langCounts.OrderByDescending(kv => kv.Value))
                CommandOutputWriter.WriteLine($"  {lang,-12} {count,6}");
            foreach (var detection in languageDetectionSamples)
            {
                CommandOutputWriter.WriteLine(
                    $"  language detection {detection.Path}: {detection.Language} ({detection.Source}, confidence {detection.Confidence})");
            }
            if (unknownExtensionWarning != null)
                ConsoleUi.PrintWarning(unknownExtensionWarning);
        }
        return CommandExitCodes.Success;
    }

    private static bool TryResolveDryRunCandidates(
        IndexCommandOptions options,
        FileIndexer dryIndexer,
        string projectPath,
        IReadOnlyList<string> normalizedUpdatePaths,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        Action<IEnumerable<FileIndexer.ScanError>> recordDryRunScanErrors,
        out IEnumerable<string> dryCandidates,
        out IEnumerable<string> dryDeleteCandidates,
        out bool authoritativeFullScan,
        out DryRunScanMetadata scanMetadata,
        out bool forceExtractorRefresh,
        out bool forceJavaScriptTypeScriptRefresh,
        out int exitCode)
    {
        dryCandidates = [];
        dryDeleteCandidates = [];
        authoritativeFullScan = false;
        scanMetadata = DryRunScanMetadata.Empty;
        forceExtractorRefresh = false;
        forceJavaScriptTypeScriptRefresh = false;
        exitCode = CommandExitCodes.Success;

        if (options.UpdateFiles.Count > 0)
        {
            // --files: only the specified files / --files: 指定ファイルのみ
            var relevantIgnoreFileChanged = ContainsRelevantIgnoreFileUpdate(projectPath, options.UpdateFiles);
            forceJavaScriptTypeScriptRefresh =
                ContainsJavaScriptTypeScriptConfigPath(normalizedUpdatePaths);
            forceExtractorRefresh =
                ContainsExtractorConfigurationPath(
                    projectPath,
                    normalizedUpdatePaths)
                || normalizedUpdatePaths.Any(
                    FileIndexer.IsAmbiguousLanguageProjectMarkerPath);
            if (relevantIgnoreFileChanged
                || ContainsIgnoreFilePath(normalizedUpdatePaths)
                || forceJavaScriptTypeScriptRefresh
                || forceExtractorRefresh)
            {
                FileIndexer.ScanFilesResult scanResult;
                try
                {
                    scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = WriteDryRunInterrupted(options, jsonOptions);
                    return false;
                }
                dryCandidates = scanResult.Files;
                // A supported control input still requires the authoritative refresh above.
                // Preserve an explicitly selected missing/unsupported indexed control as a
                // tombstone candidate as well, so dry-run reports the direct cleanup as a
                // delete instead of folding it into an incidental full-scan purge. Attribute
                // probing does not open, parse, or follow the unsupported object.
                // supported control input は上記の authoritative refresh を引き続き必要とする。
                // 明示選択された missing/unsupported の indexed control は tombstone 候補にも
                // 保持し、dry-run で偶発的な full-scan purge ではなく直接 delete として報告する。
                // attribute probe は unsupported object を open/parse/follow しない。
                dryDeleteCandidates = normalizedUpdatePaths.Where(path =>
                {
                    var absolutePath = Path.Combine(
                        projectPath,
                        path.Replace('/', Path.DirectorySeparatorChar));
                    return dryIndexer.GetFileIndexabilityForIndexing(absolutePath)
                        is FileIndexer.FileProbeStatus.Missing
                            or FileIndexer.FileProbeStatus.Unsupported;
                });
                authoritativeFullScan = true;
                scanMetadata = DryRunScanMetadata.FromScanResult(scanResult);
                recordDryRunScanErrors(scanResult.Errors);
            }
            else
            {
                dryDeleteCandidates = normalizedUpdatePaths
                    .Where(path => !File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))));
                dryCandidates = normalizedUpdatePaths
                    .Select(path => Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)));
            }
        }
        else if (options.Commits.Count > 0 || options.ChangedBetweenSpecified)
        {
            // Git update modes: files changed in commits or between refs.
            // Git更新モード: コミットまたはref間の変更ファイル。
            var changedFiles = new SortedSet<string>(StringComparer.Ordinal);
            var relevantIgnoreFileChanged = false;
            var repoRoot = GitHelper.TryGetRepositoryRoot(projectPath, cancellationToken) ?? Path.GetFullPath(projectPath);
            try
            {
                foreach (var commit in options.Commits)
                {
                    var changed = GitHelper.GetChangedFilesFromCommit(projectPath, commit, cancellationToken);
                    var normalized = NormalizeCommitFileTargets(projectPath, repoRoot, changed, out var commitTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                    foreach (var path in normalized)
                        changedFiles.Add(path);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                exitCode = WriteDryRunInterrupted(options, jsonOptions);
                return false;
            }
            catch (Exception ex)
            {
                exitCode = WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files from git commits: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                    CommandExitCodes.UsageError,
                    "Check the commit refs and rerun `cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`.",
                    CommandErrorCodes.UsageError);
                return false;
            }
            if (options.ChangedBetweenRefs.Count == 2)
            {
                try
                {
                    var changed = GitHelper.GetChangedFilesBetweenRefs(projectPath, options.ChangedBetweenRefs[0], options.ChangedBetweenRefs[1], cancellationToken);
                    var normalized = NormalizeCommitFileTargets(projectPath, repoRoot, changed, out var rangeTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;
                    foreach (var path in normalized)
                        changedFiles.Add(path);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = WriteDryRunInterrupted(options, jsonOptions);
                    return false;
                }
                catch (Exception ex)
                {
                    exitCode = WriteCommandError(
                        options.Json,
                        jsonOptions,
                        $"failed to resolve changed files between git refs: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                        CommandExitCodes.UsageError,
                        "Check the refs and rerun `cdidx index <projectPath> --changed-between <old-ref> <new-ref>`.",
                        CommandErrorCodes.UsageError);
                    return false;
                }
            }

            forceJavaScriptTypeScriptRefresh =
                ContainsJavaScriptTypeScriptConfigPath(changedFiles);
            forceExtractorRefresh =
                ContainsExtractorConfigurationPath(projectPath, changedFiles)
                || changedFiles.Any(
                    FileIndexer.IsAmbiguousLanguageProjectMarkerPath);
            if (relevantIgnoreFileChanged
                || ContainsIgnoreFilePath(changedFiles)
                || forceJavaScriptTypeScriptRefresh
                || forceExtractorRefresh)
            {
                FileIndexer.ScanFilesResult scanResult;
                try
                {
                    scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = WriteDryRunInterrupted(options, jsonOptions);
                    return false;
                }
                dryCandidates = scanResult.Files;
                authoritativeFullScan = true;
                scanMetadata = DryRunScanMetadata.FromScanResult(scanResult);
                recordDryRunScanErrors(scanResult.Errors);
            }
            else
            {
                var skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(projectPath, cancellationToken);
                dryDeleteCandidates = changedFiles
                    .Where(path => !IsSparseSkippedPath(skipWorktreePaths, path)
                                   && !File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))));
                dryCandidates = changedFiles
                    .Select(path => Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)));
            }
        }
        else
        {
            FileIndexer.ScanFilesResult scanResult;
            try
            {
                scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                exitCode = WriteDryRunInterrupted(options, jsonOptions);
                return false;
            }
            dryCandidates = scanResult.Files;
            authoritativeFullScan = true;
            scanMetadata = DryRunScanMetadata.FromScanResult(scanResult);
            recordDryRunScanErrors(scanResult.Errors);
        }

        return true;
    }

    private static int CountUnsupportedNonIndexablePaths(DryRunScanMetadata scanMetadata)
    {
        if (scanMetadata.NonIndexablePaths.Count == 0)
            return 0;

        var unknownPaths = scanMetadata.UnknownExtensionFiles.Count > 0
            ? new HashSet<string>(scanMetadata.UnknownExtensionFiles, StringComparer.Ordinal)
            : [];
        var count = 0;
        foreach (var path in scanMetadata.NonIndexablePaths)
        {
            if (!unknownPaths.Contains(path))
                count++;
        }

        return count;
    }

    private static void AddProjectedFullScanPurges(
        HashSet<string> projectedPurgePaths,
        DryRunDbSnapshot dbSnapshot,
        HashSet<string> retainedRelativePaths,
        DryRunScanMetadata scanMetadata)
    {
        if (!scanMetadata.HadErrors)
        {
            foreach (var relativePath in dbSnapshot.Files.Keys)
            {
                if (!retainedRelativePaths.Contains(relativePath))
                    projectedPurgePaths.Add(relativePath);
            }

            return;
        }

        var retainedPaths = new HashSet<string>(retainedRelativePaths, StringComparer.Ordinal);
        foreach (var relativePath in scanMetadata.ProbeFailedFilePaths)
            retainedPaths.Add(FileIndexer.NormalizeIndexPath(relativePath));

        foreach (var relativePath in scanMetadata.NonIndexablePaths)
        {
            var dbPath = FileIndexer.NormalizeIndexPath(relativePath);
            if (dbSnapshot.Files.ContainsKey(dbPath))
                projectedPurgePaths.Add(dbPath);
        }

        var listedDirectories = scanMetadata.ListedDirectories
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        var attributePrunedDirectories = scanMetadata.AttributePrunedDirectories
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        attributePrunedDirectories.UnionWith(scanMetadata.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));

        foreach (var relativePath in dbSnapshot.Files.Keys)
        {
            if (retainedPaths.Contains(relativePath))
                continue;

            if (HasListedParentDirectory(relativePath, listedDirectories)
                || IsUnderAttributePrunedDirectory(relativePath, attributePrunedDirectories))
            {
                projectedPurgePaths.Add(relativePath);
            }
        }
    }

    private static void AddProjectedPartialChecksumPurges(
        HashSet<string> projectedPurgePaths,
        DryRunDbSnapshot dbSnapshot,
        string projectPath,
        string retainedRelativePath,
        string? checksum)
    {
        if (string.IsNullOrEmpty(checksum))
            return;

        foreach (var (relativePath, rows) in dbSnapshot.Files)
        {
            if (string.Equals(relativePath, retainedRelativePath, StringComparison.Ordinal)
                || !string.Equals(rows.Checksum, checksum, StringComparison.Ordinal))
            {
                continue;
            }

            var absolutePath = Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                projectedPurgePaths.Add(relativePath);
        }
    }

    private static void AddProjectedPartialStalePurges(
        HashSet<string> projectedPurgePaths,
        DryRunDbSnapshot dbSnapshot,
        string projectPath,
        string retainedRelativePath)
    {
        var retainedDirectory = GetDirectoryPath(retainedRelativePath);
        var retainedStem = GetRelativeFileStem(retainedRelativePath);
        if (retainedStem.Length == 0)
            return;

        foreach (var relativePath in dbSnapshot.Files.Keys)
        {
            if (string.Equals(relativePath, retainedRelativePath, StringComparison.Ordinal)
                || !string.Equals(GetDirectoryPath(relativePath), retainedDirectory, StringComparison.Ordinal)
                || !string.Equals(GetRelativeFileStem(relativePath), retainedStem, StringComparison.Ordinal))
            {
                continue;
            }

            var absolutePath = Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                projectedPurgePaths.Add(relativePath);
        }
    }

    private static bool HasListedParentDirectory(string path, IReadOnlySet<string> listedDirectories)
        => listedDirectories.Contains(GetDirectoryPath(path));

    private static bool IsUnderAttributePrunedDirectory(string path, IReadOnlySet<string> attributePrunedDirectories)
    {
        if (attributePrunedDirectories.Count == 0)
            return false;

        var directory = GetDirectoryPath(path);
        while (directory.Length > 0)
        {
            if (attributePrunedDirectories.Contains(directory))
                return true;

            var separatorIndex = directory.LastIndexOf('/');
            directory = separatorIndex >= 0 ? directory[..separatorIndex] : string.Empty;
        }

        return false;
    }

    private static string GetDirectoryPath(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex >= 0 ? path[..separatorIndex] : string.Empty;
    }

    private static string GetRelativeFileStem(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        var fileName = slashIndex < 0 ? normalized : normalized[(slashIndex + 1)..];
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex <= 0 ? fileName : fileName[..dotIndex];
    }

    private static DryRunFileProbe ProbeDryRunFile(
        FileIndexer indexer,
        string absolutePath,
        string relativePath,
        string? knownLanguage,
        CancellationToken cancellationToken)
    {
        var indexability = indexer.GetFileIndexabilityForIndexing(absolutePath);
        if (indexability == FileIndexer.FileProbeStatus.ProbeFailed)
            return DryRunFileProbe.FromError("Could not probe file for indexability/language.");
        if (indexability != FileIndexer.FileProbeStatus.Supported)
            return DryRunFileProbe.FromUnsupported();

        string? reusableLanguage = knownLanguage;
        FileIndexer.LanguageDetectionResult? preLoadDetection = null;
        try
        {
            DryRunFileIndexabilityValidatedForTesting?.Invoke(absolutePath);
            var isAmbiguousExtension = FileIndexer.TryGetAmbiguousLanguageDescriptor(
                Path.GetExtension(absolutePath),
                out _);
            if (reusableLanguage == null || isAmbiguousExtension)
            {
                var detection = indexer.TryDetectLanguageForIndexing(
                    absolutePath,
                    knownIndexability: indexability,
                    deferUnknownScriptHeader: true);
                if (reusableLanguage == null)
                {
                    if (detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                        return DryRunFileProbe.FromError("Could not probe file for indexability/language.");
                    if (detection.Status != FileIndexer.FileProbeStatus.Supported)
                    {
                        var unknownLanguageProbe = indexer.ProbeUnknownLanguageForIndexing(
                            absolutePath,
                            relativePath,
                            cancellationToken);
                        detection = unknownLanguageProbe.LanguageDetection;
                        if (detection.Status is FileIndexer.FileProbeStatus.Missing
                            or FileIndexer.FileProbeStatus.ProbeFailed)
                        {
                            return DryRunFileProbe.FromError("Could not probe file for indexability/language.");
                        }
                        if (detection.Status != FileIndexer.FileProbeStatus.Supported)
                        {
                            return unknownLanguageProbe.IsCoverageCandidate
                                ? DryRunFileProbe.FromUnknownExtension()
                                : DryRunFileProbe.FromUnsupported();
                        }
                    }

                    reusableLanguage = FileIndexer.CanReuseDetectedLanguageWithoutContent(absolutePath, detection.Language)
                        ? detection.Language
                        : null;
                }

                if (detection.Status == FileIndexer.FileProbeStatus.Supported)
                    preLoadDetection = detection;
            }

            var loaded = indexer.BuildLoadedRecordWithRawBytes(
                absolutePath,
                relativePath,
                reusableLanguage);
            var record = loaded.Record;
            var reportDetection = loaded.LanguageDetection;
            if (reportDetection.DetectionSource is null
                && preLoadDetection is { DetectionSource: not null } detectedBeforeLoad
                && string.Equals(detectedBeforeLoad.Language, record.Lang, StringComparison.Ordinal))
            {
                reportDetection = detectedBeforeLoad;
            }
            return new DryRunFileProbe(
                true,
                record.Lang ?? "unknown",
                record.Checksum,
                loaded.Warning,
                Unsupported: false,
                UnknownExtension: false,
                DetectionSource: reportDetection.DetectionSource,
                DetectionConfidence: reportDetection.Confidence,
                Loaded: loaded,
                PolicySkipped: false,
                DryRunPolicySkipKind.None,
                record.Size,
                record.Modified);
        }
        catch (FileIndexer.FileTooLargeSkippedException ex)
        {
            var skipped = indexer.BuildSkippedFileRecord(
                absolutePath,
                relativePath,
                reusableLanguage);
            return DryRunFileProbe.FromPolicySkip(
                skipped,
                CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                DryRunPolicySkipKind.FileTooLarge);
        }
        catch (FileIndexer.BinaryFileSkippedException ex)
        {
            var skipped = indexer.BuildSkippedFileRecord(
                absolutePath,
                relativePath,
                reusableLanguage);
            return DryRunFileProbe.FromPolicySkip(
                skipped,
                CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                DryRunPolicySkipKind.Binary);
        }
        catch (Exception ex)
        {
            return DryRunFileProbe.FromError(CommandErrorWriter.FormatSanitizedExceptionMessage(ex));
        }
    }

    private static DryRunParsedMutationEstimate BuildDryRunParsedMutationEstimate(
        IndexCommandOptions options,
        FileIndexer indexer,
        LoadedFileRecord loaded,
        string absolutePath,
        string projectRoot,
        SymbolExtractionWorkerClient symbolExtractionWorker,
        CancellationToken cancellationToken)
    {
        var record = loaded.Record;
        var chunks = ChunkSplitter.SplitNormalized(
            0,
            loaded.Content,
            loaded.Facts);
        var generatedSuppressionIssue = indexer.IsGeneratedCodeExtractionSuppressed(record.Path)
            ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
            : null;
        if (generatedSuppressionIssue != null)
        {
            var generatedIssues = AppendIssueIfMissing(
                FileIndexer.ValidateContent(
                    record.Path,
                    loaded.RawBytes,
                    loaded.Content,
                    record.Lang,
                    loaded.Inspection,
                    loaded.Facts),
                generatedSuppressionIssue);
            return new DryRunParsedMutationEstimate(
                chunks.Count,
                0,
                0,
                0,
                generatedIssues.Count,
                0,
                SymbolCapHit: false,
                ReferenceCapHit: false);
        }

        var symbolExtraction = ExtractSymbolsWithStallTimeout(
            0,
            record.Lang,
            loaded.Content,
            absolutePath,
            projectRoot,
            record.Path,
            FormatIndexPhasePath(record.Path, "dry_run_symbols"),
            true,
            loaded.HasOversizeLine,
            loaded.ConflictMarkerLine,
            symbolExtractionWorker,
            options.SymlinkPolicy,
            cancellationToken);
        var symbols = symbolExtraction.Symbols;
        var csharpStaticInterfaceContract =
            record.Lang == "csharp"
            && CSharpStaticInterfacePrepass
                .HasCSharpStaticInterfaceContractSymbol(symbols);
        if (symbols.Count > options.MaxSymbolsPerFile)
        {
            var issueCount = symbolExtraction.RegexTimeoutIssue == null ? 1 : 2;
            return new DryRunParsedMutationEstimate(
                0,
                0,
                0,
                0,
                issueCount,
                0,
                SymbolCapHit: true,
                ReferenceCapHit: false,
                csharpStaticInterfaceContract);
        }

        SymbolExtractor.ApplyFamilyScope(
            symbols,
            indexer.GetFamilyScopeKey(absolutePath, record.Lang),
            record.Lang);
        var symbolsDroppedByKindFilter = options.SymbolKindFilter.Apply(symbols);
        if (symbols.Count > options.MaxSymbolsPerFile)
        {
            var issueCount = symbolExtraction.RegexTimeoutIssue == null ? 1 : 2;
            return new DryRunParsedMutationEstimate(
                0,
                0,
                0,
                0,
                issueCount,
                symbolsDroppedByKindFilter,
                SymbolCapHit: true,
                ReferenceCapHit: false,
                csharpStaticInterfaceContract);
        }

        FileIndexer.ValidateSymbolLineRanges(record, symbols);
        List<CodeIndex.Models.ReferenceRecord> references;
        FileIssue? referenceRegexTimeoutIssue = null;
        ReferenceExtractionResult? referenceExtraction = null;
        if (options.SymbolsOnly)
        {
            references = [];
        }
        else
        {
            using var regexTimeouts = BoundedRegex.CaptureTimeouts(
                record.Lang,
                "reference_extraction");
            referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                0,
                record.Lang,
                loaded.Content,
                loaded.HasOversizeLine,
                symbols,
                record.Path,
                workspaceSymbols: null,
                cancellationToken,
                maxReferenceCount: options.MaxReferencesPerFile + 1,
                conflictMarkerLine: loaded.ConflictMarkerLine,
                workspaceRoot: projectRoot,
                csharpStaticInterfaceMemberLookups: null);
            references = referenceExtraction.References;
            referenceRegexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
        }

        var extractedReferenceCount = references.Count;
        var referenceCapHit = extractedReferenceCount > options.MaxReferencesPerFile;
        if (referenceCapHit)
            references = [];

        IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(
            record.Path,
            loaded.RawBytes,
            loaded.Content,
            record.Lang,
            loaded.Inspection,
            loaded.Facts);
        if (symbolExtraction.RegexTimeoutIssue != null)
            issues = AppendIssue(issues, symbolExtraction.RegexTimeoutIssue);
        if (referenceRegexTimeoutIssue != null)
            issues = AppendIssue(issues, referenceRegexTimeoutIssue);
        if (referenceExtraction != null)
        {
            issues = AppendReferenceExtractionDiagnosticIssues(
                issues,
                record.Path,
                referenceExtraction.Diagnostics);
        }
        if (referenceCapHit)
        {
            issues = AppendIssue(
                issues,
                BuildReferenceCountExceededIssue(
                    record.Path,
                    extractedReferenceCount,
                    options.MaxReferencesPerFile));
        }

        var referenceLines = references
            .Select(reference => (reference.Line, reference.Context))
            .Distinct()
            .Count();
        return new DryRunParsedMutationEstimate(
            chunks.Count,
            symbols.Count,
            references.Count,
            referenceLines,
            issues.Count,
            symbolsDroppedByKindFilter,
            SymbolCapHit: false,
            ReferenceCapHit: referenceCapHit,
            csharpStaticInterfaceContract);
    }

    private static bool IsDryRunLoadedFileReusable(
        IndexCommandOptions options,
        DryRunDbSnapshot snapshot,
        string relativePath,
        FileRecord record,
        bool generatedExtractionSuppressed,
        bool authoritativeFullScan,
        string projectRoot,
        bool forceExtractorRefresh,
        bool forceJavaScriptTypeScriptRefresh,
        IReadOnlyDictionary<string, bool>
            hotspotFamilyTrustMatchesCurrent)
    {
        if (!snapshot.Files.TryGetValue(relativePath, out var existing)
            || !IsDryRunReuseAllowed(
                options,
                snapshot,
                relativePath,
                record.Lang,
                authoritativeFullScan,
                projectRoot,
                forceExtractorRefresh,
                forceJavaScriptTypeScriptRefresh,
                hotspotFamilyTrustMatchesCurrent)
            || !existing.ContentReuseEligible
            || existing.GeneratedExtractionSuppressed
                != generatedExtractionSuppressed)
        {
            return false;
        }

        var statMatches = existing.StatReuseEligible
            && existing.ModifiedUtc == record.Modified
            && existing.Size == record.Size
            && string.Equals(
                existing.Language,
                record.Lang,
                StringComparison.Ordinal);
        var checksumMatches = !string.IsNullOrEmpty(record.Checksum)
            && string.Equals(
                existing.Checksum,
                record.Checksum,
                StringComparison.Ordinal)
            && existing.Lines == record.Lines;
        return statMatches || checksumMatches;
    }

    private static bool IsDryRunStatReusable(
        IndexCommandOptions options,
        DryRunDbSnapshot snapshot,
        string relativePath,
        string language,
        long? size,
        DateTime? modified,
        bool generatedExtractionSuppressed,
        bool authoritativeFullScan,
        string projectRoot,
        bool forceExtractorRefresh,
        bool forceJavaScriptTypeScriptRefresh,
        IReadOnlyDictionary<string, bool>
            hotspotFamilyTrustMatchesCurrent)
    {
        if (!size.HasValue
            || !modified.HasValue
            || !snapshot.Files.TryGetValue(relativePath, out var existing)
            || !existing.StatReuseEligible
            || existing.GeneratedExtractionSuppressed
                != generatedExtractionSuppressed
            || !IsDryRunReuseAllowed(
                options,
                snapshot,
                relativePath,
                language,
                authoritativeFullScan,
                projectRoot,
                forceExtractorRefresh,
                forceJavaScriptTypeScriptRefresh,
                hotspotFamilyTrustMatchesCurrent))
        {
            return false;
        }

        return existing.Size == size.Value
            && existing.ModifiedUtc == modified.Value
            && string.Equals(
                existing.Language,
                language,
                StringComparison.Ordinal);
    }

    private static bool IsDryRunReuseAllowed(
        IndexCommandOptions options,
        DryRunDbSnapshot snapshot,
        string indexPath,
        string? language,
        bool authoritativeFullScan,
        string projectRoot,
        bool forceExtractorRefresh,
        bool forceJavaScriptTypeScriptRefresh,
        IReadOnlyDictionary<string, bool>
            hotspotFamilyTrustMatchesCurrent)
    {
        if (options.Rebuild
            || forceExtractorRefresh
            || string.IsNullOrWhiteSpace(language)
            || !string.Equals(
                snapshot.SymbolKindFilterSignature,
                options.SymbolKindFilter.Signature,
                StringComparison.Ordinal)
            || !DryRunExtractorContractsMatchCurrent(snapshot, language))
        {
            return false;
        }

        if (authoritativeFullScan
            && (options.SymbolsOnly
                || snapshot.SymbolsOnlyGraphOmitted
                || !AllowReuseWithCurrentHotspotFamilyTrust(
                    language,
                    hotspotFamilyTrustMatchesCurrent)))
            return false;

        if (forceJavaScriptTypeScriptRefresh
            && (IsJavaScriptTypeScriptLanguage(language)
                || IsJavaScriptTypeScriptConfigPath(indexPath)))
        {
            return false;
        }

        if (language == "csharp")
        {
            var indexedProjectRoot = snapshot.IndexedProjectRoot;
            if (!string.IsNullOrWhiteSpace(indexedProjectRoot)
                && !PathsEqual(
                    Path.GetFullPath(indexedProjectRoot),
                    projectRoot))
            {
                return false;
            }

            var currentContract = DbContext.CSharpSymbolNameContractVersion
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                snapshot.GetMeta(
                    DbContext.CSharpSymbolNameContractVersionMetaKey),
                currentContract,
                StringComparison.Ordinal)
                || snapshot.CSharpStaticInterfaceSourceEvidence is not false)
            {
                return false;
            }
        }

        if (language == "sql")
        {
            var currentContract = DbContext.SqlGraphContractVersion
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                snapshot.GetMeta(DbContext.SqlGraphContractVersionMetaKey),
                currentContract,
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (language is "verilog" or "systemverilog" or "vhdl")
        {
            var currentContract = DbContext.HdlGraphContractVersion
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                snapshot.GetMeta(DbContext.HdlGraphContractVersionMetaKey),
                currentContract,
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DryRunExtractorContractsMatchCurrent(
        DryRunDbSnapshot snapshot,
        string language)
    {
        var storedExtractorVersion = snapshot.GetMeta(
            DbContext.GetSymbolExtractorVersionMetaKey(language));
        if (storedExtractorVersion != null)
        {
            var currentExtractorVersion = SymbolExtractor
                .GetContractVersion(language)
                .ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                storedExtractorVersion,
                currentExtractorVersion,
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!SymbolExtractor
            .RequiresExplicitReferenceGraphContractStamp(language))
        {
            return true;
        }

        var storedGraphContract = snapshot.GetMeta(
            DbContext.GetDynamicReferenceGraphContractVersionMetaKey(
                language));
        var currentGraphContract = SymbolExtractor
            .GetReferenceGraphContractVersion(language)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(
            storedGraphContract,
            currentGraphContract,
            StringComparison.Ordinal);
    }

    private static void AddEstimatedExistingUpdateMutations(
        DryRunMutationEstimateAccumulator mutations,
        DryRunDbSnapshot snapshot,
        string relativePath)
    {
        mutations.Add("files", 1);
        if (!snapshot.Files.TryGetValue(relativePath, out var rows))
            return;

        AddExistingChildRows(mutations, snapshot, rows, rows.Symbols);
    }

    private static void AddEstimatedDeleteMutation(
        DryRunMutationEstimateAccumulator mutations,
        DryRunDbSnapshot snapshot,
        string relativePath)
    {
        if (!snapshot.Files.TryGetValue(relativePath, out var rows))
            return;

        mutations.Add("files", 1);
        AddExistingChildRows(mutations, snapshot, rows, rows.Symbols);
    }

    private static void AddExistingChildRows(
        DryRunMutationEstimateAccumulator mutations,
        DryRunDbSnapshot snapshot,
        DryRunExistingFileRows rows,
        long symbols)
    {
        mutations.AddExisting("chunks", rows.Chunks, snapshot.ChunksAvailable);
        mutations.AddExisting("symbols", symbols, snapshot.SymbolsAvailable);
        mutations.AddExisting(
            "symbol_references",
            rows.SymbolReferences,
            snapshot.SymbolReferencesAvailable);
        mutations.AddExisting(
            "reference_lines",
            rows.ReferenceLines,
            snapshot.ReferenceLinesAvailable);
        mutations.AddExisting("file_issues", rows.FileIssues, snapshot.FileIssuesAvailable);
    }

    private static DryRunDbSnapshot ReadDryRunDbSnapshot(
        string dbPath,
        IndexCommandOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            {
                return DryRunDbSnapshot.Empty;
            }

            using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                dbPath,
                pooling: false,
                out _,
                out _,
                out _,
                out _,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();
            if (!DryRunTableExists(connection, "files"))
                return DryRunDbSnapshot.Empty;

            var metadata = DryRunReadMetadata(connection);
            metadata.TryGetValue(
                DbContext.IndexedProjectRootMetaKey,
                out var indexedProjectRoot);
            var hasChunks = DryRunTableExists(connection, "chunks");
            var hasSymbols = DryRunTableExists(connection, "symbols");
            var hasSymbolReferences = DryRunTableExists(connection, "symbol_references");
            var hasReferenceLines = DryRunTableExists(connection, "reference_lines");
            var hasFileIssues = DryRunTableExists(connection, "file_issues");
            var hasChecksum = DryRunColumnExists(
                connection,
                "files",
                "checksum");
            var hasLanguage = DryRunColumnExists(connection, "files", "lang");
            var hasSize = DryRunColumnExists(connection, "files", "size");
            var hasModified = DryRunColumnExists(
                connection,
                "files",
                "modified");
            var hasLines = DryRunColumnExists(connection, "files", "lines");
            var hasIssueKind = hasFileIssues
                && DryRunColumnExists(
                    connection,
                    "file_issues",
                    "kind");
            var hasIssueOrigin = hasFileIssues
                && DryRunColumnExists(
                    connection,
                    "file_issues",
                    "origin");
            var hasIssueSeverity = hasFileIssues
                && DryRunColumnExists(
                    connection,
                    "file_issues",
                    "severity");
            var hasCurrentIssueSchema = hasIssueKind
                && hasIssueOrigin
                && hasIssueSeverity;

            string IssueExists(string kind) => hasIssueKind
                ? $"""
                    EXISTS (
                        SELECT 1
                        FROM file_issues i
                        WHERE i.file_id = f.id
                          AND i.kind = '{kind}'
                    )
                    """
                : "0";
            var staleIssueMetadata = hasCurrentIssueSchema
                ? """
                    EXISTS (
                        SELECT 1
                        FROM file_issues i
                        WHERE i.file_id = f.id
                          AND (
                              (i.kind IN (
                                  'replacement_char',
                                  'non_utf8_likely',
                                  'bom',
                                  'utf16_bom')
                               AND (
                                   i.origin IS NULL
                                   OR i.severity IS NULL))
                              OR (
                                  i.kind = 'bom'
                                  AND f.path LIKE '%.sln')
                          )
                    )
                    """
                : "0";

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT f.path,
                       {(hasChecksum ? "f.checksum" : "NULL")} AS checksum,
                       {(hasLanguage ? "f.lang" : "NULL")} AS lang,
                       {(hasSize ? "f.size" : "NULL")} AS size,
                       {(hasModified ? "f.modified" : "NULL")} AS modified,
                       {(hasLines ? "f.lines" : "NULL")} AS lines,
                       {(hasChunks ? "(SELECT COUNT(*) FROM chunks c WHERE c.file_id = f.id)" : "0")} AS chunks_count,
                       {(hasSymbols ? "(SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id)" : "0")} AS symbols_count,
                       {(hasSymbolReferences ? "(SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id)" : "0")} AS symbol_references_count,
                       {(hasReferenceLines ? "(SELECT COUNT(*) FROM reference_lines l WHERE l.file_id = f.id)" : "0")} AS reference_lines_count,
                       {(hasFileIssues ? "(SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id)" : "0")} AS file_issues_count,
                       {IssueExists(FileIndexer.GeneratedCodeExtractionSkippedIssueKind)} AS generated_suppressed,
                       {IssueExists("symbol_count_exceeded")} AS symbol_cap_issue,
                       {IssueExists("reference_count_exceeded")} AS reference_cap_issue,
                       {IssueExists("file_too_large")} AS file_too_large_issue,
                       {staleIssueMetadata} AS stale_issue_metadata
                FROM files f
                """;

            var files = new Dictionary<string, DryRunExistingFileRows>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var language = reader.IsDBNull(2)
                    ? null
                    : reader.GetString(2);
                var size = reader.IsDBNull(3)
                    || reader.GetValue(3) is not long rawSize
                    || rawSize < 0
                        ? null
                        : (long?)rawSize;
                var modifiedUtc = reader.GetValue(4) is not string rawModified
                    || !DateTime.TryParse(
                        rawModified,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                            | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedModifiedUtc)
                            ? null
                            : (DateTime?)parsedModifiedUtc;
                var lines = reader.IsDBNull(5)
                    || reader.GetValue(5) is not long rawLines
                    || rawLines < 0
                        ? null
                        : (long?)rawLines;
                var symbols = reader.GetInt64(7);
                var references = reader.GetInt64(8);
                var generatedSuppressed = reader.GetInt64(11) != 0;
                var hasSymbolCapIssue = reader.GetInt64(12) != 0;
                var hasReferenceCapIssue = reader.GetInt64(13) != 0;
                var hasFileTooLargeIssue = reader.GetInt64(14) != 0;
                var hasStaleIssueMetadata = reader.GetInt64(15) != 0;
                var contentReuseEligible = hasLanguage
                    && hasLines
                    && hasSymbols
                    && hasSymbolReferences
                    && hasCurrentIssueSchema
                    && symbols <= options.MaxSymbolsPerFile
                    && references <= options.MaxReferencesPerFile
                    && !hasSymbolCapIssue
                    && !hasReferenceCapIssue
                    && !hasStaleIssueMetadata;
                var maxFileSize = options.MaxFileSizeBytes
                    ?? FileIndexer.DefaultMaxFileSizeBytes;
                var statReuseEligible = contentReuseEligible
                    && size.HasValue
                    && size.Value <= maxFileSize
                    && modifiedUtc.HasValue
                    && !hasFileTooLargeIssue;
                files[reader.GetString(0)] = new DryRunExistingFileRows(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    language,
                    size,
                    modifiedUtc,
                    lines,
                    reader.GetInt64(6),
                    symbols,
                    references,
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    generatedSuppressed,
                    contentReuseEligible,
                    statReuseEligible);
            }

            return new DryRunDbSnapshot(
                files,
                indexedProjectRoot,
                metadata,
                hasChunks,
                hasSymbols,
                hasSymbolReferences,
                hasReferenceLines,
                hasFileIssues,
                ReadFailed: false);
        }
        catch (SqliteException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (global::CodeIndex.CodeIndexException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (IOException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (UnauthorizedAccessException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (ArgumentException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (NotSupportedException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
        catch (System.Security.SecurityException)
        {
            return DryRunDbSnapshot.ReadFailure;
        }
    }

    private static bool DryRunTableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1";
        SqliteCommandPolicy.Add(command, "@name", tableName);
        return command.ExecuteScalar() != null;
    }

    private static bool DryRunColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(
                reader.GetString(1),
                columnName,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string?>
        DryRunReadMetadata(SqliteConnection connection)
    {
        var metadata = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        if (!DryRunTableExists(connection, "codeindex_meta")
            || !DryRunColumnExists(
                connection,
                "codeindex_meta",
                "key")
            || !DryRunColumnExists(
                connection,
                "codeindex_meta",
                "value"))
        {
            return metadata;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM codeindex_meta";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            metadata[reader.GetString(0)] = reader.IsDBNull(1)
                ? null
                : reader.GetString(1);
        }

        return metadata;
    }

    private static int WriteDryRunInterrupted(IndexCommandOptions options, JsonSerializerOptions jsonOptions) => WriteCommandError(
        options.Json,
        jsonOptions,
        "Interrupted before dry-run scan completed.",
        CommandExitCodes.Interrupted,
        "Rerun `cdidx index --dry-run` when you are ready to inspect the candidate files again.",
        CommandErrorCodes.Interrupted);

    private readonly record struct DryRunParsedMutationEstimate(
        long Chunks,
        long Symbols,
        long SymbolReferences,
        long ReferenceLines,
        long FileIssues,
        long SymbolsDroppedByKindFilter,
        bool SymbolCapHit,
        bool ReferenceCapHit,
        bool CSharpStaticInterfaceContract = false);

    private readonly record struct DryRunProjectedCSharpSkip(
        string RelativePath,
        bool PolicySkipped);

    private sealed class DryRunMutationEstimateAccumulator
    {
        internal static readonly string[] MetricNames =
        [
            "files",
            "chunks",
            "symbols",
            "symbol_references",
            "reference_lines",
            "file_issues",
        ];

        private readonly Dictionary<string, long> values = MetricNames.ToDictionary(
            static metric => metric,
            static _ => 0L,
            StringComparer.Ordinal);
        private readonly Dictionary<string, SortedSet<string>> unknownReasons = MetricNames.ToDictionary(
            static metric => metric,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        internal void Add(string metric, long value)
            => values[metric] += value;

        internal void AddExisting(string metric, long value, bool available)
        {
            if (!available)
            {
                MarkUnknown(metric, "existing_table_unavailable");
                return;
            }

            Add(metric, value);
        }

        internal void AddParsedEstimate(DryRunParsedMutationEstimate estimate)
        {
            Add("chunks", estimate.Chunks);
            Add("symbols", estimate.Symbols);
            Add("symbol_references", estimate.SymbolReferences);
            Add("reference_lines", estimate.ReferenceLines);
            Add("file_issues", estimate.FileIssues);
        }

        internal void MarkParseUnknown(string reason)
        {
            foreach (var metric in MetricNames)
            {
                if (metric != "files")
                    MarkUnknown(metric, reason);
            }
        }

        internal void MarkAllUnknown(string reason)
        {
            foreach (var metric in MetricNames)
                MarkUnknown(metric, reason);
        }

        internal Dictionary<string, long?> BuildValues()
            => MetricNames.ToDictionary(
                static metric => metric,
                metric => unknownReasons[metric].Count == 0 ? (long?)values[metric] : null,
                StringComparer.Ordinal);

        internal Dictionary<string, IndexDryRunEstimateJsonResult> BuildDetails()
            => MetricNames.ToDictionary(
                static metric => metric,
                metric =>
                {
                    var reasons = unknownReasons[metric].ToList();
                    var value = reasons.Count == 0 ? (long?)values[metric] : null;
                    var source = metric == "files"
                        ? "filesystem_plan"
                        : "parse_only_and_index_snapshot";
                    var confidence = reasons.Count > 0
                        ? "unknown"
                        : metric == "files"
                            ? "exact"
                            : "estimate";
                    return new IndexDryRunEstimateJsonResult(
                        value,
                        source,
                        confidence,
                        reasons);
                },
                StringComparer.Ordinal);

        private void MarkUnknown(string metric, string reason)
            => unknownReasons[metric].Add(reason);
    }

    private sealed record DryRunDbSnapshot(
        IReadOnlyDictionary<string, DryRunExistingFileRows> Files,
        string? IndexedProjectRoot,
        IReadOnlyDictionary<string, string?> Metadata,
        bool ChunksAvailable,
        bool SymbolsAvailable,
        bool SymbolReferencesAvailable,
        bool ReferenceLinesAvailable,
        bool FileIssuesAvailable,
        bool ReadFailed)
    {
        internal string? SymbolKindFilterSignature
            => GetMeta(SymbolKindFilterMetaKey);

        internal bool SymbolsOnlyGraphOmitted => string.Equals(
            GetMeta(DbContext.SymbolsOnlyGraphOmittedMetaKey),
            "true",
            StringComparison.OrdinalIgnoreCase);

        internal bool? CSharpStaticInterfaceSourceEvidence
            => bool.TryParse(
                GetMeta(
                    DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey),
                out var value)
                    ? value
                    : null;

        internal string? GetMeta(string key)
            => Metadata.TryGetValue(key, out var value) ? value : null;

        public static DryRunDbSnapshot Empty { get; } = new(
            new Dictionary<string, DryRunExistingFileRows>(StringComparer.Ordinal),
            null,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            false,
            false,
            false,
            false,
            false,
            ReadFailed: false);

        public static DryRunDbSnapshot ReadFailure { get; } = Empty with
        {
            ReadFailed = true,
        };
    }

    private readonly record struct DryRunExistingFileRows(
        string? Checksum,
        string? Language,
        long? Size,
        DateTime? ModifiedUtc,
        long? Lines,
        long Chunks,
        long Symbols,
        long SymbolReferences,
        long ReferenceLines,
        long FileIssues,
        bool GeneratedExtractionSuppressed,
        bool ContentReuseEligible,
        bool StatReuseEligible);

    private readonly record struct DryRunScanMetadata(
        bool HadErrors,
        IReadOnlyList<string> NonIndexablePaths,
        IReadOnlyList<string> UnknownExtensionFiles,
        IReadOnlyList<string> ProbeFailedFilePaths,
        IReadOnlyList<string> ListedDirectories,
        IReadOnlyList<string> AttributePrunedDirectories,
        IReadOnlyList<string> NestedRepositories,
        IReadOnlyDictionary<string, string> FileLanguages,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> ProjectMarkerFingerprints)
    {
        public static DryRunScanMetadata Empty { get; } = new(
            false,
            [],
            [],
            [],
            [],
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult>(StringComparer.Ordinal));

        public static DryRunScanMetadata FromScanResult(FileIndexer.ScanFilesResult scanResult)
            => new(
                scanResult.HadErrors,
                scanResult.NonIndexablePaths,
                scanResult.UnknownExtensionFiles,
                scanResult.ProbeFailedFilePaths,
                scanResult.ListedDirectories,
                scanResult.AttributePrunedDirectories,
                scanResult.NestedRepositories,
                scanResult.FileLanguages,
                scanResult.ProjectMarkerFingerprints);
    }

    private readonly record struct DryRunFileProbe(
        bool Supported,
        string Language,
        string? Checksum,
        string? Error,
        bool Unsupported,
        bool UnknownExtension,
        string? DetectionSource,
        FileIndexer.LanguageDetectionConfidence? DetectionConfidence,
        LoadedFileRecord? Loaded,
        bool PolicySkipped,
        DryRunPolicySkipKind PolicySkipKind,
        long? Size,
        DateTime? Modified)
    {
        public static DryRunFileProbe FromError(string message) => new(
            false,
            string.Empty,
            null,
            message,
            Unsupported: false,
            UnknownExtension: false,
            null,
            null,
            null,
            PolicySkipped: false,
            DryRunPolicySkipKind.None,
            null,
            null);

        public static DryRunFileProbe FromUnsupported() => new(
            false,
            string.Empty,
            null,
            null,
            Unsupported: true,
            UnknownExtension: false,
            null,
            null,
            null,
            PolicySkipped: false,
            DryRunPolicySkipKind.None,
            null,
            null);

        public static DryRunFileProbe FromUnknownExtension() => new(
            false,
            string.Empty,
            null,
            null,
            Unsupported: false,
            UnknownExtension: true,
            null,
            null,
            null,
            PolicySkipped: false,
            DryRunPolicySkipKind.None,
            null,
            null);

        public static DryRunFileProbe FromPolicySkip(
            FileRecord record,
            string message,
            DryRunPolicySkipKind policySkipKind) => new(
                false,
                record.Lang ?? "unknown",
                record.Checksum,
                message,
                Unsupported: false,
                UnknownExtension: false,
                null,
                null,
                null,
                PolicySkipped: true,
                policySkipKind,
                record.Size,
                record.Modified);
    }

    private enum DryRunPolicySkipKind
    {
        None,
        Binary,
        FileTooLarge,
    }
}
