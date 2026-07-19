using System.Reflection;
using System.Runtime.Loader;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    private readonly string assemblyDirectory;

    internal ExtensionAssemblyLoadContext(string name, string mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        var fullMainAssemblyPath = Path.GetFullPath(mainAssemblyPath);
        resolver = new AssemblyDependencyResolver(fullMainAssemblyPath);
        assemblyDirectory = Path.GetDirectoryName(fullMainAssemblyPath) ?? string.Empty;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var sharedAssembly = ResolveDefaultAssembly(assemblyName);
        if (sharedAssembly != null)
            return sharedAssembly;

        var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName)
                           ?? ResolveStagedSiblingAssembly(assemblyName);
        return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    private string? ResolveStagedSiblingAssembly(AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name) || string.IsNullOrEmpty(assemblyDirectory))
            return null;

        var candidate = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }

    private static Assembly? ResolveDefaultAssembly(AssemblyName assemblyName)
    {
        foreach (var assembly in Default.Assemblies)
        {
            AssemblyName defaultName;
            try
            {
                defaultName = assembly.GetName();
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (AssemblyName.ReferenceMatchesDefinition(defaultName, assemblyName))
                return assembly;
        }

        return null;
    }
}
