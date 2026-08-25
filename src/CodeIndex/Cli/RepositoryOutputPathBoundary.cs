using System.Runtime.InteropServices;
using CodeIndex.Indexer;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Cli;

/// <summary>
/// Keeps writable destinations supplied by repository configuration below the config
/// workspace without following repository-controlled filesystem aliases.
/// repository config が指定する writable destination を、repository-controlled な
/// filesystem alias を辿らず config workspace 配下に保持する。
/// </summary>
internal static class RepositoryOutputPathBoundary
{
    internal const string UnsafeReason = "unsafe_output_path";
    private const string UnsafeMessage =
        "symbolic links, junctions, cross-device mount points, reparse points, devices, and dangling links are not allowed below the config workspace root";
    private static readonly AsyncLocal<Action<string, string>?> BeforeMutation = new();

    internal static Action<string, string>? BeforeMutationForTesting
    {
        get => BeforeMutation.Value;
        set => BeforeMutation.Value = value;
    }

    internal static bool TryResolveConfiguredPath(
        string rawPath,
        string workspaceRoot,
        bool destinationIsDirectory,
        out string resolvedPath,
        out string failureReason)
    {
        resolvedPath = string.Empty;
        failureReason = string.Empty;
        try
        {
            var normalizedRoot = PathCasing.NormalizeBoundaryPath(workspaceRoot);
            var fullPath = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(normalizedRoot, rawPath));
            var normalizedPath = PathCasing.NormalizeBoundaryPath(fullPath);
            if (!PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedPath))
            {
                failureReason = "outside_workspace";
                return false;
            }

            ValidatePathComponents(normalizedRoot, normalizedPath, destinationIsDirectory);
            resolvedPath = fullPath;
            return true;
        }
        catch (RepositoryOutputPathBoundaryException)
        {
            failureReason = UnsafeReason;
            return false;
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            failureReason = "invalid_path";
            return false;
        }
    }

    internal static RepositoryOutputPathGuard? CreateGuardForConfigSource(
        string environmentVariable,
        string fieldName,
        string destinationPath,
        bool destinationIsDirectory)
    {
        var sourcePath = CdidxConfigSourceResolver.GetSource(environmentVariable);
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var workspaceRoot = CdidxConfigFile.ResolveConfigWorkspaceRoot(sourcePath);
        if (!TryResolveConfiguredPath(
                destinationPath,
                workspaceRoot,
                destinationIsDirectory,
                out var resolvedPath,
                out var failureReason))
        {
            throw CreateException(fieldName, failureReason);
        }

        return new RepositoryOutputPathGuard(
            fieldName,
            PathCasing.NormalizeBoundaryPath(workspaceRoot),
            PathCasing.NormalizeBoundaryPath(resolvedPath),
            destinationIsDirectory);
    }

    internal static RepositoryOutputPathBoundaryException CreateException(string fieldName, string reason)
        => new($"repository-configured `{fieldName}` rejected ({reason}): {UnsafeMessage}.");

    internal static string ResolveCanonicalWorkspaceRoot(string workspaceRoot)
    {
        if (OperatingSystem.IsWindows())
            return PathCasing.NormalizeBoundaryPath(workspaceRoot);

        IntPtr pointer = IntPtr.Zero;
        try
        {
            pointer = UnixRealPath(workspaceRoot, IntPtr.Zero);
            var resolved = pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
            return string.IsNullOrWhiteSpace(resolved)
                ? PathCasing.NormalizeBoundaryPath(workspaceRoot)
                : PathCasing.NormalizeBoundaryPath(resolved);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return PathCasing.NormalizeBoundaryPath(workspaceRoot);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                UnixFree(pointer);
        }
    }

    internal static void ValidatePathComponents(
        string workspaceRoot,
        string path,
        bool destinationIsDirectory)
    {
        var normalizedRoot = PathCasing.NormalizeBoundaryPath(workspaceRoot);
        var normalizedPath = PathCasing.NormalizeBoundaryPath(path);
        if (!PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedPath))
            throw CreateException("output path", "outside_workspace");

        var rootAttributesStatus = FileSystemBoundary.TryGetAttributes(normalizedRoot, out var rootAttributes);
        if (rootAttributesStatus != FileSystemBoundaryProbeStatus.Found
            || (rootAttributes & FileAttributes.Directory) == 0
            || FileSystemBoundary.IsDevice(rootAttributes))
        {
            throw CreateException("output path", UnsafeReason);
        }

        var rootDevice = TryGetUnixDevice(normalizedRoot, out var device) ? device : (long?)null;
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        if (relative == ".")
            return;

        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = normalizedRoot;
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component is "." or "..")
                throw CreateException("output path", UnsafeReason);

            current = Path.Combine(current, component);
            if (IsLinkOrReparseEntry(current))
                throw CreateException("output path", UnsafeReason);

            var status = FileSystemBoundary.TryGetAttributes(current, out var attributes);
            if (status == FileSystemBoundaryProbeStatus.Missing)
                continue;
            if (status != FileSystemBoundaryProbeStatus.Found
                || FileSystemBoundary.IsSymlinkOrReparsePoint(attributes)
                || FileSystemBoundary.IsDevice(attributes))
            {
                throw CreateException("output path", UnsafeReason);
            }

            var isFinal = index == components.Length - 1;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((!isFinal && !isDirectory) || (isFinal && destinationIsDirectory && !isDirectory))
                throw CreateException("output path", UnsafeReason);
            if (isFinal && !destinationIsDirectory && isDirectory)
                throw CreateException("output path", UnsafeReason);

            if (rootDevice.HasValue
                && TryGetUnixDevice(current, out var currentDevice)
                && currentDevice != rootDevice.Value)
            {
                throw CreateException("output path", UnsafeReason);
            }
        }
    }

    private static bool IsLinkOrReparseEntry(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            directory.Refresh();
            if (!string.IsNullOrEmpty(directory.LinkTarget))
                return true;
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            if (ex is UnauthorizedAccessException)
                throw;
        }

        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            return !string.IsNullOrEmpty(file.LinkTarget);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            if (ex is UnauthorizedAccessException)
                throw;
            return false;
        }
    }

    private static bool TryGetUnixDevice(string path, out long device)
    {
        device = 0;
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            if (UnixStat(LongPath.EnsureWindowsPrefix(path), out var status) != 0)
                return false;
            device = status.Device;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsPathException(Exception ex)
        => ex is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException;

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi)]
    private static extern int UnixStat(string path, out UnixFileStatus status);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal uint Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Device;
        internal long RDevice;
        internal long Inode;
        internal uint UserFlags;
    }
}

internal sealed class RepositoryOutputPathGuard
{
    internal RepositoryOutputPathGuard(
        string fieldName,
        string workspaceRoot,
        string destinationPath,
        bool destinationIsDirectory)
    {
        FieldName = fieldName;
        WorkspaceRoot = workspaceRoot;
        CanonicalWorkspaceRoot = RepositoryOutputPathBoundary.ResolveCanonicalWorkspaceRoot(workspaceRoot);
        DestinationPath = destinationPath;
        DestinationIsDirectory = destinationIsDirectory;
    }

    internal string FieldName { get; }
    internal string WorkspaceRoot { get; }
    internal string CanonicalWorkspaceRoot { get; }
    internal string DestinationPath { get; }
    internal bool DestinationIsDirectory { get; }

    internal void CreateSensitiveDestinationDirectory()
    {
        var directory = DestinationIsDirectory
            ? DestinationPath
            : Path.GetDirectoryName(DestinationPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        CreateSensitiveDirectories(directory);
    }

    internal void PrepareMutation(string operation, string path, bool expectsDirectory = false)
    {
        ValidateRelatedPath(path, expectsDirectory);
        RepositoryOutputPathBoundary.BeforeMutationForTesting?.Invoke(operation, path);
        ValidateRelatedPath(path, expectsDirectory);
    }

    internal void CompleteMutation(string path, bool expectsDirectory = false)
        => ValidateRelatedPath(path, expectsDirectory);

    internal Stream OpenAppendUnix(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var handle = OpenUnixFile(path, create: true, append: true);
        try
        {
            return new UnixAppendStream(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void SetPrivateFileModeUnix(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var handle = OpenUnixFile(path, create: false, append: false);
        if (UnixFChmod(handle.DangerousGetHandle().ToInt32(), (uint)PrivateLogFile.PrivateFileMode) != 0)
            throw CreateNativeIOException();
    }

    internal bool DeleteFileUnix(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;

        using var parent = OpenUnixParentDirectory(path);
        var result = UnixUnlinkAt(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(path), flags: 0);
        if (result == 0)
            return true;
        if (Marshal.GetLastWin32Error() == 2)
            return false;
        throw CreateNativeIOException();
    }

    internal void MoveReplacingUnix(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var sourceParent = OpenUnixParentDirectory(source);
        using var destinationParent = OpenUnixParentDirectory(destination);
        if (UnixRenameAt(
                sourceParent.DangerousGetHandle().ToInt32(),
                Path.GetFileName(source),
                destinationParent.DangerousGetHandle().ToInt32(),
                Path.GetFileName(destination)) != 0)
        {
            throw CreateNativeIOException();
        }
    }

    internal void ValidateRelatedPath(string path, bool expectsDirectory = false)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Allows(fullPath, expectsDirectory))
            throw RepositoryOutputPathBoundary.CreateException(FieldName, "outside_workspace");

        expectsDirectory |= string.Equals(
            PathCasing.NormalizeBoundaryPath(fullPath),
            PathCasing.NormalizeBoundaryPath(DestinationPath),
            PathCasing.ComparisonFor(DestinationPath))
            && DestinationIsDirectory;
        RepositoryOutputPathBoundary.ValidatePathComponents(WorkspaceRoot, fullPath, expectsDirectory);
    }

    private void CreateSensitiveDirectories(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            CreateSensitiveDirectoriesUnix(directory);
            return;
        }

        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(directory);
        var relative = Path.GetRelativePath(WorkspaceRoot, normalizedDirectory);
        if (relative == ".")
            return;

        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = WorkspaceRoot;
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            RepositoryOutputPathBoundary.ValidatePathComponents(WorkspaceRoot, current, destinationIsDirectory: true);
            var created = false;
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(current)))
            {
                PrepareMutation("create_directory", current, expectsDirectory: true);
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(current));
                else
                    Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(current), DataDirectorySecurity.PrivateDirectoryMode);
                CompleteMutation(current, expectsDirectory: true);
                created = true;
            }

            if (!OperatingSystem.IsWindows()
                && (created || string.Equals(
                    PathCasing.NormalizeBoundaryPath(current),
                    normalizedDirectory,
                    PathCasing.ComparisonFor(normalizedDirectory))))
            {
                PrepareMutation("set_directory_permissions", current, expectsDirectory: true);
                File.SetUnixFileMode(LongPath.EnsureWindowsPrefix(current), DataDirectorySecurity.PrivateDirectoryMode);
                CompleteMutation(current, expectsDirectory: true);
            }
        }
    }

    private void CreateSensitiveDirectoriesUnix(string directory)
    {
        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(directory);
        var components = GetRelativeComponents(normalizedDirectory);
        using var root = OpenUnixRootDirectory(out var rootDevice);
        SafeFileHandle current = root;
        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var currentPath = Path.Combine(WorkspaceRoot, Path.Combine(components[..(index + 1)]));
                SafeFileHandle? next = null;
                try
                {
                    next = TryOpenUnixDirectoryAt(current, component);
                    var created = false;
                    if (next is null)
                    {
                        PrepareMutation("create_directory", currentPath, expectsDirectory: true);
                        if (UnixMkdirAt(
                                current.DangerousGetHandle().ToInt32(),
                                component,
                                (uint)DataDirectorySecurity.PrivateDirectoryMode) != 0
                            && Marshal.GetLastWin32Error() != 17)
                        {
                            throw CreateNativeIOException();
                        }
                        next = TryOpenUnixDirectoryAt(current, component) ?? throw CreateNativeIOException();
                        CompleteMutation(currentPath, expectsDirectory: true);
                        created = true;
                    }

                    EnsureUnixObject(next, rootDevice, expectedType: UnixDirectoryType);
                    if (created || index == components.Length - 1)
                    {
                        PrepareMutation("set_directory_permissions", currentPath, expectsDirectory: true);
                        if (UnixFChmod(
                                next.DangerousGetHandle().ToInt32(),
                                (uint)DataDirectorySecurity.PrivateDirectoryMode) != 0)
                        {
                            throw CreateNativeIOException();
                        }
                        CompleteMutation(currentPath, expectsDirectory: true);
                    }
                }
                catch
                {
                    next?.Dispose();
                    throw;
                }

                if (!ReferenceEquals(current, root))
                    current.Dispose();
                current = next;
            }
        }
        finally
        {
            if (!ReferenceEquals(current, root))
                current.Dispose();
        }
    }

    private SafeFileHandle OpenUnixFile(string path, bool create, bool append)
    {
        using var parent = OpenUnixParentDirectory(path, out var rootDevice);
        var flags = UnixWriteOnly | UnixCloseOnExec | UnixNoFollow;
        if (append)
            flags |= UnixAppend;
        if (create)
            flags |= UnixCreate;
        var descriptor = UnixOpenAt(
            parent.DangerousGetHandle().ToInt32(),
            Path.GetFileName(path),
            flags,
            (uint)PrivateLogFile.PrivateFileMode);
        if (descriptor < 0)
            throw CreateNativeIOException();

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            EnsureUnixObject(handle, rootDevice, expectedType: UnixRegularFileType);
            if (create
                && UnixFChmod(handle.DangerousGetHandle().ToInt32(), (uint)PrivateLogFile.PrivateFileMode) != 0)
            {
                throw CreateNativeIOException();
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenUnixParentDirectory(string path)
        => OpenUnixParentDirectory(path, out _);

    private SafeFileHandle OpenUnixParentDirectory(string path, out long rootDevice)
    {
        var parentPath = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw RepositoryOutputPathBoundary.CreateException(FieldName, "invalid_path");
        return OpenUnixDirectory(parentPath, out rootDevice);
    }

    private SafeFileHandle OpenUnixDirectory(string directory, out long rootDevice)
    {
        var components = GetRelativeComponents(PathCasing.NormalizeBoundaryPath(directory));
        var current = OpenUnixRootDirectory(out rootDevice);
        try
        {
            foreach (var component in components)
            {
                var next = TryOpenUnixDirectoryAt(current, component) ?? throw CreateNativeIOException();
                try
                {
                    EnsureUnixObject(next, rootDevice, expectedType: UnixDirectoryType);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenUnixRootDirectory(out long rootDevice)
    {
        var descriptor = UnixOpen(
            CanonicalWorkspaceRoot,
            UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixDirectory);
        if (descriptor < 0)
            throw CreateNativeIOException();

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            if (UnixFStat(handle.DangerousGetHandle(), out var status) != 0
                || (status.Mode & UnixFileTypeMask) != UnixDirectoryType)
            {
                throw CreateNativeIOException();
            }
            rootDevice = status.Device;
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? TryOpenUnixDirectoryAt(SafeFileHandle parent, string component)
    {
        var descriptor = UnixOpenAt(
            parent.DangerousGetHandle().ToInt32(),
            component,
            UnixReadOnly | UnixCloseOnExec | UnixNoFollow | UnixDirectory,
            mode: 0);
        if (descriptor >= 0)
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        if (Marshal.GetLastWin32Error() == 2)
            return null;
        throw CreateNativeIOException();
    }

    private static void EnsureUnixObject(SafeFileHandle handle, long rootDevice, int expectedType)
    {
        if (UnixFStat(handle.DangerousGetHandle(), out var status) != 0
            || (status.Mode & UnixFileTypeMask) != expectedType
            || status.Device != rootDevice)
        {
            throw CreateNativeIOException();
        }
    }

    private string[] GetRelativeComponents(string path)
    {
        if (!PathCasing.IsPathEqualOrParent(WorkspaceRoot, path))
            throw RepositoryOutputPathBoundary.CreateException(FieldName, "outside_workspace");
        var relative = Path.GetRelativePath(WorkspaceRoot, path);
        if (relative == ".")
            return [];
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static component => component is "." or ".."))
            throw RepositoryOutputPathBoundary.CreateException("output path", "outside_workspace");
        return components;
    }

    private static RepositoryOutputPathBoundaryException CreateNativeIOException()
        => RepositoryOutputPathBoundary.CreateException("output path", RepositoryOutputPathBoundary.UnsafeReason);

    private bool Allows(string path, bool expectsDirectory)
    {
        if (DestinationIsDirectory)
        {
            return PathCasing.IsPathEqualOrParent(DestinationPath, path)
                || (expectsDirectory
                    && PathCasing.IsPathEqualOrParent(path, DestinationPath)
                    && PathCasing.IsPathEqualOrParent(WorkspaceRoot, path));
        }

        if (expectsDirectory && PathCasing.IsPathEqualOrParent(path, DestinationPath))
            return PathCasing.IsPathEqualOrParent(WorkspaceRoot, path);

        var comparison = PathCasing.ComparisonFor(DestinationPath);
        if (string.Equals(DestinationPath, path, comparison))
            return true;

        var destinationDirectory = Path.GetDirectoryName(DestinationPath);
        var pathDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(destinationDirectory)
            || string.IsNullOrEmpty(pathDirectory)
            || !string.Equals(
                PathCasing.NormalizeBoundaryPath(destinationDirectory),
                PathCasing.NormalizeBoundaryPath(pathDirectory),
                comparison))
        {
            return false;
        }

        var destinationName = Path.GetFileName(DestinationPath);
        var candidateName = Path.GetFileName(path);
        if (!candidateName.StartsWith(destinationName + ".", comparison))
            return false;

        return int.TryParse(
            candidateName.AsSpan(destinationName.Length + 1),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);
    }

    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;

    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixAppend => OperatingSystem.IsMacOS() ? 0x00000008 : 0x00000400;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(int directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int UnixMkdirAt(int directory, string path, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnixUnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int UnixRenameAt(
        int sourceDirectory,
        string sourcePath,
        int destinationDirectory,
        string destinationPath);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int UnixFChmod(int descriptor, uint mode);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int UnixFStat(IntPtr descriptor, out UnixFileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal uint Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Device;
        internal long RDevice;
        internal long Inode;
        internal uint UserFlags;
    }
}

internal sealed class RepositoryOutputPathBoundaryException(string message) : IOException(message);

internal sealed class UnixAppendStream : Stream
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    internal UnixAppendStream(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_handle);
        }
    }

    public override long Position
    {
        get => Length;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        ThrowIfDisposed();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("The offset and count exceed the buffer length.");
        ThrowIfDisposed();
        if (count == 0)
            return;

        if (offset == 0 && count == buffer.Length)
        {
            WriteAll(buffer, count);
            return;
        }

        var slice = new byte[count];
        Buffer.BlockCopy(buffer, offset, slice, 0, count);
        WriteAll(slice, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;
        Write(buffer.ToArray(), 0, buffer.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }

    private void WriteAll(byte[] buffer, int count)
    {
        var written = 0;
        while (written < count)
        {
            byte[] nextBuffer;
            if (written == 0)
            {
                nextBuffer = buffer;
            }
            else
            {
                nextBuffer = new byte[count - written];
                Buffer.BlockCopy(buffer, written, nextBuffer, 0, nextBuffer.Length);
            }

            var result = UnixWrite(
                _handle.DangerousGetHandle().ToInt32(),
                nextBuffer,
                (nuint)(count - written));
            if (result > 0)
            {
                written += checked((int)result);
                continue;
            }
            if (result < 0 && Marshal.GetLastWin32Error() == 4)
                continue;
            throw new IOException("repository-configured append failed (unsafe_output_path).");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint UnixWrite(int descriptor, byte[] buffer, nuint count);
}
