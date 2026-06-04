using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
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
    long? DurationMs = null);

public sealed class PostExtractionHookRunner : IDisposable
{
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

    public static PostExtractionHookRunner DiscoverDefault()
        => Discover(GetDefaultHooksDirectory());

    public static PostExtractionHookRunner Discover(string? hooksDirectory)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var runner = new PostExtractionHookRunner(loaded, ResolveCallbackBudget());

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

                var loadContext = new AssemblyLoadContext($"cdidx-hook:{Path.GetFileNameWithoutExtension(dllPath)}", isCollectible: true);
                assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            }
            catch (Exception ex)
            {
                runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(dllPath, null, $"Failed to load hook assembly: {ex.Message}"));
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(dllPath, null, $"Failed to inspect hook assembly: {ex.Message}"));
                continue;
            }

            foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IPostExtractionHook).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is not IPostExtractionHook hook)
                        continue;

                    loaded.Add(new LoadedPostExtractionHook(
                        hook,
                        new PostExtractionHookInfo(type.Name, Path.GetFullPath(dllPath), type.FullName ?? type.Name),
                        AssemblyLoadContext.GetLoadContext(type.Assembly)));
                }
                catch (Exception ex)
                {
                    runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(dllPath, type.FullName, $"Failed to instantiate hook: {ex.Message}"));
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
                runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                    hooksDirectory,
                    null,
                    $"Hook discovery skipped remaining assemblies after the {discoveryLimit} DLL candidate limit."));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                hooksDirectory,
                null,
                $"Failed to enumerate hook directory: {ex.Message}"));
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
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                dllPath,
                null,
                $"Hook assembly skipped: could not inspect file ({ex.Message})."));
            return false;
        }

        if (!fileInfo.Exists)
        {
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                dllPath,
                null,
                "Hook assembly skipped: file does not exist."));
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                dllPath,
                null,
                "Hook assembly skipped: path is a directory."));
            return false;
        }

        if (fileInfo.Length > maxAssemblyBytes)
        {
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                dllPath,
                null,
                $"Hook assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {maxAssemblyBytes})."));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runner.diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                hooksDirectory,
                null,
                $"Failed to enumerate hook directory: {ex.Message}"));
            return false;
        }
    }

    public IReadOnlyList<PostExtractionHookInfo> Hooks => hooks.Select(hook => hook.Info).ToList();

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
                    nameof(IPostExtractionHook.OnSymbolsExtracted),
                    () => hook.Instance.OnSymbolsExtracted(context, workingSymbols)))
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
                    nameof(IPostExtractionHook.OnReferencesExtracted),
                    () => hook.Instance.OnReferencesExtracted(context, workingReferences)))
            {
                ReplaceList(references, workingReferences);
            }
        }
    }

    private bool InvokeHookWithBudget(LoadedPostExtractionHook hook, string callback, Action invoke)
    {
        if (disabledHooks.ContainsKey(hook.Info.TypeName))
            return false;

        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        var task = Task.Run(() =>
        {
            try
            {
                lock (hook.Instance)
                    invoke();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (!task.Wait(callbackBudget))
        {
            stopwatch.Stop();
            var timeoutDurationMs = Math.Max(
                stopwatch.ElapsedMilliseconds,
                (long)Math.Ceiling(callbackBudget.TotalMilliseconds));
            disabledHooks.TryAdd(hook.Info.TypeName, 0);
            diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                $"{callback} exceeded the {callbackBudget.TotalMilliseconds:0} ms callback budget; hook disabled for this index run.",
                callback,
                timeoutDurationMs));
            return false;
        }

        stopwatch.Stop();
        if (failure != null)
        {
            diagnostics.Enqueue(new PostExtractionHookDiagnostic(
                hook.Info.AssemblyPath,
                hook.Info.TypeName,
                $"{callback} failed: {failure.Message}",
                callback,
                stopwatch.ElapsedMilliseconds));
        }

        return true;
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

    private static string? GetDefaultHooksDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("CDIDX_HOOKS_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".config", "cdidx", "hooks");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
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
        IPostExtractionHook Instance,
        PostExtractionHookInfo Info,
        AssemblyLoadContext? LoadContext);
}
