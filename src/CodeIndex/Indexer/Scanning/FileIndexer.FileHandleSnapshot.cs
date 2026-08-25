using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxModificationTime = 0x0040;
    private const uint LinuxStatxInode = 0x0100;
    private const uint LinuxStatxSize = 0x0200;
    private const uint LinuxFileHandleSnapshotMask =
        LinuxStatxModificationTime | LinuxStatxInode | LinuxStatxSize;

    internal static bool TryGetFileHandleSnapshot(
        SafeFileHandle handle,
        out FileHandleSnapshot snapshot)
    {
        snapshot = default;
        if (handle.IsInvalid || handle.IsClosed || !FileIdentitySupportedPlatform)
            return false;

        try
        {
            if (IsFileIdentityWindowsPlatform)
                return TryGetWindowsFileHandleSnapshot(handle, out snapshot);

            var handleReferenceAdded = false;
            try
            {
                handle.DangerousAddRef(ref handleReferenceAdded);
                var descriptor = handle.DangerousGetHandle().ToInt32();
                if (IsMacOSPlatform)
                    return TryGetMacFileHandleSnapshot(descriptor, out snapshot);

                return TryGetLinuxFileHandleSnapshot(descriptor, out snapshot);
            }
            finally
            {
                if (handleReferenceAdded)
                    handle.DangerousRelease();
            }
        }
        catch (Exception ex) when (ex is
            DllNotFoundException
            or EntryPointNotFoundException
            or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool TryGetWindowsFileHandleSnapshot(
        SafeFileHandle handle,
        out FileHandleSnapshot snapshot)
    {
        snapshot = default;
        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        var unsignedLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        var unsignedFileTime =
            ((ulong)(uint)info.LastWriteTime.dwHighDateTime << 32)
            | (uint)info.LastWriteTime.dwLowDateTime;
        if (unsignedLength > long.MaxValue || unsignedFileTime > long.MaxValue)
            return false;

        DateTime modifiedUtc;
        try
        {
            modifiedUtc = DateTime.FromFileTimeUtc((long)unsignedFileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        snapshot = new FileHandleSnapshot(
            (long)unsignedLength,
            modifiedUtc,
            new FileIdentity(info.VolumeSerialNumber, fileIndex));
        return true;
    }

    private static bool TryGetMacFileHandleSnapshot(
        int descriptor,
        out FileHandleSnapshot snapshot)
    {
        snapshot = default;
        if (FStatMac(descriptor, out var stat) != 0
            || stat.Size < 0
            || !TryCreateUnixModifiedUtc(
                stat.ModificationTime.Seconds,
                stat.ModificationTime.Nanoseconds,
                out var modifiedUtc))
        {
            return false;
        }

        snapshot = new FileHandleSnapshot(
            stat.Size,
            modifiedUtc,
            new FileIdentity((uint)stat.DeviceId, stat.Inode));
        return true;
    }

    private static bool TryGetLinuxFileHandleSnapshot(
        int descriptor,
        out FileHandleSnapshot snapshot)
    {
        snapshot = default;
        if (StatxLinux(
                descriptor,
                string.Empty,
                LinuxAtEmptyPath,
                LinuxStatxBasicStats,
                out var stat) != 0
            || (stat.Mask & LinuxFileHandleSnapshotMask) != LinuxFileHandleSnapshotMask
            || stat.Size > long.MaxValue
            || !TryCreateUnixModifiedUtc(
                stat.ModificationTimeSeconds,
                stat.ModificationTimeNanoseconds,
                out var modifiedUtc))
        {
            return false;
        }

        snapshot = new FileHandleSnapshot(
            (long)stat.Size,
            modifiedUtc,
            new FileIdentity(
                EncodeLinuxDeviceId(stat.DeviceMajor, stat.DeviceMinor),
                stat.Inode));
        return true;
    }

    internal static bool TryCreateUnixModifiedUtc(
        long seconds,
        long nanoseconds,
        out DateTime modifiedUtc)
    {
        modifiedUtc = default;
        if (nanoseconds is < 0 or >= 1_000_000_000)
            return false;

        try
        {
            var ticksSinceUnixEpoch = checked(
                seconds * TimeSpan.TicksPerSecond
                + nanoseconds / 100);
            modifiedUtc = new DateTime(
                checked(DateTime.UnixEpoch.Ticks + ticksSinceUnixEpoch),
                DateTimeKind.Utc);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static ulong EncodeLinuxDeviceId(uint major, uint minor)
    {
        var majorBits = (ulong)major;
        var minorBits = (ulong)minor;
        return (minorBits & 0xffUL)
            | ((majorBits & 0xfffUL) << 8)
            | ((minorBits & ~0xffUL) << 12)
            | ((majorBits & ~0xfffUL) << 32);
    }
}
