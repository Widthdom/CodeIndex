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
    private const int PartialIndexFileErrorLimit = 50;

    internal static string FormatPerFileErrorLine(string label, string path, Exception ex) =>
        FormatPerFileErrorLine(label, path, ex, FormatIndexFileException(ex));

    internal static string FormatPerFileErrorLine(string label, string path, Exception ex, string message) =>
        $"  [{label}] {CollapseLineBreaks(path)}: {CollapseLineBreaks(message)}";

    internal static void LogIndexFileFailure(string eventName, string path, Exception ex) =>
        LogIndexFileFailure(eventName, path, phase: null, ex);

    internal static void LogIndexFileFailure(string eventName, string path, string? phase, Exception ex)
    {
        var phaseSuffix = string.IsNullOrWhiteSpace(phase) ? string.Empty : $" phase={CollapseLineBreaks(phase)}";
        var detail = CollapseLineBreaks(FormatIndexFileException(ex));
        GlobalToolLog.Error($"{eventName} path={CollapseLineBreaks(path)}{phaseSuffix} detail={detail}", ex);
    }

    [DoesNotReturn]
    internal static void RethrowPreservingStackTrace(Exception ex) =>
        ExceptionDispatchInfo.Capture(ex).Throw();

    internal static string FormatIndexFileException(Exception ex) =>
        ex switch
        {
            RegexMatchTimeoutException timeoutException => RuntimeSafety.FormatRegexTimeout(timeoutException),
            IndexExtractionStalledException stalledException => FormatExtractionStalledMessage(stalledException),
            SymbolExtractionWorkerFailureException workerException =>
                $"Symbol extraction worker failed. Worker diagnostic: {CollapseLineBreaks(workerException.WorkerError)}",
            _ => CommandErrorWriter.FormatSanitizedException(ex),
        };

    internal static StatusIndexFileError BuildIndexFileError(string path, string? phase, Exception ex)
    {
        var stablePhase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
        var category = ex switch
        {
            RegexMatchTimeoutException => "regex_timeout",
            IndexExtractionStalledException => "extraction_stalled",
            SqliteException => "persistence_error",
            IOException or UnauthorizedAccessException when stablePhase == "reading" => "file_read_error",
            _ when stablePhase is "chunking" or "symbols" or "references" or "validating" => "extraction_error",
            _ when stablePhase == "committing" => "persistence_error",
            _ => "index_file_error",
        };
        var (line, column) = ex is JsonException jsonException
            ? (jsonException.LineNumber + 1, jsonException.BytePositionInLine + 1)
            : ((long?)null, (long?)null);
        return new StatusIndexFileError
        {
            File = FileIndexer.NormalizePathSeparators(path),
            Category = category,
            Phase = stablePhase,
            Detail = ex is SymbolExtractionWorkerFailureException
                ? DiagnosticRedactor.BoundDiagnosticText(FormatIndexFileException(ex), maxChars: 512)
                : CommandErrorWriter.FormatSanitizedExceptionDetail(ex),
            Line = line,
            Column = column,
        };
    }

    private static string FormatExtractionStalledMessage(IndexExtractionStalledException ex)
    {
        var pathSuffix = string.IsNullOrWhiteSpace(ex.ActivePath) ? string.Empty : $" Last active phase: {ex.ActivePath}.";
        return $"Index extraction made no progress for {ConsoleUi.FormatDuration(ex.Timeout)}.{pathSuffix}{FormatWorkerDiagnosticSuffix(ex.WorkerError)}";
    }

    private static string FormatWorkerDiagnosticSuffix(string? workerError)
        => string.IsNullOrWhiteSpace(workerError)
            ? string.Empty
            : $" Worker diagnostic: {CollapseLineBreaks(workerError)}.";

    private static FileIssue BuildSymbolCountExceededIssue(string path, int symbolCount, int maxSymbolsPerFile) =>
        new()
        {
            Path = path,
            Kind = "symbol_count_exceeded",
            Line = 0,
            Message = $"Symbol extraction produced {symbolCount:N0} symbols, exceeding the --max-symbols-per-file limit of {maxSymbolsPerFile:N0}; file content, symbols, and references were not indexed. Exclude the generated/pathological file or raise --max-symbols-per-file if this is expected.",
        };

    private static FileIssue BuildReferenceCountExceededIssue(string path, int referenceCount, int maxReferencesPerFile) =>
        new()
        {
            Path = path,
            Kind = "reference_count_exceeded",
            Line = 0,
            Message = $"Reference extraction produced {referenceCount:N0} references, exceeding the --max-references-per-file limit of {maxReferencesPerFile:N0}; references were not indexed for this file. Exclude the generated/pathological file or raise --max-references-per-file if this is expected.",
        };

    internal static IReadOnlyList<FileIssue> AppendReferenceExtractionDiagnosticIssues(
        IReadOnlyList<FileIssue> issues,
        string path,
        IReadOnlyList<ReferenceExtractionDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            issues = AppendIssue(issues, new FileIssue
            {
                Path = path,
                Kind = diagnostic.Kind,
                Line = 0,
                Message = diagnostic.Message,
                Severity = FileIssue.SeverityWarning,
            });
        }

        return issues;
    }

    internal static FileIssue BuildNullByteIssue(FileIndexer.BinaryFileSkippedException ex) =>
        new()
        {
            Path = ex.RelativePath,
            Kind = "null_byte",
            Line = 0,
            Message = CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
        };

    internal static FileIssue? BuildRegexTimeoutIssue(string path, BoundedRegex.RegexTimeoutCaptureScope capture) =>
        BuildRegexTimeoutIssue(
            path,
            capture.Language,
            capture.PatternFamily,
            capture.TimeoutCount,
            capture.Diagnostics,
            capture.DiagnosticsTruncated);

    internal static FileIssue? BuildRegexTimeoutIssue(
        string path,
        string? language,
        string patternFamily,
        int timeoutCount,
        IReadOnlyList<BoundedRegex.RegexTimeoutDiagnostic> diagnostics,
        bool diagnosticsTruncated)
    {
        if (timeoutCount <= 0)
            return null;

        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "unknown" : language;
        var samples = diagnostics.Count == 0
            ? "none"
            : string.Join(", ", diagnostics.Select(static diagnostic =>
                $"{diagnostic.Operation}:{diagnostic.PatternHash} len={diagnostic.PatternLength} timeout={diagnostic.TimeoutMs:0.###}ms"));
        var truncationSuffix = diagnosticsTruncated ? "; additional timeout diagnostics omitted" : string.Empty;
        return new FileIssue
        {
            Path = path,
            Kind = "regex_timeout",
            Line = 0,
            Message = $"Regex timeout fallback occurred during {patternFamily} for language {normalizedLanguage} ({timeoutCount:N0} timeout(s); samples {samples}{truncationSuffix}); extraction used a safe no-match fallback and may be incomplete for this file.",
        };
    }

    private static bool ExistingFileBlocksReuse(
        DbWriter writer,
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        FileIssue? generatedSuppressionIssue) =>
        ExistingFileBlocksReuse(
            writer,
            fileId,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            generatedSuppressionIssue != null);

    private static bool ExistingFileBlocksReuse(
        DbWriter writer,
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool generatedExtractionSuppressed) =>
        writer.HasReusableFileBlockingIssueForFile(
            fileId,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            generatedExtractionSuppressed);

    internal static IReadOnlyList<FileIssue> AppendIssue(IReadOnlyList<FileIssue> issues, FileIssue issue)
    {
        if (issues.Count == 0)
            return [issue];

        var combined = issues.ToList();
        combined.Add(issue);
        return combined;
    }

    internal static IReadOnlyList<FileIssue> AppendIssueIfMissing(IReadOnlyList<FileIssue> issues, FileIssue issue)
    {
        for (var i = 0; i < issues.Count; i++)
        {
            if (string.Equals(issues[i].Kind, issue.Kind, StringComparison.Ordinal))
                return issues;
        }

        return AppendIssue(issues, issue);
    }

    internal static string FormatIndexPhasePath(string path, string phase) =>
        $"{path} ({phase})";

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
        CancellationToken cancellationToken)
    {
        var timeout = IndexExtractionStallTimeoutForTesting?.Invoke() ?? IndexExtractionStallTimeout;
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

    internal const int MaxScanCheckpointBytes = 1024 * 1024;
    internal const int MaxScanCheckpointJsonDepth = 16;
    internal const int MaxScanCheckpointDirectories = 4096;
    internal const int MaxScanCheckpointDirectoryLength = 4096;

    internal static IReadOnlySet<string> LoadScanCheckpoint(string path, string? currentHead) =>
        LoadScanCheckpointDetailed(path, currentHead).Directories;

    internal static ScanCheckpointLoadResult LoadScanCheckpointDetailed(string path, string? currentHead)
    {
        try
        {
            if (!File.Exists(path))
                return EmptyScanCheckpointLoadResult();
            if (string.IsNullOrWhiteSpace(currentHead))
                return IgnoredScanCheckpoint(path, "current Git HEAD is unavailable");

            var text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxScanCheckpointBytes, FileShare.ReadWrite);
            if (text is null)
                return IgnoredScanCheckpoint(path, $"file exceeds the scan checkpoint size limit of {MaxScanCheckpointBytes:N0} bytes");

            var checkpoint = BoundedJson.Deserialize<ScanCheckpoint>(
                text,
                MaxScanCheckpointBytes,
                new JsonSerializerOptions { MaxDepth = MaxScanCheckpointJsonDepth });
            if (checkpoint is null)
                return IgnoredScanCheckpoint(path, "JSON root is null or not a scan checkpoint object");
            if (checkpoint.Version != ScanCheckpointVersion)
                return IgnoredScanCheckpoint(path, FormatScanCheckpointVersionMismatch(checkpoint.Version));
            if (!string.Equals(checkpoint.GitHead, currentHead, StringComparison.Ordinal))
                return IgnoredScanCheckpoint(path, "checkpoint GitHead does not match current HEAD; checkpoint is stale");
            if (!TryBuildScanCheckpointDirectories(checkpoint.Directories, out var directories, out var directoryFailureReason))
                return IgnoredScanCheckpoint(path, directoryFailureReason);

            return new ScanCheckpointLoadResult(directories, WarningMessage: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return IgnoredScanCheckpoint(
                path,
                $"malformed checkpoint JSON, exceeded the JSON byte limit, or depth exceeds {MaxScanCheckpointJsonDepth:N0} ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
        catch (IOException ex)
        {
            return IgnoredScanCheckpoint(path, $"read failed ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
        catch (UnauthorizedAccessException ex)
        {
            return IgnoredScanCheckpoint(path, $"read failed ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
    }

    private static string FormatScanCheckpointVersionMismatch(int version) =>
        version > ScanCheckpointVersion
            ? $"future checkpoint version {version:N0} exceeds supported version {ScanCheckpointVersion:N0}"
            : $"unsupported checkpoint version {version:N0}; supported version is {ScanCheckpointVersion:N0}";

    private static ScanCheckpointLoadResult EmptyScanCheckpointLoadResult() =>
        new(EmptyScanCheckpointDirectories(), WarningMessage: null);

    private static ScanCheckpointLoadResult IgnoredScanCheckpoint(string path, string reason) =>
        new(
            EmptyScanCheckpointDirectories(),
            $"scan checkpoint ignored for {ConsoleUi.FormatBoundedValue(path)}: {reason}; continuing with a full scan.");

    private static bool TryBuildScanCheckpointDirectories(
        IReadOnlyList<string>? rawDirectories,
        out IReadOnlySet<string> directories,
        out string failureReason)
    {
        directories = EmptyScanCheckpointDirectories();
        failureReason = string.Empty;
        if (rawDirectories is not { Count: > 0 })
        {
            failureReason = "Directories must be a non-empty JSON array";
            return false;
        }
        if (rawDirectories.Count > MaxScanCheckpointDirectories)
        {
            failureReason =
                $"Directories contains {rawDirectories.Count:N0} entries, exceeding the limit of {MaxScanCheckpointDirectories:N0}";
            return false;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in rawDirectories)
        {
            if (directory is null)
            {
                failureReason = "Directories contains a null entry";
                return false;
            }
            if (directory.Length == 0)
                continue;
            if (directory.Length > MaxScanCheckpointDirectoryLength)
            {
                failureReason =
                    $"Directories contains an entry longer than {MaxScanCheckpointDirectoryLength:N0} characters";
                return false;
            }

            result.Add(directory);
        }

        if (result.Count == 0)
        {
            failureReason = "Directories contains only empty entries";
            return false;
        }

        directories = result;
        return true;
    }

    private static HashSet<string> EmptyScanCheckpointDirectories() => new(StringComparer.Ordinal);

    private static void SaveScanCheckpoint(
        string path,
        string? currentHead,
        IReadOnlySet<string> directories,
        List<CliJsonMessage> warningList,
        bool json,
        bool quiet)
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
            if (WriteScanCheckpointForTesting != null)
                WriteScanCheckpointForTesting(path);
            else
                AtomicFileWriter.WriteJson(
                    path,
                    checkpoint,
                    new JsonSerializerOptions { WriteIndented = true },
                    AtomicFileWriter.WriteProfile.Sensitive);
        }
        catch (Exception ex) when (IsScanCheckpointPersistenceException(ex))
        {
            RecordScanCheckpointPersistenceWarning(path, "save", ex, warningList, json, quiet);
        }
    }

    private static void DeleteScanCheckpoint(
        string path,
        List<CliJsonMessage> warningList,
        bool json,
        bool quiet)
    {
        try
        {
            if (File.Exists(path))
            {
                if (DeleteScanCheckpointForTesting != null)
                    DeleteScanCheckpointForTesting(path);
                else
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (IsScanCheckpointPersistenceException(ex))
        {
            RecordScanCheckpointPersistenceWarning(path, "delete", ex, warningList, json, quiet);
        }
    }

    private static bool IsScanCheckpointPersistenceException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void RecordScanCheckpointPersistenceWarning(
        string path,
        string operation,
        Exception ex,
        List<CliJsonMessage> warningList,
        bool json,
        bool quiet)
    {
        var message =
            $"scan checkpoint {operation} failed for {ConsoleUi.FormatBoundedValue(path)} " +
            $"({CommandErrorWriter.FormatSanitizedException(ex)}); continuing without failing the scan.";
        warningList.Add(new CliJsonMessage("<scan_checkpoint>", message));
        if (!json && !quiet)
            ConsoleUi.PrintWarning(message);
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
        int? initialFileCapacity,
        CancellationToken cancellationToken)
    {
        var actualMode = options.Rebuild ? "rebuild" : "incremental";
        CancellationTokenSource? spinnerCts = null;
        if (!options.Json && !options.Quiet)
            spinnerCts = ConsoleUi.StartSpinner("Scanning...", spinnerFrames);

        void ThrowIfDiscoveryCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            ConsoleUi.StopSpinner(spinnerCts);
            throw new IndexInterruptedException(0, null, actualMode);
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
            throw new IndexInterruptedException(0, null, actualMode);
        }

        var scanCheckpointPath = Path.Combine(projectRoot, ".cdidx", ScanCheckpointFileName);
        var checkpointLoadResult = LoadScanCheckpointDetailed(scanCheckpointPath, currentHeadForCheckpoint);
        var checkpointedDirectories = checkpointLoadResult.Directories;
        WriteFullScanJsonLiveness(options, "scanning files...");
        var scanHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "scanning files");
        FileIndexer.ScanFilesResult scanResult;
        try
        {
            ThrowIfDiscoveryCancelled();
            scanResult = indexer.ScanFilesDetailed(
                checkpointedDirectories,
                continueOnError: true,
                initialFileCapacity: initialFileCapacity,
                cancellationToken: cancellationToken);
            ThrowIfDiscoveryCancelled();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(0, null, actualMode);
        }
        finally
        {
            StopFullScanJsonPhaseHeartbeat(scanHeartbeat);
        }
        var files = scanResult.Files;
        ConsoleUi.StopSpinner(spinnerCts);
        WriteFullScanJsonLiveness(options, $"found {ConsoleUi.Counted(files.Count, "file", format: "N0")}; preparing database...");
        var errorList = new List<CliJsonMessage>();
        var warningList = new List<CliJsonMessage>();
        foreach (var error in scanResult.Errors)
        {
            var message = new CliJsonMessage(error.Path, error.Message);
            if (error.IsFatal)
                errorList.Add(message);
            else
                warningList.Add(message);
        }
        if (checkpointLoadResult.WarningMessage != null)
            warningList.Add(new CliJsonMessage("<scan_checkpoint>", checkpointLoadResult.WarningMessage));
        if (!options.Json && !options.Quiet)
        {
            CommandOutputWriter.WriteLine($"  Found {ConsoleUi.Counted(files.Count, "file", format: "N0")}");
            foreach (var error in scanResult.Errors)
                ConsoleUi.PrintWarning($"{error.Path}: {error.Message}");
            if (checkpointLoadResult.WarningMessage != null)
                ConsoleUi.PrintWarning(checkpointLoadResult.WarningMessage);
            CommandOutputWriter.WriteLine();
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
        return StartObservedJsonPhaseHeartbeat(
            options.Json && !options.Quiet,
            "cdidx-index",
            phase,
            ConsoleUi.TryWriteErrorLine,
            detailProvider);
    }

    private static void StopFullScanJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
        => StopObservedJsonPhaseHeartbeat(heartbeat);

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
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var priorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharp == currentMetadataTargetVersion;
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

            throw new IndexInterruptedException(filesProcessed, filesTotal, actualMode);
        }

        int? initialScanFileCapacity = options.Rebuild ? null : writer.GetIndexedFileCount();
        var discovery = DiscoverFullScanFiles(
            indexer,
            projectRoot,
            options,
            spinnerFrames,
            initialScanFileCapacity,
            cancellationToken);
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
        var knownReadableFileSizes = new long[files.Count];
        var knownReadableFileSizeKnown = new bool[files.Count];
        var knownReadableFileCount = 0;
        long knownReadableBytesRead = 0;
        void RememberReadableFileSize(int fileIndex, long size)
        {
            if (knownReadableFileSizeKnown[fileIndex])
            {
                var priorSize = knownReadableFileSizes[fileIndex];
                knownReadableBytesRead += size - priorSize;
            }
            else
            {
                knownReadableFileSizeKnown[fileIndex] = true;
                knownReadableFileCount++;
                knownReadableBytesRead += size;
            }
            knownReadableFileSizes[fileIndex] = size;
        }
        FileByteReadSummary MeasureRemainingReadableFileBytes()
        {
            long total = knownReadableBytesRead;
            long skipped = 0;
            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                if (knownReadableFileSizeKnown[fileIndex])
                    continue;

                var path = files[fileIndex];
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                        total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    skipped++;
                    RecordIndexRunDiagnostic(indexRunDiagnostics, "file_size_bytes_skipped", FormatDiagnosticPath(projectRoot, path), ex);
                }
            }

            return new FileByteReadSummary(total, skipped);
        }
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
        using var hotspotAggregateRefresh = writer.BeginDeferredHotspotReferenceAggregateRefresh();
        using var fullScanTxn = writer.BeginTransaction(cancellationToken, "full scan write phase");
        writer.MarkBatchInProgress();
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearSqlGraphContractReady();
        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
        if (options.SymbolsOnly)
            writer.ClearReferenceIdentityContractReady();
        writer.ClearMetadataTargetReady();
        FullScanWritePhaseStartedForTesting?.Invoke();
        ThrowIfFullScanCancelled(0, files.Count);

        CancellationTokenSource? purgeCts = null;
        if (!options.Json && !options.Quiet)
            purgeCts = ConsoleUi.StartSpinner("Cleaning up stale entries...", spinnerFrames);
        var purged = 0;
        var startedWithNoIndexedFiles = !writer.HasAnyIndexedFiles();
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
            SaveScanCheckpoint(
                scanCheckpointPath,
                currentHeadForCheckpoint,
                scanResult.CheckpointedDirectories,
                warningList,
                options.Json,
                options.Quiet);
            if (!startedWithNoIndexedFiles)
            {
                retainedPaths!.UnionWith(scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));

                foreach (var relPath in scanResult.NonIndexablePaths)
                {
                    var dbPath = FileIndexer.NormalizeIndexPath(relPath);
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
                purged += writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retainedPaths!, authoritativeDirectories, attributePrunedDirectories);
            }
        }
        else
        {
            if (!startedWithNoIndexedFiles && checkpointedDirectories.Count > 0)
            {
                var authoritativeDirectories = scanResult.ListedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                    .Select(FileIndexer.NormalizeIndexPath)
                    .ToHashSet(StringComparer.Ordinal);
                attributePrunedDirectories.UnionWith(scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
                purged = writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retainedPaths!, authoritativeDirectories, attributePrunedDirectories);
            }
            else if (!startedWithNoIndexedFiles)
            {
                purged = writer.PurgeFilesOutsideRetainedSet(retainedPaths!);
            }
            DeleteScanCheckpoint(scanCheckpointPath, warningList, options.Json, options.Quiet);
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
                var purgeMessage = scanHadErrors
                    ? $"  Purged {purged:N0} previously indexed files that were positively observed as no longer indexable or missing from directories whose file listing completed successfully"
                    : $"  Purged {purged:N0} stale files (missing or no longer indexable)";
                CommandOutputWriter.WriteLine(purgeMessage);
            }
            if (scanHadErrors)
                ConsoleUi.PrintWarning("Skipped authoritative purge outside directories whose file listing completed successfully because some paths could not be scanned.");
        }

        // Purge references for languages no longer graph-supported, or all references in
        // symbols-only mode so old graph rows cannot survive behind degraded readiness.
        // グラフ非対応になった言語の参照をパージする。symbols-only では古い graph 行が
        // degraded readiness の裏に残らないよう全参照を消す。
        ThrowIfFullScanCancelled(0, files.Count);
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
        var purgedRefs = startedWithNoIndexedFiles
            ? 0
            : options.SymbolsOnly
                ? writer.PurgeAllReferences()
                : writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages(projectRoot));
        if (purgedRefs > 0 && !options.Json && !options.Quiet)
        {
            var reason = options.SymbolsOnly ? "symbols-only mode" : "unsupported language";
            CommandOutputWriter.WriteLine($"  Purged {purgedRefs:N0} stale references ({reason})");
        }

        CancellationTokenSource? indexCts = null;
        int processed = 0, skipped = 0, warnings = warningList.Count, errors = errorList.Count;
        var ftsMutated = purged > 0;
        var symbolsDroppedByKindFilter = 0;
        var mutualRecursionRefreshNeeded = !options.SymbolsOnly
            && (!writer.ReferenceIdentityContractMatchesCurrent() || purged > 0 || purgedRefs > 0);

        var interactiveIndexSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        var redirectedIndexingMessagePrinted = false;
        var indexProgressVisible = false;
        HashSet<string>? reusedHotspotFamilyLanguages = null;
        HashSet<string>? skippedSymbolExtractorLanguages = null;
        var indexedSymbolExtractorLanguages = new HashSet<string>(languageCounts.Count, StringComparer.Ordinal);
        var lastJsonProgressAt = Stopwatch.GetTimestamp();
        string? currentJsonIndexFile = null;
        ActiveExtractionPhase?[] activeExtractionPhases = [];
        CancellationTokenSource? jsonHeartbeatCts = null;
        Task? jsonHeartbeatTask = null;
        var extractionParallelism = Math.Max(1, options.Parallelism);
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = !options.SymbolsOnly
            && (options.Rebuild
                || startedWithNoIndexedFiles
                || purged > 0
                || !projectRootWritten
                || !typeScriptAugmentationVersionMatchesCurrent);
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var csharpMetadataTargetsNeedRefresh = options.Rebuild
            || startedWithNoIndexedFiles
            || purged > 0
            || !priorMetadataTargetCsharpMatchesCurrent;

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

        if (purged > 0 || (options.SymbolsOnly && purgedRefs > 0))
            RequireTypeScriptAugmentationRefresh();

        var javaScriptTypeScriptRefreshRequired = forceJavaScriptTypeScriptRefresh
            || (!options.Rebuild
                && !startedWithNoIndexedFiles
                && FullScanJavaScriptTypeScriptConfigChanged());

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
            CommandOutputWriter.WriteLine(message);
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

        using var ftsBulkLoad = FtsBulkLoadTriggerGuard.Start(
            writer,
            options.Rebuild || startedWithNoIndexedFiles);

        void InsertIssuesForIndexedFile(long fileId, IReadOnlyList<FileIssue> issues)
        {
            if (startedWithNoIndexedFiles)
                writer.InsertIssuesForNewFile(fileId, issues);
            else
                writer.InsertIssues(fileId, issues);
        }

        var reusableIndexedFileStats = !forceExtractorRefresh && !options.Rebuild && !startedWithNoIndexedFiles
            ? writer.LoadReusableIndexedFileStats(
                options.MaxSymbolsPerFile,
                options.MaxReferencesPerFile,
                cancellationToken,
                initialScanFileCapacity.GetValueOrDefault())
            : null;
        Dictionary<string, IndexedFileStatReuseResult?>? csharpPrepassStatReuse = null;

        bool CanReuseCSharpPrepassTargetWithoutRead(CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (forceExtractorRefresh || options.Rebuild || startedWithNoIndexedFiles || !symbolKindFilterMatchesPrior || !csharpSymbolNameContractMatchesCurrent)
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

        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        if (options.SymbolsOnly)
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
                else
                {
                    FullScanCSharpPrepassForTesting?.Invoke();
                    csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        writer,
                        indexer,
                        csharpPrepassTargets,
                        includeExistingSymbols: !options.Rebuild && !startedWithNoIndexedFiles,
                        canReuseExistingSymbolsWithoutRead: CanReuseCSharpPrepassTargetWithoutRead,
                        reportCandidateFile: (candidateIndex, path) => SetActiveCSharpPrepassPath(activeCSharpWorkspaceFiles, candidateIndex, path),
                        parallelism: extractionParallelism,
                        cancellationToken: cancellationToken);
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

        bool TrySkipFullScanTargetBeforeContentLoad(int fileIndex)
        {
            if (!canSkipFullScanTargetsBeforeContentLoad)
                return false;

            var target = fileTargets[fileIndex];
            var language = target.Language;
            var targetRequiresRefresh = TargetRequiresJavaScriptTypeScriptRefresh(language, target.IndexPath);
            var allowReuse = symbolKindFilterMatchesPrior
                && !targetRequiresRefresh
                && !priorSymbolsOnlyGraphOmitted
                && (language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                && (language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                && (language != "sql" || sqlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(language, hotspotFamilyTrustMatchesCurrent);
            var existingFile = !allowReuse
                ? null
                : language == "csharp"
                  && csharpPrepassStatReuse != null
                  && csharpPrepassStatReuse.TryGetValue(target.IndexPath, out var cachedCSharpPrepassReuse)
                    ? cachedCSharpPrepassReuse
                    : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        language,
                        target.GeneratedExtractionSuppressed);
            if (existingFile == null)
                return false;

            skipped++;
            processed++;
            RememberReadableFileSize(fileIndex, existingFile.Value.Size);
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
            return true;
        }

        ThrowIfFullScanCancelled(processed, files.Count);
        List<int>? extractionFileIndexes = null;
        int extractionWorkItemCount;
        if (canSkipFullScanTargetsBeforeContentLoad)
        {
            extractionFileIndexes = new List<int>(fileTargets.Length);
            for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (!TrySkipFullScanTargetBeforeContentLoad(fileIndex))
                    extractionFileIndexes.Add(fileIndex);
            }
            extractionWorkItemCount = extractionFileIndexes.Count;
        }
        else
        {
            extractionWorkItemCount = fileTargets.Length;
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
                    PauseIndexSpinnerForConsoleWrite();
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
                                            workspaceRoot: projectRoot);
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
                                extractionResults.Add(
                                    FullScanFileWorkItem.Skipped(filePath, displayRelativePath, $"{displayRelativePath}: skipped because it was deleted during indexing."),
                                    extractionCancellationToken);
                            }
                            catch (Exception ex)
                            {
                                var failedPhase = Volatile.Read(ref activeExtractionPhases[workerIndex])?.Phase ?? "unknown";
                                extractionResults.Add(FullScanFileWorkItem.Failure(filePath, displayRelativePath, failedPhase, ex), extractionCancellationToken);
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
                        if (item.Exception != null)
                            RethrowPreservingStackTrace(item.Exception);

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
                                using var deleteTxn = writer.BeginTransaction(cancellationToken, "full scan delete skipped file");
                                if (writer.DeleteFileByPath(currentJsonIndexFile))
                                {
                                    ftsMutated = true;
                                    csharpMetadataTargetsNeedRefresh = true;
                                    RequireTypeScriptAugmentationRefresh();
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
                        RememberReadableFileSize(item.FileIndex, record.Size);
                        if (item.Warning != null && !options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintWarning(item.Warning);
                            ResumeIndexSpinnerAfterConsoleWrite();
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
                                    && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                                    && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                                    && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                                    && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                        }
                        if (existingId != null)
                        {
                            var stalePurged = writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
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
                                PauseIndexSpinnerForConsoleWrite();
                                ConsoleUi.ClearProgressLine();
                                CommandOutputWriter.WriteLine($"  [SKIP] {record.Path}");
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

                        if (record.Lang == "csharp")
                            csharpMetadataTargetsNeedRefresh = true;
                        if (record.Lang == "typescript")
                            RequireTypeScriptAugmentationRefresh();

                        using var txn = writer.BeginTransaction(cancellationToken, "full scan file");
                        if (!startedWithNoIndexedFiles)
                        {
                            var stalePurged = writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                            if (stalePurged > 0)
                            {
                                ftsMutated = true;
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
                        ftsMutated = true;
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
                                WriteIndexVerboseStatus($"  [OK  ] {record.Path} ({chunks.Count} chunks, generated-code extraction skipped)");
                            currentJsonIndexFile = FormatIndexPhasePath(record.Path, "committing");
                            WriteProjectRootOnce();
                            txn.Commit();
                            CountFreshInsertedRows(chunkCount: chunks.Count);

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
                                WriteIndexVerboseStatus($"  [SKIP] {record.Path} ({issue.Message})");
                            txn.Commit();
                            CountFreshInsertedRows();
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
                            IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                                ? [issue]
                                : AppendIssue([symbolRegexTimeoutIssue], issue);
                            writer.InsertSymbols([], cancellationToken);
                            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
                            writer.InsertIssues(fileId, capIssues);
                            if (options.Verbose)
                                WriteIndexVerboseStatus($"  [SKIP] {record.Path} ({issue.Message})");
                            txn.Commit();
                            CountFreshInsertedRows();
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
                                    workspaceRoot: projectRoot);
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
                        if (!string.IsNullOrWhiteSpace(record.Lang))
                            indexedSymbolExtractorLanguages.Add(record.Lang);
                        CountFreshInsertedRows(chunks.Count, symbols.Count, references.Count);

                        WriteIndexVerboseStatus($"  [OK  ] {record.Path} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
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
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.ClearProgressLine();
                            ConsoleUi.TryWriteErrorLine(FormatPerFileErrorLine("ERR ", item.FilePath, ex, errorMessage));
                            ResumeIndexSpinnerAfterConsoleWrite();
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
                postExtractionHooks?.Dispose();
            }
        }

        PauseIndexSpinnerForConsoleWrite();

        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("extraction", stopwatch));

        ThrowIfFullScanCancelled(processed, files.Count);
        if (mutualRecursionRefreshNeeded)
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
            var incrementalWrites = writer.RecordFtsIncrementalWrite();
            if (incrementalWrites >= DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold)
            {
                WriteFullScanJsonLiveness(options, "optimizing index...");
                var optimizeHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "optimizing index");
                try
                {
                    FullScanFtsOptimizeForTesting?.Invoke();
                    writer.OptimizeFtsIfIncrementalWriteThresholdReached(
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    StopFullScanJsonPhaseHeartbeat(optimizeHeartbeat);
                }
            }
        }
        if (options.MemoryTrace)
            memorySamples.Add(CaptureMemorySample("text_index", stopwatch));
        ThrowIfFullScanCancelled(processed, files.Count);
        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // ClearReadyFlags() ran at the start.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        var hasCSharpFilesAfter = startedWithNoIndexedFiles && !scanHadErrors && errors == 0
            ? languageCounts.ContainsKey("csharp")
            : writer.HasAnyFilesWithLanguage("csharp");
        var graphTableAvailableAfter = false;
        var issuesTableAvailableAfter = false;
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter;
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter;
        var foldReadyAfter = false;
        string? foldReadyReasonAfter = null;
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
            }
            writer.MarkIndexReaderContractsReady(options.SymbolsOnly);
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    FullScanCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets();
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
                if (typeScriptAugmentationNeedsRefresh)
                {
                    if (startedWithNoIndexedFiles && !languageCounts.ContainsKey("typescript"))
                    {
                        writer.MarkTypeScriptAugmentationReady();
                    }
                    else
                    {
                        FullScanTypeScriptAugmentationRebuildForTesting?.Invoke();
                        var augmentationReferences = writer.RebuildTypeScriptAugmentationReferences(projectRoot);
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
            var bytesRead = knownReadableFileCount == files.Count
                ? new FileByteReadSummary(knownReadableBytesRead, 0)
                : MeasureRemainingReadableFileBytes();
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
        var referenceGraphCompleteAfter = referenceExtractionCapHitsAfter.StateAvailable
            && referenceExtractionCapHitsAfter.HitCount == 0;
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

    private static IReadOnlyList<FileIssue> RequireWorkItemIssues(FullScanFileWorkItem item)
    {
        return item.Issues ?? throw new InvalidOperationException("Full-scan work item does not carry precomputed validation issues.");
    }

    private static int AddPostExtractionHookWarnings(PostExtractionHookRunner? runner, List<CliJsonMessage> warningList)
    {
        if (runner == null)
            return 0;

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
