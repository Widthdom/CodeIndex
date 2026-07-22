using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    internal static string? UserPatternDirectoryOverrideForTests { get; set; }

    private static IEnumerable<string> EnumeratePluginAssemblyPaths()
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot: null));

    private static IEnumerable<string> EnumeratePluginAssemblyPaths(
        IEnumerable<string> directories,
        PatternWorkspaceState? workspaceState = null)
    {
        var totalCandidates = 0;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            if (!ExecutableExtensionBoundary.TryValidateDirectory(directory, out _, out var boundaryFailure))
            {
                ReportPluginDirectorySkipped(
                    workspaceState,
                    directory,
                    boundaryFailure.Message,
                    boundaryFailure.Category);
                continue;
            }

            using var enumerator = TryEnumeratePluginFiles(workspaceState, directory);
            if (enumerator == null)
                continue;

            var directoryCandidates = 0;
            while (TryMoveNextPluginFile(workspaceState, directory, enumerator, out var pluginPath))
            {
                if (directoryCandidates >= MaxPluginAssemblyCandidatesPerDirectory)
                {
                    ReportPluginDirectorySkipped(
                        workspaceState,
                        directory,
                        $"too many plugin assembly candidates (maximum {MaxPluginAssemblyCandidatesPerDirectory} per directory)",
                        "plugin_candidate_limit_exceeded");
                    break;
                }

                if (totalCandidates >= MaxPluginAssemblyCandidatesTotal)
                {
                    ReportPluginDirectorySkipped(
                        workspaceState,
                        directory,
                        $"too many plugin assembly candidates (maximum {MaxPluginAssemblyCandidatesTotal} total)",
                        "plugin_candidate_limit_exceeded");
                    yield break;
                }

                directoryCandidates++;
                totalCandidates++;
                yield return pluginPath;
            }
        }
    }

    private static IEnumerator<string>? TryEnumeratePluginFiles(
        PatternWorkspaceState? workspaceState,
        string directory)
    {
        try
        {
            return CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(directory, "*.dll").GetEnumerator();
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "plugin",
                "Plugin directory",
                ex);
            ReportPluginDirectorySkipped(workspaceState, directory, diagnostic.Message, diagnostic.Category);
            return null;
        }
    }

    private static bool TryMoveNextPluginFile(
        PatternWorkspaceState? workspaceState,
        string directory,
        IEnumerator<string> enumerator,
        out string pluginPath)
    {
        pluginPath = string.Empty;
        try
        {
            if (!enumerator.MoveNext())
                return false;

            pluginPath = enumerator.Current;
            return true;
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "plugin",
                "Plugin directory",
                ex);
            ReportPluginDirectorySkipped(workspaceState, directory, diagnostic.Message, diagnostic.Category);
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePluginDirectories(string? projectRoot)
    {
        if (WorkspacePluginsTrusted() && !string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (var directory in EnumerateWorkspacePluginDirectories(Path.GetFullPath(projectRoot)))
                yield return directory;
        }

        if (!string.IsNullOrWhiteSpace(UserPluginDirectoryForTesting))
        {
            yield return UserPluginDirectoryForTesting;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                yield return Path.Combine(home, ".cdidx", "plugins");
        }
    }

    private static IEnumerable<string> EnumerateWorkspacePluginDirectories(string projectRoot)
    {
        yield return Path.Combine(projectRoot, ".cdidx", "plugins");
    }

    internal static IReadOnlyList<ExtensionTrustOverride> GetAcceptedTrustOverrides(string? projectRoot)
    {
        var value = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(TrustWorkspacePluginsEnvironmentVariable);
        if (!WorkspacePluginsTrusted(value) || string.IsNullOrWhiteSpace(projectRoot))
            return [];

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(projectRoot);
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            return [];
        }

        return
        [
            new ExtensionTrustOverride(
                "workspace_plugin_directory",
                TrustWorkspacePluginsEnvironmentVariable,
                DiagnosticSanitizer.ForMessage(value),
                DiagnosticSanitizer.ForPath(Path.Combine(fullRoot, ".cdidx", "plugins")),
                "Workspace plugin discovery enabled by environment; workspace plugin DLLs execute checkout-provided code.")
        ];
    }

    private static IEnumerable<string> EnumeratePatternConfigPaths(
        PatternWorkspaceState state,
        string workspaceRoot,
        bool includeUserDirectory = true,
        Func<string, bool, bool>? directoryExists = null,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput = null)
    {
        foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                     state,
                     Path.Combine(workspaceRoot, ".cdidx", "patterns"),
                     workspaceRoot,
                     isUserConfiguration: false,
                     directoryExists,
                     observeInput))
        {
            yield return path;
        }

        if (!includeUserDirectory)
            yield break;

        foreach (var path in EnumerateUserPatternConfigPaths(state, directoryExists, observeInput))
            yield return path;
    }

    private static IEnumerable<string> EnumerateUserPatternConfigPaths(
        PatternWorkspaceState state,
        Func<string, bool, bool>? directoryExists = null,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput = null)
    {
        if (!string.IsNullOrWhiteSpace(UserPatternDirectoryOverrideForTests))
        {
            foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                         state,
                         UserPatternDirectoryOverrideForTests,
                         workspaceRoot: null,
                         isUserConfiguration: true,
                         directoryExists,
                         observeInput))
            {
                yield return path;
            }

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                         state,
                         Path.Combine(home, ".config", "cdidx", "patterns"),
                         workspaceRoot: null,
                         isUserConfiguration: true,
                         directoryExists,
                         observeInput))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumeratePatternConfigPathsFromDirectory(
        PatternWorkspaceState state,
        string directory,
        string? workspaceRoot,
        bool isUserConfiguration,
        Func<string, bool, bool>? directoryExists = null,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput = null)
    {
        if (!(directoryExists?.Invoke(directory, isUserConfiguration) ?? Directory.Exists(directory)))
            yield break;
        if (!PatternDirectoryIsSafe(state, directory, workspaceRoot, observeInput))
            yield break;

        var directoryCandidates = 0;
        foreach (var searchPattern in PatternConfigSearchPatterns)
        {
            using var enumerator = TryEnumeratePatternFiles(
                state,
                directory,
                searchPattern,
                observeInput);
            if (enumerator == null)
                continue;

            while (TryMoveNextPatternFile(
                       state,
                       directory,
                       enumerator,
                       observeInput,
                       out var path))
            {
                if (directoryCandidates >= MaxPatternConfigCandidatesPerDirectory)
                {
                    ReportPatternDirectorySkipped(
                        state,
                        directory,
                        $"too many pattern config candidates (maximum {MaxPatternConfigCandidatesPerDirectory} per directory)");
                    yield break;
                }

                directoryCandidates++;
                yield return path;
            }
        }
    }

    private static IEnumerator<string>? TryEnumeratePatternFiles(
        PatternWorkspaceState state,
        string directory,
        string searchPattern,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
    {
        try
        {
            var files = EnumeratePatternFilesForTesting?.Invoke(directory, searchPattern)
                ?? CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(directory, searchPattern);
            return files.GetEnumerator();
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "pattern",
                "Pattern directory",
                ex);
            ReportPatternDirectoryRejected(state, directory, diagnostic.Message, diagnostic.Category);
            observeInput?.Invoke(directory, null, null);
            return null;
        }
    }

    private static bool TryMoveNextPatternFile(
        PatternWorkspaceState state,
        string directory,
        IEnumerator<string> enumerator,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput,
        out string patternPath)
    {
        patternPath = string.Empty;
        try
        {
            if (!enumerator.MoveNext())
                return false;

            patternPath = enumerator.Current;
            return true;
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "pattern",
                "Pattern directory",
                ex);
            ReportPatternDirectoryRejected(state, directory, diagnostic.Message, diagnostic.Category);
            observeInput?.Invoke(directory, null, null);
            return false;
        }
    }

    private static bool PatternDirectoryIsSafe(
        PatternWorkspaceState state,
        string directory,
        string? workspaceRoot,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
    {
        if (workspaceRoot != null)
        {
            var workspaceCdidxDirectory = Path.Combine(workspaceRoot, ".cdidx");
            if (DirectoryIsSymlinkOrReparsePoint(state, workspaceCdidxDirectory, observeInput))
            {
                ReportPatternDirectoryRejected(state, workspaceCdidxDirectory, "symbolic links and reparse points are not supported");
                return false;
            }
        }

        if (DirectoryIsSymlinkOrReparsePoint(state, directory, observeInput))
        {
            ReportPatternDirectoryRejected(state, directory, "symbolic links and reparse points are not supported");
            return false;
        }

        return true;
    }

    private static bool DirectoryIsSymlinkOrReparsePoint(
        PatternWorkspaceState state,
        string directory,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
    {
        try
        {
            InspectPatternDirectoryForTesting?.Invoke(directory);
            var info = new DirectoryInfo(directory);
            return FileSystemBoundary.IsSymlinkOrReparsePoint(info);
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            ReportPatternDirectoryRejected(state, directory, "could not inspect pattern directory");
            observeInput?.Invoke(directory, null, null);
            return true;
        }
    }

    private static bool WorkspacePluginsTrusted()
        => WorkspacePluginsTrusted(global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(TrustWorkspacePluginsEnvironmentVariable));

    private static bool WorkspacePluginsTrusted(string? value)
    {
        return value != null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
