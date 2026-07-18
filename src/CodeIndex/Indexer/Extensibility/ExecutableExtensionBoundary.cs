using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class ExecutableExtensionStagingHandle : IDisposable
{
    private readonly string stagingDirectory;
    private bool disposed;

    internal ExecutableExtensionStagingHandle(
        string sourcePath,
        string stagedPath,
        string fingerprint,
        string stagingDirectory)
    {
        SourcePath = sourcePath;
        StagedPath = stagedPath;
        Fingerprint = fingerprint;
        this.stagingDirectory = stagingDirectory;
    }

    internal string SourcePath { get; }

    internal string StagedPath { get; }

    internal string Fingerprint { get; }

    internal string StagingDirectory => stagingDirectory;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (File.Exists(StagedPath))
                    File.SetUnixFileMode(StagedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (Directory.Exists(stagingDirectory))
                    File.SetUnixFileMode(
                        stagingDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else if (File.Exists(StagedPath))
            {
                File.SetAttributes(StagedPath, FileAttributes.Normal);
            }

            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best effort. A process exit also makes any staged assembly unreachable by cdidx.
        }
    }
}

internal readonly record struct ExecutableExtensionBoundaryFailure(string Category, string Message);

internal static class ExecutableExtensionBoundary
{
    private const int CopyBufferBytes = 64 * 1024;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint UnixDirectoryType = 0x4000;
    private const uint UnixGroupWrite = 0x0010;
    private const uint UnixOtherWrite = 0x0002;
    private const uint UnixStickyBit = 0x0200;

    internal static Action<string, string>? StagedForTesting { get; set; }

    internal static bool TryValidateDirectory(
        string directory,
        out string fullDirectory,
        out ExecutableExtensionBoundaryFailure failure)
    {
        fullDirectory = string.Empty;
        failure = default;
        try
        {
            fullDirectory = Path.GetFullPath(directory);
            var current = new DirectoryInfo(fullDirectory);
            while (current != null)
            {
                current.Refresh();
                if (!current.Exists)
                {
                    failure = new(
                        "extension_boundary_missing_directory",
                        "Executable extension directory does not exist.");
                    return false;
                }

                var isSymlinkOrReparsePoint = FileSystemBoundary.IsSymlinkOrReparsePoint(current);
                if ((current.Attributes & FileAttributes.Directory) == 0
                    || FileSystemBoundary.IsDevice(current.Attributes)
                    || (isSymlinkOrReparsePoint && !IsSystemOwnedUnixSymlink(current.FullName)))
                {
                    failure = new(
                        "extension_boundary_unsafe_ancestor",
                        "Executable extension directory rejected: every ancestor must be a real directory without symbolic links, reparse points, or devices.");
                    return false;
                }

                if (!isSymlinkOrReparsePoint
                    && !TryValidateUnixIdentity(current.FullName, UnixDirectoryType, out failure))
                    return false;

                current = current.Parent;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            failure = new(
                "extension_boundary_directory_inspection_failed",
                "Executable extension directory rejected: directory boundary could not be inspected.");
            return false;
        }
    }

    internal static bool TryStageFile(
        string trustedDirectory,
        string sourcePath,
        long maxBytes,
        out ExecutableExtensionStagingHandle? staging,
        out ExecutableExtensionBoundaryFailure failure)
    {
        staging = null;
        failure = default;
        if (!TryValidateDirectory(trustedDirectory, out var fullDirectory, out failure))
            return false;

        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(sourcePath);
            if (!FileSystemBoundary.IsStrictDescendant(fullDirectory, fullSourcePath))
            {
                failure = new(
                    "extension_boundary_outside_directory",
                    "Executable extension file rejected: candidate is outside the validated directory boundary.");
                return false;
            }

            var sourceInfo = new FileInfo(fullSourcePath);
            sourceInfo.Refresh();
            if (!sourceInfo.Exists
                || (sourceInfo.Attributes & FileAttributes.Directory) != 0
                || FileSystemBoundary.IsSymlinkReparsePointOrDevice(sourceInfo))
            {
                failure = new(
                    "extension_boundary_not_regular_file",
                    "Executable extension file rejected: candidate must be a regular file without symbolic links, reparse points, or devices.");
                return false;
            }

            if (!TryValidateUnixIdentity(fullSourcePath, UnixRegularFileType, out failure))
                return false;

            if (sourceInfo.Length > maxBytes)
            {
                failure = new(
                    "extension_boundary_file_too_large",
                    $"Executable extension file rejected: file is too large ({sourceInfo.Length} bytes; maximum {maxBytes}).");
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            failure = new(
                "extension_boundary_file_inspection_failed",
                "Executable extension file rejected: candidate could not be inspected.");
            return false;
        }

        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cdidx-extension-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var stagedPath = Path.Combine(stagingDirectory, Path.GetFileName(fullSourcePath));
        try
        {
            using var source = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.SequentialScan);
            if (!TryValidateUnixIdentity(source, out failure))
                return false;

            Directory.CreateDirectory(stagingDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    stagingDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var destination = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                long copied = 0;
                while (true)
                {
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;

                    copied += read;
                    if (copied > maxBytes)
                        throw new InvalidDataException("Executable extension exceeded its byte budget while staging.");
                    hash.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagedPath, UnixFileMode.UserRead);
                File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            else
            {
                File.SetAttributes(stagedPath, FileAttributes.ReadOnly);
            }

            staging = new ExecutableExtensionStagingHandle(
                fullSourcePath,
                stagedPath,
                fingerprint,
                stagingDirectory);
            StagedForTesting?.Invoke(fullSourcePath, stagedPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            try
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(stagingDirectory))
                    File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Best effort after a rejected staging attempt.
            }

            failure = new(
                "extension_boundary_staging_failed",
                "Executable extension file rejected: verified bytes could not be copied into private staging.");
            return false;
        }
    }

    private static bool TryValidateUnixIdentity(
        string path,
        uint expectedFileType,
        out ExecutableExtensionBoundaryFailure failure)
    {
        failure = default;
        if (OperatingSystem.IsWindows())
            return true;

        if (!UnixFileIdentity.TryRead(path, out var mode, out var ownerId))
        {
            failure = new(
                "extension_boundary_identity_inspection_failed",
                "Executable extension path rejected: owner and mode could not be inspected.");
            return false;
        }

        if ((mode & UnixFileTypeMask) != expectedFileType)
        {
            failure = new(
                "extension_boundary_not_regular_path",
                "Executable extension path rejected: filesystem object type is not supported.");
            return false;
        }

        var effectiveUserId = UnixFileIdentity.EffectiveUserId;
        if (ownerId != 0 && ownerId != effectiveUserId)
        {
            failure = new(
                "extension_boundary_owner_mismatch",
                "Executable extension path rejected: every component must be owned by the current user or the system administrator.");
            return false;
        }

        var groupOrOtherWritable = (mode & (UnixGroupWrite | UnixOtherWrite)) != 0;
        var stickyDirectory = expectedFileType == UnixDirectoryType && (mode & UnixStickyBit) != 0;
        if (groupOrOtherWritable && !stickyDirectory)
        {
            failure = new(
                "extension_boundary_unsafe_permissions",
                "Executable extension path rejected: group- or world-writable components are not allowed.");
            return false;
        }

        return true;
    }

    private static bool TryValidateUnixIdentity(
        FileStream stream,
        out ExecutableExtensionBoundaryFailure failure)
    {
        failure = default;
        if (OperatingSystem.IsWindows())
            return true;

        if (!UnixFileIdentity.TryRead(stream, out var mode, out var ownerId))
        {
            failure = new(
                "extension_boundary_identity_inspection_failed",
                "Executable extension file rejected: the opened file identity could not be inspected.");
            return false;
        }

        if ((mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            failure = new(
                "extension_boundary_not_regular_file",
                "Executable extension file rejected: the opened candidate is not a regular file.");
            return false;
        }

        var effectiveUserId = UnixFileIdentity.EffectiveUserId;
        if (ownerId != 0 && ownerId != effectiveUserId)
        {
            failure = new(
                "extension_boundary_owner_mismatch",
                "Executable extension file rejected: the opened candidate is not owned by the current user or the system administrator.");
            return false;
        }

        if ((mode & (UnixGroupWrite | UnixOtherWrite)) != 0)
        {
            failure = new(
                "extension_boundary_unsafe_permissions",
                "Executable extension file rejected: the opened candidate is group- or world-writable.");
            return false;
        }

        return true;
    }

    private static bool IsSystemOwnedUnixSymlink(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;

        return UnixFileIdentity.TryRead(path, out _, out var ownerId) && ownerId == 0;
    }

    private static class UnixFileIdentity
    {
        internal static uint EffectiveUserId => GetEffectiveUserId();

        internal static bool TryRead(string path, out uint mode, out uint ownerId)
        {
            mode = 0;
            ownerId = 0;
            if (OperatingSystem.IsMacOS())
            {
                if (DarwinLStat(path, out var stat) != 0)
                    return false;
                mode = stat.Mode;
                ownerId = stat.OwnerId;
                return true;
            }

            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (LinuxArm64LStat(path, out var stat) != 0)
                    return false;
                mode = stat.Mode;
                ownerId = stat.OwnerId;
                return true;
            }

            if (LinuxX64LStat(path, out var linuxStat) != 0)
                return false;
            mode = linuxStat.Mode;
            ownerId = linuxStat.OwnerId;
            return true;
        }

        internal static bool TryRead(FileStream stream, out uint mode, out uint ownerId)
        {
            mode = 0;
            ownerId = 0;
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (OperatingSystem.IsMacOS())
            {
                if (DarwinFStat(descriptor, out var stat) != 0)
                    return false;
                mode = stat.Mode;
                ownerId = stat.OwnerId;
                return true;
            }

            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (LinuxArm64FStat(descriptor, out var stat) != 0)
                    return false;
                mode = stat.Mode;
                ownerId = stat.OwnerId;
                return true;
            }

            if (LinuxX64FStat(descriptor, out var linuxStat) != 0)
                return false;
            mode = linuxStat.Mode;
            ownerId = linuxStat.OwnerId;
            return true;
        }

        [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
        private static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        private static extern int DarwinLStat(string path, out DarwinStat stat);

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        private static extern int LinuxX64LStat(string path, out LinuxX64Stat stat);

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        private static extern int LinuxArm64LStat(string path, out LinuxArm64Stat stat);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int DarwinFStat(int descriptor, out DarwinStat stat);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int LinuxX64FStat(int descriptor, out LinuxX64Stat stat);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int LinuxArm64FStat(int descriptor, out LinuxArm64Stat stat);

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct DarwinStat
        {
            internal int Device;
            internal ushort Mode;
            internal ushort LinkCount;
            internal ulong Inode;
            internal uint OwnerId;
        }

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct LinuxX64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal ulong LinkCount;
            internal uint Mode;
            internal uint OwnerId;
        }

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct LinuxArm64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal uint Mode;
            internal uint LinkCount;
            internal uint OwnerId;
        }
    }
}
