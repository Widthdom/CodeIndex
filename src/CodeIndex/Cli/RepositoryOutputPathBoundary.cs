using System.Runtime.InteropServices;
using System.Text;
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
        "symbolic links, junctions, bind or cross-device mount points, reparse points, devices, and dangling links are not allowed below the config workspace root";
    private static readonly AsyncLocal<Action<string, string>?> BeforeMutation = new();
    private static readonly AsyncLocal<Func<string, string, bool>?> ContainsPath = new();
    private static readonly AsyncLocal<Func<string, ulong?>?> UnixMountId = new();
    private static readonly AsyncLocal<Func<int, ulong?>?> UnixHandleMountId = new();
    private static readonly AsyncLocal<Func<int, int>?> UnixDirectoryFsync = new();
    private static readonly AsyncLocal<Action<string, string>?> BeforeWindowsNativeMutation = new();

    internal static Action<string, string>? BeforeMutationForTesting
    {
        get => BeforeMutation.Value;
        set => BeforeMutation.Value = value;
    }

    internal static Func<string, string, bool>? ContainsPathForTesting
    {
        get => ContainsPath.Value;
        set => ContainsPath.Value = value;
    }

    internal static Func<string, ulong?>? UnixMountIdForTesting
    {
        get => UnixMountId.Value;
        set => UnixMountId.Value = value;
    }

    internal static Func<int, ulong?>? UnixHandleMountIdForTesting
    {
        get => UnixHandleMountId.Value;
        set => UnixHandleMountId.Value = value;
    }

    internal static Func<int, int>? UnixDirectoryFsyncForTesting
    {
        get => UnixDirectoryFsync.Value;
        set => UnixDirectoryFsync.Value = value;
    }

    internal static Action<string, string>? BeforeWindowsNativeMutationForTesting
    {
        get => BeforeWindowsNativeMutation.Value;
        set => BeforeWindowsNativeMutation.Value = value;
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
            if (!IsPathEqualOrParent(normalizedRoot, normalizedPath))
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
        if (!IsPathEqualOrParent(normalizedRoot, normalizedPath))
            throw CreateException("output path", "outside_workspace");

        var rootAttributesStatus = FileSystemBoundary.TryGetAttributes(normalizedRoot, out var rootAttributes);
        if (rootAttributesStatus != FileSystemBoundaryProbeStatus.Found
            || (rootAttributes & FileAttributes.Directory) == 0
            || FileSystemBoundary.IsDevice(rootAttributes))
        {
            throw CreateException("output path", UnsafeReason);
        }

        long? rootDevice = null;
        if (!OperatingSystem.IsWindows())
        {
            if (!TryGetUnixStatus(normalizedRoot, out var rootStatus)
                || (rootStatus.Mode & UnixFileTypeMask) != UnixDirectoryType)
            {
                throw CreateException("output path", UnsafeReason);
            }
            rootDevice = rootStatus.Device;
        }
        ulong? rootMountId = null;
        if (RequiresUnixMountIdentity)
        {
            var canonicalRoot = ResolveCanonicalWorkspaceRoot(normalizedRoot);
            if (!TryGetUnixMountId(canonicalRoot, out var resolvedRootMountId))
                throw CreateException("output path", UnsafeReason);
            rootMountId = resolvedRootMountId;
        }
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
                && (!TryGetUnixStatus(current, out var currentStatus)
                    || currentStatus.Device != rootDevice.Value
                    || (currentStatus.Mode & UnixFileTypeMask)
                        != (isDirectory ? UnixDirectoryType : UnixRegularFileType)))
            {
                throw CreateException("output path", UnsafeReason);
            }
            if (rootMountId.HasValue
                && (!TryGetUnixMountId(current, out var currentMountId)
                    || currentMountId != rootMountId.Value))
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

    private static bool TryGetUnixStatus(string path, out UnixFileStatus status)
    {
        status = default;
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            return UnixStat(LongPath.EnsureWindowsPrefix(path), out status) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool RequiresUnixMountIdentity =>
        OperatingSystem.IsLinux()
        || UnixMountIdForTesting is not null
        || UnixHandleMountIdForTesting is not null;

    internal static bool TryGetUnixMountId(string path, out ulong mountId)
    {
        mountId = 0;
        if (UnixMountIdForTesting is { } provider)
        {
            var value = provider(path);
            if (value.HasValue)
            {
                mountId = value.Value;
                return true;
            }
            return false;
        }

        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (UnixStatX(
                    UnixCurrentWorkingDirectory,
                    path,
                    UnixAtSymlinkNoFollow,
                    UnixStatXMountId,
                    out var status) != 0
                || (status.Mask & UnixStatXMountId) == 0)
            {
                return false;
            }

            mountId = status.MountId;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryGetUnixMountId(SafeFileHandle handle, out ulong mountId)
    {
        mountId = 0;
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (UnixHandleMountIdForTesting is { } provider)
        {
            var value = provider(descriptor);
            if (value.HasValue)
            {
                mountId = value.Value;
                return true;
            }
            return false;
        }

        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (UnixStatX(
                    descriptor,
                    string.Empty,
                    UnixAtEmptyPath | UnixAtSymlinkNoFollow,
                    UnixStatXMountId,
                    out var status) != 0
                || (status.Mask & UnixStatXMountId) == 0)
            {
                return false;
            }

            mountId = status.MountId;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static int FsyncUnixDirectory(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (UnixDirectoryFsyncForTesting is { } fsync)
            return fsync(descriptor);

        int result;
        do
        {
            result = UnixFsync(descriptor);
        }
        while (result != 0 && Marshal.GetLastWin32Error() == 4);
        return result;
    }

    private static bool IsPathException(Exception ex)
        => ex is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException;

    internal static bool IsPathEqualOrParent(string parent, string child)
        => ContainsPathForTesting?.Invoke(parent, child)
            ?? PathCasing.IsPathEqualOrParentByDirectoryNamespace(parent, child);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi)]
    private static extern int UnixStat(string path, out UnixFileStatus status);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int UnixStatX(
        int directory,
        string path,
        int flags,
        uint mask,
        out UnixStatXStatus status);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);

    private const int UnixCurrentWorkingDirectory = -100;
    private const int UnixAtSymlinkNoFollow = 0x100;
    private const int UnixAtEmptyPath = 0x1000;
    private const uint UnixStatXMountId = 0x1000;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct UnixStatXStatus
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(144)]
        internal ulong MountId;
    }

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

    internal Stream OpenAppendWindows(string path, FileShare share)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var handle = OpenWindowsPath(
            path,
            WindowsFileAppendData | WindowsFileReadAttributes | WindowsFileWriteAttributes | WindowsSynchronize,
            (uint)share,
            WindowsFileOpenIf,
            WindowsFileNormal,
            WindowsFileNonDirectory | WindowsFileSynchronousIoNonAlert);
        try
        {
            return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal FileStream OpenReplacingUnix(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var handle = OpenUnixFile(path, create: true, append: false, truncate: true);
        try
        {
            return new FileStream(handle, FileAccess.Write);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal FileStream OpenReplacingWindows(string path, bool createNew)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var handle = OpenWindowsPath(
            path,
            WindowsFileWriteData | WindowsFileReadAttributes | WindowsFileWriteAttributes | WindowsSynchronize,
            shareAccess: 0,
            createNew ? WindowsFileCreate : WindowsFileOverwriteIf,
            WindowsFileNormal,
            WindowsFileNonDirectory | WindowsFileSynchronousIoNonAlert);
        try
        {
            return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
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

    internal bool DeleteFileWindows(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var root = OpenWindowsRootDirectory();
        var relativePath = GetWindowsRelativePath(path);
        using var objectName = new WindowsObjectName(root, relativePath);
        RepositoryOutputPathBoundary.BeforeWindowsNativeMutationForTesting?.Invoke("delete", path);
        var status = WindowsNtDeleteFile(ref objectName.Attributes);
        if (status >= 0)
            return true;
        if (status is WindowsStatusObjectNameNotFound or WindowsStatusObjectPathNotFound)
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
        if (RepositoryOutputPathBoundary.FsyncUnixDirectory(destinationParent) != 0)
        {
            throw new IOException(
                "Guarded replace completed, but the target file was already replaced "
                + "and its parent directory could not be flushed.");
        }
    }

    internal void MoveReplacingWindows(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var sourceHandle = OpenWindowsPath(
            source,
            WindowsDelete | WindowsFileReadAttributes | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            WindowsFileOpen,
            WindowsFileNormal,
            WindowsFileNonDirectory | WindowsFileSynchronousIoNonAlert);
        var destinationParentPath = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw RepositoryOutputPathBoundary.CreateException(FieldName, "invalid_path");
        using var destinationParent = PathCasing.PathsEqualByDirectoryNamespace(
            WorkspaceRoot,
            destinationParentPath)
            ? OpenWindowsRootDirectory()
            : OpenWindowsPath(
                destinationParentPath,
                WindowsFileListDirectory | WindowsFileReadAttributes | WindowsSynchronize,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                WindowsFileOpen,
                WindowsFileDirectory,
                WindowsFileDirectoryOption | WindowsFileSynchronousIoNonAlert);

        var destinationName = Path.GetFileName(destination);
        var encodedName = Encoding.Unicode.GetBytes(destinationName);
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = Marshal.AllocHGlobal(nameOffset + encodedName.Length);
        try
        {
            for (var index = 0; index < nameOffset; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteByte(buffer, 0, 1);
            Marshal.WriteIntPtr(buffer, rootOffset, destinationParent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, encodedName.Length);
            Marshal.Copy(encodedName, 0, IntPtr.Add(buffer, nameOffset), encodedName.Length);
            RepositoryOutputPathBoundary.BeforeWindowsNativeMutationForTesting?.Invoke("rename", source);
            using var verificationRoot = OpenWindowsRootDirectory();
            EnsureWindowsHandleUnderRoot(verificationRoot, sourceHandle);
            EnsureWindowsHandleUnderRoot(verificationRoot, destinationParent);
            var status = WindowsNtSetInformationFile(
                sourceHandle,
                out _,
                buffer,
                (uint)(nameOffset + encodedName.Length),
                WindowsFileRenameInformation);
            if (status < 0)
                throw CreateNativeIOException();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
        if (OperatingSystem.IsWindows())
        {
            CreateSensitiveDirectoriesWindows(directory);
            return;
        }

        CreateSensitiveDirectoriesUnix(directory);
    }

    private void CreateSensitiveDirectoriesWindows(string directory)
    {
        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(directory);
        var components = GetRelativeComponents(normalizedDirectory);
        if (components.Length == 0)
            return;

        for (var index = 0; index < components.Length; index++)
        {
            var currentPath = Path.Combine(WorkspaceRoot, Path.Combine(components[..(index + 1)]));
            PrepareMutation("create_directory", currentPath, expectsDirectory: true);
            using var handle = OpenWindowsPath(
                currentPath,
                WindowsFileListDirectory
                    | WindowsFileAddSubdirectory
                    | WindowsFileReadAttributes
                    | WindowsFileWriteAttributes
                    | WindowsSynchronize,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                WindowsFileOpenIf,
                WindowsFileDirectory,
                WindowsFileDirectoryOption | WindowsFileSynchronousIoNonAlert);
            CompleteMutation(currentPath, expectsDirectory: true);
        }
    }

    private void CreateSensitiveDirectoriesUnix(string directory)
    {
        var normalizedDirectory = PathCasing.NormalizeBoundaryPath(directory);
        var components = GetRelativeComponents(normalizedDirectory);
        using var root = OpenUnixRootDirectory(out var rootIdentity);
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
                        var parentPath = index == 0
                            ? WorkspaceRoot
                            : Path.Combine(WorkspaceRoot, Path.Combine(components[..index]));
                        using var reboundParent = OpenUnixDirectory(parentPath, out var reboundIdentity);
                        if (reboundIdentity != rootIdentity)
                            throw CreateNativeIOException();
                        if (UnixMkdirAt(
                                reboundParent.DangerousGetHandle().ToInt32(),
                                component,
                                (uint)DataDirectorySecurity.PrivateDirectoryMode) != 0
                            && Marshal.GetLastWin32Error() != 17)
                        {
                            throw CreateNativeIOException();
                        }
                        next = TryOpenUnixDirectoryAt(reboundParent, component) ?? throw CreateNativeIOException();
                        CompleteMutation(currentPath, expectsDirectory: true);
                        created = true;
                    }

                    EnsureUnixObject(next, rootIdentity, expectedType: UnixDirectoryType);
                    if (created || index == components.Length - 1)
                    {
                        PrepareMutation("set_directory_permissions", currentPath, expectsDirectory: true);
                        next.Dispose();
                        next = OpenUnixDirectory(currentPath, out var reboundIdentity);
                        if (reboundIdentity != rootIdentity)
                            throw CreateNativeIOException();
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

    private SafeFileHandle OpenUnixFile(string path, bool create, bool append, bool truncate = false)
    {
        using var parent = OpenUnixParentDirectory(path, out var rootIdentity);
        var flags = UnixWriteOnly | UnixCloseOnExec | UnixNoFollow;
        if (append)
            flags |= UnixAppend;
        if (create)
            flags |= UnixCreate;
        if (truncate)
            flags |= UnixTruncate;
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
            EnsureUnixObject(handle, rootIdentity, expectedType: UnixRegularFileType);
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

    private SafeFileHandle OpenUnixParentDirectory(string path, out UnixBoundaryIdentity rootIdentity)
    {
        var parentPath = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw RepositoryOutputPathBoundary.CreateException(FieldName, "invalid_path");
        return OpenUnixDirectory(parentPath, out rootIdentity);
    }

    private SafeFileHandle OpenUnixDirectory(string directory, out UnixBoundaryIdentity rootIdentity)
    {
        var components = GetRelativeComponents(PathCasing.NormalizeBoundaryPath(directory));
        var current = OpenUnixRootDirectory(out rootIdentity);
        try
        {
            foreach (var component in components)
            {
                var next = TryOpenUnixDirectoryAt(current, component) ?? throw CreateNativeIOException();
                try
                {
                    EnsureUnixObject(next, rootIdentity, expectedType: UnixDirectoryType);
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

    private SafeFileHandle OpenUnixRootDirectory(out UnixBoundaryIdentity rootIdentity)
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
            ulong? mountId = null;
            if (RepositoryOutputPathBoundary.RequiresUnixMountIdentity)
            {
                if (!RepositoryOutputPathBoundary.TryGetUnixMountId(handle, out var resolvedMountId))
                    throw CreateNativeIOException();
                mountId = resolvedMountId;
            }
            rootIdentity = new UnixBoundaryIdentity(status.Device, mountId);
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

    private static void EnsureUnixObject(
        SafeFileHandle handle,
        UnixBoundaryIdentity rootIdentity,
        int expectedType)
    {
        if (UnixFStat(handle.DangerousGetHandle(), out var status) != 0
            || (status.Mode & UnixFileTypeMask) != expectedType
            || status.Device != rootIdentity.Device)
        {
            throw CreateNativeIOException();
        }
        if (rootIdentity.MountId.HasValue
            && (!RepositoryOutputPathBoundary.TryGetUnixMountId(handle, out var mountId)
                || mountId != rootIdentity.MountId.Value))
        {
            throw CreateNativeIOException();
        }
    }

    private string[] GetRelativeComponents(string path)
    {
        if (!RepositoryOutputPathBoundary.IsPathEqualOrParent(WorkspaceRoot, path))
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
            return RepositoryOutputPathBoundary.IsPathEqualOrParent(DestinationPath, path)
                || (expectsDirectory
                    && RepositoryOutputPathBoundary.IsPathEqualOrParent(path, DestinationPath)
                    && RepositoryOutputPathBoundary.IsPathEqualOrParent(WorkspaceRoot, path));
        }

        if (expectsDirectory && RepositoryOutputPathBoundary.IsPathEqualOrParent(path, DestinationPath))
            return RepositoryOutputPathBoundary.IsPathEqualOrParent(WorkspaceRoot, path);

        if (PathCasing.PathsEqualByDirectoryNamespace(DestinationPath, path))
            return true;

        var destinationDirectory = Path.GetDirectoryName(DestinationPath);
        var pathDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(destinationDirectory)
            || string.IsNullOrEmpty(pathDirectory)
            || !PathCasing.PathsEqualByDirectoryNamespace(
                PathCasing.NormalizeBoundaryPath(destinationDirectory),
                PathCasing.NormalizeBoundaryPath(pathDirectory)))
        {
            return false;
        }

        var comparison = PathCasing.ComparisonFor(destinationDirectory);
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

    private SafeFileHandle OpenWindowsPath(
        string path,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint attributes,
        uint options)
    {
        using var root = OpenWindowsRootDirectory();
        var relativePath = GetWindowsRelativePath(path);
        using var objectName = new WindowsObjectName(root, relativePath);
        RepositoryOutputPathBoundary.BeforeWindowsNativeMutationForTesting?.Invoke("open", path);
        var status = WindowsNtCreateFile(
            out var handle,
            desiredAccess,
            ref objectName.Attributes,
            out _,
            IntPtr.Zero,
            attributes,
            shareAccess,
            disposition,
            options,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw CreateNativeIOException();
        }

        try
        {
            EnsureWindowsHandleUnderRoot(root, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenWindowsRootDirectory()
    {
        var handle = WindowsCreateFile(
            LongPath.EnsureWindowsPrefix(CanonicalWorkspaceRoot),
            WindowsFileListDirectory | WindowsFileReadAttributes | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsFileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw CreateNativeIOException();
        }
        return handle;
    }

    private string GetWindowsRelativePath(string path)
    {
        var components = GetRelativeComponents(PathCasing.NormalizeBoundaryPath(path));
        if (components.Length == 0)
            throw RepositoryOutputPathBoundary.CreateException(FieldName, "invalid_path");
        return string.Join('\\', components);
    }

    private static void EnsureWindowsHandleUnderRoot(SafeFileHandle root, SafeFileHandle handle)
    {
        var rootPath = GetWindowsFinalPath(root);
        var handlePath = GetWindowsFinalPath(handle);
        if (!RepositoryOutputPathBoundary.IsPathEqualOrParent(rootPath, handlePath))
            throw CreateNativeIOException();
    }

    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= short.MaxValue)
        {
            var buffer = new StringBuilder(capacity);
            var length = WindowsGetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags: 0);
            if (length == 0)
                throw CreateNativeIOException();
            if (length < buffer.Capacity)
                return NormalizeWindowsFinalPath(buffer.ToString());
            capacity = checked((int)length + 1);
        }
        throw CreateNativeIOException();
    }

    private static string NormalizeWindowsFinalPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUncPrefix.Length..];
        if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
            return path[extendedPrefix.Length..];
        return path;
    }

    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;

    private readonly record struct UnixBoundaryIdentity(long Device, ulong? MountId);

    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixAppend => OperatingSystem.IsMacOS() ? 0x00000008 : 0x00000400;
    private static int UnixTruncate => OperatingSystem.IsMacOS() ? 0x00000400 : 0x00000200;

    private const uint WindowsFileListDirectory = 0x00000001;
    private const uint WindowsFileWriteData = 0x00000002;
    private const uint WindowsFileAddSubdirectory = 0x00000004;
    private const uint WindowsFileAppendData = 0x00000004;
    private const uint WindowsFileReadAttributes = 0x00000080;
    private const uint WindowsFileWriteAttributes = 0x00000100;
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsSynchronize = 0x00100000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsFileDirectory = 0x00000010;
    private const uint WindowsFileNormal = 0x00000080;
    private const uint WindowsFileOpen = 0x00000001;
    private const uint WindowsFileCreate = 0x00000002;
    private const uint WindowsFileOpenIf = 0x00000003;
    private const uint WindowsFileOverwriteIf = 0x00000005;
    private const uint WindowsFileDirectoryOption = 0x00000001;
    private const uint WindowsFileSynchronousIoNonAlert = 0x00000020;
    private const uint WindowsFileNonDirectory = 0x00000040;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsFileFlagBackupSemantics = 0x02000000;
    private const uint WindowsObjectCaseInsensitive = 0x00000040;
    private const uint WindowsObjectDontReparse = 0x00001000;
    private const int WindowsFileRenameInformation = 10;
    private const int WindowsStatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int WindowsStatusObjectPathNotFound = unchecked((int)0xC000003A);

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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle WindowsCreateFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WindowsGetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static extern int WindowsNtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref WindowsObjectAttributes objectAttributes,
        out WindowsIoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr extendedAttributes,
        uint extendedAttributesLength);

    [DllImport("ntdll.dll", EntryPoint = "NtDeleteFile")]
    private static extern int WindowsNtDeleteFile(ref WindowsObjectAttributes objectAttributes);

    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static extern int WindowsNtSetInformationFile(
        SafeFileHandle fileHandle,
        out WindowsIoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsUnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsIoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    private sealed class WindowsObjectName : IDisposable
    {
        private readonly IntPtr _nameBuffer;
        private readonly IntPtr _unicodeString;

        internal WindowsObjectName(SafeFileHandle root, string relativePath)
        {
            var byteLength = checked(relativePath.Length * sizeof(char));
            if (byteLength > ushort.MaxValue - sizeof(char))
                throw RepositoryOutputPathBoundary.CreateException("output path", "invalid_path");

            _nameBuffer = Marshal.StringToHGlobalUni(relativePath);
            _unicodeString = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsUnicodeString>());
            Marshal.StructureToPtr(
                new WindowsUnicodeString
                {
                    Length = (ushort)byteLength,
                    MaximumLength = (ushort)(byteLength + sizeof(char)),
                    Buffer = _nameBuffer,
                },
                _unicodeString,
                fDeleteOld: false);
            Attributes = new WindowsObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<WindowsObjectAttributes>(),
                RootDirectory = root.DangerousGetHandle(),
                ObjectName = _unicodeString,
                Attributes = WindowsObjectCaseInsensitive | WindowsObjectDontReparse,
            };
        }

        internal WindowsObjectAttributes Attributes;

        public void Dispose()
        {
            Marshal.FreeHGlobal(_unicodeString);
            Marshal.FreeHGlobal(_nameBuffer);
        }
    }

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
