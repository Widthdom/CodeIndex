using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
    private readonly object gate = new();
    private Process? process;
    private StringBuilder stderr = new();
    private bool disposed;

    internal PostExtractionHookCallbackWorkerClient(PostExtractionHookInfo hook)
    {
        this.hook = hook;
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
                responseTask = process!.StandardOutput.ReadLineAsync();
                sendTask = SendRequestAsync(process.StandardInput, requestJson);
            }
            catch (Exception ex)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"failed to send worker request: {ex.Message}", stopwatch.ElapsedMilliseconds);
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
                return Failure($"failed to send worker request: {sendException.Message}", stopwatch.ElapsedMilliseconds);
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
                return Failure($"failed to read worker response: {responseException.Message}", stopwatch.ElapsedMilliseconds);
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
                return Failure($"worker returned invalid JSON: {ex.Message}", stopwatch.ElapsedMilliseconds);
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
        if (!PostExtractionHookCallbackWorker.TryCreateStartInfo(hook, out var startInfo, out error))
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
            error = $"failed to start worker process: {ex.Message}";
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

    private static string BuildWorkerExitError(Process? process, string stderr, string fallback)
    {
        var exitCodeText = process == null
            ? "unknown"
            : process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : fallback;
        return $"worker exited with code {exitCodeText}: {detail}";
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
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static bool TryRunCommand(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], CommandName))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunCommand(args, input, output, error);
        return true;
    }

    internal static bool TryCreateStartInfo(
        PostExtractionHookInfo hook,
        out ProcessStartInfo startInfo,
        out string error)
    {
        var runnerAssemblyPath = typeof(PostExtractionHookCallbackWorker).Assembly.Location;
        if (string.IsNullOrWhiteSpace(runnerAssemblyPath))
        {
            startInfo = new ProcessStartInfo();
            error = "could not resolve the cdidx assembly path for isolated hook callback execution.";
            return false;
        }

        startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHostPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runnerAssemblyPath);
        startInfo.ArgumentList.Add(CommandName);
        startInfo.ArgumentList.Add(hook.AssemblyPath);
        startInfo.ArgumentList.Add(hook.TypeName);
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

    private static int RunCommand(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (args.Length != 3)
        {
            error.WriteLine("post-extraction hook callback worker requires assembly path and type name.");
            return 2;
        }

        var hookAssemblyPath = args[1];
        var hookTypeName = args[2];
        try
        {
            IPostExtractionHook? hook = null;
            string? requestJson;
            while ((requestJson = input.ReadLine()) != null)
            {
                WorkerResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<WorkerRequest>(requestJson, JsonOptions)
                        ?? throw new InvalidOperationException("worker request was empty.");
                    hook ??= CreateHook(hookAssemblyPath, hookTypeName);
                    response = InvokeInsideWorker(hook, request);
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, null, null, ex.Message);
                }

                output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                output.Flush();
            }

            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static IPostExtractionHook CreateHook(string hookAssemblyPath, string hookTypeName)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(hookAssemblyPath));
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

        return new WorkerResponse(request.Symbols, request.References, callbackFailure?.Message, null);
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
