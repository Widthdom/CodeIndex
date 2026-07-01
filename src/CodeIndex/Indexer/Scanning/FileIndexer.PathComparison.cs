namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static bool PathsEqual(string left, string right)
        => CodeIndex.Cli.PathCasing.PathsEqual(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right));

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = NormalizePathForComparison(candidateParent);
        var normalizedChild = NormalizePathForComparison(candidateChild);
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return Path.TrimEndingDirectorySeparator(fullPath);

        var remaining = Path.GetRelativePath(root, fullPath);
        if (remaining == "." || remaining.Length == 0)
            return Path.TrimEndingDirectorySeparator(fullPath);

        var current = root;
        foreach (var segment in remaining.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            current = ResolvePathComparisonSegment(current);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static string ResolvePathComparisonSegment(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            var target = info?.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } resolvedPath)
                return resolvedPath;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fullPath;
    }

    private string ToRelativePath(string absolutePath)
    {
        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }
}
