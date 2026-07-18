using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer.Hooks;

internal sealed record PostExtractionHookManifestEntry(string Name, string TypeName);

internal sealed record PostExtractionHookManifestDiagnostic(
    string? TypeName,
    string Category,
    string Message);

internal sealed record PostExtractionHookDiscoveryWorkerResponse(
    List<PostExtractionHookManifestEntry>? Hooks = null,
    List<PostExtractionHookManifestDiagnostic>? Diagnostics = null,
    string? ErrorCategory = null,
    string? Error = null);

internal sealed record PostExtractionHookDiscoveryWorkerResult(
    bool Success,
    string? ErrorCategory,
    string? Error,
    PostExtractionHookDiscoveryWorkerResponse? Response);

internal static class PostExtractionHookDiscoveryWorkerProtocol
{
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(PostExtractionHookDiscoveryWorkerJsonContext.Default.Options);

    internal static string Serialize(PostExtractionHookDiscoveryWorkerResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    internal static PostExtractionHookDiscoveryWorkerResponse Deserialize(string json, int maxBytes)
        => BoundedJson.Deserialize<PostExtractionHookDiscoveryWorkerResponse>(json, maxBytes, JsonOptions)
           ?? throw new InvalidDataException("Hook discovery worker response was empty.");
}

internal static class PostExtractionHookDiscoveryWorkerClient
{
    internal static readonly TimeSpan DefaultDiscoveryBudget = TimeSpan.FromSeconds(5);
    internal const long DefaultMemoryLimitBytes = 256L * 1024 * 1024;
    internal static TimeSpan? DiscoveryBudgetForTesting { get; set; }

    internal static PostExtractionHookDiscoveryWorkerResult Discover(
        string assemblyPath,
        int typeInspectionLimit,
        int maxProtocolLineBytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes,
        long memoryLimitBytes = DefaultMemoryLimitBytes)
    {
        if (!TryCreateStartInfo(
                assemblyPath,
                typeInspectionLimit,
                maxProtocolLineBytes,
                out var startInfo,
                out var startError))
        {
            return Failure("hook_discovery_worker_start_failed", startError);
        }

        if (memoryLimitBytes >= 64L * 1024 * 1024)
        {
            startInfo.Environment["DOTNET_GCHeapHardLimit"] =
                memoryLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderr = new WorkerOutputBuffer();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                stderr.AppendLine(eventArgs.Data);
        };
        try
        {
            if (!process.Start())
                return Failure("hook_discovery_worker_start_failed", "Hook discovery worker process did not start.");
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return Failure(
                "hook_discovery_worker_start_failed",
                SafeDiagnosticFormatter.FormatExceptionCategory("hook_discovery_worker_start_failed", ex));
        }

        var stopwatch = Stopwatch.StartNew();
        var responseTask = BoundedLineReader.ReadLineAsync(
            process.StandardOutput,
            maxProtocolLineBytes,
            maxProtocolLineBytes,
            CancellationToken.None);
        var budget = DiscoveryBudgetForTesting ?? DefaultDiscoveryBudget;
        while (!responseTask.IsCompleted)
        {
            if (stopwatch.Elapsed >= budget)
                return KillAndFail(process, "hook_discovery_timeout", "Hook discovery worker exceeded its wall-clock deadline.");

            try
            {
                if (!process.HasExited)
                {
                    process.Refresh();
                    if (process.WorkingSet64 > memoryLimitBytes)
                        return KillAndFail(process, "hook_discovery_memory_limit", "Hook discovery worker exceeded its memory budget.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return KillAndFail(process, "hook_discovery_worker_exit", "Hook discovery worker state could not be inspected.");
            }

            Thread.Sleep(10);
        }

        string? responseJson;
        try
        {
            responseJson = responseTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var category = ex is BoundedLineLengthException
                ? "hook_discovery_output_limit"
                : "hook_discovery_protocol_error";
            return KillAndFail(process, category, SafeDiagnosticFormatter.FormatExceptionCategory(category, ex));
        }

        if (responseJson == null)
        {
            var exit = process.HasExited ? process.ExitCode : (int?)null;
            return Failure(
                "hook_discovery_worker_exit",
                SafeDiagnosticFormatter.FormatWorkerExit(
                    "hook_discovery_worker_exit",
                    exit,
                    "Hook discovery worker exited before returning a manifest.",
                    stderr.GetCapturedText()));
        }

        try
        {
            var response = PostExtractionHookDiscoveryWorkerProtocol.Deserialize(responseJson, maxProtocolLineBytes);
            if (!string.IsNullOrWhiteSpace(response.Error))
                return Failure(response.ErrorCategory ?? "hook_discovery_failed", response.Error);
            return new(true, null, null, response);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return KillAndFail(
                process,
                "hook_discovery_protocol_error",
                SafeDiagnosticFormatter.FormatExceptionCategory("hook_discovery_protocol_error", ex));
        }
    }

    internal static bool TryCreateStartInfo(
        string assemblyPath,
        int typeInspectionLimit,
        int maxProtocolLineBytes,
        out ProcessStartInfo startInfo,
        out string error)
    {
        startInfo = IsolatedWorkerProcessLauncher.CreateStartInfo();
        var currentProcessPath = Environment.ProcessPath;
        var runnerAssemblyPath = IsolatedWorkerProcessLauncher.ResolveCurrentRunnerAssemblyPath(typeof(PostExtractionHookDiscoveryWorker).Assembly);
        if (IsolatedWorkerProcessLauncher.ShouldStartCurrentExecutable(
                currentProcessPath,
                runnerAssemblyPath,
                typeof(PostExtractionHookDiscoveryWorker).Assembly))
        {
            startInfo.FileName = currentProcessPath!;
        }
        else if (!IsolatedWorkerProcessLauncher.TryPrepareFrameworkDependentStartInfo(
                     startInfo,
                     currentProcessPath,
                     runnerAssemblyPath,
                     typeof(PostExtractionHookDiscoveryWorker).Assembly,
                     "could not resolve the cdidx assembly path for isolated hook discovery.",
                     "could not resolve a trusted dotnet host path for isolated hook discovery.",
                     out error))
        {
            startInfo = new ProcessStartInfo();
            return false;
        }

        CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(
            startInfo,
            PostExtractionHookDiscoveryWorker.CommandName,
            maxProtocolLineBytes,
            assemblyPath,
            typeInspectionLimit.ToString(CultureInfo.InvariantCulture));
        error = string.Empty;
        return true;
    }

    private static PostExtractionHookDiscoveryWorkerResult KillAndFail(
        Process process,
        string category,
        string message)
    {
        var cleanup = WorkerProcessCleanupDiagnostics.TryKill(process, 5000);
        return Failure(category, WorkerProcessCleanupDiagnostics.AppendToMessage(message, cleanup));
    }

    private static PostExtractionHookDiscoveryWorkerResult Failure(string category, string? message)
        => new(false, category, string.IsNullOrWhiteSpace(message) ? "Hook discovery worker failed." : message, null);
}

internal static class PostExtractionHookDiscoveryWorker
{
    internal const string CommandName = "__cdidx-post-extraction-hook-discovery";
    private const int CapturedConsoleMaxChars = 32 * 1024;

    internal static bool TryRunCommand(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], CommandName))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunCommand(args, output, error);
        return true;
    }

    private static int RunCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 5
            || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeLimit)
            || typeLimit <= 0
            || !StringComparer.Ordinal.Equals(args[3], CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption)
            || !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocolLimit)
            || protocolLimit <= 0)
        {
            error.WriteLine("Hook discovery worker requires assembly path, type limit, and protocol byte limit.");
            return 2;
        }

        PostExtractionHookDiscoveryWorkerResponse response;
        try
        {
            response = ExecuteExtensionCode(() => Discover(args[1], typeLimit));
        }
        catch (HookDiscoveryException ex)
        {
            response = new(ErrorCategory: ex.Category, Error: ex.Message);
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad(
                "Hook assembly load",
                ex);
            response = new(
                ErrorCategory: diagnostic.Category,
                Error: diagnostic.Message);
        }

        var json = PostExtractionHookDiscoveryWorkerProtocol.Serialize(response);
        if (Encoding.UTF8.GetByteCount(json) > protocolLimit)
        {
            json = PostExtractionHookDiscoveryWorkerProtocol.Serialize(
                new(ErrorCategory: "hook_discovery_output_limit", Error: "Hook discovery manifest exceeded its output budget."));
        }

        output.WriteLine(json);
        output.Flush();
        return 0;
    }

    private static PostExtractionHookDiscoveryWorkerResponse Discover(string assemblyPath, int typeLimit)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var loadContext = new ExtensionAssemblyLoadContext(
            $"cdidx-hook-discovery-worker:{Path.GetFileNameWithoutExtension(fullPath)}",
            fullPath);
        var assembly = loadContext.LoadFromAssemblyPath(fullPath);
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        Type[] types;
        var diagnostics = new List<PostExtractionHookManifestDiagnostic>();
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyTypeLoad(
                "Hook assembly type inspection",
                ex);
            diagnostics.Add(new(null, diagnostic.Category, diagnostic.Message));
        }

        if (types.Length > typeLimit)
        {
            throw new HookDiscoveryException(
                "hook_type_limit_exceeded",
                $"Hook assembly skipped: too many loadable types ({types.Length}; maximum {typeLimit}).");
        }

        var hooks = new List<PostExtractionHookManifestEntry>();
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IPostExtractionHook).IsAssignableFrom(type))
                continue;

            var typeName = type.FullName ?? type.Name;
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                diagnostics.Add(new(
                    typeName,
                    "hook_constructor_missing",
                    "Failed to instantiate hook: public parameterless constructor not found."));
                continue;
            }

            hooks.Add(new(type.Name, typeName));
        }

        return new(Hooks: hooks, Diagnostics: diagnostics);
    }

    private static T ExecuteExtensionCode<T>(Func<T> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new BoundedTextWriter(CapturedConsoleMaxChars);
        using var capturedError = new BoundedTextWriter(CapturedConsoleMaxChars);
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            return action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class HookDiscoveryException(string category, string message) : Exception(message)
    {
        internal string Category { get; } = category;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PostExtractionHookDiscoveryWorkerResponse))]
[JsonSerializable(typeof(PostExtractionHookManifestEntry))]
[JsonSerializable(typeof(PostExtractionHookManifestDiagnostic))]
internal partial class PostExtractionHookDiscoveryWorkerJsonContext : JsonSerializerContext;
