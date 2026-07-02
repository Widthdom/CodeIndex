using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal const int DryRunFileSampleLimit = 100;
    internal const int DryRunErrorSampleLimit = 100;
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
        var projectPath = options.ProjectPath!;
        var dryIndexer = new FileIndexer(
            projectPath,
            ignoreCase,
            ignoreRuleRoot,
            options.MaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: options.SymlinkPolicy,
            generatedCodePatterns: options.GeneratedCodePatterns);
        IEnumerable<string> dryCandidates;
        IEnumerable<string> dryDeleteCandidates;
        bool authoritativeFullScan;
        var errorSamples = new List<CliJsonMessage>();
        var errorCount = 0;
        var dryScanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        DryRunScanMetadata dryScanMetadata;
        var resolvedDbPath = DbPathResolver.NormalizeDbPath(DbPathResolver.ResolveForIndex(projectPath, options.DbPath, options.DataDir).DbPath);
        var dbSnapshot = ReadDryRunDbSnapshot(resolvedDbPath);
        var normalizedProjectRoot = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(dbSnapshot.IndexedProjectRoot)
            ? null
            : Path.GetFullPath(dbSnapshot.IndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var retainedRelativePaths = new HashSet<string>(StringComparer.Ordinal);
        var projectedDeletePaths = new HashSet<string>(StringComparer.Ordinal);
        var projectedPurgePaths = new HashSet<string>(StringComparer.Ordinal);
        var estimatedTableMutations = CreateEmptyEstimatedTableMutations();
        var unsupportedTotal = 0;
        var unknownExtensionTotal = 0;

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

                RecordDryRunError(scanError.Path, scanError.Message);
                if (!options.Json)
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
            }
        }

        if (!TryResolveDryRunCandidates(
            options,
            dryIndexer,
            projectPath,
            jsonOptions,
            cancellationToken,
            RecordDryRunScanErrors,
            out dryCandidates,
            out dryDeleteCandidates,
            out authoritativeFullScan,
            out dryScanMetadata,
            out var exitCode))
        {
            return exitCode;
        }

        var dryFileSamples = new List<string>();
        var dryFileCount = 0;
        var candidatePathsProcessed = 0;
        var candidatePathsTruncated = false;
        var dryRunPathLimit = options.DryRunPathLimit;
        var langCounts = new Dictionary<string, int>();
        if (authoritativeFullScan)
        {
            unknownExtensionTotal = dryScanMetadata.UnknownExtensionFiles.Count;
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
            var displayRelativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectPath, f));
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
            var probe = ProbeDryRunFile(dryIndexer, f, displayRelativePath, knownLanguage);
            if (!probe.Supported)
            {
                if (probe.UnknownExtension)
                    unknownExtensionTotal++;
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
            AddEstimatedUpdateMutation(estimatedTableMutations, dbSnapshot, dbRelativePath);
            if (dryFileSamples.Count < DryRunFileSampleLimit)
                dryFileSamples.Add(displayRelativePath);
            langCounts[probe.Language] = langCounts.GetValueOrDefault(probe.Language) + 1;
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
            AddEstimatedDeleteMutation(estimatedTableMutations, dbSnapshot, relativePath);
        foreach (var relativePath in projectedPurgePaths)
            AddEstimatedDeleteMutation(estimatedTableMutations, dbSnapshot, relativePath);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new IndexDryRunJsonResult
            {
                Status = "dry_run",
                FilesTotal = dryFileCount,
                Estimates = true,
                ProjectedFileUpdates = dryFileCount,
                ProjectedFileDeletes = projectedDeletes,
                ProjectedFilePurges = projectedPurges,
                UnsupportedTotal = unsupportedTotal,
                UnknownExtensionTotal = unknownExtensionTotal,
                CandidatePathLimit = dryRunPathLimit,
                CandidatePathsProcessed = candidatePathsProcessed,
                CandidatePathsTruncated = candidatePathsTruncated,
                TotalsLowerBound = candidatePathsTruncated,
                EstimatedTableMutations = estimatedTableMutations,
                FileSamples = dryFileSamples.Count > 0 ? dryFileSamples : null,
                FileSamplesTruncated = candidatePathsTruncated || dryFileCount > dryFileSamples.Count,
                FileSampleLimit = DryRunFileSampleLimit,
                Languages = langCounts,
                ErrorsTotal = errorCount,
                Errors = errorSamples.Count > 0 ? errorSamples : null,
                ErrorsTruncated = errorCount > errorSamples.Count,
                ErrorLimit = DryRunErrorSampleLimit,
            }, jsonContext.IndexDryRunJsonResult));
        }
        else
        {
            var lowerBound = candidatePathsTruncated ? " (truncated; totals are lower bounds)" : string.Empty;
            Console.WriteLine($"Dry run: {dryFileCount} files would be indexed{lowerBound}");
            if (candidatePathsTruncated)
                Console.WriteLine($"  candidate paths processed {candidatePathsProcessed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of limit {dryRunPathLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  projected deletes {projectedDeletes,6}");
            Console.WriteLine($"  projected purges  {projectedPurges,6}");
            foreach (var (lang, count) in langCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {lang,-12} {count,6}");
        }
        return CommandExitCodes.Success;
    }

    private static bool TryResolveDryRunCandidates(
        IndexCommandOptions options,
        FileIndexer dryIndexer,
        string projectPath,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        Action<IEnumerable<FileIndexer.ScanError>> recordDryRunScanErrors,
        out IEnumerable<string> dryCandidates,
        out IEnumerable<string> dryDeleteCandidates,
        out bool authoritativeFullScan,
        out DryRunScanMetadata scanMetadata,
        out int exitCode)
    {
        dryCandidates = [];
        dryDeleteCandidates = [];
        authoritativeFullScan = false;
        scanMetadata = DryRunScanMetadata.Empty;
        exitCode = CommandExitCodes.Success;

        if (options.UpdateFiles.Count > 0)
        {
            // --files: only the specified files / --files: 指定ファイルのみ
            var relevantIgnoreFileChanged = ContainsRelevantIgnoreFileUpdate(projectPath, options.UpdateFiles);
            var updatePaths = NormalizeUpdateFileTargets(projectPath, options.UpdateFiles, options.Json);
            if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(updatePaths))
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
                dryDeleteCandidates = updatePaths
                    .Where(path => !File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))));
                dryCandidates = updatePaths
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

            if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(changedFiles))
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
                dryDeleteCandidates = changedFiles
                    .Where(path => !File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))));
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
        string? knownLanguage)
    {
        var indexability = indexer.GetFileIndexabilityForIndexing(absolutePath);
        if (indexability == FileIndexer.FileProbeStatus.ProbeFailed)
            return DryRunFileProbe.FromError("Could not probe file for indexability/language.");
        if (indexability != FileIndexer.FileProbeStatus.Supported)
            return DryRunFileProbe.FromUnsupported();

        string? reusableLanguage = knownLanguage;
        if (reusableLanguage == null)
        {
            var detection = indexer.TryDetectLanguageForIndexing(absolutePath);
            if (detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                return DryRunFileProbe.FromError("Could not probe file for indexability/language.");
            if (detection.Status != FileIndexer.FileProbeStatus.Supported)
                return string.IsNullOrEmpty(Path.GetExtension(absolutePath))
                    ? DryRunFileProbe.FromUnsupported()
                    : DryRunFileProbe.FromUnknownExtension();

            reusableLanguage = FileIndexer.CanReuseDetectedLanguageWithoutContent(absolutePath, detection.Language)
                ? detection.Language
                : null;
        }

        try
        {
            var loaded = indexer.BuildLoadedRecordWithRawBytes(
                absolutePath,
                relativePath,
                reusableLanguage,
                indexability);
            var record = loaded.Record;
            return new DryRunFileProbe(
                true,
                record.Lang ?? "unknown",
                record.Checksum,
                loaded.Warning,
                Unsupported: false,
                UnknownExtension: false);
        }
        catch (Exception ex)
        {
            return DryRunFileProbe.FromError(CommandErrorWriter.FormatSanitizedExceptionMessage(ex));
        }
    }

    private static Dictionary<string, long> CreateEmptyEstimatedTableMutations()
        => new(StringComparer.Ordinal)
        {
            ["files"] = 0,
            ["chunks"] = 0,
            ["symbols"] = 0,
            ["symbol_references"] = 0,
            ["reference_lines"] = 0,
            ["file_issues"] = 0,
        };

    private static void AddEstimatedUpdateMutation(
        Dictionary<string, long> mutations,
        DryRunDbSnapshot snapshot,
        string relativePath)
    {
        mutations["files"]++;
        if (snapshot.Files.TryGetValue(relativePath, out var rows))
            AddExistingChildRows(mutations, rows);
    }

    private static void AddEstimatedDeleteMutation(
        Dictionary<string, long> mutations,
        DryRunDbSnapshot snapshot,
        string relativePath)
    {
        if (!snapshot.Files.TryGetValue(relativePath, out var rows))
            return;

        mutations["files"]++;
        AddExistingChildRows(mutations, rows);
    }

    private static void AddExistingChildRows(Dictionary<string, long> mutations, DryRunExistingFileRows rows)
    {
        mutations["chunks"] += rows.Chunks;
        mutations["symbols"] += rows.Symbols;
        mutations["symbol_references"] += rows.SymbolReferences;
        mutations["reference_lines"] += rows.ReferenceLines;
        mutations["file_issues"] += rows.FileIssues;
    }

    private static DryRunDbSnapshot ReadDryRunDbSnapshot(string dbPath)
    {
        try
        {
            if (!dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            {
                return DryRunDbSnapshot.Empty;
            }

            using var connection = new SqliteConnection(DbPathResolver.BuildSqliteConnectionString(dbPath, SqliteOpenMode.ReadOnly));
            connection.Open();
            if (!DryRunTableExists(connection, "files"))
                return DryRunDbSnapshot.Empty;

            var indexedProjectRoot = DryRunReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
            var hasChunks = DryRunTableExists(connection, "chunks");
            var hasSymbols = DryRunTableExists(connection, "symbols");
            var hasSymbolReferences = DryRunTableExists(connection, "symbol_references");
            var hasReferenceLines = DryRunTableExists(connection, "reference_lines");
            var hasFileIssues = DryRunTableExists(connection, "file_issues");

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT f.path,
                       f.checksum,
                       {(hasChunks ? "(SELECT COUNT(*) FROM chunks c WHERE c.file_id = f.id)" : "0")} AS chunks_count,
                       {(hasSymbols ? "(SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id)" : "0")} AS symbols_count,
                       {(hasSymbolReferences ? "(SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id)" : "0")} AS symbol_references_count,
                       {(hasReferenceLines ? "(SELECT COUNT(*) FROM reference_lines l WHERE l.file_id = f.id)" : "0")} AS reference_lines_count,
                       {(hasFileIssues ? "(SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id)" : "0")} AS file_issues_count
                FROM files f
                """;

            var files = new Dictionary<string, DryRunExistingFileRows>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                files[reader.GetString(0)] = new DryRunExistingFileRows(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6));
            }

            return new DryRunDbSnapshot(files, indexedProjectRoot);
        }
        catch (SqliteException)
        {
            return DryRunDbSnapshot.Empty;
        }
        catch (IOException)
        {
            return DryRunDbSnapshot.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return DryRunDbSnapshot.Empty;
        }
    }

    private static bool DryRunTableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1";
        SqliteCommandPolicy.Add(command, "@name", tableName);
        return command.ExecuteScalar() != null;
    }

    private static string? DryRunReadMetaString(SqliteConnection connection, string key)
    {
        if (!DryRunTableExists(connection, "codeindex_meta"))
            return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
        SqliteCommandPolicy.Add(command, "@key", key);
        return command.ExecuteScalar() as string;
    }

    private static int WriteDryRunInterrupted(IndexCommandOptions options, JsonSerializerOptions jsonOptions) => WriteCommandError(
        options.Json,
        jsonOptions,
        "Interrupted before dry-run scan completed.",
        CommandExitCodes.Interrupted,
        "Rerun `cdidx index --dry-run` when you are ready to inspect the candidate files again.",
        CommandErrorCodes.Interrupted);

    private sealed record DryRunDbSnapshot(IReadOnlyDictionary<string, DryRunExistingFileRows> Files, string? IndexedProjectRoot)
    {
        public static DryRunDbSnapshot Empty { get; } = new(new Dictionary<string, DryRunExistingFileRows>(StringComparer.Ordinal), null);
    }

    private readonly record struct DryRunExistingFileRows(
        string? Checksum,
        long Chunks,
        long Symbols,
        long SymbolReferences,
        long ReferenceLines,
        long FileIssues);

    private readonly record struct DryRunScanMetadata(
        bool HadErrors,
        IReadOnlyList<string> NonIndexablePaths,
        IReadOnlyList<string> UnknownExtensionFiles,
        IReadOnlyList<string> ProbeFailedFilePaths,
        IReadOnlyList<string> ListedDirectories,
        IReadOnlyList<string> AttributePrunedDirectories,
        IReadOnlyList<string> NestedRepositories,
        IReadOnlyDictionary<string, string> FileLanguages)
    {
        public static DryRunScanMetadata Empty { get; } = new(
            false,
            [],
            [],
            [],
            [],
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        public static DryRunScanMetadata FromScanResult(FileIndexer.ScanFilesResult scanResult)
            => new(
                scanResult.HadErrors,
                scanResult.NonIndexablePaths,
                scanResult.UnknownExtensionFiles,
                scanResult.ProbeFailedFilePaths,
                scanResult.ListedDirectories,
                scanResult.AttributePrunedDirectories,
                scanResult.NestedRepositories,
                scanResult.FileLanguages);
    }

    private readonly record struct DryRunFileProbe(
        bool Supported,
        string Language,
        string? Checksum,
        string? Error,
        bool Unsupported,
        bool UnknownExtension)
    {
        public static DryRunFileProbe FromError(string message) => new(false, string.Empty, null, message, Unsupported: false, UnknownExtension: false);
        public static DryRunFileProbe FromUnsupported() => new(false, string.Empty, null, null, Unsupported: true, UnknownExtension: false);
        public static DryRunFileProbe FromUnknownExtension() => new(false, string.Empty, null, null, Unsupported: false, UnknownExtension: true);
    }
}
