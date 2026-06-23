using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class AtomicFileWriter
{
    internal const int MaxTempFileNameChars = 120;
    private const int MaxTempStemChars = 48;
    internal static Action<string>? FlushParentDirectoryForTesting { get; set; }

    public enum WriteProfile
    {
        Public,
        Sensitive,
    }

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

    public static void WriteText(string path, string contents, Encoding encoding, WriteProfile profile)
    {
        Write(
            path,
            stream =>
            {
                using var writer = new StreamWriter(stream, encoding, bufferSize: 1024, leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
            },
            profile);
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null, Action<string>? applyFileMode = null)
    {
        Write(path, stream => JsonSerializer.Serialize(stream, value, options), applyFileMode);
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options, WriteProfile profile)
    {
        Write(path, stream => JsonSerializer.Serialize(stream, value, options), profile);
    }

    public static void WriteJson<T>(string path, T value, WriteProfile profile)
    {
        WriteJson(path, value, options: null, profile);
    }

    public static void Write(string path, Action<Stream> writeContents, Action<string>? applyFileMode = null)
        => WriteCore(path, writeContents, applyFileMode, WriteProfile.Public);

    public static void Write(string path, Action<Stream> writeContents, WriteProfile profile)
        => WriteCore(path, writeContents, ResolveProfileModeCallback(profile), profile);

    private static void WriteCore(
        string path,
        Action<Stream> writeContents,
        Action<string>? applyFileMode,
        WriteProfile profile)
    {
        ArgumentNullException.ThrowIfNull(writeContents);

        var tempPath = BuildTempPath(path);
        var ioTempPath = LongPath.EnsureWindowsPrefix(tempPath);
        var moved = false;

        try
        {
            using (var stream = CreateTempFile(ioTempPath, profile))
            {
                applyFileMode?.Invoke(ioTempPath);
                writeContents(stream);
                stream.Flush(flushToDisk: true);
            }

            MoveReplacing(tempPath, path);
            moved = true;
        }
        catch
        {
            if (!moved)
                TryDeleteFile(ioTempPath);
            throw;
        }
    }

    private static FileStream CreateTempFile(string path, WriteProfile profile)
    {
        if (profile != WriteProfile.Sensitive || OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = DataDirectorySecurity.PrivateFileMode,
            });
    }

    private static Action<string>? ResolveProfileModeCallback(WriteProfile profile)
        => profile == WriteProfile.Sensitive ? DataDirectorySecurity.ApplyPrivateFileMode : null;

    internal static void MoveReplacing(string sourcePath, string destinationPath)
    {
        MoveFileCore(sourcePath, destinationPath, overwrite: true, applyDestinationMode: null);
        FlushParentDirectoryAfterReplace(destinationPath);
    }

    internal static void MoveFile(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        Action<string>? applyDestinationMode = null)
        => MoveFileCore(sourcePath, destinationPath, overwrite, applyDestinationMode);

    internal static void PublishDirectory(string tempDirectoryPath, string destinationDirectoryPath)
    {
        Directory.Move(
            LongPath.EnsureWindowsPrefix(tempDirectoryPath),
            LongPath.EnsureWindowsPrefix(destinationDirectoryPath));
        FlushParentDirectoryAfterDirectoryPublish(destinationDirectoryPath);
    }

    internal static void DeleteFileIfExists(string path)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        if (File.Exists(ioPath))
            File.Delete(ioPath);
    }

    internal static bool TryDeleteFile(
        string path,
        Action<Exception>? onCleanupFailure = null,
        Action<string>? deleteOverride = null)
    {
        try
        {
            var ioPath = LongPath.EnsureWindowsPrefix(path);
            if (!File.Exists(ioPath))
                return false;

            if (deleteOverride != null)
                deleteOverride(path);
            else
                File.Delete(ioPath);

            return true;
        }
        catch (Exception ex) when (IsRecoverableFileMutationException(ex))
        {
            ReportCleanupFailure(onCleanupFailure, ex);
            return false;
        }
    }

    private static void MoveFileCore(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        Action<string>? applyDestinationMode)
    {
        File.Move(
            LongPath.EnsureWindowsPrefix(sourcePath),
            LongPath.EnsureWindowsPrefix(destinationPath),
            overwrite);
        applyDestinationMode?.Invoke(destinationPath);
    }

    internal static void FlushParentDirectoryAfterReplace(string path)
        => FlushParentDirectory(
            path,
            "Atomic replace completed",
            "the target file was already replaced");

    private static void FlushParentDirectoryAfterDirectoryPublish(string path)
        => FlushParentDirectory(
            path,
            "Directory publish completed",
            "the destination directory was already published");

    private static void FlushParentDirectory(string path, string completedMessage, string completedStateMessage)
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
                throw BuildDirectoryFlushException(path, completedMessage, completedStateMessage, ex);
            }
            return;
        }

        if (OperatingSystem.IsWindows())
            return;

        var fd = UnixOpen(directory, flags: 0);
        if (fd < 0)
            throw BuildDirectoryFlushException(path, completedMessage, completedStateMessage, Marshal.GetLastPInvokeError());

        try
        {
            if (UnixFsync(fd) != 0)
                throw BuildDirectoryFlushException(path, completedMessage, completedStateMessage, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private static IOException BuildDirectoryFlushException(
        string path,
        string completedMessage,
        string completedStateMessage,
        int errno)
        => new($"{completedMessage} for {ConsoleUi.FormatBoundedValue(path)}; {completedStateMessage}, but the parent directory could not be flushed to disk (errno {errno}).");

    private static IOException BuildDirectoryFlushException(
        string path,
        string completedMessage,
        string completedStateMessage,
        Exception inner)
        => new($"{completedMessage} for {ConsoleUi.FormatBoundedValue(path)}; {completedStateMessage}, but the parent directory could not be flushed to disk ({CommandErrorWriter.FormatSanitizedException(inner)}).", inner);

    private static bool IsRecoverableFileMutationException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    internal static string BuildTempPathForTesting(string path) => BuildTempPath(path);

    private static string BuildTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "target";
        if (stem.Length > MaxTempStemChars)
            stem = stem[..MaxTempStemChars];

        var tempFileName = $".cdidx-{stem}.{Guid.NewGuid():N}.tmp";
        if (tempFileName.Length > MaxTempFileNameChars)
            tempFileName = $".cdidx-{Guid.NewGuid():N}.tmp";

        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    private static void ReportCleanupFailure(Action<Exception>? failureSink, Exception exception)
    {
        if (failureSink is null)
            return;

        try
        {
            failureSink(exception);
        }
        catch
        {
            // Cleanup diagnostics must not mask the original file mutation result.
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fd);
}
