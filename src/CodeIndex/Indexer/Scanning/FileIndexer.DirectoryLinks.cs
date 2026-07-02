using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private bool ShouldSkipDirectoryLink(
        string subDir,
        List<ScanError> errors,
        HashSet<string> danglingSymlinks,
        FileAttributes? knownAttributes = null)
    {
        var isReparsePoint = knownAttributes.HasValue
            ? FileSystemBoundary.IsSymlinkOrReparsePoint(knownAttributes.Value)
            : IsReparsePoint(subDir);
        if (!isReparsePoint)
        {
            return knownAttributes.HasValue
                ? HasSkippedAttributes(knownAttributes.Value)
                : HasSkippedAttributes(subDir);
        }

        var relative = ToRelativePath(subDir);
        DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(subDir));
        FileSystemInfo? target;
        try
        {
            target = ResolveDirectoryLinkTargetForTesting != null
                ? ResolveDirectoryLinkTargetForTesting(subDir)
                : info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            target = null;
        }
        catch (DirectoryNotFoundException)
        {
            target = null;
        }
        catch (IOException)
        {
            target = null;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add(new ScanError(
                relative,
                "Skipped symlinked directory because its target could not be resolved due to permissions.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (target?.FullName is not { Length: > 0 } targetPath || !Directory.Exists(LongPath.EnsureWindowsPrefix(targetPath)))
        {
            danglingSymlinks.Add(relative);
            errors.Add(new ScanError(relative, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
            return true;
        }

        if (_symlinkPolicy == SymlinkPolicy.All)
            return false;

        if (_symlinkPolicy == SymlinkPolicy.Internal && IsPathEqualOrParent(_projectRoot, targetPath))
            return false;

        errors.Add(new ScanError(
            relative,
            $"Skipped symlinked directory outside the active symlink policy: target {FormatSymlinkPolicyTargetForDiagnostic(targetPath)}",
            ScanIssueSeverity.Warning));
        return true;
    }

    private string FormatSymlinkPolicyTargetForDiagnostic(string targetPath)
    {
        if (!IsPathEqualOrParent(_projectRoot, targetPath))
            return "<outside project root>";

        var relative = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, targetPath));
        return relative == "." ? "<project root>" : relative;
    }

    internal bool ShouldSkipDirectoryTraversal(string directory)
        => ShouldSkipDirectoryLink(
            directory,
            errors: new List<ScanError>(),
            danglingSymlinks: new HashSet<string>(StringComparer.Ordinal));

    private static string GetDirectoryTraversalIdentity(string directory, FileAttributes? knownAttributes = null)
    {
        if (knownAttributes.HasValue)
        {
            if (!FileSystemBoundary.IsSymlinkOrReparsePoint(knownAttributes.Value))
                return directory;
        }
        else if (!IsReparsePoint(directory))
        {
            return directory;
        }

        try
        {
            DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(directory));
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } targetPath)
                return targetPath;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return $"unresolved-reparse:{Path.GetFullPath(directory)}";
    }
}
