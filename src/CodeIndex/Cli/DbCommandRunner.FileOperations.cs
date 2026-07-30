using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{

    private static void ValidateCheckpointName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(InvalidCheckpointNameChars) >= 0
            || name.Contains(Path.DirectorySeparatorChar)
            || (Path.AltDirectorySeparatorChar != '\0' && name.Contains(Path.AltDirectorySeparatorChar)))
            throw new ArgumentException($"invalid checkpoint name: {FormatCheckpointNameForDiagnostic(name)}");

        if (name.Length > MaxCheckpointNameLength)
            throw new ArgumentException($"checkpoint name is too long ({name.Length} characters; max {MaxCheckpointNameLength}): {FormatCheckpointNameForDiagnostic(name)}");
    }

    private static string FormatCheckpointNameForDiagnostic(string name)
        => ConsoleUi.FormatBoundedValue(name, CheckpointNameDiagnosticTextLimit);

    private static string MakeTimestampCheckpointName()
        => GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");

    private static string MakeRestorePathSuffix()
        => GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");

    private static DateTimeOffset GetUtcNow()
        => UtcNowForTesting?.Invoke() ?? DateTimeOffset.UtcNow;

    private static string GetCheckpointRoot(string fullDbPath)
        => fullDbPath + CheckpointsDirectorySuffix;

    private static string GetRestoreBackupDirectoryPrefix(string fullDbPath)
        => Path.GetFileName(fullDbPath) + ".restore-backup-";

    private static string GetCheckpointPath(string fullDbPath, string name)
    {
        ValidateCheckpointName(name);
        return Path.Combine(GetCheckpointRoot(fullDbPath), name);
    }

    private static void CopyIfExists(string source, string destination, bool privateDestination = false)
    {
        if (!TryGetRegularExistingFile(source, out var normalizedSource))
            return;

        if (!privateDestination || OperatingSystem.IsWindows())
        {
            File.Copy(normalizedSource, LongPath.EnsureWindowsPrefix(destination), overwrite: false);
            if (privateDestination)
                DataDirectorySecurity.ApplyPrivateFileMode(destination);
            return;
        }

        using (var input = new FileStream(normalizedSource, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(
            LongPath.EnsureWindowsPrefix(destination),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = DataDirectorySecurity.PrivateFileMode,
            }))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        DataDirectorySecurity.ApplyPrivateFileMode(destination);
    }

    private static void MoveIfExists(string source, string destination, bool privateDestination = false, bool overwrite = false)
    {
        if (!TryGetRegularExistingFile(source, out var normalizedSource))
            return;

        AtomicFileWriter.MoveFile(
            normalizedSource,
            destination,
            overwrite,
            privateDestination ? DataDirectorySecurity.ApplyPrivateFileMode : null);
    }

    private static bool TryGetRegularExistingFile(string path, out string normalizedPath)
    {
        normalizedPath = LongPath.EnsureWindowsPrefix(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(normalizedPath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !ExecutableExtensionBoundary.IsRegularFilePath(normalizedPath))
            throw new InvalidOperationException($"checkpoint file is not a regular file: {ConsoleUi.FormatBoundedValue(path)}");

        return true;
    }

    private static void RestoreBackedUpFiles(string fullDbPath, string backupPath)
    {
        if (!Directory.Exists(backupPath))
            return;

        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath)), fullDbPath, privateDestination: true, overwrite: true);
        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-wal"), fullDbPath + "-wal", privateDestination: true, overwrite: true);
        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-shm"), fullDbPath + "-shm", privateDestination: true, overwrite: true);
    }

    internal static void TryDeleteTemporaryDirectory(string path, string cleanupDescription, string safeRoot, string expectedNamePrefix)
    {
        try
        {
            if (!TryValidateTemporaryDirectoryCleanupTarget(path, safeRoot, expectedNamePrefix, out var fullPath, out var validationFailure))
            {
                CommandErrorWriter.WriteStderr($"Warning: skipped deleting {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({validationFailure}).");
                return;
            }

            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return;

            if (!TryValidateTemporaryDirectoryCleanupTarget(fullPath, safeRoot, expectedNamePrefix, out fullPath, out validationFailure))
            {
                CommandErrorWriter.WriteStderr($"Warning: skipped deleting {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({validationFailure}).");
                return;
            }

            if (DeleteTemporaryDirectoryForTesting != null)
                DeleteTemporaryDirectoryForTesting(fullPath);
            else
                Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            CommandErrorWriter.WriteWarning($"failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    private static bool TryValidateTemporaryDirectoryCleanupTarget(
        string path,
        string safeRoot,
        string expectedNamePrefix,
        out string fullPath,
        out string failureReason,
        bool filesystemAwarePrefix = false)
    {
        try
        {
            var nameComparison = filesystemAwarePrefix
                ? PathCasing.ComparisonFor(safeRoot)
                : StringComparison.Ordinal;
            var options = new DirectoryCleanupBoundaryOptions(
                expectedNamePrefix,
                "target is outside the expected cleanup root",
                "target name does not match the expected temporary-directory prefix",
                "target is not a regular temporary directory",
                NameComparison: nameComparison);
            return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                path,
                safeRoot,
                options,
                out fullPath,
                out failureReason,
                filesystemAwarePrefix ? nameComparison : null);
        }
        catch (CodeIndexException ex)
        {
            fullPath = string.Empty;
            failureReason = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
            return false;
        }
    }

}
