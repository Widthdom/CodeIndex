using CodeIndex.Cli;

namespace CodeIndex.Mcp;

/// <summary>
/// Central MCP filesystem boundary checks for paths that may reach local IO.
/// ローカル IO に届き得る MCP path の境界検証を集約する。
/// </summary>
internal static class McpPathBoundary
{
    internal static bool TryValidateWorkspaceRelativePath(string value, int maxLength, string propertyName, out string? error)
    {
        if (value.Length > maxLength)
        {
            error = $"Parameter \"{propertyName}\" must be no longer than {maxLength} characters.";
            return false;
        }

        var normalized = value.Replace("\\", "/", StringComparison.Ordinal);
        if (value.IndexOf("\0", StringComparison.Ordinal) >= 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || PathUriNormalizer.HasWindowsDrivePrefix(normalized)
            || normalized.Split(new[] { '/' }, StringSplitOptions.None).Any(segment => segment == ".."))
        {
            error = $"Parameter \"{propertyName}\" must be workspace-relative and must not contain NUL bytes or `..` path traversal segments.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool IsPathWithinDirectory(string parentPath, string childPath)
    {
        var parent = ResolveExistingDirectoryPath(parentPath);
        var child = ResolveExistingDirectoryPath(childPath);

        return FileSystemBoundary.IsSameOrDescendant(parent, child);
    }

    internal static string? TryResolveRootPath(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        if (Uri.TryCreate(root, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                return null;
            return PathUriNormalizer.TryNormalizeFileUriPath(root, out var normalized, out _)
                ? FileSystemBoundary.NormalizeDirectoryPath(normalized)
                : null;
        }
        try
        {
            return FileSystemBoundary.NormalizeDirectoryPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or CodeIndexException)
        {
            return null;
        }
    }

    private static string ResolveExistingDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return fullPath;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return fullPath;

        var current = root;
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
            return fullPath;

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            try
            {
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    current = target.FullName;
            }
            catch (IOException)
            {
                return Path.GetFullPath(current);
            }
            catch (UnauthorizedAccessException)
            {
                return Path.GetFullPath(current);
            }
        }

        return Path.GetFullPath(current);
    }
}
