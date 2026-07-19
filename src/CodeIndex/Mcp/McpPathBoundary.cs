using System.Buffers.Binary;
using System.Security.Cryptography;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

/// <summary>
/// Central MCP filesystem boundary checks for paths that may reach local IO.
/// ローカル IO に届き得る MCP path の境界検証を集約する。
/// </summary>
internal static class McpPathBoundary
{
    internal sealed class IndexRootAuthorization
    {
        private readonly string _requestedPath;
        private readonly FileIndexer.FileIdentity _rootIdentity;
        private readonly Func<string, bool> _isPathAuthorized;

        internal IndexRootAuthorization(
            string requestedPath,
            string canonicalPath,
            FileIndexer.FileIdentity rootIdentity,
            Func<string, bool> isPathAuthorized)
        {
            _requestedPath = requestedPath;
            CanonicalPath = canonicalPath;
            _rootIdentity = rootIdentity;
            _isPathAuthorized = isPathAuthorized;
            CheckedRootIdentity = CreateRootIdentityToken(rootIdentity);
        }

        internal string CanonicalPath { get; }
        internal string CheckedRootIdentity { get; }

        internal void EnsureAuthorizedEntry(string path)
        {
            var currentRequestedTarget = ResolveExistingDirectoryPath(_requestedPath);
            if (!PathCasing.PathsEqual(currentRequestedTarget, CanonicalPath))
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "requested_root_changed");

            if (!FileIndexer.TryGetFileIdentity(CanonicalPath, out var currentIdentity)
                || currentIdentity != _rootIdentity)
            {
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "root_identity_changed");
            }

            if (!TryResolveExistingPath(path, out var canonicalEntry))
                throw new McpIndexEntryUnavailableException();
            if (!_isPathAuthorized(canonicalEntry))
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "entry_outside_authorized_roots");
        }

        private static string CreateRootIdentityToken(FileIndexer.FileIdentity identity)
        {
            Span<byte> identityBytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(identityBytes, identity.DeviceId);
            BinaryPrimitives.WriteUInt64BigEndian(identityBytes[8..], identity.Inode);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(identityBytes, digest);
            return $"fsid:v1:{Convert.ToHexString(digest[..16]).ToLowerInvariant()}";
        }
    }

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

    internal static bool TryCaptureIndexRoot(
        string requestedPath,
        Func<string, bool> isPathAuthorized,
        out IndexRootAuthorization? authorization,
        out string? error)
    {
        authorization = null;
        if (!Directory.Exists(requestedPath))
        {
            error = "Directory not found";
            return false;
        }

        var canonicalPath = ResolveExistingDirectoryPath(requestedPath);
        if (!isPathAuthorized(canonicalPath))
        {
            error = "Resolved directory must remain within the authorized MCP roots";
            return false;
        }

        if (!FileIndexer.TryGetFileIdentity(canonicalPath, out var identity))
        {
            error = "Directory identity could not be verified";
            return false;
        }

        authorization = new IndexRootAuthorization(
            Path.GetFullPath(requestedPath),
            canonicalPath,
            identity,
            isPathAuthorized);
        error = null;
        return true;
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

    private static bool TryResolveExistingPath(string path, out string resolvedPath)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            resolvedPath = fullPath;
            return true;
        }

        var current = root;
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            resolvedPath = fullPath;
            return true;
        }

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            try
            {
                var attributes = File.GetAttributes(current);
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    current = target.FullName;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
                resolvedPath = fullPath;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                resolvedPath = fullPath;
                return false;
            }
        }

        resolvedPath = Path.GetFullPath(current);
        return true;
    }
}

internal sealed class McpIndexAuthorizationException(string checkedRootIdentity, string reason) : Exception
{
    internal string CheckedRootIdentity { get; } = checkedRootIdentity;
    internal string Reason { get; } = reason;
}

internal sealed class McpIndexEntryUnavailableException : IOException
{
}
