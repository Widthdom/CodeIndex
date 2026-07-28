using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    private static DbCheckpointOperationResult CreateCheckpoint(string fullDbPath, string name)
    {
        ValidateCheckpointName(name);
        var root = GetCheckpointRoot(fullDbPath);
        var checkpointPath = GetCheckpointPath(fullDbPath, name);
        if (Directory.Exists(checkpointPath))
            throw new InvalidOperationException($"checkpoint already exists: {FormatCheckpointNameForDiagnostic(name)}");

        DataDirectorySecurity.CreateSensitiveDirectory(root);
        var tempPath = Path.Combine(root, ".tmp-" + name + "-" + Guid.NewGuid().ToString("N"));
        DataDirectorySecurity.CreateSensitiveDirectory(tempPath);
        try
        {
            CopyIfExists(fullDbPath, Path.Combine(tempPath, Path.GetFileName(fullDbPath)), privateDestination: true);
            CopyIfExists(fullDbPath + "-wal", Path.Combine(tempPath, Path.GetFileName(fullDbPath) + "-wal"), privateDestination: true);
            CopyIfExists(fullDbPath + "-shm", Path.Combine(tempPath, Path.GetFileName(fullDbPath) + "-shm"), privateDestination: true);
            DataDirectorySecurity.WritePrivateText(Path.Combine(tempPath, "manifest.txt"), $"name={name}{Environment.NewLine}created_at_utc={GetUtcNow():O}{Environment.NewLine}db_file={Path.GetFileName(fullDbPath)}{Environment.NewLine}");
            AtomicFileWriter.PublishDirectory(tempPath, checkpointPath);
        }
        catch
        {
            TryDeleteTemporaryDirectory(
                tempPath,
                "checkpoint temporary directory",
                root,
                ".tmp-");
            throw;
        }

        var diagnostics = new List<DbDiagnosticJsonResult>();
        var files = EnumerateCheckpointFileNames(checkpointPath, diagnostics);
        var bytes = files.Truncated
            ? (Bytes: 0L, Truncated: true)
            : SumCheckpointBytes(checkpointPath, diagnostics);
        return new DbCheckpointOperationResult(name, checkpointPath, files.Items, files.Truncated || bytes.Truncated, diagnostics, bytes.Bytes);
    }

    private static DbCheckpointOperationResult PreviewCheckpoint(string fullDbPath, string name)
    {
        ValidateCheckpointName(name);
        var checkpointPath = GetCheckpointPath(fullDbPath, name);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (Directory.Exists(LongPath.EnsureWindowsPrefix(checkpointPath)))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_already_exists",
                "A checkpoint with this name already exists; running without --dry-run would fail.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
        }

        var files = ReadCheckpointSourceFiles(fullDbPath, diagnostics);
        return new DbCheckpointOperationResult(name, checkpointPath, files.Files, files.Truncated, diagnostics, files.Bytes);
    }

    private static (List<string> Files, long Bytes, bool Truncated) ReadCheckpointSourceFiles(
        string fullDbPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var files = new List<string>();
        long bytes = 0;
        foreach (var source in new[] { fullDbPath, fullDbPath + "-wal", fullDbPath + "-shm" })
        {
            try
            {
                if (!TryGetRegularExistingFile(source, out var normalizedSource))
                    continue;

                files.Add(Path.GetFileName(source) ?? source);
                bytes += new FileInfo(normalizedSource).Length;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic(
                    "checkpoint_source_file_stat_failed",
                    $"Unable to inspect checkpoint source file ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                    source));
                return (files, bytes, Truncated: true);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return (files, bytes, Truncated: false);
    }

    private static DbCheckpointListReadResult ListCheckpoints(string fullDbPath, int limit)
    {
        var root = GetCheckpointRoot(fullDbPath);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (!Directory.Exists(root))
            return new DbCheckpointListReadResult([], DirectoryEnumerationTruncated: false, FileInspectionTruncated: false, diagnostics);

        var dbFileName = Path.GetFileName(fullDbPath);
        var entries = new List<DbCheckpointListEntryJsonResult>();
        var checkpointsTruncated = false;
        var directoriesInspected = 0;
        var directories = EnumerateCheckpointDirectories(root, diagnostics, limit + 1);
        checkpointsTruncated |= directories.Truncated;
        foreach (var path in directories.Items)
        {
            if (directoriesInspected >= limit)
            {
                checkpointsTruncated = true;
                break;
            }

            directoriesInspected++;
            if (Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal))
                continue;
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(path, dbFileName))))
                continue;

            DirectoryInfo info;
            DateTime createdAtUtc;
            try
            {
                info = new DirectoryInfo(path);
                createdAtUtc = info.CreationTimeUtc;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_directory_stat_failed", "Unable to inspect checkpoint directory metadata.", path));
                checkpointsTruncated = true;
                continue;
            }

            var bytes = SumCheckpointBytes(path, diagnostics);
            entries.Add(new DbCheckpointListEntryJsonResult(
                info.Name,
                path,
                createdAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                bytes.Bytes,
                bytes.Truncated));
        }

        entries.Sort((left, right) =>
        {
            var createdCompare = string.Compare(right.CreatedAtUtc, left.CreatedAtUtc, StringComparison.Ordinal);
            return createdCompare != 0
                ? createdCompare
                : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return new DbCheckpointListReadResult(
            entries,
            checkpointsTruncated,
            entries.Any(entry => entry.FilesTruncated),
            diagnostics);
    }

    private static DbRestoreBackupReadResult ListRestoreBackups(string fullDbPath, int limit)
    {
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (!Directory.Exists(parent))
            return new DbRestoreBackupReadResult([], DirectoryEnumerationTruncated: false, FileInspectionTruncated: false, diagnostics);

        var dbFileName = Path.GetFileName(fullDbPath);
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        var entries = new List<DbRestoreBackupEntryJsonResult>();
        var backupsTruncated = false;
        var directoriesInspected = 0;
        var directories = EnumerateRestoreBackupDirectories(parent, prefix, diagnostics, limit + 1);
        backupsTruncated |= directories.Truncated;
        foreach (var path in directories.Items)
        {
            if (directoriesInspected >= limit)
            {
                backupsTruncated = true;
                break;
            }

            directoriesInspected++;
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(path, dbFileName))))
                continue;

            DirectoryInfo info;
            DateTime createdAtUtc;
            try
            {
                info = new DirectoryInfo(path);
                createdAtUtc = info.CreationTimeUtc;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("restore_backup_directory_stat_failed", "Unable to inspect restore backup directory metadata.", path));
                backupsTruncated = true;
                continue;
            }

            var bytes = SumCheckpointBytes(path, diagnostics);
            var managed = ManagedRestoreBackupStore.TryReadSummary(
                fullDbPath,
                path,
                out var summary);
            entries.Add(new DbRestoreBackupEntryJsonResult(
                info.Name,
                path,
                managed
                    ? summary.CreatedAtUtc
                    : createdAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                bytes.Bytes,
                bytes.Truncated,
                managed ? summary.Id : null,
                managed,
                managed ? summary.Provenance : null,
                managed ? summary.SourceId : null,
                managed ? summary.UserVersion : null));
        }

        entries.Sort((left, right) =>
        {
            var createdCompare = string.Compare(right.CreatedAtUtc, left.CreatedAtUtc, StringComparison.Ordinal);
            return createdCompare != 0
                ? createdCompare
                : string.Compare(right.Name, left.Name, StringComparison.Ordinal);
        });
        return new DbRestoreBackupReadResult(entries, backupsTruncated, entries.Any(entry => entry.FilesTruncated), diagnostics);
    }

    private static DbRestoreBackupPruneResult PruneRestoreBackups(string fullDbPath, int keep, bool dryRun)
    {
        var result = ListRestoreBackups(fullDbPath, RestoreBackupPruneScanLimit);
        var diagnostics = result.Diagnostics;
        if (result.DirectoryEnumerationTruncated)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_prune_truncated",
                "Restore backup pruning was skipped because backup enumeration reached the scan limit.",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return new DbRestoreBackupPruneResult(
                Deleted: 0,
                Retained: result.Entries.Count,
                DeletedPaths: [],
                RetainedPaths: result.Entries.Select(entry => entry.BackupPath).ToList(),
                Truncated: true,
                diagnostics);
        }

        var retainedPaths = result.Entries
            .Take(keep)
            .Select(entry => entry.BackupPath)
            .ToList();
        var deletedPaths = new List<string>();
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        foreach (var entry in result.Entries.Skip(keep))
        {
            if (dryRun)
            {
                if (TryValidateTemporaryDirectoryCleanupTarget(entry.BackupPath, parent, prefix, out _, out var validationFailure))
                    deletedPaths.Add(entry.BackupPath);
                else
                {
                    diagnostics.Add(new DbDiagnosticJsonResult(
                        "restore_backup_delete_skipped",
                        $"Restore backup deletion would be skipped: {validationFailure}.",
                        ConsoleUi.FormatBoundedValue(entry.BackupPath)));
                    retainedPaths.Add(entry.BackupPath);
                }
            }
            else if (TryDeleteRestoreBackupDirectory(fullDbPath, entry.BackupPath, diagnostics))
            {
                deletedPaths.Add(entry.BackupPath);
            }
            else
            {
                retainedPaths.Add(entry.BackupPath);
            }
        }

        return new DbRestoreBackupPruneResult(
            deletedPaths.Count,
            retainedPaths.Count,
            deletedPaths,
            retainedPaths,
            result.Truncated,
            diagnostics);
    }

    private static (List<string> Items, bool Truncated) EnumerateRestoreBackupDirectories(
        string parent,
        string prefix,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var directories = new List<string>();
        try
        {
            foreach (var directory in CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(parent, prefix + "*"))
            {
                if (directories.Count >= limit)
                    return (directories, Truncated: true);
                if (Path.GetFileName(directory).StartsWith(prefix, StringComparison.Ordinal))
                    directories.Add(directory);
            }

            return (directories, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("restore_backup_directory_enumeration_failed", "Unable to enumerate every restore backup directory.", parent));
            return (directories, Truncated: true);
        }
    }

    private static bool TryDeleteRestoreBackupDirectory(
        string fullDbPath,
        string backupPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        if (!TryValidateTemporaryDirectoryCleanupTarget(backupPath, parent, prefix, out var fullPath, out var validationFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_delete_skipped",
                $"Skipped deleting restore backup directory: {validationFailure}.",
                ConsoleUi.FormatBoundedValue(backupPath)));
            return false;
        }

        try
        {
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return false;

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
            return true;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_delete_failed",
                $"Unable to delete restore backup directory ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullPath)));
            return false;
        }
    }

    private static bool TryDeleteCheckpointDirectory(
        string fullDbPath,
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        if (!TryValidateCheckpointDirectoryTarget(
                fullDbPath,
                checkpointPath,
                out var fullPath,
                out var validationFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_delete_skipped",
                $"Skipped deleting checkpoint directory: {validationFailure}.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
            return false;
        }

        try
        {
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return false;
            if (!TryValidateCheckpointDirectoryTarget(
                    fullDbPath,
                    fullPath,
                    out fullPath,
                    out validationFailure))
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_delete_skipped",
                    $"Skipped deleting checkpoint directory after revalidation: {validationFailure}.",
                    ConsoleUi.FormatBoundedValue(checkpointPath)));
                return false;
            }

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
            return true;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_delete_failed",
                $"Unable to delete checkpoint directory ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullPath)));
            return false;
        }
    }

    private static bool TryValidateCheckpointDirectoryTarget(
        string fullDbPath,
        string checkpointPath,
        out string fullPath,
        out string failureReason)
    {
        var checkpointRoot = GetCheckpointRoot(fullDbPath);
        var rootStatus = FileSystemBoundary.TryGetAttributes(checkpointRoot, out var rootAttributes);
        if (rootStatus != FileSystemBoundaryProbeStatus.Found)
        {
            fullPath = string.Empty;
            failureReason = "checkpoint root is unavailable";
            return false;
        }
        if ((rootAttributes & FileAttributes.Directory) == 0
            || FileSystemBoundary.IsSymlinkOrReparsePoint(rootAttributes)
            || FileSystemBoundary.IsDevice(rootAttributes))
        {
            fullPath = string.Empty;
            failureReason = "checkpoint root is not a regular directory";
            return false;
        }

        var options = new DirectoryCleanupBoundaryOptions(
            ExpectedNamePrefix: string.Empty,
            OutsideRootReason: "target is outside the checkpoint root",
            PrefixMismatchReason: "target name is not a checkpoint name",
            UnsafeDirectoryReason: "target is not a regular checkpoint directory");
        return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
            checkpointPath,
            checkpointRoot,
            options,
            out fullPath,
            out failureReason);
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointDirectories(
        string root,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var directories = new List<string>();
        try
        {
            foreach (var directory in CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(root))
            {
                if (directories.Count >= limit)
                    return (directories, Truncated: true);
                directories.Add(directory);
            }

            return (directories, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_directory_enumeration_failed", "Unable to enumerate every checkpoint directory.", root));
            return (directories, Truncated: true);
        }
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointFileNames(
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var files = new List<string>();
        var truncated = false;
        try
        {
            if (EnumerateCheckpointFileNamesForTesting != null)
            {
                foreach (var name in EnumerateCheckpointFileNamesForTesting(checkpointPath))
                {
                    if (files.Count >= CheckpointFileInspectLimit)
                    {
                        truncated = true;
                        break;
                    }

                    if (name is not null)
                        files.Add(name);
                }
            }
            else
            {
                var listedFiles = EnumerateCheckpointFiles(checkpointPath, diagnostics, CheckpointFileInspectLimit + 1);
                foreach (var file in listedFiles.Items)
                {
                    if (files.Count >= CheckpointFileInspectLimit)
                    {
                        truncated = true;
                        break;
                    }

                    var name = Path.GetFileName(file);
                    if (name is not null)
                        files.Add(name);
                }

                truncated = listedFiles.Truncated || listedFiles.Items.Count > CheckpointFileInspectLimit;
            }
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_enumeration_failed", "Unable to enumerate every checkpoint file.", checkpointPath));
            truncated = true;
        }

        files.Sort(StringComparer.Ordinal);
        return (files, truncated);
    }

    private static (long Bytes, bool Truncated) SumCheckpointBytes(string checkpointPath, List<DbDiagnosticJsonResult> diagnostics)
    {
        long bytes = 0;
        var filesSeen = 0;
        var files = EnumerateCheckpointFiles(checkpointPath, diagnostics, CheckpointFileInspectLimit + 1);
        foreach (var file in files.Items)
        {
            if (filesSeen >= CheckpointFileInspectLimit)
                return (bytes, Truncated: true);

            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_stat_failed", "Unable to inspect every checkpoint file.", file));
                return (bytes, Truncated: true);
            }

            filesSeen++;
        }

        return (bytes, files.Truncated);
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointFiles(
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in EnumerateCheckpointFilesForTesting?.Invoke(checkpointPath) ?? CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(checkpointPath))
            {
                if (files.Count >= limit)
                    return (files, Truncated: true);
                files.Add(file);
            }

            return (files, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_enumeration_failed", "Unable to enumerate every checkpoint file.", checkpointPath));
            return (files, Truncated: true);
        }
    }

}
