using System.Text;

namespace CodeIndex.Indexer;

internal static class FileWriteProbe
{
    internal static void WriteEmptyFile(string path, Encoding? encoding = null)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        using var stream = new FileStream(ioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (encoding is null)
            return;

        using var writer = new StreamWriter(stream, encoding);
        writer.Write(string.Empty);
    }

    internal static bool TryWriteAndDeleteEmptyFile(string path, Encoding? encoding = null)
    {
        try
        {
            WriteEmptyFile(path, encoding);
        }
        catch (Exception ex) when (IsWriteProbeFailure(ex))
        {
            return false;
        }

        return TryDeleteFileIfExists(path);
    }

    internal static void DeleteFileIfExists(string path)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        if (File.Exists(ioPath))
            File.Delete(ioPath);
    }

    private static bool TryDeleteFileIfExists(string path)
    {
        try
        {
            DeleteFileIfExists(path);
            return true;
        }
        catch (Exception ex) when (IsWriteProbeFailure(ex))
        {
            return false;
        }
    }

    private static bool IsWriteProbeFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException;
}
