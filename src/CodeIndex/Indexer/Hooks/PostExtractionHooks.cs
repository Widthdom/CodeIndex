using System.Collections.Concurrent;
using System.Diagnostics;
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

public sealed record PostExtractionHookInfo(
    string Name,
    string AssemblyPath,
    string TypeName,
    [property: JsonPropertyName("id")] string Id = "");

public sealed record PostExtractionHookDiagnostic(
    string AssemblyPath,
    string? TypeName,
    string Message,
    string? Callback = null,
    long? DurationMs = null,
    [property: JsonPropertyName("category")] string Category = "unspecified",
    [property: JsonPropertyName("hook_id")] string? HookId = null);

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
    internal const string HookLoadContextLifecycle = "isolated_worker_process_no_parent_load_context";

    private readonly List<LoadedPostExtractionHook> hooks;
    private readonly List<ExecutableExtensionStagingHandle> stagingHandles;
    private readonly ConcurrentQueue<PostExtractionHookDiagnostic> diagnostics = new();
    private readonly ConcurrentDictionary<string, byte> disabledHooks = new(StringComparer.Ordinal);
    private readonly TimeSpan callbackBudget;
    private readonly int? maxSymbolCount;
    private readonly int? maxReferenceCount;
    private int sawCSharpStaticInterfaceSourceContract;
    private bool disposed;
    internal static Func<TimeSpan>? CallbackBudgetForTesting { get; set; }
    internal static Func<int>? DiscoveryLimitForTesting { get; set; }
    internal static Func<long>? DiscoveryMaxBytesForTesting { get; set; }
    internal static Func<int>? TypeInspectionLimitForTesting { get; set; }

    private PostExtractionHookRunner(
        List<LoadedPostExtractionHook> hooks,
        List<ExecutableExtensionStagingHandle> stagingHandles,
        TimeSpan callbackBudget,
        int? maxSymbolCount,
        int? maxReferenceCount)
    {
        this.hooks = hooks;
        this.stagingHandles = stagingHandles;
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
        var stagingHandles = new List<ExecutableExtensionStagingHandle>();
        var callbackBudget = ResolveCallbackBudget();
        var runner = new PostExtractionHookRunner(loaded, stagingHandles, callbackBudget.Value, null, null);
        runner.EnqueueDiagnostic(callbackBudget.Diagnostic);
        runner.EnqueueDiagnostics(initialDiagnostics);
        var discoveryLimit = ResolveDiscoveryLimit();
        runner.EnqueueDiagnostic(discoveryLimit.Diagnostic);
        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return new PostExtractionHookDiscoverySnapshot([], runner.Diagnostics, runner.CallbackBudget, initialTrustOverrides);

        if (!PostExtractionHookAssemblyDiscovery.DirectoryIsSupported(hooksDirectory, runner.EnqueueDiagnostic))
            return new PostExtractionHookDiscoverySnapshot([], runner.Diagnostics, runner.CallbackBudget, initialTrustOverrides);

        var maxAssemblyBytes = ResolveDiscoveryMaxBytes();
        runner.EnqueueDiagnostic(maxAssemblyBytes.Diagnostic);
        var hooks = new List<PostExtractionHookInfo>();
        foreach (var dllPath in PostExtractionHookAssemblyDiscovery.EnumerateAssemblyPaths(
                     hooksDirectory,
                     discoveryLimit.Value,
                     runner.EnqueueDiagnostic))
        {
            hooks.AddRange(runner.DiscoverAssemblyManifest(
                hooksDirectory,
                dllPath,
                maxAssemblyBytes.Value,
                WorkerProtocolLineLimits.MaxLineUtf8Bytes,
                retainForCallbacks: false));
        }

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
        var stagingHandles = new List<ExecutableExtensionStagingHandle>();
        var callbackBudget = ResolveCallbackBudget();
        var runner = new PostExtractionHookRunner(
            loaded,
            stagingHandles,
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
            _ = runner.DiscoverAssemblyManifest(
                hooksDirectory,
                dllPath,
                maxAssemblyBytes.Value,
                maxProtocolLineBytes,
                retainForCallbacks: true);
        }

        return runner;
    }

    private IReadOnlyList<PostExtractionHookInfo> DiscoverAssemblyManifest(
        string hooksDirectory,
        string dllPath,
        long maxAssemblyBytes,
        int maxProtocolLineBytes,
        bool retainForCallbacks)
    {
        if (!PostExtractionHookAssemblyDiscovery.CandidateIsWithinBudget(
                dllPath,
                maxAssemblyBytes,
                EnqueueDiagnostic))
        {
            return [];
        }

        ExecutableExtensionStagingHandle? staging = null;
        try
        {
            if (!ExecutableExtensionBoundary.TryStageFile(
                    hooksDirectory,
                    dllPath,
                    maxAssemblyBytes,
                    out staging,
                    out var boundaryFailure))
            {
                EnqueueDiagnostic(
                    dllPath,
                    null,
                    boundaryFailure.Message,
                    category: boundaryFailure.Category);
                return [];
            }

            if (!PluginDependencyStager.TryStageManagedDependencies(
                    hooksDirectory,
                    staging!,
                    maxAssemblyBytes,
                    out var stagedFingerprint,
                    out var dependencyFailure,
                    requireManagedMainMetadata: false))
            {
                EnqueueDiagnostic(
                    dllPath,
                    null,
                    dependencyFailure.Message,
                    category: dependencyFailure.Category);
                return [];
            }

            var discovery = PostExtractionHookDiscoveryWorkerClient.Discover(
                staging!.StagedPath,
                ResolveTypeInspectionLimit(),
                maxProtocolLineBytes);
            if (!discovery.Success || discovery.Response?.Hooks == null)
            {
                EnqueueDiagnostic(
                    dllPath,
                    null,
                    discovery.Error ?? "Hook discovery worker did not return a manifest.",
                    category: discovery.ErrorCategory ?? "hook_discovery_failed");
                return [];
            }

            var fullPath = Path.GetFullPath(dllPath);
            foreach (var diagnostic in discovery.Response.Diagnostics ?? [])
            {
                var hookId = diagnostic.TypeName == null
                    ? null
                    : PostExtractionHookIdentity.Create(fullPath, stagedFingerprint, diagnostic.TypeName);
                EnqueueDiagnostic(
                    fullPath,
                    diagnostic.TypeName,
                    diagnostic.Message,
                    category: diagnostic.Category,
                    hookId: hookId);
            }

            var infos = new List<PostExtractionHookInfo>();
            foreach (var entry in discovery.Response.Hooks)
            {
                var hookId = PostExtractionHookIdentity.Create(fullPath, stagedFingerprint, entry.TypeName);
                var info = new PostExtractionHookInfo(
                    entry.Name,
                    retainForCallbacks ? fullPath : DiagnosticSanitizer.ForPath(fullPath),
                    entry.TypeName,
                    hookId);
                infos.Add(info);
                if (retainForCallbacks)
                {
                    hooks.Add(new LoadedPostExtractionHook(
                        info,
                        new PostExtractionHookCallbackWorkerClient(
                            info with { AssemblyPath = staging.StagedPath },
                            maxProtocolLineBytes)));
                }
            }

            if (retainForCallbacks && infos.Count > 0)
            {
                stagingHandles.Add(staging);
                staging = null;
            }

            return infos;
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad(
                "Hook discovery",
                ex);
            EnqueueDiagnostic(dllPath, null, diagnostic.Message, category: diagnostic.Category);
            return [];
        }
        finally
        {
            staging?.Dispose();
        }
    }

    public IReadOnlyList<PostExtractionHookInfo> Hooks
        => hooks.Count == 0 ? [] : hooks.Select(hook => hook.Info).ToList();

    internal int ParentLoadContextCountForTests => 0;

    internal bool SawCSharpStaticInterfaceSourceContract
        => Volatile.Read(ref sawCSharpStaticInterfaceSourceContract) != 0;

    public IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics
        => diagnostics.Count == 0 ? [] : diagnostics.ToList();

    public TimeSpan CallbackBudget => callbackBudget;

    internal void ObserveCSharpStaticInterfaceSourceSymbols(
        FileContext context,
        IEnumerable<SymbolRecord> symbols)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!string.Equals(context.Language, "csharp", StringComparison.Ordinal)
            || Volatile.Read(ref sawCSharpStaticInterfaceSourceContract) != 0)
        {
            return;
        }

        if (CSharpStaticInterfacePrepass.HasCSharpStaticInterfaceContractSymbol(symbols))
        {
            Interlocked.Exchange(ref sawCSharpStaticInterfaceSourceContract, 1);
        }
    }

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols, CancellationToken cancellationToken = default)
        => OnSymbolsExtractedCore(
            context,
            symbols,
            sourceSymbolsAlreadyObserved: false,
            cancellationToken);

    internal void OnSymbolsExtractedAfterSourceObservation(
        FileContext context,
        IList<SymbolRecord> symbols,
        CancellationToken cancellationToken = default)
        => OnSymbolsExtractedCore(
            context,
            symbols,
            sourceSymbolsAlreadyObserved: true,
            cancellationToken);

    private void OnSymbolsExtractedCore(
        FileContext context,
        IList<SymbolRecord> symbols,
        bool sourceSymbolsAlreadyObserved,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Observe built-in symbols before hooks, kind filters, or row caps can remove a
        // contract member. This closes the prepass-to-extraction mutation window for the
        // durable source-evidence stamp.
        // hook・kind filter・row capより前のbuilt-in symbolを観測し、prepass後の変更も
        // durable source evidenceへ反映する。
        if (!sourceSymbolsAlreadyObserved)
            ObserveCSharpStaticInterfaceSourceSymbols(context, symbols);

        var acceptedHookMutation = false;
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
                PostExtractionHookMutationMaterializer.RefreshCSharpDeclarationMetadataAfterHookMutation(
                    context.Language,
                    symbols as IReadOnlyList<SymbolRecord> ?? symbols.ToList(),
                    workingSymbols);
                PostExtractionHookMutationMaterializer.ReplaceList(symbols, workingSymbols);
                acceptedHookMutation = true;
            }
        }

        // Hooks can rename or add records but cannot set the internal persisted identity key.
        // Re-derive it from the accepted public name after all mutations.
        // hook は record の rename/add はできるが内部の永続化 identity key は設定できないため、
        // 全 mutation 受理後の公開名から再導出する。
        if (acceptedHookMutation)
            PostExtractionHookMutationMaterializer.RefreshLanguageIdentity(context.Language, symbols);
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var acceptedHookMutation = false;
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
                acceptedHookMutation = true;
            }
        }

        if (acceptedHookMutation)
            PostExtractionHookMutationMaterializer.RefreshLanguageIdentity(context.Language, references);
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
        if (disabledHooks.ContainsKey(hook.Info.Id))
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
            disabledHooks.TryAdd(hook.Info.Id, 0);
            EnqueueDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                WorkerProcessCleanupDiagnostics.AppendToMessage(
                    $"{callback} exceeded the {callbackBudget.TotalMilliseconds:0} ms callback budget; hook disabled for this index run.",
                    result.WorkerError),
                callback,
                result.DurationMs,
                "callback_timeout",
                hook.Info.Id);
            return false;
        }

        if (!result.Success)
        {
            disabledHooks.TryAdd(hook.Info.Id, 0);
            EnqueueDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                WorkerProcessCleanupDiagnostics.AppendToMessage(
                    $"{callback} failed in isolated worker.",
                    result.WorkerError),
                callback,
                result.DurationMs,
                ClassifyWorkerFailureCategory(result.WorkerError),
                hook.Info.Id);
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
                "hook_callback_failed",
                hook.Info.Id);
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
        string category = "unspecified",
        string? hookId = null)
    {
        diagnostics.Enqueue(PostExtractionHookDiagnosticFactory.Create(assemblyPath, typeName, message, callback, durationMs, category, hookId));
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
            category: recordKind == "symbol" ? "hook_symbol_count_truncated" : "hook_reference_count_truncated",
            hookId: hook.Info.Id);
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
        hooks.Clear();

        foreach (var staging in stagingHandles)
            staging.Dispose();
        stagingHandles.Clear();
    }

    private sealed record LoadedPostExtractionHook(
        PostExtractionHookInfo Info,
        PostExtractionHookCallbackWorkerClient Worker);

    private sealed record HookBudgetResolution<T>(T Value, PostExtractionHookDiagnostic? Diagnostic);
}
