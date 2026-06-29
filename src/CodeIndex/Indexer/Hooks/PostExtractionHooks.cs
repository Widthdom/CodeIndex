using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
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
    TimeSpan CallbackBudget,
    IReadOnlyList<ExtensionTrustOverride> TrustOverrides);

public sealed class PostExtractionHookRunner : IDisposable
{
    public const string HooksDirectoryEnvironmentVariable = "CDIDX_HOOKS_DIR";
    public const string CallbackBudgetEnvironmentVariable = "CDIDX_HOOK_CALLBACK_BUDGET_MS";
    public const string DiscoveryLimitEnvironmentVariable = "CDIDX_HOOK_DISCOVERY_MAX_DLLS";
    public const string DiscoveryMaxBytesEnvironmentVariable = "CDIDX_HOOK_DISCOVERY_MAX_BYTES";
    public static readonly TimeSpan DefaultCallbackBudget = TimeSpan.FromSeconds(5);
    internal const int DefaultDiscoveryLimit = 128;
    internal const long DefaultDiscoveryMaxBytes = 64 * 1024 * 1024;
    internal const int MaxCallbackBudgetMilliseconds = 60_000;
    internal const int MaxDiscoveryLimit = 4096;
    internal const long MaxDiscoveryMaxBytes = 512L * 1024 * 1024;
    internal const int DefaultTypeInspectionLimit = 4096;
    internal const string HookLoadContextLifecycle = "collectible_unloaded_on_runner_dispose";

    private readonly List<LoadedPostExtractionHook> hooks;
    private readonly ConcurrentQueue<PostExtractionHookDiagnostic> diagnostics = new();
    private readonly ConcurrentDictionary<string, byte> disabledHooks = new(StringComparer.Ordinal);
    private readonly TimeSpan callbackBudget;
    private readonly int? maxSymbolCount;
    private readonly int? maxReferenceCount;
    private bool disposed;
    internal static Func<TimeSpan>? CallbackBudgetForTesting { get; set; }
    internal static Func<int>? DiscoveryLimitForTesting { get; set; }
    internal static Func<long>? DiscoveryMaxBytesForTesting { get; set; }
    internal static Func<int>? TypeInspectionLimitForTesting { get; set; }
    internal static WeakReference? LastUnretainedLoadContextForTesting { get; set; }

    private PostExtractionHookRunner(
        List<LoadedPostExtractionHook> hooks,
        TimeSpan callbackBudget,
        int? maxSymbolCount,
        int? maxReferenceCount)
    {
        this.hooks = hooks;
        this.callbackBudget = callbackBudget;
        this.maxSymbolCount = maxSymbolCount;
        this.maxReferenceCount = maxReferenceCount;
    }

    public static PostExtractionHookRunner DiscoverDefault(
        long? maxFileSizeBytes = null,
        int? maxSymbolCount = null,
        int? maxReferenceCount = null)
    {
        var resolution = PostExtractionHookDirectoryResolver.ResolveDefault(includeAcceptedOverrideDiagnostic: false);
        return Discover(resolution.Directory, maxFileSizeBytes, resolution.Diagnostics, maxSymbolCount, maxReferenceCount);
    }

    public static PostExtractionHookDiscoverySnapshot DiscoverDefaultMetadata()
    {
        var resolution = PostExtractionHookDirectoryResolver.ResolveDefault(includeAcceptedOverrideDiagnostic: true);
        return DiscoverMetadata(resolution.Directory, resolution.Diagnostics, resolution.TrustOverrides);
    }

    public static PostExtractionHookDiscoverySnapshot DiscoverMetadata(string? hooksDirectory)
        => DiscoverMetadata(hooksDirectory, [], []);

    private static PostExtractionHookDiscoverySnapshot DiscoverMetadata(
        string? hooksDirectory,
        IReadOnlyList<PostExtractionHookDiagnostic> initialDiagnostics,
        IReadOnlyList<ExtensionTrustOverride> initialTrustOverrides)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var callbackBudget = ResolveCallbackBudget();
        var runner = new PostExtractionHookRunner(loaded, callbackBudget.Value, null, null);
        runner.EnqueueDiagnostic(callbackBudget.Diagnostic);
        runner.EnqueueDiagnostics(initialDiagnostics);
        var discoveryLimit = ResolveDiscoveryLimit();
        runner.EnqueueDiagnostic(discoveryLimit.Diagnostic);
        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return new PostExtractionHookDiscoverySnapshot([], runner.Diagnostics, runner.CallbackBudget, initialTrustOverrides);

        if (!PostExtractionHookAssemblyDiscovery.DirectoryIsSupported(hooksDirectory, runner.EnqueueDiagnostic))
            return new PostExtractionHookDiscoverySnapshot([], runner.Diagnostics, runner.CallbackBudget, initialTrustOverrides);

        var hooks = PostExtractionHookAssemblyDiscovery.EnumerateAssemblyPaths(
                hooksDirectory,
                discoveryLimit.Value,
                runner.EnqueueDiagnostic)
            .Select(dllPath =>
            {
                var fullPath = Path.GetFullPath(dllPath);
                return new PostExtractionHookInfo(
                    Path.GetFileNameWithoutExtension(fullPath),
                    DiagnosticSanitizer.ForPath(fullPath),
                    string.Empty);
            })
            .ToArray();

        return new PostExtractionHookDiscoverySnapshot(hooks, runner.Diagnostics, runner.CallbackBudget, initialTrustOverrides);
    }

    public static PostExtractionHookRunner Discover(
        string? hooksDirectory,
        long? maxFileSizeBytes = null,
        int? maxSymbolCount = null,
        int? maxReferenceCount = null)
        => Discover(hooksDirectory, maxFileSizeBytes, [], maxSymbolCount, maxReferenceCount);

    private static PostExtractionHookRunner Discover(
        string? hooksDirectory,
        long? maxFileSizeBytes,
        IReadOnlyList<PostExtractionHookDiagnostic> initialDiagnostics,
        int? maxSymbolCount,
        int? maxReferenceCount)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var callbackBudget = ResolveCallbackBudget();
        var runner = new PostExtractionHookRunner(
            loaded,
            callbackBudget.Value,
            PostExtractionHookMutationMaterializer.NormalizeLimit(maxSymbolCount),
            PostExtractionHookMutationMaterializer.NormalizeLimit(maxReferenceCount));
        runner.EnqueueDiagnostic(callbackBudget.Diagnostic);
        runner.EnqueueDiagnostics(initialDiagnostics);
        var maxProtocolLineBytes = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);
        var discoveryLimit = ResolveDiscoveryLimit();
        runner.EnqueueDiagnostic(discoveryLimit.Diagnostic);

        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return runner;

        if (!PostExtractionHookAssemblyDiscovery.DirectoryIsSupported(hooksDirectory, runner.EnqueueDiagnostic))
            return runner;

        var maxAssemblyBytes = ResolveDiscoveryMaxBytes();
        runner.EnqueueDiagnostic(maxAssemblyBytes.Diagnostic);
        foreach (var dllPath in PostExtractionHookAssemblyDiscovery.EnumerateAssemblyPaths(
                     hooksDirectory,
                     discoveryLimit.Value,
                     runner.EnqueueDiagnostic))
        {
            ExtensionAssemblyLoadContext? loadContext = null;
            var retainedLoadContext = false;
            Assembly assembly;
            try
            {
                if (!PostExtractionHookAssemblyDiscovery.CandidateIsWithinBudget(
                        dllPath,
                        maxAssemblyBytes.Value,
                        runner.EnqueueDiagnostic))
                    continue;

                loadContext = new ExtensionAssemblyLoadContext(
                    $"cdidx-hook:{Path.GetFileNameWithoutExtension(dllPath)}",
                    dllPath);
                assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            }
            catch (Exception ex)
            {
                var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad("Hook assembly load", ex);
                runner.EnqueueDiagnostic(dllPath, null, diagnostic.Message, category: diagnostic.Category);
                loadContext?.Unload();
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
                loadContext?.Unload();
                continue;
            }

            if (!PostExtractionHookAssemblyDiscovery.TypesAreWithinBudget(
                    dllPath,
                    types,
                    ResolveTypeInspectionLimit(),
                    runner.EnqueueDiagnostic))
            {
                loadContext?.Unload();
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
                        loadContext,
                        new PostExtractionHookCallbackWorkerClient(info, maxProtocolLineBytes)));
                    retainedLoadContext = true;
                }
                catch (Exception)
                {
                    runner.EnqueueDiagnostic(dllPath, type.FullName, "Failed to instantiate hook.", category: "activation_failed");
                }
            }

            if (!retainedLoadContext)
            {
                if (loadContext != null)
                    LastUnretainedLoadContextForTesting = new WeakReference(loadContext, trackResurrection: false);
                loadContext?.Unload();
            }
        }

        return runner;
    }

    public IReadOnlyList<PostExtractionHookInfo> Hooks => hooks.Select(hook => hook.Info).ToList();

    internal IReadOnlyList<AssemblyLoadContext?> LoadContextsForTests
        => hooks.Select(hook => hook.LoadContext).ToList();

    public IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics => diagnostics.ToList();

    public TimeSpan CallbackBudget => callbackBudget;

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workingSymbols = PostExtractionHookMutationMaterializer.CloneSymbols(symbols, maxSymbolCount, out var inputTruncated);
            if (inputTruncated && maxSymbolCount is { } symbolLimit)
            {
                EnqueueHookMaterializationDiagnostic(
                    hook,
                    nameof(IPostExtractionHook.OnSymbolsExtracted),
                    "symbol",
                    symbolLimit,
                    "input");
            }
            if (InvokeHookWithBudget(
                    hook,
                    PostExtractionHookCallbackKind.Symbols,
                    nameof(IPostExtractionHook.OnSymbolsExtracted),
                    context,
                    workingSymbols,
                    null,
                    maxSymbolCount,
                    null,
                    cancellationToken))
            {
                PostExtractionHookMutationMaterializer.ReplaceList(symbols, workingSymbols);
            }
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workingReferences = PostExtractionHookMutationMaterializer.CloneReferences(references, maxReferenceCount, out var inputTruncated);
            if (inputTruncated && maxReferenceCount is { } referenceLimit)
            {
                EnqueueHookMaterializationDiagnostic(
                    hook,
                    nameof(IPostExtractionHook.OnReferencesExtracted),
                    "reference",
                    referenceLimit,
                    "input");
            }
            if (InvokeHookWithBudget(
                    hook,
                    PostExtractionHookCallbackKind.References,
                    nameof(IPostExtractionHook.OnReferencesExtracted),
                    context,
                    null,
                    workingReferences,
                    null,
                    maxReferenceCount,
                    cancellationToken))
            {
                PostExtractionHookMutationMaterializer.ReplaceList(references, workingReferences);
            }
        }
    }

    private bool InvokeHookWithBudget(
        LoadedPostExtractionHook hook,
        PostExtractionHookCallbackKind kind,
        string callback,
        FileContext context,
        List<SymbolRecord>? symbols,
        List<ReferenceRecord>? references,
        int? maxSymbols,
        int? maxReferences,
        CancellationToken cancellationToken)
    {
        if (disabledHooks.ContainsKey(hook.Info.TypeName))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var result = hook.Worker.Invoke(
            kind,
            callback,
            context,
            symbols,
            references,
            callbackBudget,
            maxSymbols,
            maxReferences,
            cancellationToken);
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
        {
            if (result.SymbolsTruncated && maxSymbols is { } symbolLimit)
            {
                EnqueueHookMaterializationDiagnostic(
                    hook,
                    callback,
                    "symbol",
                    symbolLimit,
                    "output");
            }
            PostExtractionHookMutationMaterializer.ReplaceList(symbols, result.Symbols);
        }
        if (result.References != null && references != null)
        {
            if (result.ReferencesTruncated && maxReferences is { } referenceLimit)
            {
                EnqueueHookMaterializationDiagnostic(
                    hook,
                    callback,
                    "reference",
                    referenceLimit,
                    "output");
            }
            PostExtractionHookMutationMaterializer.ReplaceList(references, result.References);
        }

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
        diagnostics.Enqueue(PostExtractionHookDiagnosticFactory.Create(assemblyPath, typeName, message, callback, durationMs, category));
    }

    private void EnqueueDiagnostic(PostExtractionHookDiagnostic? diagnostic)
    {
        if (diagnostic != null)
            diagnostics.Enqueue(diagnostic);
    }

    private void EnqueueDiagnostics(IEnumerable<PostExtractionHookDiagnostic> items)
    {
        foreach (var item in items)
            diagnostics.Enqueue(item);
    }

    private void EnqueueHookMaterializationDiagnostic(
        LoadedPostExtractionHook hook,
        string callback,
        string recordKind,
        int maxCount,
        string direction)
    {
        EnqueueDiagnostic(
            hook.Info.AssemblyPath,
            hook.Info.TypeName,
            $"Post-extraction hook {callback} {direction} exceeded the {maxCount:N0} {recordKind} materialization budget; extra {recordKind} records were discarded.",
            callback,
            category: recordKind == "symbol" ? "hook_symbol_count_truncated" : "hook_reference_count_truncated");
    }

    private static HookBudgetResolution<TimeSpan> ResolveCallbackBudget()
    {
        if (CallbackBudgetForTesting != null)
            return NormalizeCallbackBudget(CallbackBudgetForTesting());

        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(CallbackBudgetEnvironmentVariable);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            ? NormalizeCallbackBudgetMilliseconds(milliseconds)
            : new HookBudgetResolution<TimeSpan>(DefaultCallbackBudget, null);
    }

    private static HookBudgetResolution<int> ResolveDiscoveryLimit()
    {
        if (DiscoveryLimitForTesting != null)
            return NormalizeDiscoveryLimit(DiscoveryLimitForTesting());

        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(DiscoveryLimitEnvironmentVariable);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? NormalizeDiscoveryLimit(value)
            : new HookBudgetResolution<int>(DefaultDiscoveryLimit, null);
    }

    private static HookBudgetResolution<int> NormalizeDiscoveryLimit(int value)
    {
        if (value <= 0)
            return new HookBudgetResolution<int>(DefaultDiscoveryLimit, null);

        if (value > MaxDiscoveryLimit)
        {
            return new HookBudgetResolution<int>(
                MaxDiscoveryLimit,
                PostExtractionHookDiagnosticFactory.Create(
                    "<configuration>",
                    null,
                    $"{DiscoveryLimitEnvironmentVariable} value {value} exceeded the maximum {MaxDiscoveryLimit}; clamped to {MaxDiscoveryLimit}.",
                    category: "hook_discovery_limit_clamped"));
        }

        return new HookBudgetResolution<int>(value, null);
    }

    private static HookBudgetResolution<long> ResolveDiscoveryMaxBytes()
    {
        if (DiscoveryMaxBytesForTesting != null)
            return NormalizeDiscoveryMaxBytes(DiscoveryMaxBytesForTesting());

        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(DiscoveryMaxBytesEnvironmentVariable);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? NormalizeDiscoveryMaxBytes(value)
            : new HookBudgetResolution<long>(DefaultDiscoveryMaxBytes, null);
    }

    private static HookBudgetResolution<long> NormalizeDiscoveryMaxBytes(long value)
    {
        if (value <= 0)
            return new HookBudgetResolution<long>(DefaultDiscoveryMaxBytes, null);

        if (value > MaxDiscoveryMaxBytes)
        {
            return new HookBudgetResolution<long>(
                MaxDiscoveryMaxBytes,
                PostExtractionHookDiagnosticFactory.Create(
                    "<configuration>",
                    null,
                    $"{DiscoveryMaxBytesEnvironmentVariable} value {value} exceeded the maximum {MaxDiscoveryMaxBytes}; clamped to {MaxDiscoveryMaxBytes}.",
                    category: "hook_discovery_bytes_clamped"));
        }

        return new HookBudgetResolution<long>(value, null);
    }

    private static int ResolveTypeInspectionLimit()
    {
        if (TypeInspectionLimitForTesting != null)
            return NormalizeTypeInspectionLimit(TypeInspectionLimitForTesting());

        return DefaultTypeInspectionLimit;
    }

    private static int NormalizeTypeInspectionLimit(int value)
        => value <= 0 ? DefaultTypeInspectionLimit : value;

    private static HookBudgetResolution<TimeSpan> NormalizeCallbackBudgetMilliseconds(long milliseconds)
    {
        if (milliseconds <= 0)
            return new HookBudgetResolution<TimeSpan>(DefaultCallbackBudget, null);

        if (milliseconds > MaxCallbackBudgetMilliseconds)
        {
            return new HookBudgetResolution<TimeSpan>(
                TimeSpan.FromMilliseconds(MaxCallbackBudgetMilliseconds),
                PostExtractionHookDiagnosticFactory.Create(
                    "<configuration>",
                    null,
                    $"{CallbackBudgetEnvironmentVariable} value {milliseconds} exceeded the maximum {MaxCallbackBudgetMilliseconds}; clamped to {MaxCallbackBudgetMilliseconds}.",
                    category: "hook_callback_budget_clamped"));
        }

        return new HookBudgetResolution<TimeSpan>(TimeSpan.FromMilliseconds(milliseconds), null);
    }

    private static HookBudgetResolution<TimeSpan> NormalizeCallbackBudget(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return new HookBudgetResolution<TimeSpan>(DefaultCallbackBudget, null);

        if (value.TotalMilliseconds > MaxCallbackBudgetMilliseconds)
        {
            return new HookBudgetResolution<TimeSpan>(
                TimeSpan.FromMilliseconds(MaxCallbackBudgetMilliseconds),
                PostExtractionHookDiagnosticFactory.Create(
                    "<configuration>",
                    null,
                    $"{CallbackBudgetEnvironmentVariable} value {value.TotalMilliseconds:0} exceeded the maximum {MaxCallbackBudgetMilliseconds}; clamped to {MaxCallbackBudgetMilliseconds}.",
                    category: "hook_callback_budget_clamped"));
        }

        return new HookBudgetResolution<TimeSpan>(value, null);
    }

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

    private sealed record HookBudgetResolution<T>(T Value, PostExtractionHookDiagnostic? Diagnostic);
}
