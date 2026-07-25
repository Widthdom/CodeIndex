using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    internal static void CreateDatabaseSnapshot(string sourceDbPath, string snapshotPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            sourceDbPath,
            pooling: false,
            out _,
            out _);
        using var destination = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
        source.Open();
        destination.Open();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
    }

    private static string CreateUnpooledConnectionString(string dbPath)
        => SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.Unpooled);

    internal static void ReplaceImportedDatabase(string tempPath, string fullDbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dbBackupPath = MoveExistingReplacementFileToBackup(fullDbPath);
        var sidecarBackups = new List<ReplacementBackup>(capacity: 2);
        try
        {
            AddReplacementBackup(sidecarBackups, fullDbPath + "-wal");
            AddReplacementBackup(sidecarBackups, fullDbPath + "-shm");

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFileWriter.MoveFile(
                tempPath,
                fullDbPath,
                overwrite: false,
                applyDestinationMode: ApplyImportedDatabasePrivateFileMode);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try
            {
                RollBackImportedDatabaseReplacement(fullDbPath, dbBackupPath, sidecarBackups);
            }
            catch (Exception rollbackEx) when (IsRecoverableReplacementException(rollbackEx))
            {
                CommandErrorWriter.WriteStderr($"Warning: failed to roll back cancelled imported database replacement ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).");
            }

            throw;
        }
        catch (Exception ex) when (IsRecoverableReplacementException(ex))
        {
            Exception? rollbackFailure = null;
            try
            {
                RollBackImportedDatabaseReplacement(fullDbPath, dbBackupPath, sidecarBackups);
            }
            catch (Exception rollbackEx) when (IsRecoverableReplacementException(rollbackEx))
            {
                rollbackFailure = rollbackEx;
                CommandErrorWriter.WriteStderr($"Warning: failed to roll back imported database replacement ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).");
            }

            throw new ImportReplacementException(
                "import database replacement failed; rolled back the previous destination database when possible.",
                ex,
                BuildReplacementDiagnostics(tempPath, fullDbPath, dbBackupPath, sidecarBackups, rollbackFailure));
        }

        DeleteReplacementBackup(dbBackupPath, "import replaced database backup");
        foreach (var backup in sidecarBackups)
            DeleteReplacementBackup(backup.BackupPath, "import replaced database sidecar backup", DeleteSqliteSidecarForTesting);
    }

    private static IReadOnlyList<ExportImportDiagnosticResult> BuildReplacementDiagnostics(
        string tempPath,
        string fullDbPath,
        string? dbBackupPath,
        IReadOnlyList<ReplacementBackup> sidecarBackups,
        Exception? rollbackFailure)
    {
        var diagnostics = new List<ExportImportDiagnosticResult>
        {
            CreateResidualStateDiagnostic("import_replace_destination_state", "destination database", fullDbPath),
            CreateResidualStateDiagnostic("import_replace_staged_state", "staged import database", tempPath),
        };

        if (dbBackupPath != null)
            diagnostics.Add(CreateResidualStateDiagnostic("import_replace_backup_state", "destination database backup", dbBackupPath));

        foreach (var backup in sidecarBackups)
            diagnostics.Add(CreateResidualStateDiagnostic("import_replace_sidecar_backup_state", "destination sidecar backup", backup.BackupPath));

        if (rollbackFailure != null)
        {
            diagnostics.Add(new ExportImportDiagnosticResult(
                "import_replace_rollback_failed",
                $"Rollback failed while restoring the previous destination database ({CommandErrorWriter.FormatSanitizedException(rollbackFailure)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
        }

        return diagnostics;
    }

    private static ExportImportDiagnosticResult CreateResidualStateDiagnostic(string code, string description, string path)
        => new(
            code,
            $"{description} exists after replacement failure: {(File.Exists(path) ? "true" : "false")}.",
            ConsoleUi.FormatBoundedValue(path));

    private static void ApplyImportedDatabasePrivateFileMode(string fullDbPath)
    {
        if (ApplyPrivateFileModeForTesting != null)
        {
            ApplyPrivateFileModeForTesting(fullDbPath);
            return;
        }

        DataDirectorySecurity.ApplyPrivateFileMode(fullDbPath);
    }

    private static void AddReplacementBackup(List<ReplacementBackup> backups, string path)
    {
        var backupPath = MoveExistingReplacementFileToBackup(path);
        if (backupPath != null)
            backups.Add(new ReplacementBackup(path, backupPath));
    }

    private static string? MoveExistingReplacementFileToBackup(string path)
    {
        if (!File.Exists(path))
            return null;

        var backupPath = $"{path}.replace-backup-{Guid.NewGuid():N}";
        AtomicFileWriter.MoveFile(path, backupPath, overwrite: false);
        return backupPath;
    }

    private static void RollBackImportedDatabaseReplacement(
        string fullDbPath,
        string? dbBackupPath,
        IReadOnlyList<ReplacementBackup> sidecarBackups)
    {
        if (dbBackupPath != null)
        {
            AtomicFileWriter.MoveReplacing(dbBackupPath, fullDbPath);
        }
        else if (File.Exists(fullDbPath))
        {
            AtomicFileWriter.DeleteFileIfExists(fullDbPath);
        }

        foreach (var backup in sidecarBackups)
            AtomicFileWriter.MoveReplacing(backup.BackupPath, backup.OriginalPath);
    }

    private static void DeleteReplacementBackup(string? path, string cleanupDescription, Action<string>? deleteOverride = null)
    {
        if (path != null)
            TryDeleteFile(path, cleanupDescription, deleteOverride);
    }

    private static bool IsRecoverableReplacementException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void DeleteSqliteSidecars(string dbPath, string? cleanupDescription = null)
    {
        TryDeleteFile(dbPath + "-wal", cleanupDescription, DeleteSqliteSidecarForTesting);
        TryDeleteFile(dbPath + "-shm", cleanupDescription, DeleteSqliteSidecarForTesting);
    }

    private static void TryDeleteFile(string path, string? cleanupDescription = null, Action<string>? deleteOverride = null)
    {
        try
        {
            _ = AtomicFileWriter.TryDeleteFile(
                path,
                ex =>
                {
                    if (!string.IsNullOrWhiteSpace(cleanupDescription))
                        CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
                },
                deleteOverride ?? DeleteFileForTesting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    private static void TryDeleteDirectoryIfEmpty(
        string path,
        string? cleanupDescription,
        string safeRoot,
        string expectedNamePrefix)
    {
        try
        {
            var options = new DirectoryCleanupBoundaryOptions(
                expectedNamePrefix,
                "target is outside the expected cleanup root",
                "target name does not match the expected temporary-directory prefix",
                "target is not a regular temporary directory");
            if (!FileSystemBoundary.TryValidateDirectoryCleanupTarget(path, safeRoot, options, out var fullPath, out var validationFailure))
            {
                if (!string.IsNullOrWhiteSpace(cleanupDescription))
                    CommandErrorWriter.WriteStderr($"Warning: skipped deleting {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({validationFailure}).");
                return;
            }

            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)) || CodeIndex.FileSystemTraversalPolicy.HasAnyFileSystemEntry(fullPath))
                return;

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string>? DeleteSqliteSidecarForTesting { get; set; }
    internal static Action<string>? ApplyPrivateFileModeForTesting { get; set; }

    private readonly record struct ReplacementBackup(string OriginalPath, string BackupPath);

    internal static StringComparison ResolveDatabasePathComparison(string dbPath)
    {
        if (TryReadDatabasePathCaseSensitive(dbPath, out var pathCaseSensitive))
            return pathCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return PathCasing.ComparisonFor(dbPath);
    }

    private static bool TryReadDatabasePathCaseSensitive(string dbPath, out bool pathCaseSensitive)
    {
        pathCaseSensitive = false;
        try
        {
            using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                dbPath,
                pooling: false,
                out _,
                out _);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
            SqliteCommandPolicy.Add(cmd, "@key", DbContext.WorkspacePathCaseSensitiveMetaKey);
            var raw = cmd.ExecuteScalar();
            return raw is string value && bool.TryParse(value, out pathCaseSensitive);
        }
        catch (Exception ex) when (ex is SqliteException or CodeIndexException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right, StringComparison comparison)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);

    internal static bool IsDatabaseOrSqliteSidecarPath(string path, string dbPath, StringComparison comparison)
        => IsSamePath(path, dbPath, comparison)
            || IsSamePath(path, dbPath + "-wal", comparison)
            || IsSamePath(path, dbPath + "-shm", comparison);

    internal static bool IsDatabaseOrSqliteSidecarPath(string path, string dbPath)
    {
        var liveComparison = PathCasing.ComparisonFor(dbPath);
        if (IsDatabaseOrSqliteSidecarPath(path, dbPath, liveComparison))
            return true;

        if (!TryReadDatabasePathCaseSensitive(dbPath, out var pathCaseSensitive))
            return false;

        var stampedComparison = pathCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return stampedComparison != liveComparison
            && IsDatabaseOrSqliteSidecarPath(path, dbPath, stampedComparison);
    }

    private static string SanitizeCtagsField(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

}
