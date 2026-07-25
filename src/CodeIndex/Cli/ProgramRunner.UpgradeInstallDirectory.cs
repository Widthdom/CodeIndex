using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    internal static bool CanWriteDirectory(string directory)
        => TryCheckInstallDirectoryWritable(directory, out _);

    internal static bool TryCheckInstallDirectoryWritable(string directory, out string? diagnostic)
    {
        diagnostic = null;
        string? probe = null;
        var createdProbe = false;
        try
        {
            if (!TryResolveUpgradeInstallDirectory(directory, out var fullDirectory, out diagnostic))
                return false;

            Directory.CreateDirectory(fullDirectory);
            if (!TryValidateExistingUpgradeInstallDirectory(fullDirectory, out diagnostic))
                return false;

            probe = Path.GetFullPath(Path.Combine(fullDirectory, $".cdidx-write-test-{Guid.NewGuid():N}"));
            if (!IsPathEqualOrChildNoProbe(fullDirectory, probe) || string.Equals(fullDirectory, probe, InstallDirectoryPathComparison))
            {
                diagnostic = "install directory write probe escaped the install directory.";
                return false;
            }

            FileWriteProbe.WriteEmptyFile(probe, Encoding.UTF8);
            createdProbe = true;
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = CommandErrorWriter.FormatSanitizedException(ex);
            return false;
        }
        finally
        {
            if (createdProbe && probe != null)
                TryDeleteInstallDirectoryWriteProbe(probe);
        }
    }

    private static bool TryResolveUpgradeInstallDirectory(string directory, out string fullDirectory, out string? diagnostic)
    {
        fullDirectory = string.Empty;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            diagnostic = "install directory is empty.";
            return false;
        }

        try
        {
            fullDirectory = NormalizeDirectoryBoundaryPath(Path.GetFullPath(directory));
            var root = Path.GetPathRoot(fullDirectory);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullDirectory, NormalizeDirectoryBoundaryPath(root), InstallDirectoryPathComparison))
            {
                diagnostic = "install directory must not be the filesystem root.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = CommandErrorWriter.FormatSanitizedException(ex);
            return false;
        }
    }

    private static bool TryValidateExistingUpgradeInstallDirectory(string fullDirectory, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            var directoryInfo = new DirectoryInfo(fullDirectory);
            directoryInfo.Refresh();
            if (!directoryInfo.Exists)
            {
                diagnostic = "install directory does not exist after creation.";
                return false;
            }

            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(directoryInfo.LinkTarget))
            {
                diagnostic = "install directory must not be a symbolic link or reparse point.";
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(fullDirectory);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    diagnostic = "install directory must not be group- or world-writable.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = CommandErrorWriter.FormatSanitizedException(ex);
            return false;
        }
    }

    private static string NormalizeDirectoryBoundaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, InstallDirectoryPathComparison))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison InstallDirectoryPathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsPathEqualOrChildNoProbe(string normalizedParent, string normalizedChild)
    {
        if (string.Equals(normalizedParent, normalizedChild, InstallDirectoryPathComparison))
            return true;

        var trimmedParent = normalizedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedChild.StartsWith(trimmedParent + Path.DirectorySeparatorChar, InstallDirectoryPathComparison)
            || normalizedChild.StartsWith(trimmedParent + Path.AltDirectorySeparatorChar, InstallDirectoryPathComparison);
    }

    private static void TryDeleteInstallDirectoryWriteProbe(string probePath)
    {
        try
        {
            if (!File.Exists(probePath))
                return;

            if (DeleteInstallDirectoryWriteProbeForTesting != null)
                DeleteInstallDirectoryWriteProbeForTesting(probePath);
            else
                File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CommandErrorWriter.WriteStderr($"Warning: failed to delete install directory write probe {ConsoleUi.FormatBoundedValue(probePath)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    private static int ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 1;
        if (timeout.TotalMilliseconds >= int.MaxValue)
            return int.MaxValue;
        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static string FormatDuration(TimeSpan timeout)
        => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only; callers receive the timeout diagnostic.
        }
    }
}
