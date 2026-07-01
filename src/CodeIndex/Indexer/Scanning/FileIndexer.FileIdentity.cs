using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static bool TryGetFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindowsFileIdentity(path, out identity);

            if (OperatingSystem.IsMacOS())
            {
                if (StatMac(path, out var stat) != 0)
                    return false;

                identity = new FileIdentity((uint)stat.DeviceId, stat.Inode);
                return true;
            }

            if (StatLinux(path, out var linuxStat) != 0)
                return false;

            identity = new FileIdentity(linuxStat.DeviceId, linuxStat.Inode);
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

    private static bool TryGetWindowsFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        using var handle = CreateFile(
            path,
            desiredAccess: 0,
            shareMode: FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: FileMode.Open,
            flagsAndAttributes: FileAttributes.Normal,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        identity = new FileIdentity(info.VolumeSerialNumber, fileIndex);
        return true;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatLinux(string path, out LinuxStat stat);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatMac(string path, out MacStat stat);

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
