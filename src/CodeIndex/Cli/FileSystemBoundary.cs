using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal enum FileSystemBoundaryProbeStatus
{
    Found,
    Missing,
    PermissionDenied,
    IoError,
    InvalidPath,
}

internal readonly record struct DirectoryCleanupBoundaryOptions(
    string ExpectedNamePrefix,
    string OutsideRootReason,
    string PrefixMismatchReason,
    string UnsafeDirectoryReason,
    string NotDirectoryReason = "target is not a directory",
    string InvalidPathReason = "target path is invalid");

internal static class FileSystemBoundary
{
    internal static string NormalizeDirectoryPath(string path)
        => PathCasing.NormalizeBoundaryPath(path);

    internal static bool IsSameOrDescendant(string parent, string child)
        => PathCasing.IsPathEqualOrParent(
            NormalizeDirectoryPath(parent),
            NormalizeDirectoryPath(child));

    internal static bool IsStrictDescendant(string parent, string child)
    {
        var normalizedParent = NormalizeDirectoryPath(parent);
        var normalizedChild = NormalizeDirectoryPath(child);
        return !string.Equals(normalizedParent, normalizedChild, PathCasing.ComparisonFor(normalizedParent))
            && PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    internal static bool IsSymlinkOrReparsePoint(FileAttributes attributes)
        => (attributes & FileAttributes.ReparsePoint) != 0;

    internal static bool IsDevice(FileAttributes attributes)
        => (attributes & FileAttributes.Device) != 0;

    internal static bool IsSymlinkOrReparsePoint(FileSystemInfo info)
        => IsSymlinkOrReparsePoint(info.Attributes) || !string.IsNullOrEmpty(info.LinkTarget);

    internal static bool IsSymlinkReparsePointOrDevice(FileSystemInfo info)
        => IsSymlinkOrReparsePoint(info) || IsDevice(info.Attributes);

    internal static FileSystemBoundaryProbeStatus TryGetAttributes(string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
            return FileSystemBoundaryProbeStatus.Found;
        }
        catch (FileNotFoundException)
        {
            return FileSystemBoundaryProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileSystemBoundaryProbeStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FileSystemBoundaryProbeStatus.PermissionDenied;
        }
        catch (IOException)
        {
            return FileSystemBoundaryProbeStatus.IoError;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return FileSystemBoundaryProbeStatus.InvalidPath;
        }
    }

    internal static string ClassifyProbeFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "permission_denied",
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            ArgumentException or PathTooLongException => "invalid_path",
            NotSupportedException => "not_supported",
            IOException => "io_error",
            _ => "operation_failed",
        };

    internal static bool TryValidateDirectoryCleanupTarget(
        string path,
        string safeRoot,
        DirectoryCleanupBoundaryOptions options,
        out string fullPath,
        out string failureReason)
    {
        fullPath = string.Empty;
        failureReason = string.Empty;
        try
        {
            fullPath = NormalizeDirectoryPath(path);
            var normalizedRoot = NormalizeDirectoryPath(safeRoot);
            if (string.Equals(fullPath, normalizedRoot, PathCasing.ComparisonFor(normalizedRoot))
                || !PathCasing.IsPathEqualOrParent(normalizedRoot, fullPath))
            {
                failureReason = options.OutsideRootReason;
                return false;
            }

            if (!Path.GetFileName(fullPath).StartsWith(options.ExpectedNamePrefix, StringComparison.Ordinal))
            {
                failureReason = options.PrefixMismatchReason;
                return false;
            }

            var longPath = LongPath.EnsureWindowsPrefix(fullPath);
            if (Directory.Exists(longPath))
            {
                var directoryInfo = new DirectoryInfo(fullPath);
                directoryInfo.Refresh();
                if (IsSymlinkReparsePointOrDevice(directoryInfo))
                {
                    failureReason = options.UnsafeDirectoryReason;
                    return false;
                }
            }
            else if (File.Exists(longPath))
            {
                failureReason = options.NotDirectoryReason;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or PathTooLongException or CodeIndexException)
        {
            failureReason = options.InvalidPathReason;
            return false;
        }
    }
}
