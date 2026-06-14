using System.Runtime.InteropServices;
using System.Text;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class MacProfileDetector
{
    internal const string CurrentAttrPath = "/proc/self/attr/current";
    internal const string ExecAttrPath = "/proc/self/attr/exec";
    internal const int MaxProcAttrReadChars = 4096;

    public static string? DetectCurrent()
        => DetectCurrentWithDiagnostics().Profile;

    internal static MacProfileDetectionResult DetectCurrentWithDiagnostics()
        => DetectCurrentWithDiagnostics(ReadProcAttrFile);

    internal static string? DetectCurrent(Func<string, string> readAllText)
        => DetectCurrentWithDiagnostics(readAllText).Profile;

    internal static MacProfileDetectionResult DetectCurrentWithDiagnostics(Func<string, string> readAllText)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new MacProfileDetectionResult(null, []);

        return DetectLinuxProcAttrs(readAllText);
    }

    internal static MacProfileDetectionResult DetectLinuxProcAttrsForTesting(Func<string, string> readAllText)
        => DetectLinuxProcAttrs(readAllText);

    private static MacProfileDetectionResult DetectLinuxProcAttrs(Func<string, string> readAllText)
    {
        var diagnostics = new List<MacProfileDiagnostic>();
        var current = ReadProcAttr(readAllText, CurrentAttrPath, diagnostics);
        var exec = ReadProcAttr(readAllText, ExecAttrPath, diagnostics);
        return new MacProfileDetectionResult(DetectFromProcAttrs(current, exec), diagnostics);
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

    private static string? ReadProcAttr(
        Func<string, string> readAllText,
        string path,
        IList<MacProfileDiagnostic> diagnostics)
    {
        try
        {
            var value = BoundProcAttrValue(readAllText(path))?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (UnauthorizedAccessException)
        {
            AddDiagnostic(diagnostics, path, "permission_denied", "Could not read proc attribute due to permissions.");
            return null;
        }
        catch (IOException)
        {
            AddDiagnostic(diagnostics, path, "io_error", "Could not read proc attribute due to an I/O error.");
            return null;
        }
        catch (NotSupportedException)
        {
            AddDiagnostic(diagnostics, path, "not_supported", "Could not read proc attribute because the path is not supported.");
            return null;
        }
    }

    private static void AddDiagnostic(
        IList<MacProfileDiagnostic> diagnostics,
        string path,
        string category,
        string message)
        => diagnostics.Add(new MacProfileDiagnostic(
            ConsoleUi.FormatBoundedValue(path),
            category,
            message));

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

    internal sealed record MacProfileDetectionResult(
        string? Profile,
        IReadOnlyList<MacProfileDiagnostic> Diagnostics);
}
