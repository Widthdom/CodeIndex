using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Extensibility;

internal enum ExtractorPluginWorkerRequestKind
{
    Manifest,
    Symbols,
    References,
}

internal sealed record ExtractorPluginWorkerManifestEntry(
    string TypeName,
    string? SymbolLanguage,
    List<string>? SymbolFileExtensions,
    string? ReferenceLanguage,
    List<string>? ReferenceFileExtensions)
{
    [JsonIgnore]
    internal bool SupportsSymbols => SymbolLanguage != null;

    [JsonIgnore]
    internal bool SupportsReferences => ReferenceLanguage != null;
}

internal sealed record ExtractorPluginWorkerDiagnostic(
    string? TypeName,
    string Category,
    string Message);

internal sealed record ExtractorPluginWorkerRequest(
    ExtractorPluginWorkerRequestKind Kind,
    string? TypeName = null,
    long FileId = 0,
    string? Source = null,
    ExtractionContext? Context = null);

internal sealed record ExtractorPluginWorkerResponse(
    List<ExtractorPluginWorkerManifestEntry>? Manifest = null,
    List<SymbolRecord>? Symbols = null,
    List<ReferenceRecord>? References = null,
    List<ExtractorPluginWorkerDiagnostic>? Diagnostics = null,
    string? ErrorCategory = null,
    string? Error = null);

internal sealed record ExtractorPluginWorkerResult(
    bool Success,
    string? ErrorCategory,
    string? Error,
    ExtractorPluginWorkerResponse? Response);

internal static class ExtractorPluginWorkerProtocol
{
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(ExtractorPluginWorkerJsonContext.Default.Options);

    internal static string Serialize(ExtractorPluginWorkerRequest request)
        => JsonSerializer.Serialize(request, JsonOptions);

    internal static string Serialize(ExtractorPluginWorkerResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    internal static ExtractorPluginWorkerRequest DeserializeRequest(string json, int maxBytes)
        => BoundedJson.Deserialize<ExtractorPluginWorkerRequest>(json, maxBytes, JsonOptions)
           ?? throw new InvalidDataException("Plugin worker request was empty.");

    internal static ExtractorPluginWorkerResponse DeserializeResponse(string json, int maxBytes)
        => BoundedJson.Deserialize<ExtractorPluginWorkerResponse>(json, maxBytes, JsonOptions)
           ?? throw new InvalidDataException("Plugin worker response was empty.");
}

internal sealed class ExtractorPluginWorkerClient : IDisposable
{
    internal static readonly TimeSpan DefaultOperationBudget = TimeSpan.FromSeconds(5);
    internal const long DefaultMemoryLimitBytes = 256L * 1024 * 1024;
    internal static Action? ProcessStartedForTesting { get; set; }

    private readonly string assemblyPath;
    private readonly int typeInspectionLimit;
    private readonly TimeSpan operationBudget;
    private readonly long memoryLimitBytes;
    private readonly int maxProtocolLineBytes;
    private readonly object gate = new();
    private Process? process;
    private WorkerOutputBuffer stderr = new();
    private bool disposed;

    internal ExtractorPluginWorkerClient(
        string assemblyPath,
        int typeInspectionLimit,
        TimeSpan? operationBudget = null,
        long memoryLimitBytes = DefaultMemoryLimitBytes,
        int maxProtocolLineBytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes)
    {
        this.assemblyPath = assemblyPath;
        this.typeInspectionLimit = typeInspectionLimit;
        this.operationBudget = operationBudget ?? DefaultOperationBudget;
        this.memoryLimitBytes = memoryLimitBytes;
        this.maxProtocolLineBytes = maxProtocolLineBytes;
    }

    internal ExtractorPluginWorkerResult LoadManifest()
        => Invoke(new(ExtractorPluginWorkerRequestKind.Manifest));

    internal ExtractorPluginWorkerResult ExtractSymbols(
        string typeName,
        long fileId,
        string source,
        ExtractionContext context)
        => Invoke(new(ExtractorPluginWorkerRequestKind.Symbols, typeName, fileId, source, context));

    internal ExtractorPluginWorkerResult ExtractReferences(
        string typeName,
        long fileId,
        string source,
        ExtractionContext context)
        => Invoke(new(ExtractorPluginWorkerRequestKind.References, typeName, fileId, source, context));

    internal static bool TryCreateStartInfo(
        string assemblyPath,
        int typeInspectionLimit,
        int maxProtocolLineBytes,
        out ProcessStartInfo startInfo,
        out string error)
    {
        startInfo = IsolatedWorkerProcessLauncher.CreateStartInfo();
        var currentProcessPath = Environment.ProcessPath;
        var runnerAssemblyPath = IsolatedWorkerProcessLauncher.ResolveCurrentRunnerAssemblyPath(typeof(ExtractorPluginWorker).Assembly);
        if (IsolatedWorkerProcessLauncher.ShouldStartCurrentExecutable(
                currentProcessPath,
                runnerAssemblyPath,
                typeof(ExtractorPluginWorker).Assembly))
        {
            startInfo.FileName = currentProcessPath!;
        }
        else if (!IsolatedWorkerProcessLauncher.TryPrepareFrameworkDependentStartInfo(
                     startInfo,
                     currentProcessPath,
                     runnerAssemblyPath,
                     typeof(ExtractorPluginWorker).Assembly,
                     "could not resolve the cdidx assembly path for isolated plugin execution.",
                     "could not resolve a trusted dotnet host path for isolated plugin execution.",
                     out error))
        {
            startInfo = new ProcessStartInfo();
            return false;
        }

        CodeIndex.ProcessLaunchPolicy.AddWorkerCommandArguments(
            startInfo,
            ExtractorPluginWorker.CommandName,
            maxProtocolLineBytes,
            assemblyPath,
            typeInspectionLimit.ToString(CultureInfo.InvariantCulture));
        error = string.Empty;
        return true;
    }

    private ExtractorPluginWorkerResult Invoke(ExtractorPluginWorkerRequest request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!EnsureStarted(out var startError))
                return Failure("plugin_worker_start_failed", startError);

            var requestJson = ExtractorPluginWorkerProtocol.Serialize(request);
            if (Encoding.UTF8.GetByteCount(requestJson) > maxProtocolLineBytes)
                return FailureAfterKill("plugin_worker_protocol_limit", "Plugin worker request exceeded its output protocol budget.");

            var stopwatch = Stopwatch.StartNew();
            Task<string?> responseTask;
            Task sendTask;
            try
            {
                responseTask = BoundedLineReader.ReadLineAsync(
                    process!.StandardOutput,
                    maxProtocolLineBytes,
                    maxProtocolLineBytes,
                    CancellationToken.None);
                sendTask = SendAsync(process.StandardInput, requestJson);
            }
            catch (Exception ex)
            {
                return FailureAfterKill(
                    "plugin_worker_protocol_error",
                    SafeDiagnosticFormatter.FormatExceptionCategory("plugin_worker_protocol_error", ex));
            }

            var sendLimit = WaitForTask(sendTask, stopwatch);
            if (sendLimit != null)
                return FailureAfterKill(sendLimit.Value.Category, sendLimit.Value.Message);
            if (sendTask.Exception != null)
                return FailureAfterKill(
                    "plugin_worker_protocol_error",
                    SafeDiagnosticFormatter.FormatExceptionCategory("plugin_worker_protocol_error", sendTask.Exception.GetBaseException()));

            var responseLimit = WaitForTask(responseTask, stopwatch);
            if (responseLimit != null)
                return FailureAfterKill(responseLimit.Value.Category, responseLimit.Value.Message);
            if (responseTask.Exception != null)
            {
                var exception = responseTask.Exception.GetBaseException();
                var category = exception is BoundedLineLengthException
                    ? "plugin_worker_output_limit"
                    : "plugin_worker_protocol_error";
                return FailureAfterKill(category, SafeDiagnosticFormatter.FormatExceptionCategory(category, exception));
            }

            string? responseJson;
            try
            {
                responseJson = responseTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return FailureAfterKill(
                    "plugin_worker_protocol_error",
                    SafeDiagnosticFormatter.FormatExceptionCategory("plugin_worker_protocol_error", ex));
            }

            if (responseJson == null)
            {
                var exit = process is { HasExited: true } ? process.ExitCode : (int?)null;
                return FailureAfterKill(
                    "plugin_worker_exit",
                    SafeDiagnosticFormatter.FormatWorkerExit(
                        "plugin_worker_exit",
                        exit,
                        "Plugin worker exited before returning a response.",
                        stderr.GetCapturedText()));
            }

            try
            {
                var response = ExtractorPluginWorkerProtocol.DeserializeResponse(responseJson, maxProtocolLineBytes);
                if (!string.IsNullOrWhiteSpace(response.Error))
                    return Failure(response.ErrorCategory ?? "plugin_worker_execution_failed", response.Error);
                return new(true, null, null, response);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return FailureAfterKill(
                    "plugin_worker_protocol_error",
                    SafeDiagnosticFormatter.FormatExceptionCategory("plugin_worker_protocol_error", ex));
            }
        }
    }

    private (string Category, string Message)? WaitForTask(Task task, Stopwatch stopwatch)
    {
        while (!task.IsCompleted)
        {
            if (stopwatch.Elapsed >= operationBudget)
                return ("plugin_worker_timeout", "Plugin worker exceeded its wall-clock deadline.");

            try
            {
                if (process == null || process.HasExited)
                    return ("plugin_worker_exit", "Plugin worker exited before completing the request.");
                process.Refresh();
                if (process.WorkingSet64 > memoryLimitBytes)
                    return ("plugin_worker_memory_limit", "Plugin worker exceeded its memory budget.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return ("plugin_worker_exit", "Plugin worker state could not be inspected.");
            }

            Thread.Sleep(10);
        }

        return null;
    }

    private bool EnsureStarted(out string error)
    {
        if (process is { HasExited: false })
        {
            error = string.Empty;
            return true;
        }

        ClearProcess();
        stderr = new WorkerOutputBuffer();
        if (!TryCreateStartInfo(assemblyPath, typeInspectionLimit, maxProtocolLineBytes, out var startInfo, out error))
            return false;
        if (memoryLimitBytes >= 64L * 1024 * 1024)
        {
            startInfo.Environment["DOTNET_GCHeapHardLimit"] =
                memoryLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        }

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
                error = "Plugin worker process did not start.";
                next.Dispose();
                return false;
            }

            next.BeginErrorReadLine();
            process = next;
            ProcessStartedForTesting?.Invoke();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = SafeDiagnosticFormatter.FormatExceptionCategory("plugin_worker_start_failed", ex);
            next.Dispose();
            return false;
        }
    }

    private static async Task SendAsync(TextWriter input, string requestJson)
    {
        await input.WriteLineAsync(requestJson).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    private ExtractorPluginWorkerResult FailureAfterKill(string category, string message)
    {
        var cleanup = KillProcess();
        return Failure(category, WorkerProcessCleanupDiagnostics.AppendToMessage(message, cleanup));
    }

    private static ExtractorPluginWorkerResult Failure(string category, string? message)
        => new(false, category, string.IsNullOrWhiteSpace(message) ? "Plugin worker failed." : message, null);

    private string? KillProcess()
    {
        if (process == null)
            return null;
        var cleanup = WorkerProcessCleanupDiagnostics.TryKill(process, 5000);
        ClearProcess();
        return cleanup;
    }

    private void ClearProcess()
    {
        process?.Dispose();
        process = null;
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
                // Best-effort worker shutdown.
            }

            var wait = WorkerProcessCleanupDiagnostics.WaitForExit(process, 1000);
            if (!wait.Exited)
                _ = KillProcess();
            else
                ClearProcess();
        }
    }
}

internal static class ExtractorPluginWorker
{
    internal const string CommandName = "__cdidx-extractor-plugin";
    private const int CapturedConsoleMaxChars = 32 * 1024;

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

    private static int RunCommand(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (args.Length != 5
            || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeLimit)
            || typeLimit <= 0
            || !StringComparer.Ordinal.Equals(args[3], CodeIndex.ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption)
            || !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocolLimit)
            || protocolLimit <= 0)
        {
            error.WriteLine("Extractor plugin worker requires assembly path, type limit, and protocol byte limit.");
            return 2;
        }

        var runtime = new PluginRuntime(args[1], typeLimit);
        while (true)
        {
            string? requestJson;
            try
            {
                requestJson = BoundedLineReader.ReadLine(input, protocolLimit, protocolLimit);
            }
            catch (Exception ex)
            {
                WriteResponse(output, protocolLimit, Error("plugin_worker_protocol_error", ex));
                return 1;
            }

            if (requestJson == null)
                return 0;

            ExtractorPluginWorkerResponse response;
            try
            {
                if (!WorkerProtocolJsonValidator.TryValidate(requestJson, protocolLimit, out var validationError))
                {
                    response = new(ErrorCategory: "plugin_worker_protocol_error", Error: validationError);
                }
                else
                {
                    var request = ExtractorPluginWorkerProtocol.DeserializeRequest(requestJson, protocolLimit);
                    response = ExecutePluginCode(() => runtime.Handle(request));
                }
            }
            catch (PluginRuntimeException ex)
            {
                response = new(
                    ErrorCategory: ex.Category,
                    Error: ex.Message);
            }
            catch (Exception ex)
            {
                response = Error("plugin_worker_execution_failed", ex);
            }

            WriteResponse(output, protocolLimit, response);
        }
    }

    private static ExtractorPluginWorkerResponse ExecutePluginCode(Func<ExtractorPluginWorkerResponse> action)
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
            return action();
        }
        finally
        {
            ConsoleStreamOwnership.Restore(originalOut, originalError);
        }
    }

    private static void WriteResponse(TextWriter output, int protocolLimit, ExtractorPluginWorkerResponse response)
    {
        var json = ExtractorPluginWorkerProtocol.Serialize(response);
        if (Encoding.UTF8.GetByteCount(json) > protocolLimit)
        {
            json = ExtractorPluginWorkerProtocol.Serialize(
                new ExtractorPluginWorkerResponse(
                    ErrorCategory: "plugin_worker_output_limit",
                    Error: "Plugin worker response exceeded its output budget."));
        }

        output.WriteLine(json);
        output.Flush();
    }

    private static ExtractorPluginWorkerResponse Error(string category, Exception exception)
        => new(ErrorCategory: category, Error: SafeDiagnosticFormatter.FormatExceptionCategory(category, exception));

    private sealed class PluginRuntime
    {
        private readonly string assemblyPath;
        private readonly int typeLimit;
        private Dictionary<string, object>? instances;
        private List<ExtractorPluginWorkerManifestEntry>? manifest;
        private List<ExtractorPluginWorkerDiagnostic>? diagnostics;

        internal PluginRuntime(string assemblyPath, int typeLimit)
        {
            this.assemblyPath = assemblyPath;
            this.typeLimit = typeLimit;
        }

        internal ExtractorPluginWorkerResponse Handle(ExtractorPluginWorkerRequest request)
        {
            EnsureLoaded();
            if (request.Kind == ExtractorPluginWorkerRequestKind.Manifest)
                return new(Manifest: manifest, Diagnostics: diagnostics);

            if (string.IsNullOrWhiteSpace(request.TypeName)
                || request.Source == null
                || request.Context == null
                || instances == null
                || !instances.TryGetValue(request.TypeName, out var instance))
            {
                return new(ErrorCategory: "plugin_worker_request_invalid", Error: "Plugin worker request did not identify a loaded extractor.");
            }

            return request.Kind switch
            {
                ExtractorPluginWorkerRequestKind.Symbols when instance is ISymbolExtractor symbols
                    => new(Symbols: symbols.Extract(request.FileId, request.Source, request.Context).ToList()),
                ExtractorPluginWorkerRequestKind.References when instance is IReferenceExtractor references
                    => new(References: references.Extract(request.FileId, request.Source, request.Context).ToList()),
                _ => new(ErrorCategory: "plugin_worker_role_mismatch", Error: "Plugin extractor does not support the requested role."),
            };
        }

        private void EnsureLoaded()
        {
            if (instances != null)
                return;

            instances = new(StringComparer.Ordinal);
            manifest = [];
            diagnostics = [];
            var fullPath = Path.GetFullPath(assemblyPath);
            var loadContext = new ExtensionAssemblyLoadContext(
                $"cdidx-plugin-worker:{Path.GetFileNameWithoutExtension(fullPath)}",
                fullPath);
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyTypeLoad(
                    "Plugin assembly type inspection",
                    ex);
                throw new PluginRuntimeException(diagnostic.Category, diagnostic.Message);
            }

            if (types.Length > typeLimit)
                throw new PluginRuntimeException(
                    "plugin_type_limit_exceeded",
                    $"Plugin assembly skipped: too many loadable types ({types.Length}; maximum {typeLimit}).");

            foreach (var type in types)
            {
                var supportsSymbols = type is { IsAbstract: false, IsInterface: false }
                                      && typeof(ISymbolExtractor).IsAssignableFrom(type);
                var supportsReferences = type is { IsAbstract: false, IsInterface: false }
                                         && typeof(IReferenceExtractor).IsAssignableFrom(type);
                if ((!supportsSymbols && !supportsReferences) || type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                try
                {
                    var instance = Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException("Plugin constructor returned no instance.");
                    var typeName = type.FullName
                        ?? throw new InvalidOperationException("Plugin extractor type has no full name.");
                    var symbolLanguage = supportsSymbols ? ((ISymbolExtractor)instance).Language : null;
                    var symbolExtensions = supportsSymbols ? ((ISymbolExtractor)instance).FileExtensions.ToList() : null;
                    var referenceLanguage = supportsReferences ? ((IReferenceExtractor)instance).Language : null;
                    var referenceExtensions = supportsReferences ? ((IReferenceExtractor)instance).FileExtensions.ToList() : null;
                    instances[typeName] = instance;
                    manifest.Add(new(typeName, symbolLanguage, symbolExtensions, referenceLanguage, referenceExtensions));
                }
                catch (Exception ex)
                {
                    var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyConstructorFailure(
                        "Plugin type constructor",
                        ex);
                    diagnostics.Add(new(
                        type.FullName,
                        diagnostic.Category,
                        diagnostic.Message));
                }
            }
        }
    }

    private sealed class PluginRuntimeException(string category, string message) : Exception(message)
    {
        internal string Category { get; } = category;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExtractorPluginWorkerRequest))]
[JsonSerializable(typeof(ExtractorPluginWorkerResponse))]
[JsonSerializable(typeof(ExtractorPluginWorkerManifestEntry))]
[JsonSerializable(typeof(ExtractorPluginWorkerDiagnostic))]
internal partial class ExtractorPluginWorkerJsonContext : JsonSerializerContext;
