using System.Reflection;
using System.Runtime.Loader;
using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    internal static Action? WorkspacePluginLoadedBeforeCommitForTesting { get; set; }

    private static void LoadPluginAssemblies(
        IEnumerable<string> directories,
        PatternWorkspaceState? workspaceState = null)
    {
        foreach (var pluginPath in EnumeratePluginAssemblyPaths(directories, workspaceState))
            TryLoadPlugin(pluginPath, workspaceState);
    }

    private static void TryLoadPlugin(string pluginPath, PatternWorkspaceState? workspaceState = null)
    {
        var fullPath = pluginPath;
        ExtensionAssemblyLoadContext? loadContext = null;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            if (!TryMarkPluginAssemblyPathLoaded(workspaceState, fullPath))
            {
                return;
            }

            if (!PluginAssemblyCandidateIsWithinBudget(workspaceState, fullPath))
                return;

            loadContext = new ExtensionAssemblyLoadContext(
                $"cdidx-plugin:{Path.GetFileNameWithoutExtension(fullPath)}",
                fullPath);
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            var attribute = assembly.GetCustomAttribute<CdidxPluginAttribute>();
            if (attribute == null)
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    "Plugin assembly skipped: missing CdidxPluginAttribute.",
                    countsAsSkippedFile: true,
                    category: "missing_plugin_attribute");
                return;
            }

            if (attribute.MinApiVersion > CurrentApiVersion
                || attribute.MaxApiVersion < CurrentApiVersion)
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    $"Plugin assembly skipped: API range {attribute.MinApiVersion}-{attribute.MaxApiVersion} does not include {CurrentApiVersion}.",
                    countsAsSkippedFile: true,
                    category: "incompatible_plugin_api");
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyTypeLoad("Plugin assembly type inspection", ex);
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "error",
                    diagnostic.Message,
                    countsAsSkippedFile: true,
                    category: diagnostic.Category);
                return;
            }

            if (!PluginAssemblyTypesAreWithinBudget(workspaceState, fullPath, types))
                return;

            if (workspaceState != null)
                WorkspacePluginLoadedBeforeCommitForTesting?.Invoke();

            if (workspaceState == null)
            {
                lock (Gate)
                {
                    pluginAssemblyCount++;
                    LoadedPluginAssemblyContexts.Add(loadContext);
                    loadContext = null;
                }
            }
            else
            {
                lock (workspaceState.Gate)
                {
                    if (workspaceState.Retired)
                        return;

                    workspaceState.PluginAssemblyCount++;
                    workspaceState.PluginLoadContexts.Add(loadContext);
                    workspaceState.PublishSnapshot();
                    loadContext = null;
                }
            }

            foreach (var type in types)
            {
                if (type is { IsAbstract: false, IsInterface: false } && type.GetConstructor(Type.EmptyTypes) != null)
                    TryRegisterPluginType(type, fullPath, workspaceState);
            }
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad("Plugin assembly load", ex);
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                diagnostic.Message,
                countsAsSkippedFile: true,
                category: diagnostic.Category);
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static void UnloadPluginAssemblyContexts()
        => UnloadPluginAssemblyContexts(LoadedPluginAssemblyContexts);

    private static void UnloadPluginAssemblyContexts(List<AssemblyLoadContext> loadContexts)
    {
        foreach (var loadContext in loadContexts)
        {
            if (loadContext.IsCollectible)
                loadContext.Unload();
        }

        loadContexts.Clear();
    }

    private static bool PluginAssemblyCandidateIsWithinBudget(PatternWorkspaceState? workspaceState, string fullPath)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: could not inspect file.",
                countsAsSkippedFile: true,
                category: "plugin_file_inspection_failed");
            return false;
        }

        if (!fileInfo.Exists)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: file does not exist.",
                countsAsSkippedFile: true,
                category: "plugin_file_missing");
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: path is a directory.",
                countsAsSkippedFile: true,
                category: "plugin_path_is_directory");
            return false;
        }

        if (FileSystemBoundary.IsSymlinkOrReparsePoint(fileInfo))
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: symbolic links and reparse points are not supported.",
                countsAsSkippedFile: true,
                category: "plugin_reparse_point");
            return false;
        }

        if (fileInfo.Length > MaxPluginAssemblyBytes)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "skipped",
                $"Plugin assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {MaxPluginAssemblyBytes}).",
                countsAsSkippedFile: true,
                category: "plugin_file_too_large");
            return false;
        }

        return true;
    }

    private static bool PluginAssemblyTypesAreWithinBudget(
        PatternWorkspaceState? workspaceState,
        string fullPath,
        IReadOnlyCollection<Type> types)
    {
        var limit = ResolveTypeInspectionLimit();
        if (types.Count <= limit)
            return true;

        RecordPluginDiagnostic(
            workspaceState,
            "plugin",
            fullPath,
            typeName: null,
            severity: "skipped",
            $"Plugin assembly skipped: too many loadable types ({types.Count}; maximum {limit}).",
            countsAsSkippedFile: true,
            category: "plugin_type_limit_exceeded");
        return false;
    }

    private static void TryRegisterPluginType(
        Type type,
        string pluginPath,
        PatternWorkspaceState? workspaceState)
    {
        var supportsSymbolExtraction = typeof(ISymbolExtractor).IsAssignableFrom(type);
        var supportsReferenceExtraction = typeof(IReferenceExtractor).IsAssignableFrom(type);
        if (!supportsSymbolExtraction && !supportsReferenceExtraction)
            return;

        try
        {
            var instance = Activator.CreateInstance(type);
            if (workspaceState == null)
            {
                if (supportsSymbolExtraction && instance is ISymbolExtractor symbolExtractor)
                    Register(symbolExtractor);

                if (supportsReferenceExtraction && instance is IReferenceExtractor referenceExtractor)
                    Register(referenceExtractor);
            }
            else
            {
                lock (workspaceState.Gate)
                {
                    if (workspaceState.Retired)
                        return;

                    if (supportsSymbolExtraction && instance is ISymbolExtractor symbolExtractor)
                        workspaceState.WorkspaceSymbolExtractors[NormalizePluginLanguage(symbolExtractor.Language)] = symbolExtractor;

                    if (supportsReferenceExtraction && instance is IReferenceExtractor referenceExtractor)
                        workspaceState.WorkspaceReferenceExtractors[NormalizePluginLanguage(referenceExtractor.Language)] = referenceExtractor;

                    workspaceState.PublishSnapshot();
                }
            }
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyConstructorFailure("Plugin type constructor", ex);
            RecordPluginDiagnostic(
                workspaceState,
                "plugin_type",
                pluginPath,
                type.FullName,
                severity: "error",
                diagnostic.Message,
                countsAsSkippedFile: false,
                category: diagnostic.Category);
        }
    }

    private static bool TryMarkPluginAssemblyPathLoaded(PatternWorkspaceState? workspaceState, string fullPath)
    {
        if (workspaceState == null)
        {
            lock (Gate)
                return TryMarkPluginAssemblyPathLoaded(fullPath);
        }

        lock (workspaceState.Gate)
        {
            if (workspaceState.Retired)
                return false;

            if (workspaceState.LoadedPluginPaths.Any(path =>
                    string.Equals(path, fullPath, PathCasing.ComparisonFor(fullPath))))
            {
                return false;
            }

            workspaceState.LoadedPluginPaths.Add(fullPath);
            return true;
        }
    }

    private static void RecordPluginDiagnostic(
        PatternWorkspaceState? workspaceState,
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile,
        string category)
    {
        if (workspaceState == null)
        {
            RecordDiagnostic(kind, path, typeName, severity, message, countsAsSkippedFile, category);
            return;
        }

        RecordPatternDiagnostic(workspaceState, kind, path, typeName, severity, message, countsAsSkippedFile, category);
    }
}
