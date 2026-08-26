using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const int LinuxAtCurrentWorkingDirectory = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const uint LinuxStatxBasicStats = 0x07ff;
    private const FileAttributes WindowsFileFlagBackupSemantics = (FileAttributes)0x02000000;
    private const FileAttributes WindowsFileFlagOpenReparsePoint = (FileAttributes)0x00200000;
    private const FileAccess WindowsFileListDirectory = (FileAccess)0x00000001;
    private const int WindowsFileIdBothDirectoryInfo = 10;
    private const int WindowsNoMoreFiles = 18;
    private const int WindowsDirectoryEntryBufferBytes = 64 * 1024;
    private const int WindowsDirectoryEntryNameOffset = 104;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int MacOpenDirectory = 0x100000;
    private const int MacOpenNoFollow = 0x100;
    private static readonly bool IsLinuxPlatform = OperatingSystem.IsLinux();
    private static readonly bool IsMacOSPlatform = OperatingSystem.IsMacOS();
    private static readonly bool IsFileIdentityWindowsPlatform = OperatingSystem.IsWindows();
    private static readonly bool FileIdentitySupportedPlatform = IsLinuxPlatform || IsMacOSPlatform || IsFileIdentityWindowsPlatform;

    internal static bool TryGetFileIdentity(string path, out FileIdentity identity)
        => TryGetFileIdentity(path, out identity, out _);

    internal static bool TryOpenDirectoryIdentityHandle(
        string path,
        out SafeFileHandle handle,
        out FileIdentity identity)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        identity = default;
        if (!FileIdentitySupportedPlatform)
            return false;

        try
        {
            if (IsFileIdentityWindowsPlatform)
            {
                handle.Dispose();
                handle = CreateFile(
                    LongPath.EnsureWindowsPrefix(path),
                    desiredAccess: WindowsFileListDirectory,
                    shareMode: FileShare.ReadWrite | FileShare.Delete,
                    securityAttributes: IntPtr.Zero,
                    creationDisposition: FileMode.Open,
                    flagsAndAttributes: WindowsFileFlagBackupSemantics | WindowsFileFlagOpenReparsePoint,
                    templateFile: IntPtr.Zero);
            }
            else
            {
                var flags = IsLinuxPlatform
                    ? LinuxOpenDirectory | LinuxOpenNoFollow
                    : MacOpenDirectory | MacOpenNoFollow;
                var descriptor = OpenUnix(path, flags);
                if (descriptor < 0)
                    return false;

                handle.Dispose();
                handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }

            if (handle.IsInvalid || !TryGetFileIdentity(handle, out identity))
            {
                handle.Dispose();
                handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            handle.Dispose();
            handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
            return false;
        }
    }

    internal static bool TryGetFileIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (handle.IsInvalid || handle.IsClosed || !FileIdentitySupportedPlatform)
            return false;

        try
        {
            if (IsFileIdentityWindowsPlatform)
            {
                if (!GetFileInformationByHandle(handle, out var windowsInfo))
                    return false;

                var fileIndex = ((ulong)windowsInfo.FileIndexHigh << 32) | windowsInfo.FileIndexLow;
                identity = new FileIdentity(windowsInfo.VolumeSerialNumber, fileIndex);
                return true;
            }

            var descriptor = handle.DangerousGetHandle().ToInt32();
            if (IsMacOSPlatform)
            {
                if (FStatMac(descriptor, out var macStat) != 0)
                    return false;

                identity = new FileIdentity((uint)macStat.DeviceId, macStat.Inode);
                return true;
            }

            if (FStatLinux(descriptor, out var linuxStat) != 0)
                return false;

            identity = new FileIdentity(linuxStat.DeviceId, linuxStat.Inode);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryGetDirectoryHandlePath(SafeFileHandle handle, out string path)
    {
        path = string.Empty;
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        if (IsLinuxPlatform)
        {
            path = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}";
            return true;
        }

        return false;
    }

    internal static bool TryEnumerateDirectoryHandleEntries(
        SafeFileHandle handle,
        out IReadOnlyList<string> entryNames)
    {
        entryNames = [];
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        if (IsLinuxPlatform)
        {
            if (!TryGetDirectoryHandlePath(handle, out var handlePath))
                return false;

            entryNames = CodeIndex.FileSystemTraversalPolicy
                .EnumerateFileSystemEntries(handlePath)
                .Select(static entry => Path.GetFileName(entry))
                .ToArray();
            return true;
        }

        if (IsFileIdentityWindowsPlatform)
            return TryEnumerateWindowsDirectoryHandleEntries(handle, out entryNames);
        if (!IsMacOSPlatform)
            return false;

        var duplicateDescriptor = DuplicateUnixDescriptor(handle.DangerousGetHandle().ToInt32());
        if (duplicateDescriptor < 0)
            return false;

        var directory = OpenDirectoryStream(duplicateDescriptor);
        if (directory == IntPtr.Zero)
        {
            _ = CloseUnixDescriptor(duplicateDescriptor);
            return false;
        }

        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectoryEntry(directory);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                        return false;
                    break;
                }

                const int macNameLengthOffset = 18;
                const int macNameOffset = 21;
                var nameLength = (ushort)Marshal.ReadInt16(entry, macNameLengthOffset);
                if (nameLength == 0 || nameLength > 1024)
                    return false;

                var bytes = new byte[nameLength];
                Marshal.Copy(IntPtr.Add(entry, macNameOffset), bytes, 0, nameLength);
                var name = Encoding.UTF8.GetString(bytes);
                if (name is not "." and not "..")
                    names.Add(name);
            }

            entryNames = names;
            return true;
        }
        finally
        {
            _ = CloseDirectoryStream(directory);
        }
    }

    private static bool TryEnumerateWindowsDirectoryHandleEntries(
        SafeFileHandle handle,
        out IReadOnlyList<string> entryNames)
    {
        entryNames = [];
        var buffer = Marshal.AllocHGlobal(WindowsDirectoryEntryBufferBytes);
        try
        {
            var names = new List<string>();
            while (true)
            {
                if (!GetFileInformationByHandleEx(
                        handle,
                        WindowsFileIdBothDirectoryInfo,
                        buffer,
                        WindowsDirectoryEntryBufferBytes))
                {
                    if (Marshal.GetLastPInvokeError() == WindowsNoMoreFiles)
                    {
                        entryNames = names;
                        return true;
                    }

                    return false;
                }

                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > WindowsDirectoryEntryBufferBytes - WindowsDirectoryEntryNameOffset)
                        return false;

                    var entry = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var nameByteLength = Marshal.ReadInt32(entry, 60);
                    if (nameByteLength < 0
                        || (nameByteLength & 1) != 0
                        || nameByteLength > WindowsDirectoryEntryBufferBytes - offset - WindowsDirectoryEntryNameOffset)
                    {
                        return false;
                    }

                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(entry, WindowsDirectoryEntryNameOffset),
                        nameByteLength / sizeof(char));
                    if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                        names.Add(name);

                    if (nextOffset == 0)
                        break;
                    if (nextOffset < WindowsDirectoryEntryNameOffset)
                        return false;
                    offset = checked(offset + nextOffset);
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool TryGetFileIdentity(string path, out FileIdentity identity, out ulong linkCount)
    {
        identity = default;
        linkCount = 0;
        if (!FileIdentitySupportedPlatform)
            return false;

        try
        {
            if (IsFileIdentityWindowsPlatform)
                return TryGetWindowsFileIdentity(path, out identity, out linkCount);

            if (IsMacOSPlatform)
            {
                if (StatMac(path, out var stat) != 0)
                    return false;

                identity = new FileIdentity((uint)stat.DeviceId, stat.Inode);
                linkCount = stat.LinkCount;
                return true;
            }

            if (StatLinux(path, out var linuxStat) != 0)
                return false;

            identity = new FileIdentity(linuxStat.DeviceId, linuxStat.Inode);
            linkCount = linuxStat.LinkCount;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryGetUnixFileOwnerId(string path, out uint ownerId)
        => TryGetUnixFileOwnerAndGroupIds(path, out ownerId, out _);

    internal static bool TryGetUnixFileOwnerAndGroupIds(string path, out uint ownerId, out uint groupId)
    {
        ownerId = 0;
        groupId = 0;
        if (!IsLinuxPlatform && !IsMacOSPlatform)
            return false;

        try
        {
            if (IsMacOSPlatform)
            {
                if (StatMac(path, out var stat) != 0)
                    return false;

                ownerId = stat.Uid;
                groupId = stat.Gid;
                return true;
            }

            if (!TryGetLinuxStatx(path, out var linuxStat))
                return false;

            ownerId = linuxStat.Uid;
            groupId = linuxStat.Gid;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryGetFileLinkCount(string path, out ulong linkCount)
    {
        linkCount = 0;
        if (IsLinuxPlatform)
        {
            if (!TryGetLinuxStatx(path, out var linuxStat))
                return false;

            linkCount = linuxStat.LinkCount;
            return true;
        }

        return TryGetFileIdentity(path, out _, out linkCount);
    }

    private static bool TryGetLinuxStatx(string path, out LinuxStatx stat)
    {
        stat = default;
        try
        {
            return StatxLinux(
                       LinuxAtCurrentWorkingDirectory,
                       path,
                       LinuxAtSymlinkNoFollow,
                       LinuxStatxBasicStats,
                       out stat)
                   == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetWindowsFileIdentity(string path, out FileIdentity identity, out ulong linkCount)
    {
        identity = default;
        linkCount = 0;
        using var handle = CreateFile(
            LongPath.EnsureWindowsPrefix(path),
            desiredAccess: 0,
            shareMode: FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: FileMode.Open,
            flagsAndAttributes: Directory.Exists(path)
                ? WindowsFileFlagBackupSemantics
                : FileAttributes.Normal,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        identity = new FileIdentity(info.VolumeSerialNumber, fileIndex);
        linkCount = info.NumberOfLinks;
        return true;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatLinux(string path, out LinuxStat stat);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatMac(string path, out MacStat stat);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatLinux(int descriptor, out LinuxStat stat);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatMac(int descriptor, out MacStat stat);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(string path, int flags);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateUnixDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr OpenDirectoryStream(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDirectoryEntry(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectoryStream(IntPtr directory);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseUnixDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxLinux(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx stat);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        [MarshalAs(UnmanagedType.U4)] FileAccess desiredAccess,
        [MarshalAs(UnmanagedType.U4)] FileShare shareMode,
        IntPtr securityAttributes,
        [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
        [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out WindowsFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong DeviceId;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        public int Pad0;
        public ulong Rdev;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessTimeSeconds;
        public long AccessTimeNanoseconds;
        public long ModificationTimeSeconds;
        public long ModificationTimeNanoseconds;
        public long ChangeTimeSeconds;
        public long ChangeTimeNanoseconds;
        public long Unused0;
        public long Unused1;
        public long Unused2;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(16)]
        public uint LinkCount;

        [FieldOffset(20)]
        public uint Uid;

        [FieldOffset(24)]
        public uint Gid;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(40)]
        public ulong Size;

        [FieldOffset(112)]
        public long ModificationTimeSeconds;

        [FieldOffset(120)]
        public uint ModificationTimeNanoseconds;

        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public int DeviceId;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint Uid;
        public uint Gid;
        public int Rdev;
        public MacTimespec AccessTime;
        public MacTimespec ModificationTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Qspare0;
        public long Qspare1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
