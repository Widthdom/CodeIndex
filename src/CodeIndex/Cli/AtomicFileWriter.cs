using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class AtomicFileWriter
{
    internal static Action<string>? FlushParentDirectoryForTesting { get; set; }

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
            FlushParentDirectory(path);
        }
        catch
        {
            if (!moved)
                TryDelete(ioTempPath);
            throw;
        }
    }

    private static void FlushParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
            return;

        if (FlushParentDirectoryForTesting != null)
        {
            try
            {
                FlushParentDirectoryForTesting(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw BuildDirectoryFlushException(path, ex);
            }
            return;
        }

        if (OperatingSystem.IsWindows())
            return;

        var fd = UnixOpen(directory, flags: 0);
        if (fd < 0)
            throw BuildDirectoryFlushException(path, Marshal.GetLastPInvokeError());

        try
        {
            if (UnixFsync(fd) != 0)
                throw BuildDirectoryFlushException(path, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private static IOException BuildDirectoryFlushException(string path, int errno)
        => new($"Atomic replace completed for {ConsoleUi.FormatBoundedValue(path)}, but the parent directory could not be flushed to disk (errno {errno}).");

    private static IOException BuildDirectoryFlushException(string path, Exception inner)
        => new($"Atomic replace completed for {ConsoleUi.FormatBoundedValue(path)}, but the parent directory could not be flushed to disk ({CommandErrorWriter.FormatSanitizedException(inner)}).", inner);

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

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fd);
}
