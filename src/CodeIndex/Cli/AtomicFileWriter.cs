using System.Text;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class AtomicFileWriter
{
    public static void WriteText(string path, string contents, Encoding encoding, Action<string>? applyFileMode = null)
    {
        Write(
            path,
            stream =>
            {
                using var writer = new StreamWriter(stream, encoding, bufferSize: 1024, leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
            },
            applyFileMode);
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null, Action<string>? applyFileMode = null)
    {
        Write(path, stream => JsonSerializer.Serialize(stream, value, options), applyFileMode);
    }

    public static void Write(string path, Action<Stream> writeContents, Action<string>? applyFileMode = null)
    {
        ArgumentNullException.ThrowIfNull(writeContents);

        var tempPath = BuildTempPath(path);
        var ioTempPath = LongPath.EnsureWindowsPrefix(tempPath);
        var ioTargetPath = LongPath.EnsureWindowsPrefix(path);
        var moved = false;

        try
        {
            using (var stream = new FileStream(ioTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                applyFileMode?.Invoke(ioTempPath);
                writeContents(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(ioTempPath, ioTargetPath, overwrite: true);
            moved = true;
            applyFileMode?.Invoke(ioTargetPath);
        }
        catch
        {
            if (!moved)
                TryDelete(ioTempPath);
            throw;
        }
    }

    private static string BuildTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
