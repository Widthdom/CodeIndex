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
    private static int RunRestore(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
            return WriteCommandError(options.Json, jsonOptions, "restore requires a checkpoint name", CommandExitCodes.UsageError, "Use `cdidx db restore <name> --db <path>`.", CommandErrorCodes.UsageError);
        if (!ValidateWritableFileDb(options, jsonOptions, "restore", out var fullDbPath, out var validationExitCode))
            return validationExitCode;

        var checkpointPath = string.Empty;
        try
        {
            checkpointPath = GetCheckpointPath(fullDbPath, options.Name);
            if (!Directory.Exists(checkpointPath))
                return WriteCommandError(options.Json, jsonOptions, $"checkpoint not found: {FormatCheckpointNameForDiagnostic(options.Name)}", CommandExitCodes.NotFound, "Run `cdidx db checkpoints --list` to see available checkpoints.", CommandErrorCodes.CheckpointNotFound);

            var preview = PreviewRestoreCheckpoint(fullDbPath, options.Name, checkpointPath);
            if (options.RestoreDryRun)
                return WriteRestoreDryRunResult(options, jsonOptions, fullDbPath, options.Name, checkpointPath, preview);
            if (!preview.Ready)
                throw new InvalidOperationException("checkpoint validation failed");

            var backupPath = RestoreCheckpoint(fullDbPath, options.Name, checkpointPath);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbRestoreJsonResult("success", fullDbPath, options.Name, checkpointPath, backupPath),
                    CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreJsonResult));
            }
            else
            {
                Console.WriteLine("Restored database checkpoint.");
                Console.WriteLine($"  database  : {fullDbPath}");
                Console.WriteLine($"  checkpoint: {options.Name}");
                Console.WriteLine($"  backup    : {backupPath}");
            }

            return CommandExitCodes.Success;
        }
        catch (DbRestoreOperationException ex)
        {
            return WriteRestoreError(options, jsonOptions, fullDbPath, options.Name, checkpointPath, ex);
        }
        catch (Exception ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to restore database checkpoint: {CommandErrorWriter.FormatSanitizedException(ex)}",
                CommandExitCodes.DatabaseError,
                "Ensure no cdidx writer is running, then retry `cdidx db restore <name>`.",
                CommandErrorCodes.DbError,
                category: DiagnosticRedactor.ClassifyException(ex));
        }
    }

    private static int WriteRestoreDryRunResult(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        string name,
        string checkpointPath,
        DbRestorePreviewResult preview)
    {
        const string hint = "Fix the reported checkpoint, path, or free-space diagnostics before running without --dry-run.";
        var message = preview.Ready
            ? null
            : "database restore dry run found blocking validation failures";
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbRestoreDryRunJsonResult(
                    preview.Ready ? "dry_run" : "invalid",
                    fullDbPath,
                    name,
                    checkpointPath,
                    DryRun: true,
                    preview.Ready,
                    preview.ManifestValid,
                    preview.PathsValid,
                    preview.SpaceCheckAvailable,
                    preview.SpaceSufficient,
                    preview.RequiredSpaceBytes,
                    preview.AvailableSpaceBytes,
                    preview.Files,
                    preview.Bytes,
                    preview.Diagnostics,
                    message,
                    preview.Ready ? null : CommandErrorCodes.DbError,
                    preview.Ready ? null : hint),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreDryRunJsonResult));
        }
        else
        {
            Console.WriteLine("Database restore dry run.");
            Console.WriteLine($"  database  : {fullDbPath}");
            Console.WriteLine($"  checkpoint: {checkpointPath}");
            Console.WriteLine("  side effect: none (run without --dry-run to replace the DB)");
            Console.WriteLine($"  manifest  : {(preview.ManifestValid ? "valid" : "invalid")}");
            Console.WriteLine($"  paths     : {(preview.PathsValid ? "valid" : "invalid")}");
            Console.WriteLine($"  bytes     : {preview.Bytes:N0}");
            Console.WriteLine($"  available : {(preview.AvailableSpaceBytes is long available ? available.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "unknown")}");
            Console.WriteLine($"  space     : {(preview.SpaceSufficient is true ? "sufficient" : preview.SpaceSufficient is false ? "insufficient" : "unknown")}");
            Console.WriteLine($"  ready     : {(preview.Ready ? "yes" : "no")}");
            foreach (var diagnostic in preview.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
            if (!preview.Ready)
                CommandErrorWriter.WriteStderr($"Error: {message}. Hint: {hint}");
        }

        return preview.Ready ? CommandExitCodes.Success : CommandExitCodes.DatabaseError;
    }

    private static int WriteRestoreError(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        string name,
        string checkpointPath,
        DbRestoreOperationException ex)
    {
        var primary = ex.InnerException ?? ex;
        var message = $"failed to restore database checkpoint: {CommandErrorWriter.FormatSanitizedException(primary)}";
        const string hint = "Ensure no cdidx writer is running, then retry `cdidx db restore <name>`.";
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbRestoreJsonResult(
                    "error",
                    fullDbPath,
                    name,
                    string.IsNullOrWhiteSpace(ex.CheckpointPath) ? checkpointPath : ex.CheckpointPath,
                    ex.BackupPath,
                    message,
                    CommandErrorCodes.DbError,
                    hint,
                    ex.RollbackFailure is not null,
                    ex.RollbackFailure),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreJsonResult));
            return CommandExitCodes.DatabaseError;
        }

        return WriteCommandError(
            false,
            jsonOptions,
            message,
            CommandExitCodes.DatabaseError,
            hint,
            CommandErrorCodes.DbError,
            category: DiagnosticRedactor.ClassifyException(primary));
    }

    private static int RunRestoreBackups(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (!options.RestoreBackupsList && !options.RestoreBackupsPrune)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "restore-backups requires --list or --prune",
                CommandExitCodes.UsageError,
                "Use `cdidx db restore-backups --list` or `cdidx db restore-backups --prune --keep <n>`.",
                CommandErrorCodes.UsageError);

        if (options.RestoreBackupsList && options.RestoreBackupsPrune)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "restore-backups accepts only one of --list or --prune",
                CommandExitCodes.UsageError,
                "Choose `--list` or `--prune`.",
                CommandErrorCodes.UsageError);

        if (!TryResolveFileDb(options.DbPath, out var fullDbPath, out var error))
            return WriteCommandError(options.Json, jsonOptions, error, CommandExitCodes.DatabaseError, "Use a filesystem database path, not a SQLite URI.", CommandErrorCodes.DbError);

        if (options.RestoreBackupsList)
        {
            var result = ListRestoreBackups(fullDbPath, RestoreBackupListEntryLimit);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbRestoreBackupListJsonResult(
                        fullDbPath,
                        result.Entries,
                        result.Truncated,
                        RestoreBackupListEntryLimit,
                        CheckpointFileInspectLimit,
                        result.Diagnostics),
                    CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreBackupListJsonResult));
            }
            else
            {
                Console.WriteLine("Database restore backups");
                Console.WriteLine($"  database: {fullDbPath}");
                if (result.Truncated)
                    Console.WriteLine($"  truncated: yes (restore backup limit {RestoreBackupListEntryLimit:N0}, file limit {CheckpointFileInspectLimit:N0} per backup)");
                if (result.Entries.Count == 0)
                {
                    Console.WriteLine("  backups: none");
                }
                else
                {
                    foreach (var entry in result.Entries)
                        Console.WriteLine($"  {entry.Name}  {entry.CreatedAtUtc}  {entry.Bytes:N0} bytes{(entry.FilesTruncated ? " (files truncated)" : string.Empty)}");
                }

                foreach (var diagnostic in result.Diagnostics)
                    Console.Error.WriteLine($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
            }

            return CommandExitCodes.Success;
        }

        var pruneResult = PruneRestoreBackups(fullDbPath, options.RestoreBackupsKeep, options.RestoreBackupsDryRun);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbRestoreBackupPruneJsonResult(
                    options.RestoreBackupsDryRun ? "dry_run" : "success",
                    fullDbPath,
                    options.RestoreBackupsKeep,
                    options.RestoreBackupsDryRun,
                    pruneResult.Deleted,
                    pruneResult.Retained,
                    pruneResult.DeletedPaths,
                    pruneResult.RetainedPaths,
                    pruneResult.Truncated,
                    RestoreBackupPruneScanLimit,
                    pruneResult.Diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreBackupPruneJsonResult));
        }
        else
        {
            Console.WriteLine(options.RestoreBackupsDryRun
                ? "Database restore backup prune dry run."
                : "Pruned database restore backups.");
            Console.WriteLine($"  database: {fullDbPath}");
            Console.WriteLine($"  keep    : {options.RestoreBackupsKeep:N0}");
            Console.WriteLine($"  side effect: {(options.RestoreBackupsDryRun ? "none" : "older restore backup directories removed")}");
            Console.WriteLine($"  {(options.RestoreBackupsDryRun ? "would delete" : "deleted"),-12}: {pruneResult.Deleted:N0}");
            foreach (var path in pruneResult.DeletedPaths)
                Console.WriteLine($"    {path}");
            Console.WriteLine($"  retained: {pruneResult.Retained:N0}");
            foreach (var path in pruneResult.RetainedPaths)
                Console.WriteLine($"    {path}");
            if (pruneResult.Truncated)
                Console.WriteLine($"  truncated: yes (restore backup scan limit {RestoreBackupPruneScanLimit:N0})");
            foreach (var diagnostic in pruneResult.Diagnostics)
                Console.Error.WriteLine($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
        }

        return CommandExitCodes.Success;
    }

}
