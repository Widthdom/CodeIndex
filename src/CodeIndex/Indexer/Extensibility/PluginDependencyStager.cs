using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Indexer.Extensibility;

internal static class PluginDependencyStager
{
    internal const int MaxManagedDependencies = 64;
    internal const long MaxManagedDependencyBytes = 256L * 1024 * 1024;

    internal static bool TryStageManagedDependencies(
        string sourceDirectory,
        ExecutableExtensionStagingHandle mainAssembly,
        long maxDependencyFileBytes,
        out string stagedFingerprint,
        out ExecutableExtensionBoundaryFailure failure,
        bool requireManagedMainMetadata = true)
    {
        stagedFingerprint = mainAssembly.Fingerprint;
        failure = default;
        var queuedNames = new Queue<string>();
        var visitedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagedIdentities = new List<string> { $"{Path.GetFileName(mainAssembly.StagedPath)}:{mainAssembly.Fingerprint}" };
        if (!EnqueueReferences(mainAssembly.StagedPath, queuedNames, out failure))
        {
            if (requireManagedMainMetadata)
                return false;
            failure = default;
            return true;
        }

        var dependencyCount = 0;
        long dependencyBytes = 0;
        while (queuedNames.TryDequeue(out var assemblyName))
        {
            if (!visitedNames.Add(assemblyName) || !IsSafeSimpleAssemblyName(assemblyName))
                continue;

            var sourcePath = Path.Combine(sourceDirectory, assemblyName + ".dll");
            if (!File.Exists(sourcePath))
                continue;

            dependencyCount++;
            if (dependencyCount > MaxManagedDependencies)
            {
                failure = new(
                    "plugin_dependency_limit_exceeded",
                    $"Plugin dependencies rejected: more than {MaxManagedDependencies} managed sibling assemblies were required.");
                return false;
            }

            ExecutableExtensionStagingHandle? dependency = null;
            try
            {
                if (!ExecutableExtensionBoundary.TryStageFile(
                        sourceDirectory,
                        sourcePath,
                        maxDependencyFileBytes,
                        out dependency,
                        out failure))
                {
                    return false;
                }

                var length = new FileInfo(dependency!.StagedPath).Length;
                dependencyBytes = checked(dependencyBytes + length);
                if (dependencyBytes > MaxManagedDependencyBytes)
                {
                    failure = new(
                        "plugin_dependency_bytes_exceeded",
                        $"Plugin dependencies rejected: managed sibling assemblies exceed {MaxManagedDependencyBytes} bytes in total.");
                    return false;
                }

                var destinationPath = Path.Combine(
                    mainAssembly.StagingDirectory,
                    Path.GetFileName(dependency.StagedPath));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        mainAssembly.StagingDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(
                        dependency.StagingDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                File.Move(dependency.StagedPath, destinationPath);
                stagedIdentities.Add($"{Path.GetFileName(destinationPath)}:{dependency.Fingerprint}");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead);
                    File.SetUnixFileMode(
                        mainAssembly.StagingDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
                else
                {
                    File.SetAttributes(destinationPath, FileAttributes.ReadOnly);
                }

                if (!EnqueueReferences(destinationPath, queuedNames, out failure))
                    return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
            {
                failure = new(
                    "plugin_dependency_staging_failed",
                    $"Plugin dependency staging failed ({ex.GetType().Name}).");
                return false;
            }
            finally
            {
                dependency?.Dispose();
            }
        }

        stagedIdentities.Sort(StringComparer.OrdinalIgnoreCase);
        stagedFingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', stagedIdentities))))
            .ToLowerInvariant();
        return true;
    }

    private static bool EnqueueReferences(
        string assemblyPath,
        Queue<string> queue,
        out ExecutableExtensionBoundaryFailure failure)
    {
        failure = default;
        if (!PluginMetadataInspector.TryReadAssemblyReferences(assemblyPath, out var references, out var error))
        {
            failure = new("plugin_dependency_metadata_invalid", error);
            return false;
        }

        foreach (var reference in references)
            queue.Enqueue(reference);
        return true;
    }

    private static bool IsSafeSimpleAssemblyName(string assemblyName)
        => assemblyName.Length <= 256
           && assemblyName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
           && assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
