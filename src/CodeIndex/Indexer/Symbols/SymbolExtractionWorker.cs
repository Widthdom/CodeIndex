using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal sealed record SymbolExtractionWorkerResult(
    bool Success,
    bool TimedOut,
    string? WorkerError,
    long DurationMs,
    List<SymbolRecord>? Symbols);

internal sealed class SymbolExtractionWorkerClient : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private StringBuilder stderr = new();
    private bool disposed;

    internal SymbolExtractionWorkerResult Invoke(
        long fileId,
        string? lang,
        string content,
        string filePath,
        string projectRoot,
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

            var request = new SymbolExtractionWorker.WorkerRequest(fileId, lang, content, filePath, projectRoot);
            var requestJson = JsonSerializer.Serialize(request, SymbolExtractionWorker.JsonOptions);
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
                return Failure($"failed to send symbol extraction request: {ex.Message}", stopwatch.ElapsedMilliseconds);
            }

            if (!WaitForTask(sendTask, waitMilliseconds, cancellationToken, out var sendException))
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            if (sendException != null)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"failed to send symbol extraction request: {sendException.Message}", stopwatch.ElapsedMilliseconds);
            }

            waitMilliseconds = GetRemainingWaitMilliseconds(stopwatch, callbackBudget);
            if (waitMilliseconds <= 0 || !WaitForTask(responseTask, waitMilliseconds, cancellationToken, out var responseException))
            {
                KillWorker();
                stopwatch.Stop();
                return TimedOut(stopwatch.ElapsedMilliseconds);
            }

            if (responseException != null)
            {
                KillWorker();
                stopwatch.Stop();
                return Failure($"failed to read symbol extraction response: {responseException.Message}", stopwatch.ElapsedMilliseconds);
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

            SymbolExtractionWorker.WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                    responseJson,
                    SymbolExtractionWorker.JsonOptions);
            }
            catch (JsonException ex)
            {
                KillWorker();
                return Failure($"worker returned invalid JSON: {ex.Message}", stopwatch.ElapsedMilliseconds);
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
                Symbols: response.Symbols);
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
        if (!SymbolExtractionWorker.TryCreateStartInfo(out var startInfo, out error))
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
            error = $"failed to start symbol extraction worker process: {ex.Message}";
            next.Dispose();
            return false;
        }
    }

    private void KillWorker()
    {
        if (process == null)
            return;

        SymbolExtractionWorker.TryKillProcess(process);
        ClearExitedWorker();
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

    private static SymbolExtractionWorkerResult TimedOut(long durationMs)
        => new(
            Success: false,
            TimedOut: true,
            WorkerError: null,
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
            if (!task.Wait(milliseconds, cancellationToken))
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillWorker();
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
    internal const string DelayEnvironmentVariable = "CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DELAY_MS";
    internal const string CompletionPathEnvironmentVariable = "CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DONE_PATH";
    internal const string ConsoleStdoutEnvironmentVariable = "CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_STDOUT";
    private const int CapturedConsoleMaxChars = 32 * 1024;
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

    internal static bool TryCreateStartInfo(out ProcessStartInfo startInfo, out string error)
    {
        return TryCreateStartInfo(
            Environment.ProcessPath,
            typeof(SymbolExtractionWorker).Assembly.Location,
            out startInfo,
            out error);
    }

    internal static bool TryCreateStartInfo(
        string? currentProcessPath,
        string? runnerAssemblyPath,
        out ProcessStartInfo startInfo,
        out string error)
    {
        startInfo = CreateStartInfo();
        if (ShouldStartCurrentExecutable(currentProcessPath, runnerAssemblyPath))
        {
            startInfo.FileName = currentProcessPath!;
            startInfo.ArgumentList.Add(CommandName);
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(runnerAssemblyPath))
        {
            startInfo = new ProcessStartInfo();
            error = "could not resolve the cdidx assembly path for isolated symbol extraction.";
            return false;
        }

        startInfo.FileName = ResolveDotnetHostPath();
        startInfo.ArgumentList.Add(runnerAssemblyPath);
        startInfo.ArgumentList.Add(CommandName);
        ApplyCurrentRuntimeRollForward(startInfo);

        error = string.Empty;
        return true;
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
        var appName = typeof(SymbolExtractionWorker).Assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(appName)
            && string.Equals(processName, appName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(runnerAssemblyPath);
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
        if (args.Length != 1)
        {
            error.WriteLine("symbol extraction worker does not accept positional arguments.");
            return 2;
        }

        try
        {
            string? requestJson;
            while ((requestJson = input.ReadLine()) != null)
            {
                WorkerResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<WorkerRequest>(requestJson, JsonOptions)
                        ?? throw new InvalidOperationException("worker request was empty.");
                    response = InvokeInsideWorker(request);
                }
                catch (Exception ex)
                {
                    response = new WorkerResponse(null, ex.Message, null);
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

    private static WorkerResponse InvokeInsideWorker(WorkerRequest request)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(CapturedConsoleMaxChars);
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            WriteConsoleOutputForTestingIfRequested();
            DelayForTestingIfRequested();
            var symbols = SymbolExtractor.Extract(
                request.FileId,
                request.Lang,
                request.Content,
                request.FilePath,
                request.ProjectRoot,
                CancellationToken.None);
            return new WorkerResponse(symbols, null, capturedError.GetCapturedText());
        }
        catch (Exception ex)
        {
            return new WorkerResponse(null, ex.Message, capturedError.GetCapturedText());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void WriteConsoleOutputForTestingIfRequested()
    {
        var stdout = Environment.GetEnvironmentVariable(ConsoleStdoutEnvironmentVariable);
        if (!string.IsNullOrEmpty(stdout))
            Console.Out.WriteLine(stdout);
    }

    private static void DelayForTestingIfRequested()
    {
        var raw = Environment.GetEnvironmentVariable(DelayEnvironmentVariable);
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds <= 0)
        {
            return;
        }

        Thread.Sleep(milliseconds);
        var completionPath = Environment.GetEnvironmentVariable(CompletionPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(completionPath))
            File.WriteAllText(completionPath, "completed");
    }

    private static string ResolveDotnetHostPath()
    {
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotnetHostPath))
            return dotnetHostPath;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && IsDotnetHostPath(processPath))
        {
            return processPath;
        }

        return "dotnet";
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
        var frameworkName = typeof(SymbolExtractionWorker)
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
        long FileId,
        string? Lang,
        string Content,
        string FilePath,
        string ProjectRoot);

    internal sealed record WorkerResponse(
        List<SymbolRecord>? Symbols,
        string? WorkerError,
        string? CapturedStderr);

    private sealed class BoundedTextWriter(int maxChars) : TextWriter
    {
        private readonly StringBuilder builder = new();
        private bool truncated;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (builder.Length < maxChars)
            {
                builder.Append(value);
                return;
            }

            truncated = true;
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            Append(value.AsSpan());
        }

        public override void Write(char[] buffer, int index, int count)
            => Append(buffer.AsSpan(index, count));

        internal string GetCapturedText()
        {
            if (!truncated)
                return builder.ToString();

            return builder
                .AppendLine()
                .Append("[cdidx] captured worker console output truncated.")
                .ToString();
        }

        private void Append(ReadOnlySpan<char> value)
        {
            var remaining = maxChars - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                return;
            }

            if (value.Length <= remaining)
            {
                builder.Append(value);
                return;
            }

            builder.Append(value[..remaining]);
            truncated = true;
        }
    }
}
