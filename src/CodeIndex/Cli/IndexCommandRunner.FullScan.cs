using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static string FormatPerFileErrorLine(string label, string path, Exception ex) =>
        FormatPerFileErrorLine(label, path, ex, FormatIndexFileException(ex));

    internal static string FormatPerFileErrorLine(string label, string path, Exception ex, string message) =>
        $"  [{label}] {CollapseLineBreaks(path)}: {CollapseLineBreaks(message)}";

    internal static string FormatIndexFileException(Exception ex) =>
        ex switch
        {
            RegexMatchTimeoutException timeoutException => RuntimeSafety.FormatRegexTimeout(timeoutException),
            IndexExtractionStalledException stalledException => FormatExtractionStalledMessage(stalledException),
            _ => ex.Message,
        };

    private static string FormatExtractionStalledMessage(IndexExtractionStalledException ex)
    {
        var pathSuffix = string.IsNullOrWhiteSpace(ex.ActivePath) ? string.Empty : $" Last active phase: {ex.ActivePath}.";
        return $"Index extraction made no progress for {ConsoleUi.FormatDuration(ex.Timeout)}.{pathSuffix}";
    }

    private static FileIssue BuildSymbolCountExceededIssue(string path, int symbolCount, int maxSymbolsPerFile) =>
        new()
        {
            Path = path,
            Kind = "symbol_count_exceeded",
            Line = 0,
            Message = $"Symbol extraction produced {symbolCount:N0} symbols, exceeding the --max-symbols-per-file limit of {maxSymbolsPerFile:N0}; file content, symbols, and references were not indexed. Exclude the generated/pathological file or raise --max-symbols-per-file if this is expected.",
        };

    internal static string FormatIndexPhasePath(string path, string phase) =>
        $"{path} ({phase})";

    internal static string? GetJsonIndexHeartbeatPath(string? currentFile, IEnumerable<string> activeExtractionPhases)
    {
        if (!string.IsNullOrEmpty(currentFile))
            return currentFile;

        return activeExtractionPhases.FirstOrDefault(static phase => !string.IsNullOrEmpty(phase));
    }

    internal static bool TryGetFullScanExtractionStallPath(
        int filesProcessed,
        int filesTotal,
        TimeSpan timeout,
        long lastProgressTimestamp,
        string? currentFile,
        IEnumerable<string> activeExtractionPhases,
        out string? activePath)
    {
        activePath = null;
        if (filesTotal <= 0 || filesProcessed >= filesTotal || timeout <= TimeSpan.Zero)
            return false;

        if (Stopwatch.GetElapsedTime(lastProgressTimestamp) < timeout)
            return false;

        activePath = GetJsonIndexHeartbeatPath(currentFile, activeExtractionPhases);
        return true;
    }

    private static void ThrowIfFullScanExtractionStalled(
        int filesProcessed,
        int filesTotal,
        TimeSpan timeout,
        long lastProgressTimestamp,
        string? currentFile,
        ConcurrentDictionary<int, string> activeExtractionPhases,
        Action cancelStalledWork)
    {
        if (!TryGetFullScanExtractionStallPath(
                filesProcessed,
                filesTotal,
                timeout,
                lastProgressTimestamp,
                currentFile,
                activeExtractionPhases.OrderBy(static kvp => kvp.Key).Select(static kvp => kvp.Value),
                out var activePath))
        {
            return;
        }

        cancelStalledWork();
        throw new IndexExtractionStalledException(filesProcessed, filesTotal, timeout, activePath);
    }

    private static List<SymbolRecord> ExtractSymbolsWithStallTimeout(
        long fileId,
        string? lang,
        string content,
        string filePath,
        string projectRoot,
        string phasePath,
        CancellationToken cancellationToken)
    {
        var timeout = IndexExtractionStallTimeoutForTesting?.Invoke() ?? IndexExtractionStallTimeout;
        if (timeout <= TimeSpan.Zero)
            return SymbolExtractor.Extract(fileId, lang, content, filePath, projectRoot, cancellationToken);

        using var extractionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var extractionToken = extractionCts.Token;
        var task = Task.Run(
            () => SymbolExtractor.Extract(fileId, lang, content, filePath, projectRoot, extractionToken),
            CancellationToken.None);
        try
        {
            if (task.Wait(timeout, cancellationToken))
                return task.GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            throw ex.InnerExceptions[0];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        extractionCts.Cancel();
        try
        {
            task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
        }
        catch (OperationCanceledException)
        {
        }

        throw new IndexExtractionStalledException(0, null, timeout, phasePath);
    }

    private static string CollapseLineBreaks(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (value.IndexOfAny(['\r', '\n']) < 0)
            return value;
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            buffer.Append(ch == '\r' || ch == '\n' ? ' ' : ch);
        return buffer.ToString();
    }

    private static int? RejectUnresolvedMergeState(string projectRoot, bool json, JsonSerializerOptions jsonOptions)
    {
        var status = GitHelper.TryGetWorktreeStatus(projectRoot);
        if (status == null || status.UnresolvedMergeFiles.Count == 0)
            return null;

        var paths = string.Join(", ", status.UnresolvedMergeFiles.Take(5));
        if (status.UnresolvedMergeFiles.Count > 5)
            paths += $", ... {status.UnresolvedMergeFiles.Count - 5:N0} more";

        return WriteCommandError(
            json,
            jsonOptions,
            $"unresolved merge conflicts detected; refusing to index conflicted files ({paths})",
            CommandExitCodes.UsageError,
            "Resolve the conflicts and run `git merge --continue`, or abort the merge with `git merge --abort`, then rerun `cdidx index`.",
            CommandErrorCodes.UsageError);
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
        => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, message, exitCode, hint, errorCode: errorCode);

    private static int WriteInterruptedResult(bool json, JsonSerializerOptions jsonOptions, int filesProcessed, int? filesTotal)
    {
        var totalSuffix = filesTotal is > 0 ? $" of {filesTotal.Value:N0}" : string.Empty;
        return WriteCommandError(
            json,
            jsonOptions,
            $"Interrupted; partial progress saved ({filesProcessed:N0}{totalSuffix} files processed).",
            CommandExitCodes.Interrupted,
            "Rerun `cdidx index` to finish refreshing the index. Press Ctrl-C again during a future run to force-exit.",
            CommandErrorCodes.Interrupted);
    }

    private static int WriteExtractionStalledResult(bool json, JsonSerializerOptions jsonOptions, IndexExtractionStalledException ex)
    {
        var totalSuffix = ex.FilesTotal is > 0 ? $" of {ex.FilesTotal.Value:N0}" : string.Empty;
        var pathSuffix = string.IsNullOrWhiteSpace(ex.ActivePath) ? string.Empty : $" Last active phase: {ex.ActivePath}.";
        return WriteCommandError(
            json,
            jsonOptions,
            $"Index extraction made no progress for {ConsoleUi.FormatDuration(ex.Timeout)} ({ex.FilesProcessed:N0}{totalSuffix} files processed).{pathSuffix}",
            CommandExitCodes.CancelledBySignal,
            "Rerun with `--verbose` to inspect progress, lower `--parallelism`, exclude the reported file, or lower `--max-symbols-per-file` to skip pathological symbol output.",
            CommandErrorCodes.IndexExtractionStalled);
    }

    internal static bool HandleIndexCancelKeyPress(CancellationTokenSource cancellation, ref bool firstCancelHandled)
    {
        if (!firstCancelHandled && !cancellation.IsCancellationRequested)
        {
            firstCancelHandled = true;
            cancellation.Cancel();
            return true;
        }

        return false;
    }

    private static IDisposable RegisterIndexCancelKeyPress(CancellationTokenSource cancellation)
    {
        var firstCancelHandled = false;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = HandleIndexCancelKeyPress(cancellation, ref firstCancelHandled);
        };

        try
        {
            Console.CancelKeyPress += handler;
            return new CancelKeyPressRegistration(handler);
        }
        catch (PlatformNotSupportedException)
        {
            return NullDisposable.Instance;
        }
    }

    private static IDisposable RegisterIndexTerminateSignal(CancellationTokenSource cancellation)
    {
        if (OperatingSystem.IsWindows())
            return NullDisposable.Instance;

        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        }
        catch (PlatformNotSupportedException)
        {
            return NullDisposable.Instance;
        }
    }

    private static int WriteDatabaseFilesystemError(bool json, JsonSerializerOptions jsonOptions, string dbPath, Exception ex)
    {
        var transient = ex is SqliteException { SqliteErrorCode: 5 or 6 };
        GlobalToolLog.Error($"index_database_filesystem_error db={CollapseLineBreaks(dbPath)}\n{GlobalToolLog.FormatExceptionChain(ex)}");
        return WriteCommandError(
            json,
            jsonOptions,
            $"database write failed for {dbPath}: {CollapseLineBreaks(ex.Message)}",
            transient ? CommandExitCodes.TransientDatabaseError : CommandExitCodes.DatabaseError,
            transient
                ? "Another process may be holding the database. Wait for it to finish, or retry with backoff."
                : BuildDatabaseFilesystemHint(ex),
            transient ? CommandErrorCodes.DbLocked : CommandErrorCodes.DbNotWritable);
    }

    private static string BuildDatabaseFilesystemHint(Exception ex)
    {
        if (ex is SqliteException sqlite && MacProfileDetector.IsPermissionStyleSqliteError(sqlite))
            return MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent());

        if (ex is UnauthorizedAccessException)
            return MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent());

        return "Check that the database file and parent directory exist and are writable, then retry `cdidx index`.";
    }

    private static bool IsDatabaseFilesystemError(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException
        || ex is SqliteException { SqliteErrorCode: 5 or 6 or 8 or 10 or 14 };

    internal const int MaxScanCheckpointBytes = 1024 * 1024;

    private static IReadOnlySet<string> LoadScanCheckpoint(string path, string? currentHead)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentHead) || !File.Exists(path))
                return new HashSet<string>(StringComparer.Ordinal);

            var text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxScanCheckpointBytes, FileShare.ReadWrite);
            if (text is null)
                return new HashSet<string>(StringComparer.Ordinal);

            var checkpoint = JsonSerializer.Deserialize<ScanCheckpoint>(text);
            if (checkpoint is not { Version: ScanCheckpointVersion }
                || !string.Equals(checkpoint.GitHead, currentHead, StringComparison.Ordinal)
                || checkpoint.Directories is not { Count: > 0 })
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return checkpoint.Directories
                .Where(directory => directory.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveScanCheckpoint(string path, string? currentHead, IReadOnlySet<string> directories)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentHead))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var checkpoint = new ScanCheckpoint(
                ScanCheckpointVersion,
                currentHead,
                directories
                    .Where(directory => directory.Length > 0)
                    .OrderBy(directory => directory, StringComparer.Ordinal)
                    .ToList());
            AtomicFileWriter.WriteJson(path, checkpoint, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteScanCheckpoint(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record FullScanDiscoveryResult(
        FileIndexer.ScanFilesResult ScanResult,
        IReadOnlyList<string> Files,
        List<CliJsonMessage> ErrorList,
        List<CliJsonMessage> WarningList,
        string? CurrentHeadForCheckpoint,
        string ScanCheckpointPath,
        IReadOnlySet<string> CheckpointedDirectories);

    private static FullScanDiscoveryResult DiscoverFullScanFiles(
        FileIndexer indexer,
        string projectRoot,
        IndexCommandOptions options,
        string[] spinnerFrames,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? spinnerCts = null;
        if (!options.Json && !options.Quiet)
            spinnerCts = ConsoleUi.StartSpinner("Scanning...", spinnerFrames);

        void ThrowIfDiscoveryCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            ConsoleUi.StopSpinner(spinnerCts);
            throw new IndexInterruptedException(0, null);
        }

        string? currentHeadForCheckpoint;
        try
        {
            ThrowIfDiscoveryCancelled();
            currentHeadForCheckpoint = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.StopSpinner(spinnerCts);
            throw new IndexInterruptedException(0, null);
        }

        var scanCheckpointPath = Path.Combine(projectRoot, ".cdidx", ScanCheckpointFileName);
        var checkpointedDirectories = LoadScanCheckpoint(scanCheckpointPath, currentHeadForCheckpoint);
        WriteFullScanJsonLiveness(options, "scanning files...");
        var scanHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "scanning files");
        FileIndexer.ScanFilesResult scanResult;
        try
        {
            ThrowIfDiscoveryCancelled();
            scanResult = indexer.ScanFilesDetailed(checkpointedDirectories, continueOnError: true, cancellationToken: cancellationToken);
            ThrowIfDiscoveryCancelled();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(0, null);
        }
        finally
        {
            StopFullScanJsonPhaseHeartbeat(scanHeartbeat);
        }
        var files = scanResult.Files;
        ConsoleUi.StopSpinner(spinnerCts);
        WriteFullScanJsonLiveness(options, $"found {ConsoleUi.Counted(files.Count, "file", format: "N0")}; preparing database...");
        var fatalScanErrors = scanResult.Errors
            .Where(error => error.IsFatal)
            .ToList();
        var warningScanErrors = scanResult.Errors
            .Where(error => !error.IsFatal)
            .ToList();
        var errorList = fatalScanErrors
            .Select(error => new CliJsonMessage(error.Path, error.Message))
            .ToList();
        var warningList = warningScanErrors
            .Select(error => new CliJsonMessage(error.Path, error.Message))
            .ToList();
        if (!options.Json && !options.Quiet)
        {
            Console.WriteLine($"  Found {ConsoleUi.Counted(files.Count, "file", format: "N0")}");
            foreach (var error in scanResult.Errors)
                ConsoleUi.PrintWarning($"{error.Path}: {error.Message}");
            Console.WriteLine();
        }

        return new FullScanDiscoveryResult(
            scanResult,
            files,
            errorList,
            warningList,
            currentHeadForCheckpoint,
            scanCheckpointPath,
            checkpointedDirectories);
    }

    private static void WriteFullScanJsonLiveness(IndexCommandOptions options, string message)
    {
        if (!options.Json || options.Quiet)
            return;

        ConsoleUi.TryWriteErrorLine($"cdidx: {message}");
    }

    private static (CancellationTokenSource Cts, Task Task)? StartFullScanJsonPhaseHeartbeat(
        IndexCommandOptions options,
        string phase,
        Func<string?>? detailProvider = null)
    {
        if (!options.Json || options.Quiet)
            return null;

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var task = Task.Run(async () =>
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

                var detail = detailProvider?.Invoke();
                var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
                ConsoleUi.TryWriteErrorLine($"cdidx: still {phase}{suffix}...");
            }
        }, token);
        return (cts, task);
    }

    private static void StopFullScanJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
    {
        if (heartbeat == null)
            return;

        heartbeat.Value.Cts.Cancel();
        try
        {
            heartbeat.Value.Task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
        }
        heartbeat.Value.Cts.Dispose();
    }

    private static int RunFullScan(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        string resolvedDbPath,
        IndexCommandOptions options,
        Stopwatch stopwatch,
        DateTime runStartedAtUtc,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
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
        bool showNextSteps,
        CancellationToken cancellationToken)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        var memorySamples = options.MemoryTrace ? new List<IndexMemorySampleJsonResult> { CaptureMemorySample("start", stopwatch) } : [];
        _ = priorMetadataTargetCsharp; // full-scan resolver runs unconditionally on success / 成功時に常に再解決するため不要
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions);
        if (unresolvedMergeExitCode != null)
            return unresolvedMergeExitCode.Value;

        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            options.SymbolKindFilter.Signature,
            StringComparison.Ordinal);

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

            throw new IndexInterruptedException(filesProcessed, filesTotal);
        }

        var discovery = DiscoverFullScanFiles(indexer, projectRoot, options, spinnerFrames, cancellationToken);
        var scanResult = discovery.ScanResult;
        var files = discovery.Files;
        var errorList = discovery.ErrorList;
        var warningList = discovery.WarningList;
        var currentHeadForCheckpoint = discovery.CurrentHeadForCheckpoint;
        var scanCheckpointPath = discovery.ScanCheckpointPath;
        var checkpointedDirectories = discovery.CheckpointedDirectories;
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("scan", stopwatch));

        // Full-scan commits to mutating the DB from here on. Keep the whole write phase in
        // one outer transaction so Ctrl-C/SIGTERM can roll back the batch marker,
        // readiness demotion, stale-file purge, and per-file writes instead of leaving a
        // half-cleared index.
        // full-scan の書き込み全体を outer transaction に入れ、中断時に batch marker /
        // readiness clear / purge / per-file write をまとめて rollback する。
        ThrowIfFullScanCancelled(0, files.Count);
        using var fullScanTxn = writer.BeginTransaction();
        writer.MarkBatchInProgress();
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();
        FullScanWritePhaseStartedForTesting?.Invoke();
        ThrowIfFullScanCancelled(0, files.Count);

        CancellationTokenSource? purgeCts = null;
        if (!options.Json && !options.Quiet)
            purgeCts = ConsoleUi.StartSpinner("Cleaning up stale entries...", spinnerFrames);
        var purged = 0;
        var retainedPaths = files
            .Select(path => FileIndexer.NormalizeIndexPath(Path.GetRelativePath(projectRoot, path)))
            .ToHashSet(StringComparer.Ordinal);
        if (scanResult.HadErrors)
        {
            SaveScanCheckpoint(scanCheckpointPath, currentHeadForCheckpoint, scanResult.CheckpointedDirectories);
            retainedPaths.UnionWith(scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));

            foreach (var relPath in scanResult.NonIndexablePaths)
            {
                var dbPath = FileIndexer.NormalizeIndexPath(relPath);
                if (!writer.HasFileAtPath(dbPath))
                    continue;

                if (writer.DeleteFileByPath(dbPath))
                    purged++;
            }

            var authoritativeDirectories = scanResult.ListedDirectories
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);
            var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);
            attributePrunedDirectories.UnionWith(scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
            purged += writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retainedPaths, authoritativeDirectories, attributePrunedDirectories);
        }
        else
        {
            if (checkpointedDirectories.Count > 0)
            {
                var authoritativeDirectories = scanResult.ListedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                attributePrunedDirectories.UnionWith(scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
                purged = writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retainedPaths, authoritativeDirectories, attributePrunedDirectories);
            }
            else
            {
                purged = writer.PurgeFilesOutsideRetainedSet(retainedPaths);
            }
            DeleteScanCheckpoint(scanCheckpointPath);
        }
        if (purged > 0)
            WriteProjectRootOnce();
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("purge", stopwatch));
        ConsoleUi.StopSpinner(purgeCts);
        WriteFullScanJsonLiveness(options, purged > 0
            ? $"purged {purged:N0} stale file(s); preparing index writes..."
            : "preparing index writes...");
        if (!options.Json && !options.Quiet)
        {
            if (purged > 0)
            {
                var purgeMessage = scanResult.HadErrors
                    ? $"  Purged {purged:N0} previously indexed files that were positively observed as no longer indexable or missing from directories whose file listing completed successfully"
                    : $"  Purged {purged:N0} stale files (missing or no longer indexable)";
                Console.WriteLine(purgeMessage);
            }
            if (scanResult.HadErrors)
                ConsoleUi.PrintWarning("Skipped authoritative purge outside directories whose file listing completed successfully because some paths could not be scanned.");
        }

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        ThrowIfFullScanCancelled(0, files.Count);
        var purgedRefs = writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());
        if (purgedRefs > 0 && !options.Json && !options.Quiet)
            Console.WriteLine($"  Purged {purgedRefs:N0} stale references (unsupported language)");

        CancellationTokenSource? indexCts = null;
        int processed = 0, skipped = 0, warnings = warningList.Count, errors = errorList.Count;
        var symbolsDroppedByKindFilter = 0;

        var interactiveIndexSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        var redirectedIndexingMessagePrinted = false;
        var indexProgressVisible = false;
        var reusedHotspotFamilyLanguages = new HashSet<string>(StringComparer.Ordinal);
        var skippedSymbolExtractorLanguages = new HashSet<string>(StringComparer.Ordinal);
        var lastJsonProgressAt = Stopwatch.GetTimestamp();
        string? currentJsonIndexFile = null;
        var activeJsonExtractionPhases = new ConcurrentDictionary<int, string>();
        CancellationTokenSource? jsonHeartbeatCts = null;
        Task? jsonHeartbeatTask = null;
        using var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault();
        var extractionParallelism = Math.Max(1, options.Parallelism);
        var hasPostExtractionHooks = postExtractionHooks.Hooks.Count > 0;
        var parallelizeExtraction = (options.Rebuild || writer.GetCounts().files == 0 || headChangeDetected)
            && !options.SymbolKindFilter.IsActive
            && !hasPostExtractionHooks;
        FullScanExtractionSchedulingForTesting?.Invoke(
            parallelizeExtraction,
            headChangeDetected ? "head_changed" : null);

        void StartIndexSpinnerIfNeeded()
        {
            if (!interactiveIndexSpinner || indexCts != null)
                return;

            indexCts = ConsoleUi.StartSpinner("Indexing...", spinnerFrames);
        }

        void PauseIndexSpinnerForConsoleWrite()
        {
            if (indexCts == null)
                return;

            ConsoleUi.StopSpinner(indexCts);
            indexCts = null;
        }

        void ResumeIndexSpinnerAfterConsoleWrite()
        {
            if (!interactiveIndexSpinner || processed >= files.Count || indexProgressVisible)
                return;

            StartIndexSpinnerIfNeeded();
        }

        void WriteIndexVerboseStatus(string message)
        {
            if (!options.Verbose || options.Quiet)
                return;

            if (options.Json)
            {
                ConsoleUi.TryWriteErrorLine(message);
                return;
            }

            PauseIndexSpinnerForConsoleWrite();
            ConsoleUi.ClearProgressLine();
            Console.WriteLine(message);
            ResumeIndexSpinnerAfterConsoleWrite();
        }

        void EnsureIndexingActivityVisible()
        {
            if (options.Json || options.Quiet)
                return;

            if (indexProgressVisible)
                return;

            if (interactiveIndexSpinner)
            {
                StartIndexSpinnerIfNeeded();
                return;
            }

            if (redirectedIndexingMessagePrinted)
                return;

            Console.WriteLine("Indexing...");
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
                        activeJsonExtractionPhases.OrderBy(static kvp => kvp.Key).Select(static kvp => kvp.Value));
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

        WriteFullScanJsonLiveness(options, "preparing C# workspace symbols...");
        string? currentCSharpWorkspaceFile = null;
        var csharpWorkspaceHeartbeat = StartFullScanJsonPhaseHeartbeat(
            options,
            "preparing C# workspace symbols",
            () => currentCSharpWorkspaceFile);
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        try
        {
            csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                writer,
                indexer,
                projectRoot,
                files,
                path => currentCSharpWorkspaceFile = path,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(0, files.Count);
        }
        finally
        {
            currentCSharpWorkspaceFile = null;
            StopFullScanJsonPhaseHeartbeat(csharpWorkspaceHeartbeat);
        }

        EnsureIndexingActivityVisible();
        ReportJsonIndexProgressIfNeeded();
        StartJsonHeartbeatIfNeeded();

        try
        {
            if (!options.Json && !options.Quiet)
            {
                PauseIndexSpinnerForConsoleWrite();
                indexProgressVisible = true;
                ConsoleUi.PrintProgress(0, files.Count);
            }

            using var extractionResults = new BlockingCollection<FullScanFileWorkItem>(Math.Max(1, extractionParallelism * 4));
            using var extractionStallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var extractionCancellationToken = extractionStallCts.Token;
            var nextFileIndex = -1;
            var workers = Enumerable.Range(0, extractionParallelism)
                .Select(workerIndex => Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        extractionCancellationToken.ThrowIfCancellationRequested();
                        var fileIndex = Interlocked.Increment(ref nextFileIndex);
                        if (fileIndex >= files.Count)
                            break;

                        var filePath = files[fileIndex];
                        try
                        {
                            var relativeFilePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, filePath));
                            activeJsonExtractionPhases[workerIndex] = FormatIndexPhasePath(relativeFilePath, "reading");
                            var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(filePath, extractionCancellationToken);
                            IReadOnlyList<ChunkRecord>? chunks = null;
                            IReadOnlyList<SymbolRecord>? symbols = null;
                            IReadOnlyList<ReferenceRecord>? references = null;
                            IReadOnlyList<FileIssue>? issues = null;
                            if (parallelizeExtraction)
                            {
                                activeJsonExtractionPhases[workerIndex] = FormatIndexPhasePath(record.Path, "chunking");
                                chunks = ChunkSplitter.Split(0, content);
                                activeJsonExtractionPhases[workerIndex] = FormatIndexPhasePath(record.Path, "symbols");
                                symbols = ExtractSymbolsWithStallTimeout(
                                    0,
                                    record.Lang,
                                    content,
                                    filePath,
                                    Path.GetFullPath(options.ProjectPath!),
                                    activeJsonExtractionPhases[workerIndex],
                                    extractionCancellationToken);
                                if (symbols.Count > options.MaxSymbolsPerFile)
                                {
                                    var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                                    extractionResults.Add(
                                        FullScanFileWorkItem.Success(filePath, record, string.Empty, rawBytes, issue.Message, [], [], [], [issue]),
                                        extractionCancellationToken);
                                    continue;
                                }
                                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                                activeJsonExtractionPhases[workerIndex] = FormatIndexPhasePath(record.Path, "references");
                                references = ReferenceExtractor.Extract(
                                    0,
                                    record.Lang,
                                    content,
                                    symbols,
                                    record.Path,
                                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                                    extractionCancellationToken);
                                activeJsonExtractionPhases[workerIndex] = FormatIndexPhasePath(record.Path, "validating");
                                issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                            }
                            extractionResults.Add(
                                FullScanFileWorkItem.Success(filePath, record, content, rawBytes, warning, chunks, symbols, references, issues),
                                extractionCancellationToken);
                        }
                        catch (OperationCanceledException) when (extractionCancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (FileIndexer.BinaryFileSkippedException ex)
                        {
                            extractionResults.Add(FullScanFileWorkItem.Skipped(filePath, ex.Message), extractionCancellationToken);
                        }
                        catch (FileIndexer.FileTooLargeSkippedException ex)
                        {
                            var record = indexer.BuildSkippedFileRecord(filePath);
                            var issue = new FileIssue
                            {
                                Path = ex.RelativePath,
                                Kind = "file_too_large",
                                Line = 0,
                                Message = ex.Message,
                            };
                            extractionResults.Add(
                                FullScanFileWorkItem.Success(filePath, record, string.Empty, [], ex.Message, [], [], [], [issue]),
                                extractionCancellationToken);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, filePath));
                            extractionResults.Add(
                                FullScanFileWorkItem.Skipped(filePath, $"{relativePath}: skipped because it was deleted during indexing."),
                                extractionCancellationToken);
                        }
                        catch (Exception ex)
                        {
                            extractionResults.Add(FullScanFileWorkItem.Failure(filePath, ex), extractionCancellationToken);
                        }
                        finally
                        {
                            activeJsonExtractionPhases.TryRemove(workerIndex, out _);
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
                        activeJsonExtractionPhases,
                        extractionStallCts.Cancel);
                    continue;
                }

                lastExtractionProgressAt = Stopwatch.GetTimestamp();
                currentJsonIndexFile = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, item.FilePath));
                EnsureIndexingActivityVisible();
                if (item.Exception is IndexExtractionStalledException stalledException)
                    throw stalledException;

                try
                {
                    if (item.Exception != null)
                        throw item.Exception;

                    if (item.Record == null)
                    {
                        warnings++;
                        warningList.Add(new CliJsonMessage(currentJsonIndexFile, item.Warning ?? "File skipped"));
                        if (!options.Json && !options.Quiet && item.Warning != null)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(item.Warning);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }

                        if (writer.HasFileAtPath(currentJsonIndexFile))
                        {
                            using var deleteTxn = writer.BeginTransaction();
                            if (writer.DeleteFileByPath(currentJsonIndexFile))
                            {
                                WriteProjectRootOnce();
                                deleteTxn.Commit();
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
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintProgress(processed, files.Count);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    var record = item.Record!;
                    if (item.Warning != null && !options.Json && !options.Quiet)
                    {
                        PauseIndexSpinnerForConsoleWrite();
                        ConsoleUi.PrintWarning(item.Warning);
                        ResumeIndexSpinnerAfterConsoleWrite();
                    }

                    long? existingId = null;
                    if (!options.Rebuild)
                    {
                        existingId = writer.GetUnchangedFileId(
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
                                && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                                && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                    }
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
                        writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                        skipped++;
                        processed++;
                        if (!string.IsNullOrWhiteSpace(record.Lang))
                            skippedSymbolExtractorLanguages.Add(record.Lang);
                        if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang) && record.Lang != null)
                            reusedHotspotFamilyLanguages.Add(record.Lang);
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.ClearProgressLine();
                            Console.WriteLine($"  [SKIP] {record.Path}");
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        if (!options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintProgress(processed, files.Count);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        ReportJsonIndexProgressIfNeeded();
                        currentJsonIndexFile = null;
                        continue;
                    }

                    using var txn = writer.BeginTransaction();
                    writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                    var fileId = writer.UpsertFile(record);
                    currentJsonIndexFile = FormatIndexPhasePath(record.Path, "chunking");
                    var chunks = item.Chunks == null
                        ? ChunkSplitter.Split(fileId, item.Content!)
                        : ReassignChunkFileIds(item.Chunks, fileId);
                    currentJsonIndexFile = FormatIndexPhasePath(record.Path, "symbols");
                    var symbols = item.Symbols == null
                        ? ExtractSymbolsWithStallTimeout(
                            fileId,
                            record.Lang,
                            item.Content!,
                            item.FilePath,
                            Path.GetFullPath(options.ProjectPath!),
                            currentJsonIndexFile,
                            cancellationToken)
                        : ReassignSymbolFileIds(item.Symbols, fileId);
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        writer.InsertSymbols([]);
                        writer.InsertReferences([]);
                        writer.InsertIssues(fileId, [issue]);
                        if (options.Verbose)
                            WriteIndexVerboseStatus($"  [SKIP] {record.Path} ({issue.Message})");
                        txn.Commit();
                        processed++;
                        if (!options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintProgress(processed, files.Count);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        ReportJsonIndexProgressIfNeeded();
                        currentJsonIndexFile = null;
                        continue;
                    }
                    if (item.Symbols == null)
                        SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(item.FilePath, record.Lang));
                    var fileContext = new FileContext(projectRoot, record.Path, item.FilePath, record.Lang);
                    var mutableSymbols = symbols as IList<SymbolRecord> ?? symbols.ToList();
                    postExtractionHooks.OnSymbolsExtracted(fileContext, mutableSymbols);
                    symbolsDroppedByKindFilter += options.SymbolKindFilter.Apply(mutableSymbols);
                    symbols = (IReadOnlyList<SymbolRecord>)mutableSymbols;
                    if (symbols.Count > options.MaxSymbolsPerFile)
                    {
                        var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                        writer.InsertSymbols([]);
                        writer.InsertReferences([]);
                        writer.InsertIssues(fileId, [issue]);
                        if (options.Verbose)
                            WriteIndexVerboseStatus($"  [SKIP] {record.Path} ({issue.Message})");
                        txn.Commit();
                        processed++;
                        if (!options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintProgress(processed, files.Count);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        ReportJsonIndexProgressIfNeeded();
                        currentJsonIndexFile = null;
                        continue;
                    }
                    writer.InsertChunks(chunks);
                    FileIndexer.ValidateSymbolLineRanges(record, symbols);
                    writer.InsertSymbols(symbols);
                    currentJsonIndexFile = FormatIndexPhasePath(record.Path, "references");
                    var references = item.References == null
                        ? ReferenceExtractor.Extract(
                            fileId,
                            record.Lang,
                            item.Content!,
                            symbols,
                            record.Path,
                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                            cancellationToken)
                        : ReassignReferenceFileIds(item.References, fileId);
                    postExtractionHooks.OnReferencesExtracted(fileContext, AsMutableList(references));
                    writer.InsertReferences(references);
                    currentJsonIndexFile = FormatIndexPhasePath(record.Path, "validating");
                    var issues = item.Issues ?? FileIndexer.ValidateContent(record.Path, item.RawBytes!, item.Content!);
                    writer.InsertIssues(fileId, issues);
                    currentJsonIndexFile = FormatIndexPhasePath(record.Path, "committing");
                    WriteProjectRootOnce();
                    txn.Commit();

                    WriteIndexVerboseStatus($"  [OK  ] {record.Path} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                }
                catch (IndexExtractionStalledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    GlobalToolLog.Error($"index_file_failed path={CollapseLineBreaks(item.FilePath)}\n{GlobalToolLog.FormatExceptionChain(ex)}");
                    errors++;
                    var errorMessage = FormatIndexFileException(ex);
                    errorList.Add(new CliJsonMessage(item.FilePath, errorMessage));
                    if (!options.Json)
                    {
                        PauseIndexSpinnerForConsoleWrite();
                        ConsoleUi.ClearProgressLine();
                        ConsoleUi.TryWriteErrorLine(FormatPerFileErrorLine("ERR ", item.FilePath, ex, errorMessage));
                        ResumeIndexSpinnerAfterConsoleWrite();
                    }
                }

                processed++;
                currentJsonIndexFile = null;
                ThrowIfFullScanCancelled(processed, files.Count);
                ReportJsonIndexProgressIfNeeded();
                if (!options.Json && !options.Quiet)
                {
                    PauseIndexSpinnerForConsoleWrite();
                    ConsoleUi.PrintProgress(processed, files.Count);
                    ResumeIndexSpinnerAfterConsoleWrite();
                }
            }
            Task.WaitAll(workers, cancellationToken);
        }
        finally
        {
            currentJsonIndexFile = null;
            StopJsonHeartbeat();
        }

        PauseIndexSpinnerForConsoleWrite();

        ThrowIfFullScanCancelled(processed, files.Count);
        WriteFullScanJsonLiveness(options, "optimizing index...");
        var optimizeHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "optimizing index");
        try
        {
            writer.OptimizeFts();
        }
        finally
        {
            StopFullScanJsonPhaseHeartbeat(optimizeHeartbeat);
        }
        ThrowIfFullScanCancelled(processed, files.Count);
        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // ClearReadyFlags() ran at the start.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        var graphTableAvailableAfter = false;
        var issuesTableAvailableAfter = false;
        var csharpSymbolNameReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var csharpMetadataTargetReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var foldReadyAfter = false;
        string? foldReadyReasonAfter = null;
        if (errors == 0)
        {
            // Full-scan covers the whole repo, so it may always stamp Graph / Issues on
            // success regardless of what the DB carried before. Fold still gates on the
            // backfill verification below because incremental-by-default full scans skip
            // unchanged legacy files whose folded columns remain NULL.
            // full-scan は全repo をカバーするため、Graph / Issues は常に stamp。Fold のみ条件付き。
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkSqlGraphContractReady();
            writer.MarkCSharpSymbolNameContractReady();
            // Issue #435: resolve every C# class-like row and stamp readiness. Full-scan
            // touches the entire repo, so the resolver output is authoritative regardless
            // of which individual files were reparsed.
            // Issue #435: full-scan は全リポジトリを touch するため resolver の出力は
            // 全行 authoritative。必ず再解決して stamp する。
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
            graphTableAvailableAfter = true;
            issuesTableAvailableAfter = true;
            csharpSymbolNameReadyAfter = true;
            writer.RebuildTypeScriptAugmentationReferences(projectRoot);
            RestampHotspotFamilyTrustForFullScan(
                writer,
                reusedHotspotFamilyLanguages,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // FoldReady must reflect reality (#86). Full-scan is INCREMENTAL by default — it
            // skips unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows
            // keep NULL name_folded / *_folded values. Stamping FoldReady anyway would flip
            // readers onto the folded-equality path and silently miss those rows. Verify
            // every existing row has its folded column populated before stamping, and tell
            // the user how to upgrade when not (only --rebuild / a truly-fresh index can
            // guarantee 100% backfill on a legacy DB).
            // fold は実検証が通ったときだけ stamp。legacy DB で skip された行は NULL のため、
            // 黙って stamp すると reader が fold 経路で legacy 行を見逃す。codex #86 レビュー。
            var backfillReady = skipped == 0
                ? writer.AllFoldedColumnsBackfilled()
                : writer.AllFoldedColumnsBackfilled(skippedSymbolExtractorLanguages);
            var foldedKeysCurrent = skipped == 0 || writer.AllFoldedColumnValuesMatchCurrentFold();
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent
                && foldFingerprintMatchesCurrent
                && writer.SymbolExtractorVersionsMatchCurrent(skippedSymbolExtractorLanguages);
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
            if (backfillReady && foldedKeysCurrent && (skipped == 0 || canRestampExistingFoldTrust))
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; if a concurrent writer slipped
                // in a NULL-folded row between the upfront check and this stamp, the stamp is
                // skipped and we degrade to the legacy reason instead of silent misadvertisement.
                // Issue #1535.
                // BEGIN IMMEDIATE 内で再検証するため、concurrent writer による NULL 差し込みで
                // stamp は失敗し、silent な fold-trust 誤広告ではなく legacy 理由に降格する。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady(stampCurrentSymbolExtractorVersions: skipped == 0);
                if (!foldReadyAfter)
                {
                    backfillReady = false;
                    foldReadyReasonAfter = GetFoldReadyReason(false, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);
                }
            }
            else
                foldReadyReasonAfter = GetFoldReadyReason(backfillReady, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);

            writer.WriteCdidxWriterVersion(ConsoleUi.LoadVersion());
            writer.SetMeta(SymbolKindFilterMetaKey, options.SymbolKindFilter.Signature);

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
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, currentHeadCommit);
            writer.SetMeta(DbContext.IndexedHeadCommitBranchMetaKey, GitHelper.TryGetHeadBranch(projectRoot, cancellationToken));
            writer.SetMeta(
                DbContext.LastFullScanElapsedMsMetaKey,
                stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // #1509: also stamp the always-updated "last indexed HEAD" triple (SHA + branch +
            // timestamp). Unlike #1508's IndexedHeadCommitMetaKey which only fires here on
            // full scans, this triple is also stamped at the end of incremental update runs
            // (see RunUpdateMode) so cross-session `commits_ahead_of_indexed_head` always
            // reflects the true HEAD at the time of the most recent successful index.
            // #1509: あらゆる成功 index の終端で更新する HEAD トリプル (SHA + branch + 時刻) も
            // ここで stamp する。full scan / partial update を問わず最新の HEAD を保存する。
            StampIndexedHeadMetadata(writer, projectRoot, cancellationToken);
            if (options.MemoryTrace)
                memorySamples.Add(CaptureMemorySample("finalize", stopwatch));
            var memoryTimelineForStamp = BuildMemoryTimeline(memorySamples);
            StampLastIndexRunMetadata(
                writer,
                options.Rebuild ? "rebuild" : "incremental",
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                files.Count,
                skipped,
                errors,
                SumReadableFileBytes(files),
                processed,
                purged,
                memoryTimelineForStamp);
        }
        writer.ClearBatchInProgress();
        fullScanTxn.Commit();
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
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        var languageCounts = files
            .Select(static file => FileIndexer.TryDetectLanguage(file))
            .Where(static detection => detection.Status == FileIndexer.FileProbeStatus.Supported && detection.Language != null)
            .GroupBy(static detection => detection.Language!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
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
            Console.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
            {
                Status = errors > 0 ? "partial" : "success",
                Mode = options.Rebuild ? "rebuild" : "incremental",
                Summary = new IndexFullScanSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
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
                Warnings = warningList.Count > 0 ? warningList : null,
                MemoryTimeline = memoryTimeline,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, jsonContext.IndexFullScanJsonResult));
        }
        else if (!options.Quiet)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine();
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", ConsoleUi.FormatNumber(totalFiles), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", ConsoleUi.FormatNumber(totalChunks), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", ConsoleUi.FormatNumber(totalSymbols), indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", ConsoleUi.FormatNumber(totalReferences), indent: "  "));
            if (skipped > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", $"{ConsoleUi.FormatNumber(skipped)} (unchanged)", indent: "  "));
            if (scanResult.DanglingSymlinks.Count > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Dangling symlinks", $"{ConsoleUi.FormatNumber(scanResult.DanglingSymlinks.Count)} skipped", indent: "  "));
            if (options.Verbose && scanResult.UnknownExtensionFiles.Count > 0)
            {
                Console.WriteLine($"  Unknown extension files: {ConsoleUi.FormatNumber(scanResult.UnknownExtensionFiles.Count)}");
                foreach (var relPath in scanResult.UnknownExtensionFiles.Take(5))
                    Console.WriteLine($"    {relPath}");
                if (scanResult.UnknownExtensionFiles.Count > 5)
                    Console.WriteLine($"    ... {ConsoleUi.FormatNumber(scanResult.UnknownExtensionFiles.Count - 5)} more");
            }
            if (warnings > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", ConsoleUi.FormatNumber(warnings), indent: "  "));
            if (errors > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Errors", ConsoleUi.FormatNumber(errors), indent: "  "));
            if (symbolsDroppedByKindFilter > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Filtered symbols", ConsoleUi.FormatNumber(symbolsDroppedByKindFilter), indent: "  "));
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

        return CommandExitCodes.Success;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (left == null || right == null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static string GetFoldReadyReason(bool backfillReady, bool foldVersionMatchesCurrent, bool foldFingerprintMatchesCurrent)
    {
        if (!backfillReady)
            return DegradationReasonCodes.MissingFoldBackfill;

        if (!foldVersionMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyVersion;

        if (!foldFingerprintMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyFingerprint;

        return DegradationReasonCodes.FoldRowsNotRestamped;
    }

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldBackfillCommand(string resolvedDbPath)
        => $"cdidx backfill-fold --db {QuoteCommandArgument(resolvedDbPath)}";

    private static string BuildFoldRebuildCommand(string projectRoot, string resolvedDbPath)
        => $"cdidx index {QuoteCommandArgument(projectRoot)} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";

    private static IReadOnlyList<ChunkRecord> ReassignChunkFileIds(IReadOnlyList<ChunkRecord> chunks, long fileId)
    {
        foreach (var chunk in chunks)
            chunk.FileId = fileId;
        return chunks;
    }

    private static IReadOnlyList<SymbolRecord> ReassignSymbolFileIds(IReadOnlyList<SymbolRecord> symbols, long fileId)
    {
        foreach (var symbol in symbols)
            symbol.FileId = fileId;
        return symbols;
    }

    private static IReadOnlyList<ReferenceRecord> ReassignReferenceFileIds(IReadOnlyList<ReferenceRecord> references, long fileId)
    {
        foreach (var reference in references)
            reference.FileId = fileId;
        return references;
    }

    private static IList<T> AsMutableList<T>(IReadOnlyList<T> records)
    {
        if (records is IList<T> mutable)
            return mutable;

        throw new InvalidOperationException("Post-extraction hooks require mutable extraction result lists.");
    }

    private static int AddPostExtractionHookWarnings(PostExtractionHookRunner runner, List<CliJsonMessage> warningList)
    {
        var added = 0;
        foreach (var diagnostic in runner.Diagnostics)
        {
            warningList.Add(new CliJsonMessage(
                string.IsNullOrWhiteSpace(diagnostic.TypeName) ? diagnostic.AssemblyPath : diagnostic.TypeName,
                diagnostic.Message));
            added++;
        }

        return added;
    }

    private static FoldOnlyRemediation? BuildFoldOnlyReadinessRemediation(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady,
        string? foldReadyReason,
        string projectRoot,
        string resolvedDbPath)
    {
        if (!IsFoldOnlyReadinessDegraded(
                graphTableAvailable,
                issuesTableAvailable,
                sqlGraphContractReady,
                hotspotFamilyReady,
                csharpSymbolNameReady,
                csharpMetadataTargetReady,
                foldReady))
        {
            return null;
        }

        return new FoldOnlyRemediation(
            BuildFoldNotReadyExplanation(foldReadyReason),
            BuildFoldBackfillCommand(resolvedDbPath),
            BuildFoldRebuildCommand(projectRoot, resolvedDbPath));
    }

    private static bool IsFoldOnlyReadinessDegraded(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady)
        => !foldReady
           && graphTableAvailable
           && issuesTableAvailable
           && sqlGraphContractReady
           && hotspotFamilyReady
           && csharpSymbolNameReady
           && csharpMetadataTargetReady;

    private static string GetIndexReadinessWarning(bool graphTableAvailable, bool issuesTableAvailable, bool sqlGraphContractReady, bool hotspotFamilyReady, bool csharpSymbolNameReady, bool csharpMetadataTargetReady, bool foldReady, string? foldReadyReason, string projectRoot, string resolvedDbPath)
    {
        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailable,
            issuesTableAvailable,
            sqlGraphContractReady,
            hotspotFamilyReady,
            csharpSymbolNameReady,
            csharpMetadataTargetReady,
            foldReady,
            foldReadyReason,
            projectRoot,
            resolvedDbPath);
        if (foldOnlyRemediation != null)
        {
            return $"Index completed with fold-only degraded readiness (fold_ready=false). {foldOnlyRemediation.DegradedReason} Run `{foldOnlyRemediation.RecommendedAction}` to restamp folded-name columns in place, or `{foldOnlyRemediation.AlternativeAction}` for a full rebuild.";
        }

        var degradedParts = new List<string>();
        if (!graphTableAvailable)
            degradedParts.Add(DegradationReasonCodes.GraphTableMissing);
        if (!issuesTableAvailable)
            degradedParts.Add(DegradationReasonCodes.IssuesTableMissing);
        if (!sqlGraphContractReady)
            degradedParts.Add(DegradationReasonCodes.SqlGraphContractNotReady);
        if (!hotspotFamilyReady)
            degradedParts.Add(DegradationReasonCodes.HotspotFamilyNotReady);
        if (!csharpSymbolNameReady)
            degradedParts.Add(DegradationReasonCodes.CSharpSymbolNameNotReady);
        if (!csharpMetadataTargetReady)
            degradedParts.Add(DegradationReasonCodes.CSharpMetadataTargetNotReady);
        if (!foldReady)
            degradedParts.Add(DegradationReasonCodes.FoldReadyNotReady);

        return $"Index completed with degraded readiness ({string.Join(", ", degradedParts)}). Run `cdidx status --db \"{resolvedDbPath}\" --json` to inspect the current DB state.";
    }

    private static string QuoteCommandArgument(string value)
    {
        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

}
