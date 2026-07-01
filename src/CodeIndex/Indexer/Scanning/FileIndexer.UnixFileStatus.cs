using System.Runtime.InteropServices;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static class UnixFileStatus
    {
        internal const int FileTypeMask = 0xF000;
        internal const int RegularFile = 0x8000;

        internal static bool TryGetFileMode(string filePath, out int mode)
        {
            mode = 0;
            if (NativeMethods.Stat(filePath, out var status) != 0)
                return false;

            mode = status.Mode;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileStatus
        {
            internal FileStatusFlags Flags;
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
            internal long Dev;
            internal long RDev;
            internal long Ino;
            internal uint UserFlags;
        }

        [Flags]
        private enum FileStatusFlags : uint
        {
            None = 0,
        }

        private static class NativeMethods
        {
            [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi)]
            internal static extern int Stat(string path, out FileStatus output);
        }
    }
}
