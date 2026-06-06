using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class PrivateLogFile
{
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const int MaxExistingFilesToHarden = 128;

    internal static FileStream OpenAppend(string path, FileShare share = FileShare.ReadWrite)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Append, FileAccess.Write, share);

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = share,
            UnixCreateMode = PrivateFileMode,
        });
    }

    internal static StreamWriter OpenAppendText(string path)
        => new(OpenAppend(path), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

    internal static void TrySetPrivatePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    internal static void HardenExisting(string directory, string pattern)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var hardened = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                TrySetPrivatePermissions(file.FullName);
                hardened++;
                if (hardened >= MaxExistingFilesToHarden)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    internal static void PruneOldFiles(string directory, string pattern, int retainedFileCount)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directory);
            var retainedFiles = SelectRetainedFiles(
                directoryInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly),
                retainedFileCount);
            var retainedPaths = new HashSet<string>(
                retainedFiles.Select(file => file.FullName),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var file in directoryInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldPruneFile(file, retainedPaths, retainedFiles, retainedFileCount))
                    file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static IReadOnlyList<FileInfo> SelectRetainedFiles(IEnumerable<FileInfo> files, int retainedFileCount)
    {
        if (retainedFileCount <= 0)
            return [];

        var retained = new List<FileInfo>(retainedFileCount);
        foreach (var file in files)
            AddRetainedFile(retained, file, retainedFileCount);

        return retained;
    }

    private static void AddRetainedFile(List<FileInfo> retained, FileInfo file, int retainedFileCount)
    {
        var insertAt = retained.FindIndex(existing => CompareRetentionOrder(file, existing) > 0);
        if (insertAt < 0)
        {
            if (retained.Count < retainedFileCount)
                retained.Add(file);
            return;
        }

        retained.Insert(insertAt, file);
        if (retained.Count > retainedFileCount)
            retained.RemoveAt(retained.Count - 1);
    }

    private static bool ShouldPruneFile(
        FileInfo file,
        HashSet<string> retainedPaths,
        IReadOnlyList<FileInfo> retainedFiles,
        int retainedFileCount)
    {
        if (retainedPaths.Contains(file.FullName))
            return false;
        if (retainedFileCount <= 0)
            return true;
        if (retainedFiles.Count < retainedFileCount)
            return false;

        return CompareRetentionOrder(file, retainedFiles[^1]) < 0;
    }

    private static int CompareRetentionOrder(FileInfo left, FileInfo right)
    {
        var modified = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        if (modified != 0)
            return modified;

        return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    internal static bool TryRotateSlots(string path, int retainedFileCount)
    {
        try
        {
            SafeDelete(SlotPath(path, retainedFileCount - 1));

            for (var slot = retainedFileCount - 2; slot >= 1; slot--)
            {
                var current = LongPath.EnsureWindowsPrefix(SlotPath(path, slot));
                var next = LongPath.EnsureWindowsPrefix(SlotPath(path, slot + 1));
                if (!File.Exists(current))
                    continue;
                if (File.Exists(next))
                    SafeDelete(next);
                File.Move(current, next);
            }

            var ioPath = LongPath.EnsureWindowsPrefix(path);
            if (File.Exists(ioPath))
            {
                var first = LongPath.EnsureWindowsPrefix(SlotPath(path, 1));
                if (File.Exists(first))
                    SafeDelete(first);
                File.Move(ioPath, first);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SlotPath(string path, int slot)
        => slot <= 0 ? path : path + "." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore: rotation is best-effort.
        }
    }
}
