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
        var resolution = ResolveDefaultHooksDirectory(includeAcceptedOverrideDiagnostic: false);
        return Discover(resolution.Directory, maxFileSizeBytes, resolution.Diagnostics, maxSymbolCount, maxReferenceCount);
    }

    public static PostExtractionHookDiscoverySnapshot DiscoverDefaultMetadata()
    {
        var resolution = ResolveDefaultHooksDirectory(includeAcceptedOverrideDiagnostic: true);
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

        var hooks = EnumerateHookAssemblyPaths(hooksDirectory, runner, discoveryLimit.Value)
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
            NormalizeHookMaterializationLimit(maxSymbolCount),
            NormalizeHookMaterializationLimit(maxReferenceCount));
        runner.EnqueueDiagnostic(callbackBudget.Diagnostic);
        runner.EnqueueDiagnostics(initialDiagnostics);
        var maxProtocolLineBytes = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);
        var discoveryLimit = ResolveDiscoveryLimit();
        runner.EnqueueDiagnostic(discoveryLimit.Diagnostic);

        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return runner;

        var maxAssemblyBytes = ResolveDiscoveryMaxBytes();
        runner.EnqueueDiagnostic(maxAssemblyBytes.Diagnostic);
        foreach (var dllPath in EnumerateHookAssemblyPaths(hooksDirectory, runner, discoveryLimit.Value))
        {
            ExtensionAssemblyLoadContext? loadContext = null;
            var retainedLoadContext = false;
            Assembly assembly;
            try
            {
                if (!HookAssemblyCandidateIsWithinBudget(dllPath, runner, maxAssemblyBytes.Value))
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

            if (!HookAssemblyTypesAreWithinBudget(dllPath, types, runner))
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
            var workingSymbols = CloneSymbols(symbols, maxSymbolCount, out var inputTruncated);
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
            var workingReferences = CloneReferences(references, maxReferenceCount, out var inputTruncated);
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
                    maxReferenceCount))
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
        List<ReferenceRecord>? references,
        int? maxSymbols,
        int? maxReferences)
    {
        if (disabledHooks.ContainsKey(hook.Info.TypeName))
            return false;

        var result = hook.Worker.Invoke(
            kind,
            callback,
            context,
            symbols,
            references,
            callbackBudget,
            maxSymbols,
            maxReferences);
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
            ReplaceList(symbols, result.Symbols);
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
            ReplaceList(references, result.References);
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
        diagnostics.Enqueue(CreateDiagnostic(assemblyPath, typeName, message, callback, durationMs, category));
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

    private static int? NormalizeHookMaterializationLimit(int? value)
        => value is > 0 ? value : null;

    private static HookBudgetResolution<TimeSpan> ResolveCallbackBudget()
    {
        if (CallbackBudgetForTesting != null)
            return NormalizeCallbackBudget(CallbackBudgetForTesting());

        var raw = Environment.GetEnvironmentVariable(CallbackBudgetEnvironmentVariable);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            ? NormalizeCallbackBudgetMilliseconds(milliseconds)
            : new HookBudgetResolution<TimeSpan>(DefaultCallbackBudget, null);
    }

    private static HookBudgetResolution<int> ResolveDiscoveryLimit()
    {
        if (DiscoveryLimitForTesting != null)
            return NormalizeDiscoveryLimit(DiscoveryLimitForTesting());

        var raw = Environment.GetEnvironmentVariable(DiscoveryLimitEnvironmentVariable);
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
                CreateDiagnostic(
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

        var raw = Environment.GetEnvironmentVariable(DiscoveryMaxBytesEnvironmentVariable);
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
                CreateDiagnostic(
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

    private static bool HookAssemblyTypesAreWithinBudget(
        string dllPath,
        IReadOnlyCollection<Type> types,
        PostExtractionHookRunner runner)
    {
        var limit = ResolveTypeInspectionLimit();
        if (types.Count <= limit)
            return true;

        runner.EnqueueDiagnostic(
            dllPath,
            null,
            $"Hook assembly skipped: too many loadable types ({types.Count}; maximum {limit}).",
            category: "hook_type_limit_exceeded");
        return false;
    }

    private static HookBudgetResolution<TimeSpan> NormalizeCallbackBudgetMilliseconds(long milliseconds)
    {
        if (milliseconds <= 0)
            return new HookBudgetResolution<TimeSpan>(DefaultCallbackBudget, null);

        if (milliseconds > MaxCallbackBudgetMilliseconds)
        {
            return new HookBudgetResolution<TimeSpan>(
                TimeSpan.FromMilliseconds(MaxCallbackBudgetMilliseconds),
                CreateDiagnostic(
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
                CreateDiagnostic(
                    "<configuration>",
                    null,
                    $"{CallbackBudgetEnvironmentVariable} value {value.TotalMilliseconds:0} exceeded the maximum {MaxCallbackBudgetMilliseconds}; clamped to {MaxCallbackBudgetMilliseconds}.",
                    category: "hook_callback_budget_clamped"));
        }

        return new HookBudgetResolution<TimeSpan>(value, null);
    }

    private static List<SymbolRecord> CloneSymbols(IEnumerable<SymbolRecord> symbols, int? maxCount, out bool truncated)
    {
        var result = new List<SymbolRecord>();
        truncated = false;
        foreach (var symbol in symbols)
        {
            if (maxCount is { } limit && result.Count >= limit)
            {
                truncated = true;
                break;
            }

            result.Add(CloneSymbol(symbol));
        }

        return result;
    }

    private static SymbolRecord CloneSymbol(SymbolRecord symbol)
        => new()
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
        };

    private static List<ReferenceRecord> CloneReferences(IEnumerable<ReferenceRecord> references, int? maxCount, out bool truncated)
    {
        var result = new List<ReferenceRecord>();
        truncated = false;
        foreach (var reference in references)
        {
            if (maxCount is { } limit && result.Count >= limit)
            {
                truncated = true;
                break;
            }

            result.Add(CloneReference(reference));
        }

        return result;
    }

    private static ReferenceRecord CloneReference(ReferenceRecord reference)
        => new()
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
        };

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
            [],
            []);
    }

    private static HookDirectoryResolution ResolveOverrideHooksDirectory(
        string overridePath,
        bool includeAcceptedOverrideDiagnostic)
    {
        var diagnostics = new List<PostExtractionHookDiagnostic>();
        var trustOverrides = new List<ExtensionTrustOverride>();
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
            return new HookDirectoryResolution(null, diagnostics, []);
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
                return new HookDirectoryResolution(null, diagnostics, []);
            }

            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || !string.IsNullOrEmpty(directoryInfo.LinkTarget))
            {
                diagnostics.Add(CreateDiagnostic(
                    fullPath,
                    null,
                    "Hook directory override rejected: symbolic links and reparse points are not supported.",
                    category: "hook_directory_override_rejected"));
                return new HookDirectoryResolution(null, diagnostics, []);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(CreateDiagnostic(
                fullPath,
                null,
                "Hook directory override rejected: directory could not be inspected.",
                category: "hook_directory_override_inspection_failed"));
            return new HookDirectoryResolution(null, diagnostics, []);
        }

        AddUnixPermissionDiagnostic(fullPath, diagnostics);
        if (includeAcceptedOverrideDiagnostic)
        {
            diagnostics.Add(CreateDiagnostic(
                fullPath,
                null,
                "Hook directory override accepted: hook assemblies execute local extension code from this trusted directory.",
                category: "hook_directory_override_accepted"));
            trustOverrides.Add(new ExtensionTrustOverride(
                "hook_directory_override",
                HooksDirectoryEnvironmentVariable,
                DiagnosticSanitizer.ForPath(overridePath),
                DiagnosticSanitizer.ForPath(fullPath),
                "Hook directory override accepted by environment; hook assemblies execute local extension code from this trusted directory."));
        }

        return new HookDirectoryResolution(fullPath, diagnostics, trustOverrides);
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
        IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics,
        IReadOnlyList<ExtensionTrustOverride> TrustOverrides);

    private sealed record HookBudgetResolution<T>(T Value, PostExtractionHookDiagnostic? Diagnostic);
}
