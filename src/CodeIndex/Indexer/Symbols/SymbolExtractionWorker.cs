using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal sealed record SymbolExtractionWorkerResult(
    bool Success,
    bool TimedOut,
    string? WorkerError,
    long DurationMs,
    List<SymbolRecord>? Symbols,
    int RegexTimeoutCount = 0,
    List<BoundedRegex.RegexTimeoutDiagnostic>? RegexTimeoutDiagnostics = null,
    bool RegexTimeoutDiagnosticsTruncated = false);

internal sealed class SymbolExtractionWorkerFailureException(string workerError)
    : InvalidOperationException(workerError)
{
    internal string WorkerError { get; } = workerError;
}

internal sealed class SymbolExtractionWorkerClient : IDisposable
{
    private static readonly byte[] ProtocolLineTerminator = [(byte)'\n'];
    private readonly int maxProtocolLineBytes;
    private readonly object gate = new();
    private Process? process;
    private WorkerOutputBuffer stderr = new();
    private bool disposed;

    internal SymbolExtractionWorkerClient(long? maxFileSizeBytes = null)
    {
        maxProtocolLineBytes = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);
    }

    internal SymbolExtractionWorkerResult Invoke(
        long fileId,
        string? lang,
        string content,
        string filePath,
        string projectRoot,
        TimeSpan callbackBudget,
        CancellationToken cancellationToken = default,
        FileIndexer.SymlinkPolicy symlinkPolicy = FileIndexer.SymlinkPolicy.All)
        => Invoke(
            fileId,
            lang,
            content,
            filePath,
            projectRoot,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            callbackBudget,
            cancellationToken,
            symlinkPolicy);

    internal SymbolExtractionWorkerResult Invoke(
        long fileId,
        string? lang,
        string content,
        string filePath,
        string projectRoot,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        TimeSpan callbackBudget,
        CancellationToken cancellationToken = default,
        FileIndexer.SymlinkPolicy symlinkPolicy = FileIndexer.SymlinkPolicy.All)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var stopwatch = Stopwatch.StartNew();
            if (!EnsureStarted(out var startError))
            {
                stopwatch.Stop();
                return Failure(startError, stopwatch.ElapsedMilliseconds);
            }

            var request = new SymbolExtractionWorker.WorkerRequest(
                fileId,
                lang,
                content,
                filePath,
                projectRoot,
                contentIsNormalized,
                hasOversizeLine,
                conflictMarkerLine,
                symlinkPolicy);
            var waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0)
            {
                return TimedOutAfterKill(stopwatch);
            }

            Task<ReadOnlyMemory<byte>?> responseTask;
            Task sendTask;
            try
            {
                responseTask = BoundedLineReader.ReadUtf8LineAsync(
                    process!.StandardOutput.BaseStream,
                    maxProtocolLineBytes,
                    cancellationToken);
                sendTask = SendRequestAsync(
                    process.StandardInput.BaseStream,
                    request,
                    maxProtocolLineBytes,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while sending symbol extraction request.",
                    stopwatch);
            }

            if (!WaitForTask(sendTask, waitMilliseconds, cancellationToken, out var sendException))
            {
                return TimedOutAfterKill(stopwatch);
            }

            if (sendException != null)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", sendException)} while sending symbol extraction request.",
                    stopwatch);
            }

            waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0 || !WaitForTask(responseTask, waitMilliseconds, cancellationToken, out var responseException))
            {
                return TimedOutAfterKill(stopwatch);
            }

            if (responseException != null)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", responseException)} while reading symbol extraction response.",
                    stopwatch);
            }

            if (CallbackBudgetExceeded(stopwatch, callbackBudget))
            {
                return TimedOutAfterKill(stopwatch);
            }

            stopwatch.Stop();
            // WaitForTask already proved the async read completed within the worker budget;
            // GetResult only observes that completed protocol response on this sync API.
            var responseUtf8 = responseTask.GetAwaiter().GetResult();
            if (responseUtf8 == null)
            {
                var workerError = BuildWorkerExitError(process, stderr.GetCapturedText(), "worker exited before returning a response.");
                ClearExitedWorker();
                return Failure(workerError, stopwatch.ElapsedMilliseconds);
            }

            SymbolExtractionWorker.WorkerResponse? response;
            try
            {
                response = BoundedJson.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                    responseUtf8.Value.Span,
                    maxProtocolLineBytes,
                    SymbolExtractionWorker.JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while parsing symbol extraction response.",
                    stopwatch);
            }

            if (response == null)
                return Failure("worker returned an empty response.", stopwatch.ElapsedMilliseconds);
            ForwardCapturedStderr(response.CapturedStderr);
            if (!string.IsNullOrWhiteSpace(response.WorkerError))
                return Failure(response.WorkerError, stopwatch.ElapsedMilliseconds);
            if (response.Symbols == null)
                return Failure("worker response omitted symbols.", stopwatch.ElapsedMilliseconds);

            return new SymbolExtractionWorkerResult(
                Success: true,
                TimedOut: false,
                WorkerError: null,
                DurationMs: stopwatch.ElapsedMilliseconds,
                Symbols: response.Symbols,
                RegexTimeoutCount: response.RegexTimeoutCount,
                RegexTimeoutDiagnostics: response.RegexTimeoutDiagnostics,
                RegexTimeoutDiagnosticsTruncated: response.RegexTimeoutDiagnosticsTruncated);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            if (process == null)
                return;

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Best effort: disposal should not throw after indexing has completed.
            }

            var waitResult = WaitForWorkerExit(process, 1000);
            if (!waitResult.Exited)
            {
                var cleanupDiagnostic = WorkerProcessCleanupDiagnostics.Combine(waitResult.Diagnostic, KillWorker());
                LogCleanupDiagnostic("symbol_extraction_worker_cleanup_failed", cleanupDiagnostic);
            }
            else
            {
                ClearExitedWorker();
            }
        }
    }

    private bool EnsureStarted(out string error)
    {
        if (process is { HasExited: false })
        {
            error = string.Empty;
            return true;
        }

        ClearExitedWorker();
        stderr = new WorkerOutputBuffer();
        if (!SymbolExtractionWorker.TryCreateStartInfo(maxProtocolLineBytes, out var startInfo, out error))
            return false;

        var next = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        next.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                stderr.AppendLine(eventArgs.Data);
        };

        try
        {
            if (!next.Start())
            {
                error = "symbol extraction worker process did not start.";
                next.Dispose();
                return false;
            }

            next.BeginErrorReadLine();
            process = next;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = SafeDiagnosticFormatter.FormatExceptionCategory("worker_start_failed", ex);
            next.Dispose();
            return false;
        }
    }

    private string? KillWorker()
    {
        if (process == null)
            return null;

        var cleanupDiagnostic = SymbolExtractionWorker.TryKillProcess(process);
        ClearExitedWorker();
        return cleanupDiagnostic;
    }

    private void ClearExitedWorker()
    {
        if (process == null)
            return;

        process.Dispose();
        process = null;
    }

    private static SymbolExtractionWorkerResult Failure(string? message, long durationMs)
        => new(
            Success: false,
            TimedOut: false,
            WorkerError: string.IsNullOrWhiteSpace(message) ? "isolated symbol extraction worker failed." : message,
            DurationMs: Math.Max(0, durationMs),
            Symbols: null);

    private SymbolExtractionWorkerResult FailureAfterKill(string message, Stopwatch stopwatch)
    {
        var cleanupDiagnostic = KillWorker();
        stopwatch.Stop();
        return Failure(
            WorkerProcessCleanupDiagnostics.AppendToMessage(message, cleanupDiagnostic),
            stopwatch.ElapsedMilliseconds);
    }

    private SymbolExtractionWorkerResult TimedOutAfterKill(Stopwatch stopwatch)
    {
        var cleanupDiagnostic = KillWorker();
        stopwatch.Stop();
        return TimedOut(stopwatch.ElapsedMilliseconds, cleanupDiagnostic);
    }

    private static SymbolExtractionWorkerResult TimedOut(long durationMs, string? workerError = null)
        => new(
            Success: false,
            TimedOut: true,
            WorkerError: workerError,
            DurationMs: Math.Max(0, durationMs),
            Symbols: null);

    internal static async Task SendRequestAsync(
        Stream input,
        SymbolExtractionWorker.WorkerRequest request,
        int maxProtocolLineBytes,
        CancellationToken cancellationToken = default)
    {
        const int escapedCharacterBufferSize = 4 * 1024;
        var metadata = SymbolExtractionWorker.WorkerRequestMetadata.From(request);
        var metadataUtf8 = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            SymbolExtractionWorker.JsonOptions);
        var writtenBytes = 0;
        writtenBytes = await WriteBoundedRequestBytesAsync(
            input,
            metadataUtf8.AsMemory(0, metadataUtf8.Length - 1),
            writtenBytes,
            request.Content.Length,
            maxProtocolLineBytes,
            cancellationToken).ConfigureAwait(false);
        writtenBytes = await WriteBoundedRequestBytesAsync(
            input,
            SymbolExtractionWorker.RequestContentPrefix,
            writtenBytes,
            request.Content.Length,
            maxProtocolLineBytes,
            cancellationToken).ConfigureAwait(false);

        var escapedCharacters = ArrayPool<char>.Shared.Rent(escapedCharacterBufferSize);
        var escapedUtf8 = ArrayPool<byte>.Shared.Rent(
            Encoding.UTF8.GetMaxByteCount(escapedCharacterBufferSize));
        try
        {
            var contentOffset = 0;
            while (contentOffset < request.Content.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = JavaScriptEncoder.Default.Encode(
                    request.Content.AsSpan(contentOffset),
                    escapedCharacters.AsSpan(0, escapedCharacterBufferSize),
                    out var charactersConsumed,
                    out var charactersWritten,
                    isFinalBlock: true);
                if (charactersConsumed == 0 && charactersWritten == 0)
                    throw new JsonException("Symbol worker request content could not be JSON-escaped.");

                contentOffset += charactersConsumed;
                var utf8BytesWritten = Encoding.UTF8.GetBytes(
                    escapedCharacters.AsSpan(0, charactersWritten),
                    escapedUtf8);
                writtenBytes = await WriteBoundedRequestBytesAsync(
                    input,
                    escapedUtf8.AsMemory(0, utf8BytesWritten),
                    writtenBytes,
                    request.Content.Length,
                    maxProtocolLineBytes,
                    cancellationToken).ConfigureAwait(false);

                if (status == OperationStatus.Done)
                    break;
                if (status != OperationStatus.DestinationTooSmall)
                    throw new JsonException("Symbol worker request content contains invalid UTF-16.");
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(escapedCharacters, clearArray: true);
            ArrayPool<byte>.Shared.Return(escapedUtf8, clearArray: true);
        }

        _ = await WriteBoundedRequestBytesAsync(
            input,
            SymbolExtractionWorker.RequestSuffix,
            writtenBytes,
            request.Content.Length,
            maxProtocolLineBytes,
            cancellationToken).ConfigureAwait(false);
        await input.WriteAsync(ProtocolLineTerminator, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> WriteBoundedRequestBytesAsync(
        Stream output,
        ReadOnlyMemory<byte> bytes,
        int writtenBytes,
        int contentCharacters,
        int maxProtocolLineBytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length > maxProtocolLineBytes - writtenBytes)
        {
            throw new BoundedLineLengthException(
                contentCharacters,
                writtenBytes,
                maxProtocolLineBytes,
                maxProtocolLineBytes);
        }

        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return writtenBytes + bytes.Length;
    }

    private bool WaitForTask(Task task, int milliseconds, CancellationToken cancellationToken, out Exception? exception)
    {
        try
        {
            task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).GetAwaiter().GetResult();
            exception = null;
            return true;
        }
        catch (TimeoutException)
        {
            exception = null;
            return false;
        }
        catch (AggregateException ex)
        {
            exception = ex.GetBaseException();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = KillWorker();
            throw;
        }
        catch (Exception ex)
        {
            exception = ex;
            return true;
        }
    }

    private static int GetRemainingWaitMilliseconds(Stopwatch stopwatch, TimeSpan callbackBudget)
    {
        var remainingMilliseconds = callbackBudget.TotalMilliseconds - stopwatch.Elapsed.TotalMilliseconds;
        if (remainingMilliseconds <= 0)
            return 0;

        return Math.Max(1, (int)Math.Ceiling(Math.Min(remainingMilliseconds, int.MaxValue)));
    }

    private static bool CallbackBudgetExceeded(Stopwatch stopwatch, TimeSpan callbackBudget)
        => stopwatch.Elapsed > callbackBudget;

    private static string BuildWorkerExitError(Process? process, string stderr, string fallback)
    {
        var exitCode = process == null ? (int?)null : process.ExitCode;
        return SafeDiagnosticFormatter.FormatWorkerExit("worker_protocol_error", exitCode, fallback, stderr);
    }

    internal static WorkerProcessExitWaitResult WaitForWorkerExit(Process process, int milliseconds)
        => WorkerProcessCleanupDiagnostics.WaitForExit(process, milliseconds);

    private static void LogCleanupDiagnostic(string message, string? diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic))
            GlobalToolLog.Error($"{message} {diagnostic}");
    }

    private static void ForwardCapturedStderr(string? capturedStderr)
    {
        if (!string.IsNullOrEmpty(capturedStderr))
            Console.Error.Write(capturedStderr);
    }
}

internal sealed class WorkerPatternConfigRootSnapshot(string projectRoot)
{
    private readonly HashSet<string> inspectedOrdinal = new(StringComparer.Ordinal);
    private readonly HashSet<string> inspectedIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

    internal string ProjectRoot { get; } = projectRoot;
    internal long LastAccessSequence { get; set; }
    internal int RetainedDirectoryCount => inspectedOrdinal.Count + inspectedIgnoreCase.Count;

    internal bool Contains(string normalizedPatternDirectory, bool ignoreCase)
        => (ignoreCase ? inspectedIgnoreCase : inspectedOrdinal).Contains(normalizedPatternDirectory);

    internal bool Add(string normalizedPatternDirectory, bool ignoreCase)
        => (ignoreCase ? inspectedIgnoreCase : inspectedOrdinal).Add(normalizedPatternDirectory);
}

internal sealed class WorkerPatternConfigDiscoveryCache
{
    internal const int DefaultMaxRootSnapshots = ExtractorPluginRegistry.MaxRetainedWorkspaceSnapshots;
    internal const int DefaultMaxDirectoriesPerRoot = 4096;
    internal const int DefaultMaxDirectoriesPerWorker = 8192;

    private readonly int maxRootSnapshots;
    private readonly int maxDirectoriesPerRoot;
    private readonly int maxDirectoriesPerWorker;
    private readonly List<WorkerPatternConfigRootSnapshot> roots = [];
    private long accessSequence;

    internal WorkerPatternConfigDiscoveryCache(
        int maxRootSnapshots = DefaultMaxRootSnapshots,
        int maxDirectoriesPerRoot = DefaultMaxDirectoriesPerRoot,
        int maxDirectoriesPerWorker = DefaultMaxDirectoriesPerWorker)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRootSnapshots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDirectoriesPerRoot, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDirectoriesPerWorker, 1);
        this.maxRootSnapshots = maxRootSnapshots;
        this.maxDirectoriesPerRoot = maxDirectoriesPerRoot;
        this.maxDirectoriesPerWorker = maxDirectoriesPerWorker;
    }

    internal int RootCount => roots.Count;
    internal int RetainedDirectoryCount { get; private set; }

    internal bool TryGetRoot(string projectRoot, out WorkerPatternConfigRootSnapshot snapshot)
    {
        var normalizedRoot = PathCasing.NormalizeBoundaryPath(projectRoot);
        foreach (var candidate in roots)
        {
            if (!PathCasing.PathsEqual(candidate.ProjectRoot, normalizedRoot))
                continue;

            Touch(candidate);
            snapshot = candidate;
            return true;
        }

        snapshot = null!;
        return false;
    }

    internal WorkerPatternConfigRootSnapshot AddReloadedRoot(string projectRoot)
    {
        var normalizedRoot = PathCasing.NormalizeBoundaryPath(projectRoot);
        if (TryGetRoot(normalizedRoot, out var existing))
            return existing;

        if (roots.Count >= maxRootSnapshots)
        {
            var evicted = roots.MinBy(candidate => candidate.LastAccessSequence)!;
            roots.Remove(evicted);
            RetainedDirectoryCount -= evicted.RetainedDirectoryCount;
        }

        var snapshot = new WorkerPatternConfigRootSnapshot(normalizedRoot);
        Touch(snapshot);
        roots.Add(snapshot);
        return snapshot;
    }

    internal bool ShouldInspectPatternDirectory(
        WorkerPatternConfigRootSnapshot root,
        string patternDirectory)
    {
        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(patternDirectory);
        var ignoreCase = PathCasing.IsIgnoreCase(GetPatternDirectoryCaseReference(normalizedDirectory));
        return !root.Contains(normalizedDirectory, ignoreCase);
    }

    internal void RecordInspectedPatternDirectory(
        WorkerPatternConfigRootSnapshot root,
        string patternDirectory)
    {
        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(patternDirectory);
        var ignoreCase = PathCasing.IsIgnoreCase(GetPatternDirectoryCaseReference(normalizedDirectory));
        if (root.Contains(normalizedDirectory, ignoreCase))
            return;

        // Retain existing entries when saturated. New directories continue through
        // uncached discovery so a bounded cache never causes a config to be skipped.
        if (root.RetainedDirectoryCount >= maxDirectoriesPerRoot
            || RetainedDirectoryCount >= maxDirectoriesPerWorker)
        {
            return;
        }

        if (root.Add(normalizedDirectory, ignoreCase))
            RetainedDirectoryCount++;
    }

    internal void Reset()
    {
        roots.Clear();
        RetainedDirectoryCount = 0;
        accessSequence = 0;
    }

    private void Touch(WorkerPatternConfigRootSnapshot root)
        => root.LastAccessSequence = ++accessSequence;

    private static string GetPatternDirectoryCaseReference(string normalizedPatternDirectory)
    {
        // The registry has not performed its reparse-point checks when the cache
        // predicate runs. Probe the owning source directory, not an untrusted
        // .cdidx/patterns link target, while retaining the full lexical cache key.
        var cdidxDirectory = Path.GetDirectoryName(normalizedPatternDirectory);
        return string.IsNullOrEmpty(cdidxDirectory)
            ? normalizedPatternDirectory
            : Path.GetDirectoryName(cdidxDirectory) ?? normalizedPatternDirectory;
    }
}

internal static class SymbolExtractionWorker
{
    internal const string CommandName = "__cdidx-symbol-extraction";
    internal const int WorkerKillWaitMilliseconds = 5000;
    internal const int MaxDelayMillisecondsForTesting = 5000;
    private const string TestDelayMillisecondsOption = "--test-delay-ms";
    private const string TestConsoleStdoutOption = "--test-console-stdout";
    private const int CapturedConsoleMaxChars = 32 * 1024;
    internal static readonly byte[] RequestContentPrefix = ",\"Content\":\""u8.ToArray();
    internal static readonly byte[] RequestSuffix = "\"}"u8.ToArray();
    private static readonly object PatternConfigDiscoveryGate = new();
    private static WorkerPatternConfigDiscoveryCache patternConfigDiscoveryCache = new();

    internal static string FormatExecutionFailure(Exception ex)
        => SafeDiagnosticFormatter.FormatExceptionCategoryWithOrigin("worker_execution_failed", ex);
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(SymbolExtractionWorkerJsonContext.Default.Options);
    internal static int? DelayMillisecondsForTesting { get; set; }
    internal static string? ConsoleStdoutForTesting { get; set; }
    internal static Func<WorkerPatternConfigDiscoveryCache>? PatternConfigDiscoveryCacheFactoryForTesting { get; set; }

    internal static bool TryRunCommand(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        out int exitCode,
        int maxProtocolLineCharacters = WorkerProtocolLineLimits.MaxLineCharacters,
        int maxProtocolLineUtf8Bytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], CommandName))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunCommand(
            args,
            (maxCharacters, maxUtf8Bytes, _) => ReadTextRequestFrame(input, maxCharacters, maxUtf8Bytes),
            response => WriteResponse(output, response),
            error,
            maxProtocolLineCharacters,
            maxProtocolLineUtf8Bytes,
            cancellationToken);
        return true;
    }

    internal static bool TryRunCommand(
        string[] args,
        Stream input,
        Stream output,
        TextWriter error,
        out int exitCode,
        int maxProtocolLineCharacters = WorkerProtocolLineLimits.MaxLineCharacters,
        int maxProtocolLineUtf8Bytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], CommandName))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunCommand(
            args,
            (maxCharacters, maxUtf8Bytes, token) => ReadUtf8RequestFrame(
                input,
                maxCharacters,
                maxUtf8Bytes,
                token),
            response => WriteResponse(output, response),
            error,
            maxProtocolLineCharacters,
            maxProtocolLineUtf8Bytes,
            cancellationToken);
        return true;
    }

    internal static bool TryCreateStartInfo(out ProcessStartInfo startInfo, out string error)
    {
        return TryCreateStartInfo(
            WorkerProtocolLineLimits.MaxLineUtf8Bytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(int maxProtocolLineBytes, out ProcessStartInfo startInfo, out string error)
    {
        return TryCreateStartInfo(
            Environment.ProcessPath,
            IsolatedWorkerProcessLauncher.ResolveCurrentRunnerAssemblyPath(typeof(SymbolExtractionWorker).Assembly),
            maxProtocolLineBytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        string? currentProcessPath,
        string? runnerAssemblyPath,
        out ProcessStartInfo startInfo,
        out string error)
    {
        return TryCreateStartInfo(
            currentProcessPath,
            runnerAssemblyPath,
            WorkerProtocolLineLimits.MaxLineUtf8Bytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        string? currentProcessPath,
        string? runnerAssemblyPath,
        int maxProtocolLineBytes,
        out ProcessStartInfo startInfo,
        out string error)
    {
        startInfo = IsolatedWorkerProcessLauncher.CreateStartInfo();
        if (IsolatedWorkerProcessLauncher.ShouldStartCurrentExecutable(
                currentProcessPath,
                runnerAssemblyPath,
                typeof(SymbolExtractionWorker).Assembly))
        {
            startInfo.FileName = currentProcessPath!;
            CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(startInfo, CommandName, maxProtocolLineBytes);
            AddTestingArguments(startInfo);
            error = string.Empty;
            return true;
        }

        if (!IsolatedWorkerProcessLauncher.TryPrepareFrameworkDependentStartInfo(
                startInfo,
                currentProcessPath,
                runnerAssemblyPath,
                typeof(SymbolExtractionWorker).Assembly,
                "could not resolve the cdidx assembly path for isolated symbol extraction.",
                "could not resolve a trusted dotnet host path for isolated symbol extraction; run cdidx through an absolute dotnet host path or use a self-contained cdidx executable.",
                out error))
        {
            startInfo = new ProcessStartInfo();
            return false;
        }

        CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(startInfo, CommandName, maxProtocolLineBytes);
        AddTestingArguments(startInfo);

        error = string.Empty;
        return true;
    }

    internal static string? TryKillProcess(Process process)
        => WorkerProcessCleanupDiagnostics.TryKill(process, WorkerKillWaitMilliseconds);

    private static int RunCommand(
        string[] args,
        ReadRequestFrame readRequestFrame,
        Action<WorkerResponse> writeResponse,
        TextWriter error,
        int maxProtocolLineCharacters,
        int maxProtocolLineUtf8Bytes,
        CancellationToken cancellationToken)
    {
        if (!TryResolveWorkerOptions(
                args,
                maxProtocolLineCharacters,
                maxProtocolLineUtf8Bytes,
                out var workerOptions,
                out var protocolLimitError))
        {
            error.WriteLine(protocolLimitError);
            return 2;
        }

        ResetPatternConfigDiscoveryForWorker();
        maxProtocolLineCharacters = workerOptions.MaxProtocolLineCharacters;
        maxProtocolLineUtf8Bytes = workerOptions.MaxProtocolLineUtf8Bytes;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkerResponse response;
                WorkerRequest request;
                WorkerRequestFrame? requestFrame;
                try
                {
                    requestFrame = readRequestFrame(
                        maxProtocolLineCharacters,
                        maxProtocolLineUtf8Bytes,
                        cancellationToken);
                }
                catch (BoundedLineLengthException ex)
                {
                    response = new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex), null);
                    writeResponse(response);
                    return 1;
                }

                if (requestFrame is null)
                    break;

                if (!TryValidateRequestFrame(
                        requestFrame.Value,
                        maxProtocolLineCharacters,
                        maxProtocolLineUtf8Bytes,
                        out var validationError))
                {
                    response = new WorkerResponse(null, validationError, null);
                    writeResponse(response);
                    continue;
                }

                try
                {
                    request = DeserializeRequestFrame(requestFrame.Value, maxProtocolLineUtf8Bytes)
                        ?? throw new InvalidOperationException("worker request was empty.");
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex), null);
                    writeResponse(response);
                    continue;
                }

                try
                {
                    response = InvokeInsideWorker(request, workerOptions, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_execution_failed", ex), null);
                }

                writeResponse(response);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            error.WriteLine(SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex));
            return 1;
        }
    }

    private static WorkerRequestFrame? ReadTextRequestFrame(
        TextReader input,
        int maxProtocolLineCharacters,
        int maxProtocolLineUtf8Bytes)
    {
        var requestJson = BoundedLineReader.ReadLine(
            input,
            maxProtocolLineCharacters,
            maxProtocolLineUtf8Bytes);
        return requestJson is null
            ? null
            : WorkerRequestFrame.FromText(requestJson);
    }

    private static WorkerRequestFrame? ReadUtf8RequestFrame(
        Stream input,
        int maxProtocolLineCharacters,
        int maxProtocolLineUtf8Bytes,
        CancellationToken cancellationToken)
    {
        var requestUtf8 = BoundedLineReader.ReadUtf8LineAsync(
                input,
                maxProtocolLineUtf8Bytes,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (requestUtf8 is null)
            return null;

        var utf8ByteCount = requestUtf8.Value.Length;
        if (utf8ByteCount > maxProtocolLineCharacters)
        {
            var characterCount = Encoding.UTF8.GetCharCount(requestUtf8.Value.Span);
            if (characterCount > maxProtocolLineCharacters)
            {
                throw new BoundedLineLengthException(
                    characterCount,
                    utf8ByteCount,
                    maxProtocolLineCharacters,
                    maxProtocolLineUtf8Bytes);
            }
        }

        return WorkerRequestFrame.FromUtf8(requestUtf8.Value);
    }

    private static bool TryValidateRequestFrame(
        WorkerRequestFrame requestFrame,
        int maxProtocolLineCharacters,
        int maxProtocolLineUtf8Bytes,
        out string validationError)
        => requestFrame.IsUtf8
            ? WorkerProtocolJsonValidator.TryValidate(
                requestFrame.Utf8Json,
                maxProtocolLineCharacters,
                maxProtocolLineUtf8Bytes,
                out validationError)
            : WorkerProtocolJsonValidator.TryValidate(
                requestFrame.Json!,
                maxProtocolLineCharacters,
                out validationError);

    private static WorkerRequest? DeserializeRequestFrame(
        WorkerRequestFrame requestFrame,
        int maxProtocolLineUtf8Bytes)
        => requestFrame.IsUtf8
            ? BoundedJson.Deserialize<WorkerRequest>(requestFrame.Utf8Json.Span, maxProtocolLineUtf8Bytes, JsonOptions)
            : BoundedJson.Deserialize<WorkerRequest>(requestFrame.Json!, maxProtocolLineUtf8Bytes, JsonOptions);

    private static void WriteResponse(TextWriter output, WorkerResponse response)
    {
        output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        output.Flush();
    }

    private static void WriteResponse(Stream output, WorkerResponse response)
    {
        JsonSerializer.Serialize(output, response, JsonOptions);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    private delegate WorkerRequestFrame? ReadRequestFrame(
        int maxProtocolLineCharacters,
        int maxProtocolLineUtf8Bytes,
        CancellationToken cancellationToken);

    private readonly record struct WorkerRequestFrame(
        string? Json,
        ReadOnlyMemory<byte> Utf8Json,
        bool IsUtf8)
    {
        internal static WorkerRequestFrame FromText(string json)
            => new(json, ReadOnlyMemory<byte>.Empty, IsUtf8: false);

        internal static WorkerRequestFrame FromUtf8(ReadOnlyMemory<byte> utf8Json)
            => new(null, utf8Json, IsUtf8: true);
    }

    private static WorkerResponse InvokeInsideWorker(WorkerRequest request, WorkerOptions options, CancellationToken cancellationToken)
    {
        using var capturedOut = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var consoleOwnership = ConsoleStreamOwnership.Enter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            WriteConsoleOutputForTestingIfRequested(options);
            DelayForTestingIfRequested(options, cancellationToken);
            using var regexTimeouts = BoundedRegex.CaptureTimeouts(request.Lang, "symbol_extraction");
            using var typeScriptPathAliasFileSystemPolicy =
                SymbolExtractor.EnterTypeScriptPathAliasFileSystemPolicy(
                    request.SymlinkPolicy,
                    request.ProjectRoot);
            var patternConfigsAlreadyLoaded = EnsurePatternConfigsLoadedForWorker(request.ProjectRoot, request.FilePath);
            var symbols = request.ContentIsNormalized && request.HasOversizeLine is { } hasOversizeLine
                ? SymbolExtractor.ExtractNormalized(
                    request.FileId,
                    request.Lang,
                    request.Content,
                    hasOversizeLine,
                    request.FilePath,
                    request.ProjectRoot,
                    cancellationToken,
                    request.ConflictMarkerLine,
                    patternConfigsAlreadyLoaded: patternConfigsAlreadyLoaded)
                : patternConfigsAlreadyLoaded
                    ? SymbolExtractor.ExtractWithPatternConfigsLoaded(
                        request.FileId,
                        request.Lang,
                        request.Content,
                        request.FilePath,
                        request.ProjectRoot,
                        cancellationToken)
                    : SymbolExtractor.Extract(
                        request.FileId,
                        request.Lang,
                        request.Content,
                        request.FilePath,
                        request.ProjectRoot,
                        cancellationToken);
            return new WorkerResponse(
                symbols,
                null,
                capturedError.GetCapturedText(),
                regexTimeouts.TimeoutCount,
                regexTimeouts.Diagnostics.ToList(),
                regexTimeouts.DiagnosticsTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WorkerResponse(null, FormatExecutionFailure(ex), capturedError.GetCapturedText());
        }
        finally
        {
            ConsoleStreamOwnership.Restore(originalOut, originalError);
        }
    }

    private static void WriteConsoleOutputForTestingIfRequested(WorkerOptions options)
    {
        if (!string.IsNullOrEmpty(options.ConsoleStdoutForTesting))
            Console.Out.WriteLine(options.ConsoleStdoutForTesting);
    }

    private static void DelayForTestingIfRequested(WorkerOptions options, CancellationToken cancellationToken)
    {
        if (options.DelayMillisecondsForTesting is not { } milliseconds)
            return;

        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool EnsurePatternConfigsLoadedForWorker(string? projectRoot, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;

        var fullRoot = PathCasing.NormalizeBoundaryPath(projectRoot);
        lock (PatternConfigDiscoveryGate)
        {
            if (!patternConfigDiscoveryCache.TryGetRoot(fullRoot, out var rootSnapshot))
            {
                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(fullRoot);
                rootSnapshot = patternConfigDiscoveryCache.AddReloadedRoot(fullRoot);
            }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var fullPath = Path.IsPathRooted(filePath)
                    ? Path.GetFullPath(filePath)
                    : Path.GetFullPath(Path.Combine(fullRoot, filePath));
                ExtractorPluginRegistry.LoadPatternConfigsForPath(
                    fullPath,
                    fullRoot,
                    includeWorkspaceRoot: false,
                    includeUserDirectory: false,
                    shouldInspectWorkspacePatternDirectory: patternDirectory =>
                        patternConfigDiscoveryCache.ShouldInspectPatternDirectory(rootSnapshot, patternDirectory),
                    workspacePatternDirectoryInspected: patternDirectory =>
                        patternConfigDiscoveryCache.RecordInspectedPatternDirectory(rootSnapshot, patternDirectory));
            }

            return true;
        }
    }

    private static void ResetPatternConfigDiscoveryForWorker()
    {
        lock (PatternConfigDiscoveryGate)
        {
            patternConfigDiscoveryCache = PatternConfigDiscoveryCacheFactoryForTesting?.Invoke()
                ?? new WorkerPatternConfigDiscoveryCache();
            patternConfigDiscoveryCache.Reset();
        }
    }

    private static void AddTestingArguments(ProcessStartInfo startInfo)
    {
        if (DelayMillisecondsForTesting is { } delayMilliseconds && delayMilliseconds > 0)
        {
            var boundedDelay = Math.Min(delayMilliseconds, MaxDelayMillisecondsForTesting);
            startInfo.ArgumentList.Add(TestDelayMillisecondsOption);
            startInfo.ArgumentList.Add(boundedDelay.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(ConsoleStdoutForTesting))
        {
            startInfo.ArgumentList.Add(TestConsoleStdoutOption);
            startInfo.ArgumentList.Add(ConsoleStdoutForTesting);
        }
    }

    private static bool TryResolveWorkerOptions(
        string[] args,
        int fallbackMaxProtocolLineCharacters,
        int fallbackMaxProtocolLineUtf8Bytes,
        out WorkerOptions options,
        out string error)
    {
        var maxProtocolLineCharacters = fallbackMaxProtocolLineCharacters;
        var maxProtocolLineUtf8Bytes = fallbackMaxProtocolLineUtf8Bytes;
        int? delayMillisecondsForTesting = null;
        string? consoleStdoutForTesting = null;
        error = string.Empty;
        options = new WorkerOptions(
            maxProtocolLineCharacters,
            maxProtocolLineUtf8Bytes,
            delayMillisecondsForTesting,
            consoleStdoutForTesting);

        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length)
            {
                error = BuildWorkerOptionError();
                return false;
            }

            var value = args[++index];
            if (StringComparer.Ordinal.Equals(option, CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocolBytes)
                && protocolBytes > 0)
            {
                maxProtocolLineCharacters = protocolBytes;
                maxProtocolLineUtf8Bytes = protocolBytes;
                continue;
            }

            if (StringComparer.Ordinal.Equals(option, TestDelayMillisecondsOption)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay)
                && delay is > 0 and <= MaxDelayMillisecondsForTesting)
            {
                delayMillisecondsForTesting = delay;
                continue;
            }

            if (StringComparer.Ordinal.Equals(option, TestConsoleStdoutOption))
            {
                consoleStdoutForTesting = value;
                continue;
            }

            error = BuildWorkerOptionError();
            return false;
        }

        options = new WorkerOptions(
            maxProtocolLineCharacters,
            maxProtocolLineUtf8Bytes,
            delayMillisecondsForTesting,
            consoleStdoutForTesting);
        return true;
    }

    private static string BuildWorkerOptionError()
        => "symbol extraction worker accepts only "
            + $"`{CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption} <bytes>`, "
            + $"`{TestDelayMillisecondsOption} <milliseconds>`, or "
            + $"`{TestConsoleStdoutOption} <text>`.";

    internal sealed record WorkerRequest(
        long FileId,
        string? Lang,
        string Content,
        string FilePath,
        string ProjectRoot,
        bool ContentIsNormalized = false,
        bool? HasOversizeLine = null,
        int? ConflictMarkerLine = null,
        FileIndexer.SymlinkPolicy SymlinkPolicy = FileIndexer.SymlinkPolicy.All);

    internal sealed record WorkerRequestMetadata(
        long FileId,
        string? Lang,
        string FilePath,
        string ProjectRoot,
        bool ContentIsNormalized,
        bool? HasOversizeLine,
        int? ConflictMarkerLine,
        FileIndexer.SymlinkPolicy SymlinkPolicy)
    {
        internal static WorkerRequestMetadata From(WorkerRequest request) =>
            new(
                request.FileId,
                request.Lang,
                request.FilePath,
                request.ProjectRoot,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                request.SymlinkPolicy);
    }

    internal sealed record WorkerResponse(
        List<SymbolRecord>? Symbols,
        string? WorkerError,
        string? CapturedStderr,
        int RegexTimeoutCount = 0,
        List<BoundedRegex.RegexTimeoutDiagnostic>? RegexTimeoutDiagnostics = null,
        bool RegexTimeoutDiagnosticsTruncated = false);

    private sealed record WorkerOptions(
        int MaxProtocolLineCharacters,
        int MaxProtocolLineUtf8Bytes,
        int? DelayMillisecondsForTesting,
        string? ConsoleStdoutForTesting);

}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SymbolExtractionWorker.WorkerRequest))]
[JsonSerializable(typeof(SymbolExtractionWorker.WorkerRequestMetadata))]
[JsonSerializable(typeof(SymbolExtractionWorker.WorkerResponse))]
internal partial class SymbolExtractionWorkerJsonContext : JsonSerializerContext;
