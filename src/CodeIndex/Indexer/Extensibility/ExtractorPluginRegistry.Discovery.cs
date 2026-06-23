using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static IEnumerable<string> EnumeratePluginAssemblyPaths()
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot: null));

    private static IEnumerable<string> EnumeratePluginAssemblyPaths(IEnumerable<string> directories)
    {
        var totalCandidates = 0;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            using var enumerator = TryEnumeratePluginFiles(directory);
            if (enumerator == null)
                continue;

            var directoryCandidates = 0;
            while (TryMoveNextPluginFile(directory, enumerator, out var pluginPath))
            {
                if (directoryCandidates >= MaxPluginAssemblyCandidatesPerDirectory)
                {
                    ReportPluginDirectorySkipped(
                        directory,
                        $"too many plugin assembly candidates (maximum {MaxPluginAssemblyCandidatesPerDirectory} per directory)",
                        "plugin_candidate_limit_exceeded");
                    break;
                }

                if (totalCandidates >= MaxPluginAssemblyCandidatesTotal)
                {
                    ReportPluginDirectorySkipped(
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

    private static IEnumerator<string>? TryEnumeratePluginFiles(string directory)
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
            ReportPluginDirectorySkipped(directory, diagnostic.Message, diagnostic.Category);
            return null;
        }
    }

    private static bool TryMoveNextPluginFile(string directory, IEnumerator<string> enumerator, out string pluginPath)
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
            ReportPluginDirectorySkipped(directory, diagnostic.Message, diagnostic.Category);
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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".cdidx", "plugins");
    }

    private static IEnumerable<string> EnumerateWorkspacePluginDirectories(string projectRoot)
    {
        yield return Path.Combine(projectRoot, ".cdidx", "plugins");
    }

    internal static IReadOnlyList<ExtensionTrustOverride> GetAcceptedTrustOverrides(string? projectRoot)
    {
        var value = Environment.GetEnvironmentVariable(TrustWorkspacePluginsEnvironmentVariable);
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

    private static IEnumerable<string> EnumeratePatternConfigPaths(string workspaceRoot, bool includeUserDirectory = true)
    {
        foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                     Path.Combine(workspaceRoot, ".cdidx", "patterns"),
                     workspaceRoot))
        {
            yield return path;
        }

        if (!includeUserDirectory)
            yield break;

        foreach (var path in EnumerateUserPatternConfigPaths())
            yield return path;
    }

    private static IEnumerable<string> EnumerateUserPatternConfigPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                         Path.Combine(home, ".config", "cdidx", "patterns"),
                         workspaceRoot: null))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumeratePatternConfigPathsFromDirectory(string directory, string? workspaceRoot)
    {
        if (!Directory.Exists(directory) || !PatternDirectoryIsSafe(directory, workspaceRoot))
            yield break;

        var directoryCandidates = 0;
        foreach (var searchPattern in PatternConfigSearchPatterns)
        {
            using var enumerator = TryEnumeratePatternFiles(directory, searchPattern);
            if (enumerator == null)
                continue;

            while (TryMoveNextPatternFile(directory, enumerator, out var path))
            {
                if (directoryCandidates >= MaxPatternConfigCandidatesPerDirectory)
                {
                    ReportPatternDirectorySkipped(
                        directory,
                        $"too many pattern config candidates (maximum {MaxPatternConfigCandidatesPerDirectory} per directory)");
                    yield break;
                }

                directoryCandidates++;
                yield return path;
            }
        }
    }

    private static IEnumerator<string>? TryEnumeratePatternFiles(string directory, string searchPattern)
    {
        try
        {
            return CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(directory, searchPattern).GetEnumerator();
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "pattern",
                "Pattern directory",
                ex);
            ReportPatternDirectoryRejected(directory, diagnostic.Message, diagnostic.Category);
            return null;
        }
    }

    private static bool TryMoveNextPatternFile(string directory, IEnumerator<string> enumerator, out string patternPath)
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
            ReportPatternDirectoryRejected(directory, diagnostic.Message, diagnostic.Category);
            return false;
        }
    }

    private static bool PatternDirectoryIsSafe(string directory, string? workspaceRoot)
    {
        if (workspaceRoot != null)
        {
            var workspaceCdidxDirectory = Path.Combine(workspaceRoot, ".cdidx");
            if (DirectoryIsSymlinkOrReparsePoint(workspaceCdidxDirectory))
            {
                ReportPatternDirectoryRejected(workspaceCdidxDirectory, "symbolic links and reparse points are not supported");
                return false;
            }
        }

        if (DirectoryIsSymlinkOrReparsePoint(directory))
        {
            ReportPatternDirectoryRejected(directory, "symbolic links and reparse points are not supported");
            return false;
        }

        return true;
    }

    private static bool DirectoryIsSymlinkOrReparsePoint(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return FileSystemBoundary.IsSymlinkOrReparsePoint(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportPatternDirectoryRejected(directory, "could not inspect pattern directory");
            return true;
        }
    }

    private static bool WorkspacePluginsTrusted()
        => WorkspacePluginsTrusted(Environment.GetEnvironmentVariable(TrustWorkspacePluginsEnvironmentVariable));

    private static bool WorkspacePluginsTrusted(string? value)
    {
        return value != null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
