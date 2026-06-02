using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class PrivateLogFile
{
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

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
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                TrySetPrivatePermissions(file.FullName);
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
            var oldFiles = new DirectoryInfo(directory)
                .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(retainedFileCount)
                .ToList();

            foreach (var file in oldFiles)
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
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
