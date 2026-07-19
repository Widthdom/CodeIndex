using System.Buffers.Binary;
using System.Security.Cryptography;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Mcp;

/// <summary>
/// Central MCP filesystem boundary checks for paths that may reach local IO.
/// ローカル IO に届き得る MCP path の境界検証を集約する。
/// </summary>
internal static class McpPathBoundary
{
    internal sealed class IndexRootAuthorization : IDisposable
    {
        private readonly string _requestedPath;
        private readonly FileIndexer.FileIdentity _rootIdentity;
        private readonly SafeFileHandle _rootHandle;
        private readonly Func<string, bool> _isPathAuthorized;
        private readonly Action<string>? _entryOpenBoundary;
        private readonly Action<string>? _directoryEnumerationBoundary;
        private readonly Action<string>? _directoryEnumerationCompleted;

        internal IndexRootAuthorization(
            string requestedPath,
            string canonicalPath,
            FileIndexer.FileIdentity rootIdentity,
            SafeFileHandle rootHandle,
            Func<string, bool> isPathAuthorized,
            Action<string>? entryOpenBoundary,
            Action<string>? directoryEnumerationBoundary,
            Action<string>? directoryEnumerationCompleted)
        {
            _requestedPath = requestedPath;
            CanonicalPath = canonicalPath;
            _rootIdentity = rootIdentity;
            _rootHandle = rootHandle;
            _isPathAuthorized = isPathAuthorized;
            _entryOpenBoundary = entryOpenBoundary;
            _directoryEnumerationBoundary = directoryEnumerationBoundary;
            _directoryEnumerationCompleted = directoryEnumerationCompleted;
            CheckedRootIdentity = CreateRootIdentityToken(rootIdentity);
        }

        internal string CanonicalPath { get; }
        internal string CheckedRootIdentity { get; }

        internal void EnsureAuthorizedEntry(string path)
        {
            path = LongPath.RemoveWindowsPrefix(path);
            EnsureStableRoot();

            if (!TryResolveExistingPath(path, out var canonicalEntry))
                throw new McpIndexEntryUnavailableException();
            if (!_isPathAuthorized(canonicalEntry))
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "entry_outside_authorized_roots");
        }

        internal FileStream OpenAuthorizedRead(string path)
        {
            path = LongPath.RemoveWindowsPrefix(path);
            var expectedIdentity = CaptureAuthorizedEntryIdentity(path);
            _entryOpenBoundary?.Invoke(path);

            var stream = BoundedFile.OpenReadForIndexContent(path);
            try
            {
                EnsureOpenedEntryIdentity(path, stream.SafeFileHandle, expectedIdentity, "entry_identity_changed");
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal IEnumerable<string> EnumerateAuthorizedFileSystemEntries(string path)
        {
            path = LongPath.RemoveWindowsPrefix(path);
            var expectedIdentity = CaptureAuthorizedEntryIdentity(path);
            _entryOpenBoundary?.Invoke(path);
            if (!FileIndexer.TryOpenDirectoryIdentityHandle(path, out var directoryHandle, out var openedIdentity))
            {
                if (FileIndexer.TryGetFileIdentity(path, out var currentIdentity)
                    && currentIdentity != expectedIdentity)
                {
                    throw new McpIndexAuthorizationException(CheckedRootIdentity, "directory_identity_changed");
                }

                throw new McpIndexEntryUnavailableException();
            }

            using (directoryHandle)
            {
                if (openedIdentity != expectedIdentity)
                    throw new McpIndexAuthorizationException(CheckedRootIdentity, "directory_identity_changed");

                EnsureOpenedEntryIdentity(path, directoryHandle, expectedIdentity, "directory_identity_changed");
                _directoryEnumerationBoundary?.Invoke(path);
                if (!FileIndexer.TryEnumerateDirectoryHandleEntries(directoryHandle, out var entryNames))
                {
                    throw new McpIndexAuthorizationException(
                        CheckedRootIdentity,
                        "handle_bound_enumeration_unavailable");
                }
                _directoryEnumerationCompleted?.Invoke(path);
                var entries = entryNames.Select(entryName => Path.Combine(path, entryName)).ToArray();

                EnsureOpenedEntryIdentity(path, directoryHandle, expectedIdentity, "directory_identity_changed");
                foreach (var entry in entries)
                    EnsureAuthorizedEntry(entry);
                return entries;
            }
        }

        public void Dispose()
            => _rootHandle.Dispose();

        private FileIndexer.FileIdentity CaptureAuthorizedEntryIdentity(string path)
        {
            EnsureAuthorizedEntry(path);
            if (!TryResolveExistingPath(path, out var canonicalEntry))
                throw new McpIndexEntryUnavailableException();
            if (!FileIndexer.TryGetFileIdentity(canonicalEntry, out var identity))
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new FileNotFoundException();
                throw new McpIndexEntryUnavailableException();
            }

            return identity;
        }

        private void EnsureOpenedEntryIdentity(
            string path,
            SafeFileHandle openedHandle,
            FileIndexer.FileIdentity expectedIdentity,
            string identityChangedReason)
        {
            EnsureStableRoot();
            if (!TryResolveExistingPath(path, out var canonicalEntry))
                throw new McpIndexEntryUnavailableException();
            if (!_isPathAuthorized(canonicalEntry))
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "entry_outside_authorized_roots");
            if (!FileIndexer.TryGetFileIdentity(canonicalEntry, out var currentIdentity)
                || !FileIndexer.TryGetFileIdentity(openedHandle, out var openedIdentity))
            {
                throw new McpIndexEntryUnavailableException();
            }

            if (currentIdentity != expectedIdentity || openedIdentity != expectedIdentity)
                throw new McpIndexAuthorizationException(CheckedRootIdentity, identityChangedReason);
        }

        private void EnsureStableRoot()
        {
            var currentRequestedTarget = ResolveExistingDirectoryPath(_requestedPath);
            if (!PathCasing.PathsEqual(currentRequestedTarget, CanonicalPath))
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "requested_root_changed");

            if (!FileIndexer.TryGetFileIdentity(CanonicalPath, out var currentPathIdentity)
                || !FileIndexer.TryGetFileIdentity(_rootHandle, out var currentHandleIdentity)
                || currentPathIdentity != _rootIdentity
                || currentHandleIdentity != _rootIdentity)
            {
                throw new McpIndexAuthorizationException(CheckedRootIdentity, "root_identity_changed");
            }
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
        Action<string>? entryOpenBoundary,
        Action<string>? directoryEnumerationBoundary,
        Action<string>? directoryEnumerationCompleted,
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

        if (!FileIndexer.TryOpenDirectoryIdentityHandle(canonicalPath, out var rootHandle, out var identity))
        {
            error = "Directory identity could not be verified";
            return false;
        }

        authorization = new IndexRootAuthorization(
            Path.GetFullPath(requestedPath),
            canonicalPath,
            identity,
            rootHandle,
            isPathAuthorized,
            entryOpenBoundary,
            directoryEnumerationBoundary,
            directoryEnumerationCompleted);
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

internal sealed class McpIndexAuthorizationException(string checkedRootIdentity, string reason)
    : Exception, IFileSystemAuthorizationFailure
{
    internal string CheckedRootIdentity { get; } = checkedRootIdentity;
    internal string Reason { get; } = reason;
}

internal sealed class McpIndexEntryUnavailableException : IOException
{
}
