using System.Reflection;
using System.Runtime.Loader;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;

    internal ExtensionAssemblyLoadContext(string name, string mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        resolver = new AssemblyDependencyResolver(Path.GetFullPath(mainAssemblyPath));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var sharedAssembly = ResolveDefaultAssembly(assemblyName);
        if (sharedAssembly != null)
            return sharedAssembly;

        var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
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
