using CodeIndex.Cli;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static bool ProbeFileSystemIgnoreCase(string projectRoot)
    {
        var normalizedRoot = projectRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(projectRoot);
            if (FileSystemIgnoreCaseProbeForTesting is { } probeOverride)
                return probeOverride(normalizedRoot);

            var existingDirectoryIgnoreCase = ProbeExistingDirectoryIgnoreCase(normalizedRoot);
            if (existingDirectoryIgnoreCase.HasValue)
                return existingDirectoryIgnoreCase.Value;

            return CaseSensitivityProbeDirectory.ProbeIgnoreCase(normalizedRoot, "case-probe-");
        }
        catch (CaseSensitivityProbeException)
        {
            throw;
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            throw new CaseSensitivityProbeException(
                "Failed to determine filesystem case sensitivity.",
                normalizedRoot,
                ex);
        }
    }

    private static bool IsCaseSensitivityProbeFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static bool? ProbeExistingDirectoryIgnoreCase(string directory)
    {
        try
        {
            var normalizedDirectory = NormalizeDirectoryCaseProbePath(directory);
            return CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(normalizedDirectory);
        }
        catch
        {
            return null;
        }
    }

    private static bool? ProbeExistingDirectoryIgnoreCase(
        string directory,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalizedDirectory = NormalizeDirectoryCaseProbePath(directory);
            return CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(
                normalizedDirectory,
                entries,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDirectoryCaseProbePath(string directory)
        => Path.IsPathFullyQualified(directory) ? directory : Path.GetFullPath(directory);

    private bool DirectoryUsesIgnoreCase(string directory)
        => DirectoryUsesIgnoreCase(directory, entries: null, CancellationToken.None);

    private bool DirectoryUsesIgnoreCase(
        string directory,
        IReadOnlyList<string>? entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizeDirectoryCaseProbePath(directory);
        if (_directoryIgnoreCaseCache.TryGetValue(fullPath, out var ignoreCase))
            return ignoreCase;

        var probeResult = _usesDefaultDirectoryIgnoreCaseProbe && entries is not null
            ? ProbeExistingDirectoryIgnoreCase(fullPath, entries, cancellationToken)
            : _directoryIgnoreCaseProbe(fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        ignoreCase = probeResult ?? _ignoreCase;
        _directoryIgnoreCaseCache[fullPath] = ignoreCase;
        return ignoreCase;
    }
}
