using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    List<ReferenceRecord>? References);

internal sealed class PostExtractionHookCallbackWorkerClient : IDisposable
{
    private readonly PostExtractionHookInfo hook;
    private readonly int maxProtocolLineBytes;
    private readonly object gate = new();
    private Process? process;
    private StringBuilder stderr = new();
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
        TimeSpan callbackBudget)
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

            var request = new PostExtractionHookCallbackWorker.WorkerRequest(
                callback,
                context,
                symbols?.ToList(),
                references?.ToList());
            var requestJson = JsonSerializer.Serialize(request, PostExtractionHookCallbackWorker.JsonOptions);
            var waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0)
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            Task<string?> responseTask;
            Task sendTask;
            try
            {
                responseTask = BoundedLineReader.ReadLineAsync(
                    process!.StandardOutput,
                    maxProtocolLineBytes,
                    maxProtocolLineBytes,
                    CancellationToken.None);
                sendTask = SendRequestAsync(process.StandardInput, requestJson);
            }
            catch (Exception ex)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while sending hook callback request.", stopwatch.ElapsedMilliseconds);
            }

            if (!WaitForTask(sendTask, waitMilliseconds, out var sendException))
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            if (sendException != null)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", sendException)} while sending hook callback request.", stopwatch.ElapsedMilliseconds);
            }

            waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0 || !WaitForTask(responseTask, waitMilliseconds, out var responseException))
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            if (responseException != null)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", responseException)} while reading hook callback response.", stopwatch.ElapsedMilliseconds);
            }

            if (CallbackBudgetExceeded(stopwatch, callbackBudget))
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            stopwatch.Stop();
            var responseJson = responseTask.GetAwaiter().GetResult();
            if (responseJson == null)
            {
                var workerError = BuildWorkerExitError(process, stderr.ToString(), "worker exited before returning a response.");
                ClearExitedWorker();
                return Failure(workerError, stopwatch.ElapsedMilliseconds);
            }

            PostExtractionHookCallbackWorker.WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<PostExtractionHookCallbackWorker.WorkerResponse>(
                    responseJson,
                    PostExtractionHookCallbackWorker.JsonOptions);
            }
            catch (JsonException ex)
            {
                KillWorker();
                return Failure($"{SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex)} while parsing hook callback response.", stopwatch.ElapsedMilliseconds);
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
                References: response.References);
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

            if (!WaitForWorkerExit(process, 1000))
                KillWorker();
            else
                ClearExitedWorker();
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
        stderr = new StringBuilder();
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

    private void KillWorker()
    {
        if (process == null)
            return;

        PostExtractionHookCallbackWorker.TryKillProcess(process);
        ClearExitedWorker();
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

    private static PostExtractionHookCallbackResult TimedOut(long durationMs)
        => new(
            Success: false,
            TimedOut: true,
            WorkerError: null,
            CallbackError: null,
            DurationMs: Math.Max(0, durationMs),
            Symbols: null,
            References: null);

    private static async Task SendRequestAsync(TextWriter input, string requestJson)
    {
        await input.WriteLineAsync(requestJson).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    private static bool WaitForTask(Task task, int milliseconds, out Exception? exception)
    {
        try
        {
            if (!task.Wait(milliseconds))
            {
                exception = null;
                return false;
            }

            exception = null;
            return true;
        }
        catch (AggregateException ex)
        {
            exception = ex.GetBaseException();
            return true;
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
        return SafeDiagnosticFormatter.FormatWorkerExit("worker_protocol_error", exitCode, fallback);
    }

    private static bool WaitForWorkerExit(Process process, int milliseconds)
    {
        try
        {
            return process.WaitForExit(milliseconds);
        }
        catch
        {
            return false;
        }
    }
}

internal static class PostExtractionHookCallbackWorker
{
    internal const string CommandName = "__cdidx-post-extraction-hook-callback";
    internal const int WorkerKillWaitMilliseconds = 5000;
    private const string ProtocolMaxLineBytesOption = "--protocol-max-line-bytes";
    internal static readonly JsonSerializerOptions JsonOptions = PostExtractionHookCallbackWorkerJsonContext.Default.Options;

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
            ResolveCurrentRunnerAssemblyPath(),
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
        startInfo = CreateStartInfo();
        if (ShouldStartCurrentExecutable(currentProcessPath, runnerAssemblyPath))
        {
            startInfo.FileName = currentProcessPath!;
            startInfo.ArgumentList.Add(CommandName);
            startInfo.ArgumentList.Add(hook.AssemblyPath);
            startInfo.ArgumentList.Add(hook.TypeName);
            AddProtocolLineLimitArguments(startInfo, maxProtocolLineBytes);
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(runnerAssemblyPath))
        {
            startInfo = new ProcessStartInfo();
            error = "could not resolve the cdidx assembly path for isolated hook callback execution.";
            return false;
        }

        startInfo.FileName = ResolveDotnetHostPath();
        startInfo.ArgumentList.Add(runnerAssemblyPath);
        startInfo.ArgumentList.Add(CommandName);
        startInfo.ArgumentList.Add(hook.AssemblyPath);
        startInfo.ArgumentList.Add(hook.TypeName);
        AddProtocolLineLimitArguments(startInfo, maxProtocolLineBytes);
        ApplyCurrentRuntimeRollForward(startInfo);

        error = string.Empty;
        return true;
    }

    internal static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: timeout reporting must not fail because cleanup failed.
        }

        try
        {
            process.WaitForExit(WorkerKillWaitMilliseconds);
        }
        catch
        {
            // Best effort: the parent continues with the timeout diagnostic.
        }
    }

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

                try
                {
                    request = JsonSerializer.Deserialize<WorkerRequest>(requestJson, JsonOptions)
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
        using var capturedOut = new StringWriter();
        using var capturedError = new StringWriter();
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

        return new WorkerResponse(
            request.Symbols,
            request.References,
            callbackFailure is null ? null : SafeDiagnosticFormatter.FormatExceptionCategory("hook_callback_failed", callbackFailure),
            null);
    }

    private static void AddProtocolLineLimitArguments(ProcessStartInfo startInfo, int maxProtocolLineBytes)
    {
        startInfo.ArgumentList.Add(ProtocolMaxLineBytesOption);
        startInfo.ArgumentList.Add(maxProtocolLineBytes.ToString(CultureInfo.InvariantCulture));
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
            && StringComparer.Ordinal.Equals(args[3], ProtocolMaxLineBytesOption)
            && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            maxProtocolLineCharacters = parsed;
            maxProtocolLineUtf8Bytes = parsed;
            return true;
        }

        error = $"post-extraction hook callback worker requires assembly path, type name, and optional `{ProtocolMaxLineBytesOption} <bytes>`.";
        return false;
    }

    private static string ResolveDotnetHostPath()
    {
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotnetHostPath))
            return dotnetHostPath;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    private static ProcessStartInfo CreateStartInfo()
        => new()
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
        };

    private static bool ShouldStartCurrentExecutable(string? currentProcessPath, string? runnerAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(currentProcessPath) || IsDotnetHostPath(currentProcessPath))
            return false;

        var processName = Path.GetFileNameWithoutExtension(currentProcessPath);
        var appName = typeof(PostExtractionHookCallbackWorker).Assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(appName)
            && string.Equals(processName, appName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(runnerAssemblyPath);
    }

    private static string? ResolveCurrentRunnerAssemblyPath()
    {
        var assemblyName = typeof(PostExtractionHookCallbackWorker).Assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;

        var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool IsDotnetHostPath(string path)
        => string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static void ApplyCurrentRuntimeRollForward(ProcessStartInfo startInfo)
    {
        var targetMajor = GetRunnerTargetFrameworkMajor();
        if (targetMajor.HasValue && Environment.Version.Major > targetMajor.Value)
            startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
    }

    private static int? GetRunnerTargetFrameworkMajor()
    {
        var frameworkName = typeof(PostExtractionHookCallbackWorker)
            .Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()
            ?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
            return null;

        const string versionPrefix = "Version=v";
        var versionIndex = frameworkName.IndexOf(versionPrefix, StringComparison.OrdinalIgnoreCase);
        if (versionIndex < 0)
            return null;

        var majorStart = versionIndex + versionPrefix.Length;
        var majorEnd = frameworkName.IndexOf('.', majorStart);
        var majorText = majorEnd < 0
            ? frameworkName[majorStart..]
            : frameworkName[majorStart..majorEnd];
        return int.TryParse(
            majorText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var major)
            ? major
            : null;
    }

    internal sealed record WorkerRequest(
        string Callback,
        FileContext Context,
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References);

    internal sealed record WorkerResponse(
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References,
        string? CallbackError,
        string? WorkerError);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerRequest))]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerResponse))]
internal partial class PostExtractionHookCallbackWorkerJsonContext : JsonSerializerContext;
