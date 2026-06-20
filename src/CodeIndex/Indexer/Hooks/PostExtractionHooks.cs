using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Diagnostics;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Hooks;

public interface IPostExtractionHook
{
    void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols);

    void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references);
}

public sealed record FileContext(string ProjectRoot, string Path, string FullPath, string? Language);

public sealed record PostExtractionHookInfo(string Name, string AssemblyPath, string TypeName);

public sealed record PostExtractionHookDiagnostic(
    string AssemblyPath,
    string? TypeName,
    string Message,
    string? Callback = null,
    long? DurationMs = null,
    [property: JsonPropertyName("category")] string Category = "unspecified");

public sealed record PostExtractionHookDiscoverySnapshot(
    IReadOnlyList<PostExtractionHookInfo> Hooks,
    IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics,
    TimeSpan CallbackBudget);

public sealed class PostExtractionHookRunner : IDisposable
{
    public const string HooksDirectoryEnvironmentVariable = "CDIDX_HOOKS_DIR";
    public const string CallbackBudgetEnvironmentVariable = "CDIDX_HOOK_CALLBACK_BUDGET_MS";
    public const string DiscoveryLimitEnvironmentVariable = "CDIDX_HOOK_DISCOVERY_MAX_DLLS";
    public const string DiscoveryMaxBytesEnvironmentVariable = "CDIDX_HOOK_DISCOVERY_MAX_BYTES";
    public static readonly TimeSpan DefaultCallbackBudget = TimeSpan.FromSeconds(5);
    internal const int DefaultDiscoveryLimit = 128;
    internal const long DefaultDiscoveryMaxBytes = 64 * 1024 * 1024;

    private readonly List<LoadedPostExtractionHook> hooks;
    private readonly ConcurrentQueue<PostExtractionHookDiagnostic> diagnostics = new();
    private readonly ConcurrentDictionary<string, byte> disabledHooks = new(StringComparer.Ordinal);
    private readonly TimeSpan callbackBudget;
    private bool disposed;
    internal static Func<TimeSpan>? CallbackBudgetForTesting { get; set; }
    internal static Func<int>? DiscoveryLimitForTesting { get; set; }
    internal static Func<long>? DiscoveryMaxBytesForTesting { get; set; }

    private PostExtractionHookRunner(List<LoadedPostExtractionHook> hooks, TimeSpan callbackBudget)
    {
        this.hooks = hooks;
        this.callbackBudget = callbackBudget;
    }

    public static PostExtractionHookRunner DiscoverDefault(long? maxFileSizeBytes = null)
    {
        var resolution = ResolveDefaultHooksDirectory(includeAcceptedOverrideDiagnostic: false);
        return Discover(resolution.Directory, maxFileSizeBytes, resolution.Diagnostics);
    }

    public static PostExtractionHookDiscoverySnapshot DiscoverDefaultMetadata()
    {
        var resolution = ResolveDefaultHooksDirectory(includeAcceptedOverrideDiagnostic: true);
        return DiscoverMetadata(resolution.Directory, resolution.Diagnostics);
    }

    public static PostExtractionHookDiscoverySnapshot DiscoverMetadata(string? hooksDirectory)
        => DiscoverMetadata(hooksDirectory, []);

    private static PostExtractionHookDiscoverySnapshot DiscoverMetadata(
        string? hooksDirectory,
        IReadOnlyList<PostExtractionHookDiagnostic> initialDiagnostics)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var runner = new PostExtractionHookRunner(loaded, ResolveCallbackBudget());
        runner.EnqueueDiagnostics(initialDiagnostics);
        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return new PostExtractionHookDiscoverySnapshot([], runner.Diagnostics, runner.CallbackBudget);

        var hooks = EnumerateHookAssemblyPaths(hooksDirectory, runner, ResolveDiscoveryLimit())
            .Select(dllPath =>
            {
                var fullPath = Path.GetFullPath(dllPath);
                return new PostExtractionHookInfo(
                    Path.GetFileNameWithoutExtension(fullPath),
                    DiagnosticSanitizer.ForPath(fullPath),
                    string.Empty);
            })
            .ToArray();

        return new PostExtractionHookDiscoverySnapshot(hooks, runner.Diagnostics, runner.CallbackBudget);
    }

    public static PostExtractionHookRunner Discover(string? hooksDirectory, long? maxFileSizeBytes = null)
        => Discover(hooksDirectory, maxFileSizeBytes, []);

    private static PostExtractionHookRunner Discover(
        string? hooksDirectory,
        long? maxFileSizeBytes,
        IReadOnlyList<PostExtractionHookDiagnostic> initialDiagnostics)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var runner = new PostExtractionHookRunner(loaded, ResolveCallbackBudget());
        runner.EnqueueDiagnostics(initialDiagnostics);
        var maxProtocolLineBytes = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);

        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return runner;

        var maxAssemblyBytes = ResolveDiscoveryMaxBytes();
        foreach (var dllPath in EnumerateHookAssemblyPaths(hooksDirectory, runner, ResolveDiscoveryLimit()))
        {
            Assembly assembly;
            try
            {
                if (!HookAssemblyCandidateIsWithinBudget(dllPath, runner, maxAssemblyBytes))
                    continue;

                var loadContext = new ExtensionAssemblyLoadContext(
                    $"cdidx-hook:{Path.GetFileNameWithoutExtension(dllPath)}",
                    dllPath);
                assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            }
            catch (Exception ex)
            {
                var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad("Hook assembly load", ex);
                runner.EnqueueDiagnostic(dllPath, null, diagnostic.Message, category: diagnostic.Category);
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyTypeLoad("Hook assembly type inspection", ex);
                runner.EnqueueDiagnostic(dllPath, null, diagnostic.Message, category: diagnostic.Category);
                continue;
            }

            foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IPostExtractionHook).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        runner.EnqueueDiagnostic(
                            dllPath,
                            type.FullName,
                            "Failed to instantiate hook: public parameterless constructor not found.",
                            category: "hook_constructor_missing");
                        continue;
                    }

                    var info = new PostExtractionHookInfo(type.Name, Path.GetFullPath(dllPath), type.FullName ?? type.Name);
                    loaded.Add(new LoadedPostExtractionHook(
                        info,
                        AssemblyLoadContext.GetLoadContext(type.Assembly),
                        new PostExtractionHookCallbackWorkerClient(info, maxProtocolLineBytes)));
                }
                catch (Exception)
                {
                    runner.EnqueueDiagnostic(dllPath, type.FullName, "Failed to instantiate hook.", category: "activation_failed");
                }
            }
        }

        return runner;
    }

    private static IReadOnlyList<string> EnumerateHookAssemblyPaths(
        string hooksDirectory,
        PostExtractionHookRunner runner,
        int discoveryLimit)
    {
        using var enumerator = TryEnumerateHookFiles(hooksDirectory, runner);
        if (enumerator == null)
            return [];

        var candidates = new List<string>(Math.Min(discoveryLimit, 128));
        while (TryMoveNextHookFile(hooksDirectory, enumerator, runner, out var dllPath))
        {
            if (candidates.Count >= discoveryLimit)
            {
                runner.EnqueueDiagnostic(
                    hooksDirectory,
                    null,
                    $"Hook discovery skipped remaining assemblies after the {discoveryLimit} DLL candidate limit.",
                    category: "hook_candidate_limit_exceeded");
                break;
            }

            candidates.Add(dllPath);
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerator<string>? TryEnumerateHookFiles(string hooksDirectory, PostExtractionHookRunner runner)
    {
        try
        {
            return Directory.EnumerateFiles(hooksDirectory, "*.dll", SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "hook",
                "Hook directory",
                ex);
            runner.EnqueueDiagnostic(
                hooksDirectory,
                null,
                $"Hook directory skipped: {diagnostic.Message}.",
                category: diagnostic.Category);
            return null;
        }
    }

    private static bool HookAssemblyCandidateIsWithinBudget(
        string dllPath,
        PostExtractionHookRunner runner,
        long maxAssemblyBytes)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(dllPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            runner.EnqueueDiagnostic(
                dllPath,
                null,
                "Hook assembly skipped: could not inspect file.",
                category: "hook_file_inspection_failed");
            return false;
        }

        if (!fileInfo.Exists)
        {
            runner.EnqueueDiagnostic(
                dllPath,
                null,
                "Hook assembly skipped: file does not exist.",
                category: "hook_file_missing");
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            runner.EnqueueDiagnostic(
                dllPath,
                null,
                "Hook assembly skipped: path is a directory.",
                category: "hook_path_is_directory");
            return false;
        }

        if (fileInfo.Length > maxAssemblyBytes)
        {
            runner.EnqueueDiagnostic(
                dllPath,
                null,
                $"Hook assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {maxAssemblyBytes}).",
                category: "hook_file_too_large");
            return false;
        }

        return true;
    }

    private static bool TryMoveNextHookFile(
        string hooksDirectory,
        IEnumerator<string> enumerator,
        PostExtractionHookRunner runner,
        out string dllPath)
    {
        dllPath = string.Empty;
        try
        {
            if (!enumerator.MoveNext())
                return false;

            dllPath = enumerator.Current;
            return true;
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "hook",
                "Hook directory",
                ex);
            runner.EnqueueDiagnostic(
                hooksDirectory,
                null,
                $"Hook directory skipped: {diagnostic.Message}.",
                category: diagnostic.Category);
            return false;
        }
    }

    public IReadOnlyList<PostExtractionHookInfo> Hooks => hooks.Select(hook => hook.Info).ToList();

    internal IReadOnlyList<AssemblyLoadContext?> LoadContextsForTests
        => hooks.Select(hook => hook.LoadContext).ToList();

    public IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics => diagnostics.ToList();

    public TimeSpan CallbackBudget => callbackBudget;

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var hook in hooks)
        {
            var workingSymbols = CloneSymbols(symbols);
            if (InvokeHookWithBudget(
                    hook,
                    PostExtractionHookCallbackKind.Symbols,
                    nameof(IPostExtractionHook.OnSymbolsExtracted),
                    context,
                    workingSymbols,
                    null))
            {
                ReplaceList(symbols, workingSymbols);
            }
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var hook in hooks)
        {
            var workingReferences = CloneReferences(references);
            if (InvokeHookWithBudget(
                    hook,
                    PostExtractionHookCallbackKind.References,
                    nameof(IPostExtractionHook.OnReferencesExtracted),
                    context,
                    null,
                    workingReferences))
            {
                ReplaceList(references, workingReferences);
            }
        }
    }

    private bool InvokeHookWithBudget(
        LoadedPostExtractionHook hook,
        PostExtractionHookCallbackKind kind,
        string callback,
        FileContext context,
        List<SymbolRecord>? symbols,
        List<ReferenceRecord>? references)
    {
        if (disabledHooks.ContainsKey(hook.Info.TypeName))
            return false;

        var result = hook.Worker.Invoke(
            kind,
            callback,
            context,
            symbols,
            references,
            callbackBudget);
        if (result.TimedOut)
        {
            disabledHooks.TryAdd(hook.Info.TypeName, 0);
            EnqueueDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                WorkerProcessCleanupDiagnostics.AppendToMessage(
                    $"{callback} exceeded the {callbackBudget.TotalMilliseconds:0} ms callback budget; hook disabled for this index run.",
                    result.WorkerError),
                callback,
                result.DurationMs,
                "callback_timeout");
            return false;
        }

        if (!result.Success)
        {
            disabledHooks.TryAdd(hook.Info.TypeName, 0);
            EnqueueDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                WorkerProcessCleanupDiagnostics.AppendToMessage(
                    $"{callback} failed in isolated worker.",
                    result.WorkerError),
                callback,
                result.DurationMs,
                ClassifyWorkerFailureCategory(result.WorkerError));
            return false;
        }

        if (result.Symbols != null && symbols != null)
            ReplaceList(symbols, result.Symbols);
        if (result.References != null && references != null)
            ReplaceList(references, result.References);

        if (result.CallbackError != null)
        {
            EnqueueDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                $"{callback} failed.",
                callback,
                result.DurationMs,
                "hook_callback_failed");
        }

        return true;
    }

    private static string ClassifyWorkerFailureCategory(string? workerError)
    {
        if (string.IsNullOrWhiteSpace(workerError))
            return "callback_worker_failed";

        if (workerError.StartsWith("worker_execution_failed:", StringComparison.Ordinal))
        {
            if (workerError.Contains(nameof(FileNotFoundException), StringComparison.Ordinal)
                || workerError.Contains(nameof(FileLoadException), StringComparison.Ordinal))
            {
                return "dependency_resolution_failed";
            }

            if (workerError.Contains(nameof(TypeLoadException), StringComparison.Ordinal))
                return "type_load_failed";

            return "constructor_failed";
        }
        if (workerError.StartsWith("worker_start_failed:", StringComparison.Ordinal))
            return "worker_start_failed";
        if (workerError.StartsWith("worker_protocol_error:", StringComparison.Ordinal))
            return "worker_protocol_error";

        return "callback_worker_failed";
    }

    private void EnqueueDiagnostic(
        string assemblyPath,
        string? typeName,
        string message,
        string? callback = null,
        long? durationMs = null,
        string category = "unspecified")
    {
        diagnostics.Enqueue(CreateDiagnostic(assemblyPath, typeName, message, callback, durationMs, category));
    }

    private void EnqueueDiagnostics(IEnumerable<PostExtractionHookDiagnostic> items)
    {
        foreach (var item in items)
            diagnostics.Enqueue(item);
    }

    private static TimeSpan ResolveCallbackBudget()
    {
        if (CallbackBudgetForTesting != null)
            return NormalizeCallbackBudget(CallbackBudgetForTesting());

        var raw = Environment.GetEnvironmentVariable(CallbackBudgetEnvironmentVariable);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            ? NormalizeCallbackBudgetMilliseconds(milliseconds)
            : DefaultCallbackBudget;
    }

    private static int ResolveDiscoveryLimit()
    {
        if (DiscoveryLimitForTesting != null)
            return NormalizeDiscoveryLimit(DiscoveryLimitForTesting());

        var raw = Environment.GetEnvironmentVariable(DiscoveryLimitEnvironmentVariable);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? NormalizeDiscoveryLimit(value)
            : DefaultDiscoveryLimit;
    }

    private static int NormalizeDiscoveryLimit(int value)
        => value <= 0 ? DefaultDiscoveryLimit : value;

    private static long ResolveDiscoveryMaxBytes()
    {
        if (DiscoveryMaxBytesForTesting != null)
            return NormalizeDiscoveryMaxBytes(DiscoveryMaxBytesForTesting());

        var raw = Environment.GetEnvironmentVariable(DiscoveryMaxBytesEnvironmentVariable);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? NormalizeDiscoveryMaxBytes(value)
            : DefaultDiscoveryMaxBytes;
    }

    private static long NormalizeDiscoveryMaxBytes(long value)
        => value <= 0 ? DefaultDiscoveryMaxBytes : value;

    private static TimeSpan NormalizeCallbackBudgetMilliseconds(long milliseconds)
        => milliseconds <= 0
            ? DefaultCallbackBudget
            : TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue));

    private static TimeSpan NormalizeCallbackBudget(TimeSpan value)
        => value <= TimeSpan.Zero
            ? DefaultCallbackBudget
            : TimeSpan.FromMilliseconds(Math.Min(value.TotalMilliseconds, int.MaxValue));

    private static List<SymbolRecord> CloneSymbols(IEnumerable<SymbolRecord> symbols)
        => symbols.Select(symbol => new SymbolRecord
        {
            Id = symbol.Id,
            FileId = symbol.FileId,
            Kind = symbol.Kind,
            SubKind = symbol.SubKind,
            Name = symbol.Name,
            Line = symbol.Line,
            StartLine = symbol.StartLine,
            StartColumn = symbol.StartColumn,
            EndLine = symbol.EndLine,
            BodyStartLine = symbol.BodyStartLine,
            BodyEndLine = symbol.BodyEndLine,
            Signature = symbol.Signature,
            ContainerKind = symbol.ContainerKind,
            ContainerName = symbol.ContainerName,
            ContainerQualifiedName = symbol.ContainerQualifiedName,
            FamilyKey = symbol.FamilyKey,
            Visibility = symbol.Visibility,
            ReturnType = symbol.ReturnType,
            IsMetadataTarget = symbol.IsMetadataTarget,
            MetadataTargetSource = symbol.MetadataTargetSource,
            SameLineSignatureOccurrenceIndex = symbol.SameLineSignatureOccurrenceIndex,
        }).ToList();

    private static List<ReferenceRecord> CloneReferences(IEnumerable<ReferenceRecord> references)
        => references.Select(reference => new ReferenceRecord
        {
            Id = reference.Id,
            FileId = reference.FileId,
            SymbolName = reference.SymbolName,
            ReferenceKind = reference.ReferenceKind,
            Line = reference.Line,
            Column = reference.Column,
            Context = reference.Context,
            ContainerKind = reference.ContainerKind,
            ContainerName = reference.ContainerName,
            IsSelfReference = reference.IsSelfReference,
            IsMutualRecursion = reference.IsMutualRecursion,
        }).ToList();

    private static void ReplaceList<T>(IList<T> target, IReadOnlyList<T> replacement)
    {
        target.Clear();
        foreach (var item in replacement)
            target.Add(item);
    }

    private static HookDirectoryResolution ResolveDefaultHooksDirectory(bool includeAcceptedOverrideDiagnostic)
    {
        var overridePath = Environment.GetEnvironmentVariable(HooksDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return ResolveOverrideHooksDirectory(overridePath, includeAcceptedOverrideDiagnostic);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new HookDirectoryResolution(
            string.IsNullOrWhiteSpace(home)
                ? null
                : Path.Combine(home, ".config", "cdidx", "hooks"),
            []);
    }

    private static HookDirectoryResolution ResolveOverrideHooksDirectory(
        string overridePath,
        bool includeAcceptedOverrideDiagnostic)
    {
        var diagnostics = new List<PostExtractionHookDiagnostic>();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(overridePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            diagnostics.Add(CreateDiagnostic(
                overridePath,
                null,
                "Hook directory override rejected: path could not be resolved.",
                category: "hook_directory_override_invalid_path"));
            return new HookDirectoryResolution(null, diagnostics);
        }

        try
        {
            var directoryInfo = new DirectoryInfo(fullPath);
            if (!directoryInfo.Exists)
            {
                diagnostics.Add(CreateDiagnostic(
                    fullPath,
                    null,
                    "Hook directory override rejected: directory does not exist.",
                    category: "hook_directory_override_missing"));
                return new HookDirectoryResolution(null, diagnostics);
            }

            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || !string.IsNullOrEmpty(directoryInfo.LinkTarget))
            {
                diagnostics.Add(CreateDiagnostic(
                    fullPath,
                    null,
                    "Hook directory override rejected: symbolic links and reparse points are not supported.",
                    category: "hook_directory_override_rejected"));
                return new HookDirectoryResolution(null, diagnostics);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(CreateDiagnostic(
                fullPath,
                null,
                "Hook directory override rejected: directory could not be inspected.",
                category: "hook_directory_override_inspection_failed"));
            return new HookDirectoryResolution(null, diagnostics);
        }

        AddUnixPermissionDiagnostic(fullPath, diagnostics);
        if (includeAcceptedOverrideDiagnostic)
        {
            diagnostics.Add(CreateDiagnostic(
                fullPath,
                null,
                "Hook directory override accepted: hook assemblies execute local extension code from this trusted directory.",
                category: "hook_directory_override_accepted"));
        }

        return new HookDirectoryResolution(fullPath, diagnostics);
    }

    private static void AddUnixPermissionDiagnostic(string fullPath, List<PostExtractionHookDiagnostic> diagnostics)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    fullPath,
                    null,
                    "Hook directory override warning: directory is group- or world-writable; only trusted users should be able to modify hook assemblies.",
                    category: "hook_directory_override_unsafe_permissions"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(CreateDiagnostic(
                fullPath,
                null,
                "Hook directory override warning: directory permissions could not be inspected.",
                category: "hook_directory_override_permission_inspection_failed"));
        }
    }

    private static PostExtractionHookDiagnostic CreateDiagnostic(
        string assemblyPath,
        string? typeName,
        string message,
        string? callback = null,
        long? durationMs = null,
        string category = "unspecified")
        => new(
            DiagnosticSanitizer.ForPath(assemblyPath),
            DiagnosticSanitizer.ForOptionalLabel(typeName),
            DiagnosticSanitizer.ForMessage(message),
            DiagnosticSanitizer.ForOptionalLabel(callback),
            durationMs,
            DiagnosticSanitizer.ForMessage(category));

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var hook in hooks)
        {
            hook.Worker.Dispose();
        }

        var loadContexts = hooks
            .Select(hook => hook.LoadContext)
            .Where(loadContext => loadContext is { IsCollectible: true })
            .Distinct()
            .ToList();
        hooks.Clear();

        foreach (var loadContext in loadContexts)
        {
            loadContext!.Unload();
        }
    }

    private sealed record LoadedPostExtractionHook(
        PostExtractionHookInfo Info,
        AssemblyLoadContext? LoadContext,
        PostExtractionHookCallbackWorkerClient Worker);

    private sealed record HookDirectoryResolution(
        string? Directory,
        IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics);
}
