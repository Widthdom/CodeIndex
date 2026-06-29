using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Hooks;

internal enum PostExtractionHookCallbackKind
{
    Symbols,
    References,
}

internal sealed record PostExtractionHookCallbackResult(
    bool Success,
    bool TimedOut,
    string? WorkerError,
    string? CallbackError,
    long DurationMs,
    List<SymbolRecord>? Symbols,
    List<ReferenceRecord>? References,
    bool SymbolsTruncated = false,
    bool ReferencesTruncated = false);

internal sealed class PostExtractionHookCallbackWorkerClient : IDisposable
{
    private readonly PostExtractionHookInfo hook;
    private readonly int maxProtocolLineBytes;
    private readonly object gate = new();
    private Process? process;
    private WorkerOutputBuffer stderr = new();
    private bool disposed;

    internal PostExtractionHookCallbackWorkerClient(PostExtractionHookInfo hook, int maxProtocolLineBytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes)
    {
        this.hook = hook;
        this.maxProtocolLineBytes = maxProtocolLineBytes;
    }

    internal PostExtractionHookCallbackResult Invoke(
        PostExtractionHookCallbackKind kind,
        string callback,
        FileContext context,
        IReadOnlyList<SymbolRecord>? symbols,
        IReadOnlyList<ReferenceRecord>? references,
        TimeSpan callbackBudget,
        int? maxSymbols = null,
        int? maxReferences = null,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            if (!EnsureStarted(out var startError))
            {
                stopwatch.Stop();
                return Failure(startError, stopwatch.ElapsedMilliseconds);
            }

            var request = new PostExtractionHookCallbackWorker.WorkerRequest(
                callback,
                context,
                symbols?.ToList(),
                references?.ToList(),
                maxSymbols,
                maxReferences);
            var requestJson = JsonSerializer.Serialize(request, PostExtractionHookCallbackWorker.JsonOptions);
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
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while sending hook callback request.",
                    stopwatch);
            }

            if (!WaitForTask(sendTask, waitMilliseconds, cancellationToken, out var sendException))
            {
                return TimedOutAfterKill(stopwatch);
            }

            if (sendException != null)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", sendException)} while sending hook callback request.",
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
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", responseException)} while reading hook callback response.",
                    stopwatch);
            }

            if (CallbackBudgetExceeded(stopwatch, callbackBudget))
            {
                return TimedOutAfterKill(stopwatch);
            }

            stopwatch.Stop();
            // WaitForTask already proved the async read completed within the callback budget;
            // GetResult only observes that completed protocol response on this sync API.
            var responseJson = responseTask.GetAwaiter().GetResult();
            if (responseJson == null)
            {
                var workerError = BuildWorkerExitError(process, stderr.GetCapturedText(), "worker exited before returning a response.");
                ClearExitedWorker();
                return Failure(workerError, stopwatch.ElapsedMilliseconds);
            }

            PostExtractionHookCallbackWorker.WorkerResponse? response;
            try
            {
                response = BoundedJson.Deserialize<PostExtractionHookCallbackWorker.WorkerResponse>(
                    responseJson,
                    maxProtocolLineBytes,
                    PostExtractionHookCallbackWorker.JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return FailureAfterKill(
                    $"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while parsing hook callback response.",
                    stopwatch);
            }

            if (response == null)
                return Failure("worker returned an empty response.", stopwatch.ElapsedMilliseconds);
            if (!string.IsNullOrWhiteSpace(response.WorkerError))
                return Failure(response.WorkerError, stopwatch.ElapsedMilliseconds);
            if (kind == PostExtractionHookCallbackKind.Symbols && response.Symbols == null)
                return Failure("worker response omitted symbols.", stopwatch.ElapsedMilliseconds);
            if (kind == PostExtractionHookCallbackKind.References && response.References == null)
                return Failure("worker response omitted references.", stopwatch.ElapsedMilliseconds);

            return new PostExtractionHookCallbackResult(
                Success: true,
                TimedOut: false,
                WorkerError: null,
                CallbackError: response.CallbackError,
                DurationMs: stopwatch.ElapsedMilliseconds,
                Symbols: response.Symbols,
                References: response.References,
                SymbolsTruncated: response.SymbolsTruncated,
                ReferencesTruncated: response.ReferencesTruncated);
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
                LogCleanupDiagnostic("post_extraction_hook_worker_cleanup_failed", cleanupDiagnostic);
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
        if (!PostExtractionHookCallbackWorker.TryCreateStartInfo(hook, maxProtocolLineBytes, out var startInfo, out error))
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
                error = "worker process did not start.";
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

        var cleanupDiagnostic = PostExtractionHookCallbackWorker.TryKillProcess(process);
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

    private static PostExtractionHookCallbackResult Failure(string? message, long durationMs)
        => new(
            Success: false,
            TimedOut: false,
            WorkerError: string.IsNullOrWhiteSpace(message) ? "isolated hook callback worker failed." : message,
            CallbackError: null,
            DurationMs: Math.Max(0, durationMs),
            Symbols: null,
            References: null);

    private PostExtractionHookCallbackResult FailureAfterKill(string message, Stopwatch stopwatch)
    {
        var cleanupDiagnostic = KillWorker();
        stopwatch.Stop();
        return Failure(
            WorkerProcessCleanupDiagnostics.AppendToMessage(message, cleanupDiagnostic),
            stopwatch.ElapsedMilliseconds);
    }

    private PostExtractionHookCallbackResult TimedOutAfterKill(Stopwatch stopwatch)
    {
        var cleanupDiagnostic = KillWorker();
        stopwatch.Stop();
        return TimedOut(stopwatch.ElapsedMilliseconds, cleanupDiagnostic);
    }

    private static PostExtractionHookCallbackResult TimedOut(long durationMs, string? workerError = null)
        => new(
            Success: false,
            TimedOut: true,
            WorkerError: workerError,
            CallbackError: null,
            DurationMs: Math.Max(0, durationMs),
            Symbols: null,
            References: null);

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
}

internal static class PostExtractionHookCallbackWorker
{
    internal const string CommandName = "__cdidx-post-extraction-hook-callback";
    internal const int WorkerKillWaitMilliseconds = 5000;
    private const int CapturedConsoleMaxChars = 32 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(PostExtractionHookCallbackWorkerJsonContext.Default.Options);

    internal static bool TryRunCommand(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        out int exitCode,
        int maxProtocolLineCharacters = WorkerProtocolLineLimits.MaxLineCharacters,
        int maxProtocolLineUtf8Bytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], CommandName))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunCommand(args, input, output, error, maxProtocolLineCharacters, maxProtocolLineUtf8Bytes);
        return true;
    }

    internal static bool TryCreateStartInfo(
        PostExtractionHookInfo hook,
        out ProcessStartInfo startInfo,
        out string error)
    {
        return TryCreateStartInfo(
            hook,
            WorkerProtocolLineLimits.MaxLineUtf8Bytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        PostExtractionHookInfo hook,
        int maxProtocolLineBytes,
        out ProcessStartInfo startInfo,
        out string error)
    {
        return TryCreateStartInfo(
            hook,
            Environment.ProcessPath,
            IsolatedWorkerProcessLauncher.ResolveCurrentRunnerAssemblyPath(typeof(PostExtractionHookCallbackWorker).Assembly),
            maxProtocolLineBytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        PostExtractionHookInfo hook,
        string? currentProcessPath,
        string? runnerAssemblyPath,
        out ProcessStartInfo startInfo,
        out string error)
    {
        return TryCreateStartInfo(
            hook,
            currentProcessPath,
            runnerAssemblyPath,
            WorkerProtocolLineLimits.MaxLineUtf8Bytes,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        PostExtractionHookInfo hook,
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
                typeof(PostExtractionHookCallbackWorker).Assembly))
        {
            startInfo.FileName = currentProcessPath!;
            CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(
                startInfo,
                CommandName,
                maxProtocolLineBytes,
                hook.AssemblyPath,
                hook.TypeName);
            error = string.Empty;
            return true;
        }

        if (!IsolatedWorkerProcessLauncher.TryPrepareFrameworkDependentStartInfo(
                startInfo,
                currentProcessPath,
                runnerAssemblyPath,
                typeof(PostExtractionHookCallbackWorker).Assembly,
                "could not resolve the cdidx assembly path for isolated hook callback execution.",
                "could not resolve a trusted dotnet host path for isolated hook callback execution; run cdidx through an absolute dotnet host path or use a self-contained cdidx executable.",
                out error))
        {
            startInfo = new ProcessStartInfo();
            return false;
        }

        CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(
            startInfo,
            CommandName,
            maxProtocolLineBytes,
            hook.AssemblyPath,
            hook.TypeName);

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
        int maxProtocolLineUtf8Bytes)
    {
        if (!TryResolveProtocolLineLimit(
                args,
                maxProtocolLineCharacters,
                maxProtocolLineUtf8Bytes,
                out var resolvedProtocolLineCharacters,
                out var resolvedProtocolLineUtf8Bytes,
                out var protocolLimitError))
        {
            error.WriteLine(protocolLimitError);
            return 2;
        }

        maxProtocolLineCharacters = resolvedProtocolLineCharacters;
        maxProtocolLineUtf8Bytes = resolvedProtocolLineUtf8Bytes;

        var hookAssemblyPath = args[1];
        var hookTypeName = args[2];
        try
        {
            IPostExtractionHook? hook = null;
            while (true)
            {
                WorkerResponse response;
                WorkerRequest request;
                string? requestJson;
                try
                {
                    requestJson = BoundedLineReader.ReadLine(input, maxProtocolLineCharacters, maxProtocolLineUtf8Bytes);
                }
                catch (BoundedLineLengthException ex)
                {
                    response = new WorkerResponse(null, null, null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex));
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
                    return 1;
                }

                if (requestJson is null)
                    break;

                if (!WorkerProtocolJsonValidator.TryValidate(requestJson, maxProtocolLineCharacters, out var validationError))
                {
                    response = new WorkerResponse(null, null, null, validationError);
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
                    continue;
                }

                try
                {
                    request = BoundedJson.Deserialize<WorkerRequest>(requestJson, maxProtocolLineUtf8Bytes, JsonOptions)
                        ?? throw new InvalidOperationException("worker request was empty.");
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, null, null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex));
                    output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                    output.Flush();
                    continue;
                }

                try
                {
                    hook ??= CreateHook(hookAssemblyPath, hookTypeName);
                    response = InvokeInsideWorker(hook, request);
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, null, null, SafeDiagnosticFormatter.FormatExceptionCategory("worker_execution_failed", ex));
                }

                output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                output.Flush();
            }

            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex));
            return 1;
        }
    }

    private static IPostExtractionHook CreateHook(string hookAssemblyPath, string hookTypeName)
    {
        var fullPath = Path.GetFullPath(hookAssemblyPath);
        var loadContext = new ExtensionAssemblyLoadContext(
            $"cdidx-hook-worker:{Path.GetFileNameWithoutExtension(fullPath)}",
            fullPath);
        var assembly = loadContext.LoadFromAssemblyPath(fullPath);
        var type = assembly.GetType(hookTypeName, throwOnError: true)
            ?? throw new InvalidOperationException($"hook type `{hookTypeName}` was not found.");
        return Activator.CreateInstance(type) as IPostExtractionHook
            ?? throw new InvalidOperationException($"hook type `{hookTypeName}` could not be instantiated as `{nameof(IPostExtractionHook)}`.");
    }

    private static WorkerResponse InvokeInsideWorker(IPostExtractionHook hook, WorkerRequest request)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(CapturedConsoleMaxChars);
        Exception? callbackFailure = null;
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            if (request.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted))
            {
                if (request.Symbols == null)
                    throw new InvalidOperationException("symbol callback request omitted symbols.");
                hook.OnSymbolsExtracted(request.Context, request.Symbols);
            }
            else if (request.Callback == nameof(IPostExtractionHook.OnReferencesExtracted))
            {
                if (request.References == null)
                    throw new InvalidOperationException("reference callback request omitted references.");
                hook.OnReferencesExtracted(request.Context, request.References);
            }
            else
            {
                throw new InvalidOperationException($"unknown hook callback `{request.Callback}`.");
            }
        }
        catch (Exception ex)
        {
            callbackFailure = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var symbolsTruncated = TrimToLimit(request.Symbols, request.MaxSymbols);
        var referencesTruncated = TrimToLimit(request.References, request.MaxReferences);
        return new WorkerResponse(
            request.Symbols,
            request.References,
            callbackFailure is null ? null : SafeDiagnosticFormatter.FormatExceptionCategory("hook_callback_failed", callbackFailure),
            null,
            symbolsTruncated,
            referencesTruncated);
    }

    private static bool TrimToLimit<T>(List<T>? items, int? maxCount)
    {
        if (items == null || maxCount is not { } limit || limit <= 0 || items.Count <= limit)
            return false;

        items.RemoveRange(limit, items.Count - limit);
        return true;
    }

    private static bool TryResolveProtocolLineLimit(
        string[] args,
        int fallbackMaxProtocolLineCharacters,
        int fallbackMaxProtocolLineUtf8Bytes,
        out int maxProtocolLineCharacters,
        out int maxProtocolLineUtf8Bytes,
        out string error)
    {
        maxProtocolLineCharacters = fallbackMaxProtocolLineCharacters;
        maxProtocolLineUtf8Bytes = fallbackMaxProtocolLineUtf8Bytes;
        error = string.Empty;
        if (args.Length == 3)
            return true;

        if (args.Length == 5
            && StringComparer.Ordinal.Equals(args[3], CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption)
            && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            maxProtocolLineCharacters = parsed;
            maxProtocolLineUtf8Bytes = parsed;
            return true;
        }

        error = $"post-extraction hook callback worker requires assembly path, type name, and optional `{CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption} <bytes>`.";
        return false;
    }

    internal sealed record WorkerRequest(
        string Callback,
        FileContext Context,
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References,
        int? MaxSymbols = null,
        int? MaxReferences = null);

    internal sealed record WorkerResponse(
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References,
        string? CallbackError,
        string? WorkerError,
        bool SymbolsTruncated = false,
        bool ReferencesTruncated = false);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerRequest))]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerResponse))]
internal partial class PostExtractionHookCallbackWorkerJsonContext : JsonSerializerContext;
