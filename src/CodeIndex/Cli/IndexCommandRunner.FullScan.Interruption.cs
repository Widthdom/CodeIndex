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
    internal static string? GetActiveCSharpPrepassPath(string?[] activePaths)
    {
        for (var index = 0; index < activePaths.Length; index++)
        {
            var path = Volatile.Read(ref activePaths[index]);
            if (path != null)
                return path;
        }

        return null;
    }

    internal static void SetActiveCSharpPrepassPath(string?[] activePaths, int index, string? path) =>
        Volatile.Write(ref activePaths[index], path);

    private sealed record ActiveExtractionPhase(string Path, string Phase)
    {
        public string Format() => FormatIndexPhasePath(Path, Phase);
    }

    private static IEnumerable<string> FormatActiveExtractionPhases(ActiveExtractionPhase?[] phases)
    {
        for (var index = 0; index < phases.Length; index++)
        {
            var phase = Volatile.Read(ref phases[index]);
            if (phase != null)
                yield return phase.Format();
        }
    }

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
        ActiveExtractionPhase?[] activeExtractionPhases,
        Action cancelStalledWork)
    {
        if (!TryGetFullScanExtractionStallPath(
                filesProcessed,
                filesTotal,
                timeout,
                lastProgressTimestamp,
                currentFile,
                FormatActiveExtractionPhases(activeExtractionPhases),
                out var activePath))
        {
            return;
        }

        cancelStalledWork();
        throw new IndexExtractionStalledException(filesProcessed, filesTotal, timeout, activePath);
    }

    private sealed record SymbolExtractionResult(List<SymbolRecord> Symbols, FileIssue? RegexTimeoutIssue);

    private static SymbolExtractionResult ExtractSymbolsWithStallTimeout(
        long fileId,
        string? lang,
        string content,
        string filePath,
        string projectRoot,
        string issuePath,
        string phasePath,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        SymbolExtractionWorkerClient worker,
        CancellationToken cancellationToken,
        TimeSpan? stallTimeoutOverride = null)
    {
        var timeout = stallTimeoutOverride
            ?? IndexExtractionStallTimeoutForTesting?.Invoke()
            ?? IndexExtractionStallTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            using var regexTimeouts = BoundedRegex.CaptureTimeouts(lang, "symbol_extraction");
            var symbols = contentIsNormalized && hasOversizeLine is { } knownHasOversizeLine
                ? SymbolExtractor.ExtractNormalized(fileId, lang, content, knownHasOversizeLine, filePath, projectRoot, cancellationToken, conflictMarkerLine, patternConfigsAlreadyLoaded: true)
                : SymbolExtractor.ExtractWithPatternConfigsLoaded(fileId, lang, content, filePath, projectRoot, cancellationToken);
            return new SymbolExtractionResult(symbols, BuildRegexTimeoutIssue(issuePath, regexTimeouts));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = worker.Invoke(
            fileId,
            lang,
            content,
            filePath,
            projectRoot,
            contentIsNormalized,
            hasOversizeLine,
            conflictMarkerLine,
            timeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut)
            throw new IndexExtractionStalledException(0, null, timeout, phasePath, result.WorkerError);
        if (!result.Success)
            throw new SymbolExtractionWorkerFailureException(result.WorkerError ?? "isolated symbol extraction worker failed.");

        var regexTimeoutIssue = BuildRegexTimeoutIssue(
            issuePath,
            lang,
            "symbol_extraction",
            result.RegexTimeoutCount,
            result.RegexTimeoutDiagnostics ?? [],
            result.RegexTimeoutDiagnosticsTruncated);
        return new SymbolExtractionResult(result.Symbols ?? [], regexTimeoutIssue);
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

    private static int? RejectUnresolvedMergeState(
        string projectRoot,
        bool json,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var status = GitHelper.TryGetWorktreeStatus(projectRoot, cancellationToken);
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

    private static bool InterruptedProgressIsPersisted(string mode, long filesProcessed)
        => string.Equals(mode, "update", StringComparison.Ordinal) && filesProcessed > 0;

    private static string BuildInterruptedRecoveryHint(string mode, bool progressPersisted)
    {
        if (progressPersisted)
            return "Rerun `cdidx index` to finish refreshing the remaining files; completed update-mode file transactions remain in the index. Press Ctrl-C again during a future run to force-exit.";

        if (string.Equals(mode, "update", StringComparison.Ordinal))
            return "Rerun `cdidx index` to retry the update; no update-mode file transaction completed before the interruption. Press Ctrl-C again during a future run to force-exit.";

        return "Rerun `cdidx index` to retry from the previous durable index; interrupted full-scan and rebuild writes are rolled back. Press Ctrl-C again during a future run to force-exit.";
    }

    private static int WriteInterruptedResult(
        bool json,
        JsonSerializerOptions jsonOptions,
        int filesProcessed,
        int? filesTotal,
        string mode,
        bool progressPersisted)
    {
        var totalSuffix = filesTotal is > 0 ? $" of {filesTotal.Value:N0}" : string.Empty;
        var progressDescription = progressPersisted
            ? "completed update progress was saved"
            : string.Equals(mode, "update", StringComparison.Ordinal)
                ? "no update progress was saved"
                : $"{DescribeInterruptedRollbackMode(mode)} progress was rolled back";
        return WriteCommandError(
            json,
            jsonOptions,
            $"Interrupted; {progressDescription} ({filesProcessed:N0}{totalSuffix} files processed).",
            CommandExitCodes.Interrupted,
            BuildInterruptedRecoveryHint(mode, progressPersisted),
            CommandErrorCodes.Interrupted);
    }

    private static string DescribeInterruptedRollbackMode(string mode)
        => string.Equals(mode, "rebuild", StringComparison.Ordinal)
            ? "rebuild"
            : "full-scan";

    private static int WriteExtractionStalledResult(bool json, JsonSerializerOptions jsonOptions, IndexExtractionStalledException ex)
    {
        var totalSuffix = ex.FilesTotal is > 0 ? $" of {ex.FilesTotal.Value:N0}" : string.Empty;
        var pathSuffix = string.IsNullOrWhiteSpace(ex.ActivePath) ? string.Empty : $" Last active phase: {ex.ActivePath}.";
        return WriteCommandError(
            json,
            jsonOptions,
            $"Index extraction made no progress for {ConsoleUi.FormatDuration(ex.Timeout)} ({ex.FilesProcessed:N0}{totalSuffix} files processed).{pathSuffix}{FormatWorkerDiagnosticSuffix(ex.WorkerError)}",
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
            $"database write failed for {dbPath}: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
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
}
