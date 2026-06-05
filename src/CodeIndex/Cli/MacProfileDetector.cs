using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class MacProfileDetector
{
    internal const string CurrentAttrPath = "/proc/self/attr/current";
    internal const string ExecAttrPath = "/proc/self/attr/exec";
    internal const int MaxProcAttrReadChars = 4096;

    public static string? DetectCurrent()
        => DetectCurrent(ReadProcAttrFile);

    internal static string? DetectCurrent(Func<string, string> readAllText)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        var current = ReadProcAttr(readAllText, CurrentAttrPath);
        var exec = ReadProcAttr(readAllText, ExecAttrPath);

        return DetectFromProcAttrs(current, exec);
    }

    internal static string? DetectFromProcAttrs(string? current, string? exec)
    {
        current = BoundProcAttrValue(current);
        exec = BoundProcAttrValue(exec);

        var appArmor = TryExtractAppArmorProfile(current) ?? TryExtractAppArmorProfile(exec);
        if (appArmor != null)
            return $"apparmor:{appArmor}";

        var selinux = TryExtractSelinuxContext(current) ?? TryExtractSelinuxContext(exec);
        return selinux == null ? null : $"selinux:{selinux}";
    }

    internal static string BuildDatabaseHint(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return "Hint: check that `--db` points to a readable SQLite file, verify parent directory permissions, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";

        var displayProfile = FormatProfileForHint(profile);
        if (profile.StartsWith("apparmor:", StringComparison.OrdinalIgnoreCase))
            return $"Hint: this looks like an AppArmor confinement restriction ({displayProfile}); check `aa-status`, snap/flatpak permissions, and audit logs, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";

        if (profile.StartsWith("selinux:", StringComparison.OrdinalIgnoreCase))
            return $"Hint: this looks like an SELinux confinement restriction ({displayProfile}); check `getenforce`, `ausearch`, and `audit2why`, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";

        return $"Hint: this looks like a Linux MAC confinement restriction ({displayProfile}); check AppArmor/SELinux audit logs, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";
    }

    internal static bool IsPermissionStyleSqliteError(SqliteException ex)
        => ex.SqliteErrorCode is 3 or 10 or 14 or 23;

    internal static string ReadProcAttrFileForTesting(string path) => ReadProcAttrFile(path);

    private static string? ReadProcAttr(Func<string, string> readAllText, string path)
    {
        try
        {
            var value = BoundProcAttrValue(readAllText(path))?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ReadProcAttrFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
        var buffer = new char[MaxProcAttrReadChars];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static string? BoundProcAttrValue(string? value)
    {
        if (value == null)
            return null;

        return value.Length <= MaxProcAttrReadChars ? value : value[..MaxProcAttrReadChars];
    }

    private static string FormatProfileForHint(string profile)
    {
        return ConsoleUi.FormatBoundedValue(profile)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static string? TryExtractAppArmorProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "unconfined")
            return null;

        var marker = value.IndexOf(" (", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        var mode = value[(marker + 2)..].TrimEnd(')', ' ');
        return mode.Equals("enforce", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("complain", StringComparison.OrdinalIgnoreCase)
            ? value[..marker]
            : null;
    }

    private static string? TryExtractSelinuxContext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value == "unconfined"
            || !value.Contains(':', StringComparison.Ordinal)
            || value.Contains(" (", StringComparison.Ordinal))
        {
            return null;
        }

        return value;
    }
}
