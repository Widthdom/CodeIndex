using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private void RecordDanglingFileSystemEntries(
        string dir,
        DirectoryScanState scanState,
        CancellationToken cancellationToken)
    {
        var candidateLimit = _maxDanglingFileSystemEntryScanCandidates;
        var candidateCount = 0;
        foreach (var enumeratedEntry in CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateCount++;
            if (candidateCount > candidateLimit)
            {
                var relativeDir = ToRelativePath(dir);
                scanState.Errors.Add(new ScanError(
                    relativeDir,
                    $"Dangling filesystem entry scan truncated after {candidateLimit:N0} candidate(s); additional dangling symlink diagnostics in this directory may be omitted.",
                    ScanIssueSeverity.Warning));
                return;
            }

            var entry = LongPath.RemoveWindowsPrefix(enumeratedEntry);
            if (!IsReparsePoint(entry) || ReparsePointTargetExists(entry))
                continue;

            var relativeEntry = ToRelativePath(entry);
            scanState.DanglingSymlinks.Add(relativeEntry);
            scanState.Errors.Add(new ScanError(relativeEntry, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
            scanState.ListedDirectories.Add(relativeEntry);
            scanState.FullyScannedDirectories.Add(relativeEntry);
            scanState.AttributePrunedDirectories.Add(relativeEntry);
        }
    }

    private void RecordDanglingFileSystemEntry(string entry, DirectoryScanState scanState)
    {
        var relativeEntry = ToRelativePath(entry);
        scanState.DanglingSymlinks.Add(relativeEntry);
        scanState.Errors.Add(new ScanError(relativeEntry, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
        scanState.ListedDirectories.Add(relativeEntry);
        scanState.FullyScannedDirectories.Add(relativeEntry);
        scanState.AttributePrunedDirectories.Add(relativeEntry);
    }

    private static bool ReparsePointTargetExists(string path)
    {
        var entryPath = LongPath.EnsureWindowsPrefix(path);
        if (Directory.Exists(entryPath))
            return true;

        try
        {
            FileInfo info = new(entryPath);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is not { Length: > 0 } targetPath)
                return false;

            var targetEntryPath = LongPath.EnsureWindowsPrefix(targetPath);
            return File.Exists(targetEntryPath) || Directory.Exists(targetEntryPath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
