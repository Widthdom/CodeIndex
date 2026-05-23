using System.Collections.Concurrent;
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

public sealed record PostExtractionHookDiagnostic(string AssemblyPath, string? TypeName, string Message);

public sealed class PostExtractionHookRunner : IDisposable
{
    private readonly List<LoadedPostExtractionHook> hooks;
    private readonly ConcurrentQueue<PostExtractionHookDiagnostic> diagnostics = new();
    private bool disposed;

    private PostExtractionHookRunner(List<LoadedPostExtractionHook> hooks)
    {
        this.hooks = hooks;
    }

    public static PostExtractionHookRunner DiscoverDefault()
        => Discover(GetDefaultHooksDirectory());

    public static PostExtractionHookRunner Discover(string? hooksDirectory)
    {
        var loaded = new List<LoadedPostExtractionHook>();
        var runner = new PostExtractionHookRunner(loaded);

        if (string.IsNullOrWhiteSpace(hooksDirectory) || !Directory.Exists(hooksDirectory))
            return runner;

        foreach (var dllPath in Directory.EnumerateFiles(hooksDirectory, "*.dll").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            Assembly assembly;
            try
            {
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

    public IReadOnlyList<PostExtractionHookInfo> Hooks => hooks.Select(hook => hook.Info).ToList();

    public IReadOnlyList<PostExtractionHookDiagnostic> Diagnostics => diagnostics.ToList();

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var hook in hooks)
        {
            try
            {
                lock (hook.Instance)
                    hook.Instance.OnSymbolsExtracted(context, symbols);
            }
            catch (Exception ex)
            {
                diagnostics.Enqueue(new PostExtractionHookDiagnostic(hook.Info.AssemblyPath, hook.Info.TypeName, $"OnSymbolsExtracted failed: {ex.Message}"));
            }
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var hook in hooks)
        {
            try
            {
                lock (hook.Instance)
                    hook.Instance.OnReferencesExtracted(context, references);
            }
            catch (Exception ex)
            {
                diagnostics.Enqueue(new PostExtractionHookDiagnostic(hook.Info.AssemblyPath, hook.Info.TypeName, $"OnReferencesExtracted failed: {ex.Message}"));
            }
        }
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
