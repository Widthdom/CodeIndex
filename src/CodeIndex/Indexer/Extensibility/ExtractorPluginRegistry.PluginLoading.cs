using System.Reflection;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static void LoadPluginAssemblies(IEnumerable<string> directories)
    {
        var pluginPaths = EnumeratePluginAssemblyPaths(directories).ToArray();
        foreach (var pluginPath in pluginPaths)
            TryLoadPlugin(pluginPath);
    }

    private static void TryLoadPlugin(string pluginPath)
    {
        var fullPath = pluginPath;
        ExtensionAssemblyLoadContext? loadContext = null;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            lock (Gate)
            {
                if (!LoadedPluginAssemblyPaths.Add(fullPath))
                    return;
            }

            if (!PluginAssemblyCandidateIsWithinBudget(fullPath))
                return;

            loadContext = new ExtensionAssemblyLoadContext(
                $"cdidx-plugin:{Path.GetFileNameWithoutExtension(fullPath)}",
                fullPath);
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            var attribute = assembly.GetCustomAttribute<CdidxPluginAttribute>();
            if (attribute == null)
            {
                RecordDiagnostic(
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
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    $"Plugin assembly skipped: API range {attribute.MinApiVersion}-{attribute.MaxApiVersion} does not include {CurrentApiVersion}.",
                    countsAsSkippedFile: true,
                    category: "incompatible_plugin_api");
                return;
            }

            lock (Gate)
            {
                pluginAssemblyCount++;
                LoadedPluginAssemblyContexts.Add(loadContext);
                loadContext = null;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: false, IsInterface: false } && type.GetConstructor(Type.EmptyTypes) != null)
                    TryRegisterPluginType(type, fullPath);
            }
        }
        catch (Exception)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Failed to load plugin assembly.",
                countsAsSkippedFile: true,
                category: "assembly_load_failed");
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static void UnloadPluginAssemblyContexts()
    {
        foreach (var loadContext in LoadedPluginAssemblyContexts)
        {
            if (loadContext.IsCollectible)
                loadContext.Unload();
        }

        LoadedPluginAssemblyContexts.Clear();
    }

    private static bool PluginAssemblyCandidateIsWithinBudget(string fullPath)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordDiagnostic(
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
            RecordDiagnostic(
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
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: path is a directory.",
                countsAsSkippedFile: true,
                category: "plugin_path_is_directory");
            return false;
        }

        if (fileInfo.Length > MaxPluginAssemblyBytes)
        {
            RecordDiagnostic(
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

    private static void TryRegisterPluginType(Type type, string pluginPath)
    {
        try
        {
            if (typeof(ISymbolExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is ISymbolExtractor symbolExtractor)
            {
                Register(symbolExtractor);
            }

            if (typeof(IReferenceExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is IReferenceExtractor referenceExtractor)
            {
                Register(referenceExtractor);
            }
        }
        catch (Exception)
        {
            RecordDiagnostic(
                "plugin_type",
                pluginPath,
                type.FullName,
                severity: "error",
                "Failed to instantiate plugin type.",
                countsAsSkippedFile: false,
                category: "plugin_type_instantiation_failed");
        }
    }
}
