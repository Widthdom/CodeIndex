using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        return IsCdidxDirectory(path)
            ? CreateDirectoryWithPrivateMode(path)
            : Directory.CreateDirectory(path);
    }

    public static DirectoryInfo CreateSensitiveDirectory(string path)
    {
        return CreateDirectoryWithPrivateMode(path);
    }

    public static DirectoryInfo? CreateSensitiveParentDirectoryForFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        return Directory.Exists(directory) && IsSharedDirectoryRoot(directory)
            ? new DirectoryInfo(directory)
            : CreateSensitiveDirectory(directory);
    }

    public static DirectoryInfo CreateSensitiveTempDirectory(string prefix)
    {
        var path = Path.Combine(GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        return CreateSensitiveDirectory(path);
    }

    public static string ResolveSensitiveTempFallbackDirectory(string purpose)
        => Path.GetFullPath(Path.Combine(
            ResolveSensitiveTempFallbackRootDirectory(),
            NormalizeSensitiveTempFallbackPurpose(purpose)));

    private static string ResolveSensitiveTempFallbackRootDirectory()
    {
        var identity = $"{Environment.UserName}\0{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var scope = "cdidx-u" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return Path.GetFullPath(Path.Combine(GetTempPath(), scope));
    }

    private static string NormalizeSensitiveTempFallbackPurpose(string purpose)
        => string.IsNullOrWhiteSpace(purpose)
            ? "state"
            : purpose.Trim();

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

    private static DirectoryInfo CreateDirectoryWithPrivateMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Directory.CreateDirectory(path);

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) && IsSharedDirectoryRoot(fullPath))
            return new DirectoryInfo(fullPath);

        EnsureSensitiveTempFallbackRoot(fullPath);
        RejectUnsafeDirectoryTarget(fullPath);

        var directory = Directory.CreateDirectory(fullPath, PrivateDirectoryMode);
        RejectUnsafeDirectoryTarget(directory.FullName);
        ApplyPrivateMode(directory.FullName);
        return directory;
    }

    private static void EnsureSensitiveTempFallbackRoot(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = ResolveSensitiveTempFallbackRootDirectory();
        if (!IsSameDirectory(path, root) && !IsDirectoryDescendant(path, root))
            return;

        RejectUnsafeDirectoryTarget(root);
        Directory.CreateDirectory(root, PrivateDirectoryMode);
        RejectUnsafeDirectoryTarget(root);
        ApplyPrivateMode(root);
    }

    private static void RejectUnsafeDirectoryTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Refusing to use symbolic link or reparse point directory {ConsoleUi.FormatBoundedValue(path)} for sensitive cdidx state.");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static bool IsSharedDirectoryRoot(string path)
    {
        if (IsSameDirectory(path, GetTempPath()))
            return true;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && IsSameDirectory(path, userProfile))
            return true;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData) && IsSameDirectory(path, localAppData))
            return true;

        var pathRoot = Path.GetPathRoot(Path.GetFullPath(path));
        return !string.IsNullOrWhiteSpace(pathRoot) && IsSameDirectory(path, pathRoot);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDirectoryDescendant(string path, string ancestor)
    {
        try
        {
            var normalizedPath = NormalizeDirectory(path);
            var normalizedAncestor = NormalizeDirectory(ancestor);
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return normalizedPath.Length > normalizedAncestor.Length
                && normalizedPath.StartsWith(normalizedAncestor, comparison)
                && IsDirectorySeparator(normalizedPath[normalizedAncestor.Length]);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDirectorySeparator(char value)
        => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static string NormalizeDirectory(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string GetTempPath() => Path.GetTempPath();

    public static void WritePrivateText(string path, string contents, Encoding? encoding = null)
    {
        var outputEncoding = encoding is null || encoding.CodePage == Encoding.UTF8.CodePage ? Utf8NoBom : encoding;
        AtomicFileWriter.WriteText(path, contents, outputEncoding, AtomicFileWriter.WriteProfile.Sensitive);
    }

    public static byte[]? ReadBytesWithinLimit(string path, int maxBytes, FileShare share = FileShare.Read)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum byte count must be positive.");

        var ioPath = LongPath.EnsureWindowsPrefix(path);
        using var stream = File.Open(ioPath, FileMode.Open, FileAccess.Read, share);
        using var output = new MemoryStream(capacity: Math.Min(maxBytes, 8192));
        var buffer = new byte[Math.Min(maxBytes, 8192)];
        var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                return null;

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public static string? ReadTextWithinLimit(string path, int maxBytes, FileShare share = FileShare.Read)
    {
        var bytes = ReadBytesWithinLimit(path, maxBytes, share);
        return bytes is null ? null : DecodeText(bytes);
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // DecodeText is reached only from ReadTextWithinLimit after ReadBytesWithinLimit enforces maxBytes.
        return reader.ReadToEnd();
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
