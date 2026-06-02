using System.Runtime.InteropServices;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class DataDirectorySecurity
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal const UnixFileMode PermissionBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static DirectoryInfo CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if (IsCdidxDirectory(directory.FullName))
            ApplyPrivateMode(directory.FullName);
        return directory;
    }

    public static DirectoryInfo CreateSensitiveDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        ApplyPrivateMode(directory.FullName);
        return directory;
    }

    public static void ApplyPrivateMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    public static void ApplyPrivateFileMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        File.SetUnixFileMode(path, PrivateFileMode);
    }

    public static void WritePrivateText(string path, string contents, Encoding? encoding = null)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        using var stream = File.Open(ioPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        ApplyPrivateFileMode(ioPath);
        stream.SetLength(0);
        var outputEncoding = encoding is null || encoding.CodePage == Encoding.UTF8.CodePage ? Utf8NoBom : encoding;
        using var writer = new StreamWriter(stream, outputEncoding);
        writer.Write(contents);
    }

    public static string? ReadTextWithinLimit(string path, int maxBytes, FileShare share = FileShare.Read)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum byte count must be positive.");

        var ioPath = LongPath.EnsureWindowsPrefix(path);
        using var stream = File.Open(ioPath, FileMode.Open, FileAccess.Read, share);
        using var output = new MemoryStream(capacity: Math.Min(maxBytes, 8192));
        var buffer = new byte[Math.Min(maxBytes + 1, 8192)];
        var total = 0;
        while (true)
        {
            var remaining = maxBytes + 1 - total;
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                return null;

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static string? GetUnixModeString(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !IsCdidxDirectory(path) ||
            !Directory.Exists(path))
        {
            return null;
        }

        var mode = File.GetUnixFileMode(path) & PermissionBits;
        return Convert.ToString((int)mode, 8).PadLeft(4, '0');
    }

    private static bool IsCdidxDirectory(string path) =>
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            ".cdidx",
            StringComparison.Ordinal);
}
