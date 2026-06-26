using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Cli;
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

internal sealed class SymbolExtractionWorkerClient : IDisposable
{
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
        CancellationToken cancellationToken = default)
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
            cancellationToken);

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
        CancellationToken cancellationToken = default)
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
                conflictMarkerLine);
            var requestJson = JsonSerializer.Serialize(request, SymbolExtractionWorker.JsonOptions);
            var waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0)
            {
                return TimedOutAfterKill(stopwatch);
            }

            Task<string?> responseTask;
            Task sendTask;
            try
            {
                responseTask = BoundedLineReader.ReadLineAsync(
                    process!.StandardOutput,
                    maxProtocolLineBytes,
                    maxProtocolLineBytes,
                    cancellationToken);
                sendTask = SendRequestAsync(process.StandardInput, requestJson);
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
            var responseJson = responseTask.GetAwaiter().GetResult();
            if (responseJson == null)
            {
                var workerError = BuildWorkerExitError(process, stderr.GetCapturedText(), "worker exited before returning a response.");
                ClearExitedWorker();
                return Failure(workerError, stopwatch.ElapsedMilliseconds);
            }

            SymbolExtractionWorker.WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                    responseJson,
                    SymbolExtractionWorker.JsonOptions);
            }
            catch (JsonException ex)
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

    private static async Task SendRequestAsync(TextWriter input, string requestJson)
    {
        await input.WriteLineAsync(requestJson).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
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

internal static class SymbolExtractionWorker
{
    internal const string CommandName = "__cdidx-symbol-extraction";
    internal const int WorkerKillWaitMilliseconds = 5000;
    internal const int MaxDelayMillisecondsForTesting = 5000;
    private const string ProtocolMaxLineBytesOption = "--protocol-max-line-bytes";
    private const string TestDelayMillisecondsOption = "--test-delay-ms";
    private const string TestConsoleStdoutOption = "--test-console-stdout";
    private const int CapturedConsoleMaxChars = 32 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(SymbolExtractionWorkerJsonContext.Default.Options);
    internal static int? DelayMillisecondsForTesting { get; set; }
    internal static string? ConsoleStdoutForTesting { get; set; }

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

        exitCode = RunCommand(args, input, output, error, maxProtocolLineCharacters, maxProtocolLineUtf8Bytes, cancellationToken);
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
            startInfo.ArgumentList.Add(CommandName);
            CodeIndex.ProcessLaunchPolicy.AddInvariantIntArgument(startInfo, ProtocolMaxLineBytesOption, maxProtocolLineBytes);
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

        startInfo.ArgumentList.Add(CommandName);
        CodeIndex.ProcessLaunchPolicy.AddInvariantIntArgument(startInfo, ProtocolMaxLineBytesOption, maxProtocolLineBytes);
        AddTestingArguments(startInfo);

        error = string.Empty;
        return true;
    }

    internal static string? TryKillProcess(Process process)
        => WorkerProcessCleanupDiagnostics.TryKill(process, WorkerKillWaitMilliseconds);

    private static int RunCommand(
        string[] args,
        TextReader input,
        TextWriter output,
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

        maxProtocolLineCharacters = workerOptions.MaxProtocolLineCharacters;
        maxProtocolLineUtf8Bytes = workerOptions.MaxProtocolLineUtf8Bytes;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkerResponse response;
                WorkerRequest request;
                string? requestJson;
                try
                {
                    requestJson = BoundedLineReader.ReadLine(input, maxProtocolLineCharacters, maxProtocolLineUtf8Bytes);
                }
                catch (BoundedLineLengthException ex)
                {
                    response = new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex), null);
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
                    return 1;
                }

                if (requestJson is null)
                    break;

                if (!WorkerProtocolJsonValidator.TryValidate(requestJson, maxProtocolLineCharacters, out var validationError))
                {
                    response = new WorkerResponse(null, validationError, null);
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
                    continue;
                }

                try
                {
                    request = JsonSerializer.Deserialize<WorkerRequest>(requestJson, JsonOptions)
                        ?? throw new InvalidOperationException("worker request was empty.");
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex), null);
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
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

                output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                output.Flush();
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

    private static WorkerResponse InvokeInsideWorker(WorkerRequest request, WorkerOptions options, CancellationToken cancellationToken)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(CapturedConsoleMaxChars);
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            WriteConsoleOutputForTestingIfRequested(options);
            DelayForTestingIfRequested(options, cancellationToken);
            using var regexTimeouts = BoundedRegex.CaptureTimeouts(request.Lang, "symbol_extraction");
            var symbols = request.ContentIsNormalized && request.HasOversizeLine is { } hasOversizeLine
                ? SymbolExtractor.ExtractNormalized(
                    request.FileId,
                    request.Lang,
                    request.Content,
                    hasOversizeLine,
                    request.FilePath,
                    request.ProjectRoot,
                    cancellationToken,
                    request.ConflictMarkerLine)
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
            return new WorkerResponse(null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_execution_failed", ex), capturedError.GetCapturedText());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
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
            if (StringComparer.Ordinal.Equals(option, ProtocolMaxLineBytesOption)
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
            + $"`{ProtocolMaxLineBytesOption} <bytes>`, "
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
        int? ConflictMarkerLine = null);

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
[JsonSerializable(typeof(SymbolExtractionWorker.WorkerResponse))]
internal partial class SymbolExtractionWorkerJsonContext : JsonSerializerContext;
