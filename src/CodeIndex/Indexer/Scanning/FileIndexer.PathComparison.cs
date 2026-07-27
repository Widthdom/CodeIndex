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

    private static bool IsLexicalPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateParent));
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateChild));
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    internal static string ResolveFileReadPath(string path)
        => NormalizePathForComparison(path);

    internal static bool FileReadPathsEqual(string left, string right)
        => CodeIndex.Cli.PathCasing.PathsEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return Path.TrimEndingDirectorySeparator(fullPath);

        if (fullPath.Length <= root.Length)
            return Path.TrimEndingDirectorySeparator(fullPath);

        var current = root;
        var remaining = fullPath.AsSpan(root.Length);
        while (!remaining.IsEmpty)
        {
            var separatorIndex = remaining.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segment = separatorIndex >= 0 ? remaining[..separatorIndex] : remaining;
            if (segment.Length != 0 && !IsCurrentDirectorySegment(segment))
            {
                current = Path.Join(current.AsSpan(), segment);
                current = ResolvePathComparisonSegment(current);
            }

            if (separatorIndex < 0)
                break;
            remaining = remaining[(separatorIndex + 1)..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsCurrentDirectorySegment(ReadOnlySpan<char> segment)
        => segment.Length == 1 && segment[0] == '.';

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
        if (TryGetRelativePathFromProjectRootPrefix(absolutePath, out var fastRelativePath))
            return fastRelativePath;

        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }

    private static string CreateProjectRootRelativePrefix(string projectRoot)
        => Path.EndsInDirectorySeparator(projectRoot)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;

    private bool TryGetRelativePathFromProjectRootPrefix(string absolutePath, out string relativePath)
    {
        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(absolutePath, _projectRoot, comparison))
        {
            relativePath = string.Empty;
            return true;
        }

        if (absolutePath.StartsWith(_projectRootRelativePrefix, comparison))
        {
            relativePath = NormalizePathSeparators(absolutePath[_projectRootRelativePrefix.Length..]);
            return true;
        }

        relativePath = string.Empty;
        return false;
    }
}
