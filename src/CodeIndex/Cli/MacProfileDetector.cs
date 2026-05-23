using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class MacProfileDetector
{
    internal const string CurrentAttrPath = "/proc/self/attr/current";
    internal const string ExecAttrPath = "/proc/self/attr/exec";

    public static string? DetectCurrent()
        => DetectCurrent(File.ReadAllText);

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

        if (profile.StartsWith("apparmor:", StringComparison.OrdinalIgnoreCase))
            return $"Hint: this looks like an AppArmor confinement restriction ({profile}); check `aa-status`, snap/flatpak permissions, and audit logs, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";

        if (profile.StartsWith("selinux:", StringComparison.OrdinalIgnoreCase))
            return $"Hint: this looks like an SELinux confinement restriction ({profile}); check `getenforce`, `ausearch`, and `audit2why`, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";

        return $"Hint: this looks like a Linux MAC confinement restriction ({profile}); check AppArmor/SELinux audit logs, move the index to a writable location, or use a SQLite `file:` URI with `immutable=1` for read-only mounts.";
    }

    internal static bool IsPermissionStyleSqliteError(SqliteException ex)
        => ex.SqliteErrorCode is 3 or 10 or 14 or 23;

    private static string? ReadProcAttr(Func<string, string> readAllText, string path)
    {
        try
        {
            var value = readAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
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
