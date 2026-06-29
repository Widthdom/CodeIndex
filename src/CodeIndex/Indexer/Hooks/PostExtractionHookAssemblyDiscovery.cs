using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookAssemblyDiscovery
{
    internal static bool DirectoryIsSupported(
        string hooksDirectory,
        Action<PostExtractionHookDiagnostic> reportDiagnostic)
    {
        DirectoryInfo directoryInfo;
        try
        {
            directoryInfo = new DirectoryInfo(hooksDirectory);
            directoryInfo.Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                hooksDirectory,
                null,
                "Hook directory skipped: could not inspect directory.",
                category: "hook_directory_inspection_failed"));
            return false;
        }

        if (!directoryInfo.Exists)
            return false;

        if (FileSystemBoundary.IsSymlinkOrReparsePoint(directoryInfo))
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                hooksDirectory,
                null,
                "Hook directory skipped: symbolic links and reparse points are not supported.",
                category: "hook_directory_reparse_point"));
            return false;
        }

        return true;
    }

    internal static IReadOnlyList<string> EnumerateAssemblyPaths(
        string hooksDirectory,
        int discoveryLimit,
        Action<PostExtractionHookDiagnostic> reportDiagnostic)
    {
        using var enumerator = TryEnumerateHookFiles(hooksDirectory, reportDiagnostic);
        if (enumerator == null)
            return [];

        var candidates = new List<string>(Math.Min(discoveryLimit, 128));
        while (TryMoveNextHookFile(hooksDirectory, enumerator, reportDiagnostic, out var dllPath))
        {
            if (candidates.Count >= discoveryLimit)
            {
                reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                    hooksDirectory,
                    null,
                    $"Hook discovery skipped remaining assemblies after the {discoveryLimit} DLL candidate limit.",
                    category: "hook_candidate_limit_exceeded"));
                break;
            }

            candidates.Add(dllPath);
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool CandidateIsWithinBudget(
        string dllPath,
        long maxAssemblyBytes,
        Action<PostExtractionHookDiagnostic> reportDiagnostic)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(dllPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                dllPath,
                null,
                "Hook assembly skipped: could not inspect file.",
                category: "hook_file_inspection_failed"));
            return false;
        }

        if (!fileInfo.Exists)
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                dllPath,
                null,
                "Hook assembly skipped: file does not exist.",
                category: "hook_file_missing"));
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                dllPath,
                null,
                "Hook assembly skipped: path is a directory.",
                category: "hook_path_is_directory"));
            return false;
        }

        if (FileSystemBoundary.IsSymlinkOrReparsePoint(fileInfo))
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                dllPath,
                null,
                "Hook assembly skipped: symbolic links and reparse points are not supported.",
                category: "hook_reparse_point"));
            return false;
        }

        if (fileInfo.Length > maxAssemblyBytes)
        {
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                dllPath,
                null,
                $"Hook assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {maxAssemblyBytes}).",
                category: "hook_file_too_large"));
            return false;
        }

        return true;
    }

    internal static bool TypesAreWithinBudget(
        string dllPath,
        IReadOnlyCollection<Type> types,
        int limit,
        Action<PostExtractionHookDiagnostic> reportDiagnostic)
    {
        if (types.Count <= limit)
            return true;

        reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
            dllPath,
            null,
            $"Hook assembly skipped: too many loadable types ({types.Count}; maximum {limit}).",
            category: "hook_type_limit_exceeded"));
        return false;
    }

    private static IEnumerator<string>? TryEnumerateHookFiles(
        string hooksDirectory,
        Action<PostExtractionHookDiagnostic> reportDiagnostic)
    {
        try
        {
            return CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(hooksDirectory, "*.dll").GetEnumerator();
        }
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "hook",
                "Hook directory",
                ex);
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                hooksDirectory,
                null,
                $"Hook directory skipped: {diagnostic.Message}.",
                category: diagnostic.Category));
            return null;
        }
    }

    private static bool TryMoveNextHookFile(
        string hooksDirectory,
        IEnumerator<string> enumerator,
        Action<PostExtractionHookDiagnostic> reportDiagnostic,
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
        catch (Exception ex) when (ExtensionDiscoveryDiagnosticClassifier.IsDiscoveryException(ex))
        {
            var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
                "hook",
                "Hook directory",
                ex);
            reportDiagnostic(PostExtractionHookDiagnosticFactory.Create(
                hooksDirectory,
                null,
                $"Hook directory skipped: {diagnostic.Message}.",
                category: diagnostic.Category));
            return false;
        }
    }
}
