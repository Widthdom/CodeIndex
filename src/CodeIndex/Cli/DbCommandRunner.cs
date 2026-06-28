using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs `db` subcommands that operate directly on the SQLite file (integrity check, schema, prune).
/// SQLite ファイル本体に対する `db` サブコマンド（整合性チェック、schema、prune）を実行する。
/// </summary>
public static class DbCommandRunner
{
    private const string CheckpointsDirectorySuffix = ".checkpoints";
    private const string AutoCheckpointPrefix = "auto-";
    internal const int MaxCheckpointNameLength = 128;
    private const int CheckpointNameDiagnosticTextLimit = 80;
    internal const int CheckpointListEntryLimit = 100;
    internal const int CheckpointFileInspectLimit = 32;
    internal const int RestoreBackupListEntryLimit = 100;
    internal const int RestoreBackupPruneScanLimit = 1_000;
    internal const int DefaultRestoreBackupKeepCount = 10;
    internal const int MaxRestoreBackupKeepCount = 1_000;
    internal const int IntegrityCheckRowLimit = 100;
    internal const int IntegrityCheckTextLimit = 4096;
    internal const int SchemaEntryLimit = 200;
    internal const int SchemaSqlTextLimit = 8192;
    private static readonly string[] SchemaObjectTypes = ["table", "index", "trigger", "view"];
    private static readonly char[] InvalidCheckpointNameChars = Path.GetInvalidFileNameChars();
    private static readonly AsyncLocal<Action<string, string>?> ScopedMaintenanceProgressForTesting = new();
    internal static Action? RestoreFailureAfterBackupForTesting { get; set; }
    internal static Action<string>? DeleteTemporaryDirectoryForTesting { get; set; }
    internal static Func<string, IEnumerable<string>>? EnumerateCheckpointFilesForTesting { get; set; }
    internal static Func<IEnumerable<string>>? IntegrityCheckRowsForTesting { get; set; }
    internal static Func<string, IEnumerable<string>>? EnumerateCheckpointFileNamesForTesting { get; set; }
    private static readonly AsyncLocal<Func<DateTimeOffset>?> ScopedUtcNowForTesting = new();
    internal static Func<DateTimeOffset>? UtcNowForTesting
    {
        get => ScopedUtcNowForTesting.Value;
        set => ScopedUtcNowForTesting.Value = value;
    }
    internal static Action<string, string>? MaintenanceProgressForTesting
    {
        get => ScopedMaintenanceProgressForTesting.Value;
        set => ScopedMaintenanceProgressForTesting.Value = value;
    }

    public static int Run(string[] cmdArgs, JsonSerializerOptions jsonOptions)
        => Run(cmdArgs, jsonOptions, CancellationToken.None);

    public static int Run(string[] cmdArgs, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var options = ParseArgs(cmdArgs);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx db --integrity-check --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (!options.IntegrityCheck && !options.Schema && !options.Prune && !options.Checkpoint && !options.ListCheckpoints && !options.Restore && !options.RestoreBackups)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db requires a mode flag",
                CommandExitCodes.UsageError,
                "Pass `integrity`, `--integrity-check`, `schema`, `prune --dry-run|--apply`, `checkpoint [name]`, `checkpoints --list`, `restore <name>`, or `restore-backups --list|--prune --keep <n>`.",
                CommandErrorCodes.UsageError);

        if ((options.IntegrityCheck ? 1 : 0)
            + (options.Schema ? 1 : 0)
            + (options.Prune ? 1 : 0)
            + (options.Checkpoint ? 1 : 0)
            + (options.ListCheckpoints ? 1 : 0)
            + (options.Restore ? 1 : 0)
            + (options.RestoreBackups ? 1 : 0) > 1)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db accepts exactly one mode",
                CommandExitCodes.UsageError,
                "Run one of `cdidx db integrity`, `cdidx db --integrity-check`, `cdidx db schema`, `cdidx db prune --dry-run|--apply`, `cdidx db checkpoint [name]`, `cdidx db checkpoints --list`, `cdidx db restore <name>`, or `cdidx db restore-backups --list|--prune --keep <n>`.",
                CommandErrorCodes.UsageError);

        var dbPath = options.DbPath;
        var isUri = SqliteFileUri.StartsWithFileScheme(dbPath);
        if (!SqliteFileUri.TryValidateBounds(dbPath, out var parseError))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"invalid --db file URI: {SqliteFileUri.FormatParseError(parseError)}",
                CommandExitCodes.DatabaseError,
                "Pass a valid SQLite file URI whose full value and query string fit within the supported limits.",
                CommandErrorCodes.DbError);

        if (!isUri && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database not found: {dbPath}",
                CommandExitCodes.NotFound,
                "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.",
                CommandErrorCodes.DbNotFound);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.Schema)
                return RunSchema(options, jsonOptions, dbPath, isUri, cancellationToken);

            if (options.Prune)
                return RunPrune(options, jsonOptions, dbPath, isUri, cancellationToken);

            if (options.Checkpoint)
                return RunCheckpoint(options, jsonOptions);

            if (options.ListCheckpoints)
                return RunListCheckpoints(options, jsonOptions);

            if (options.Restore)
                return RunRestore(options, jsonOptions);

            if (options.RestoreBackups)
                return RunRestoreBackups(options, jsonOptions);

            return RunIntegrityCheck(options, jsonOptions, dbPath, isUri, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WriteMaintenanceCancelled(options.Json, jsonOptions, "db maintenance command");
        }
    }

    public static int RunIntegrityCheck(string[] cmdArgs, JsonSerializerOptions jsonOptions) => Run(cmdArgs, jsonOptions);

    internal static string CreateAutomaticCheckpoint(string dbPath)
    {
        var fullDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var name = AutoCheckpointPrefix + MakeTimestampCheckpointName();
        return CreateCheckpoint(fullDbPath, name).CheckpointPath;
    }

    private static int RunIntegrityCheck(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri, CancellationToken cancellationToken)
    {
        try
        {
            ReportMaintenanceProgress("integrity_check", "start", dbPath);
            var result = RunIntegrityCheckPragma(dbPath, cancellationToken);
            ReportMaintenanceProgress("integrity_check", "complete", dbPath);
            var issues = result.Rows;
            var ok = issues.Count == 1 && string.Equals(issues[0], "ok", StringComparison.Ordinal);
            var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
            var displayDbPath = DbPathResolver.FormatDbPathForDisplay(dbPath);

            if (options.Json)
            {
                var severity = ok ? "ok" : "error";
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbIntegrityCheckJsonResult(
                        displayDbPath,
                        ok,
                        severity,
                        ok ? "integrity_ok" : "integrity_failed",
                        ok ? new List<string>() : issues,
                        result.Truncated,
                        result.RowsTruncated,
                        result.TextTruncated,
                        IntegrityCheckRowLimit,
                        IntegrityCheckTextLimit),
                    jsonContext.DbIntegrityCheckJsonResult));
            }
            else
            {
                Console.WriteLine("Integrity check");
                Console.WriteLine($"  database: {displayDbPath}");
                Console.WriteLine($"  result  : {(ok ? "ok" : "corrupted")}");
                if (!ok)
                {
                    Console.WriteLine($"  issues  : {ConsoleUi.Counted(issues.Count, "row")}{(result.RowsTruncated ? " (truncated)" : string.Empty)}");
                    if (result.Truncated)
                        Console.WriteLine($"  truncated: yes (row limit {IntegrityCheckRowLimit:N0}, text limit {IntegrityCheckTextLimit:N0} chars)");
                    foreach (var line in issues)
                        Console.WriteLine($"    - {line}");
                    Console.WriteLine();
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbIntegrityFailed}]: SQLite reported integrity_check failures.");
                    CommandErrorWriter.WriteStderr("Hint: rebuild with `cdidx index <projectPath> --rebuild` to discard the corrupted DB and start fresh.");
                }
            }

            return ok ? CommandExitCodes.Success : CommandExitCodes.DatabaseError;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to run integrity check: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx db --integrity-check`. If this persists, the DB may be unreadable; rebuild with `cdidx index <projectPath> --rebuild`.",
                CommandErrorCodes.DbError);
        }
    }

    private static int RunSchema(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri, CancellationToken cancellationToken)
    {
        try
        {
            ReportMaintenanceProgress("schema", "start", dbPath);
            var schema = ReadSchema(dbPath, options, cancellationToken);
            ReportMaintenanceProgress("schema", "complete", dbPath);
            var fullPath = DbPathResolver.FormatDbPathForDisplay(dbPath);
            if (options.Json)
            {
                var severity = schema.Truncated ? "warn" : "ok";
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbSchemaJsonResult(
                        fullPath,
                        schema.UserVersion,
                        severity,
                        schema.Truncated ? "schema_truncated" : "schema_ok",
                        schema.ObjectTypeCounts,
                        schema.ObjectTypeOmittedCounts,
                        schema.Entries,
                        schema.Truncated,
                        schema.EntriesTruncated,
                        schema.SqlTruncated,
                        options.SchemaEntryLimit,
                        options.SchemaSqlTextLimit,
                        options.SchemaSummaryOnly,
                        options.SchemaType,
                        options.SchemaName,
                        options.SchemaIncludeInternal),
                    jsonContext.DbSchemaJsonResult));
            }
            else
            {
                Console.WriteLine("Database schema");
                Console.WriteLine($"  database    : {fullPath}");
                Console.WriteLine($"  user_version: {schema.UserVersion}");
                if (options.SchemaType is not null || options.SchemaName is not null || options.SchemaSummaryOnly || !options.SchemaIncludeInternal)
                {
                    Console.WriteLine($"  type filter : {options.SchemaType ?? "(any)"}");
                    Console.WriteLine($"  name filter : {options.SchemaName ?? "(any)"}");
                    Console.WriteLine($"  summary only: {(options.SchemaSummaryOnly ? "yes" : "no")}");
                    Console.WriteLine($"  internal    : {(options.SchemaIncludeInternal ? "included" : "excluded")}");
                }
                if (schema.Truncated)
                    Console.WriteLine($"  truncated   : yes (entry limit {options.SchemaEntryLimit:N0}, SQL text limit {options.SchemaSqlTextLimit:N0} chars)");
                if (options.SchemaSummaryOnly)
                {
                    Console.WriteLine("  objects     : " + string.Join(", ", schema.ObjectTypeCounts.Select(kv => $"{kv.Key}={kv.Value:N0}")));
                    return CommandExitCodes.Success;
                }

                foreach (var entry in schema.Entries)
                {
                    Console.WriteLine();
                    Console.WriteLine($"-- {entry.Type}: {entry.Name}");
                    if (!string.IsNullOrWhiteSpace(entry.Sql))
                        Console.WriteLine(entry.Sql);
                }
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to read schema: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx db schema`. If this persists, rebuild with `cdidx index <projectPath> --rebuild`.",
                CommandErrorCodes.DbError);
        }
    }

    private static int RunPrune(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri, CancellationToken cancellationToken)
    {
        if (!options.PruneApply && !options.PruneDryRun)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db prune requires --dry-run or --apply",
                CommandExitCodes.UsageError,
                "Use `cdidx db prune --dry-run` to inspect stale rows, then `cdidx db prune --apply` to delete them.",
                CommandErrorCodes.UsageError);

        if (options.PruneApply && options.PruneDryRun)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db prune accepts only one of --dry-run or --apply",
                CommandExitCodes.UsageError,
                "Choose `--dry-run` or `--apply`.",
                CommandErrorCodes.UsageError);

        if (isUri && DbPathResolver.UriRequestsReadOnly(dbPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for prune: {dbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path, or omit read-only URI parameters such as `immutable=1` / `mode=ro`.",
                CommandErrorCodes.DbNotWritable);

        try
        {
            ReportMaintenanceProgress("prune", "start", dbPath);
            var result = PruneOrphans(dbPath, apply: options.PruneApply, cancellationToken);
            ReportMaintenanceProgress("prune", "complete", dbPath);
            var fullPath = DbPathResolver.FormatDbPathForDisplay(dbPath);
            if (options.Json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbPruneJsonResult(
                        "success",
                        fullPath,
                        options.PruneDryRun,
                        result.OrphanSymbolReferences,
                        result.OrphanReferenceLines,
                        result.OrphanSymbols,
                        result.Total,
                        result.Warnings),
                    jsonContext.DbPruneJsonResult));
            }
            else
            {
                Console.WriteLine(options.PruneApply ? "Pruned database stale rows." : "Database prune dry run.");
                Console.WriteLine($"  database                 : {fullPath}");
                Console.WriteLine($"  orphan symbol_references : {result.OrphanSymbolReferences:N0}");
                Console.WriteLine($"  orphan reference_lines   : {result.OrphanReferenceLines:N0}");
                Console.WriteLine($"  orphan symbols           : {result.OrphanSymbols:N0}");
                Console.WriteLine($"  total                    : {result.Total:N0}");
                foreach (var warning in result.Warnings)
                    CommandErrorWriter.WriteStderr($"Warning [{warning.Code}]: {warning.Message}");
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to prune database: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Ensure no other writer is holding the database lock, then retry `cdidx db prune --dry-run`.",
                CommandErrorCodes.DbError);
        }
    }

    private static int RunCheckpoint(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (!ValidateWritableFileDb(options, jsonOptions, "checkpoint", out var fullDbPath, out var validationExitCode))
            return validationExitCode;

        try
        {
            if (options.CheckpointDryRun)
            {
                var preview = PreviewCheckpoint(fullDbPath, options.Name ?? MakeTimestampCheckpointName());
                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new DbCheckpointJsonResult(
                            "dry_run",
                            fullDbPath,
                            preview.Name,
                            preview.CheckpointPath,
                            preview.Files,
                            preview.FilesTruncated,
                            CheckpointFileInspectLimit,
                            preview.Diagnostics,
                            DryRun: true,
                            Bytes: preview.Bytes),
                        CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointJsonResult));
                }
                else
                {
                    Console.WriteLine("Database checkpoint dry run.");
                    Console.WriteLine($"  database  : {fullDbPath}");
                    Console.WriteLine($"  name      : {preview.Name}");
                    Console.WriteLine($"  checkpoint: {preview.CheckpointPath}");
                    Console.WriteLine($"  side effect: none (run without --dry-run to copy DB/WAL/SHM files)");
                    Console.WriteLine($"  files     : {ConsoleUi.Counted(preview.Files.Count, "file")}{(preview.FilesTruncated ? " (truncated)" : string.Empty)}");
                    Console.WriteLine($"  bytes     : {preview.Bytes:N0}");
                }

                foreach (var diagnostic in preview.Diagnostics)
                    CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");

                return CommandExitCodes.Success;
            }

            var result = CreateCheckpoint(fullDbPath, options.Name ?? MakeTimestampCheckpointName());
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbCheckpointJsonResult(
                        "success",
                        fullDbPath,
                        result.Name,
                        result.CheckpointPath,
                        result.Files,
                        result.FilesTruncated,
                        CheckpointFileInspectLimit,
                        result.Diagnostics,
                        Bytes: result.Bytes),
                    CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointJsonResult));
            }
            else
            {
                Console.WriteLine("Created database checkpoint.");
                Console.WriteLine($"  database  : {fullDbPath}");
                Console.WriteLine($"  name      : {result.Name}");
                Console.WriteLine($"  checkpoint: {result.CheckpointPath}");
                Console.WriteLine($"  files     : {ConsoleUi.Counted(result.Files.Count, "file")}{(result.FilesTruncated ? " (truncated)" : string.Empty)}");
                Console.WriteLine($"  bytes     : {result.Bytes:N0}");
            }

            foreach (var diagnostic in result.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");

            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            var safeMessage = ex is ArgumentException
                ? ex.Message
                : $"failed to create database checkpoint: {CommandErrorWriter.FormatSanitizedException(ex)}";
            return WriteCommandError(
                options.Json,
                jsonOptions,
                safeMessage,
                CommandExitCodes.DatabaseError,
                "Ensure the database and checkpoint directory are writable, then retry `cdidx db checkpoint`.",
                CommandErrorCodes.DbError,
                category: ex is ArgumentException ? null : DiagnosticRedactor.ClassifyException(ex));
        }
    }

    private static int RunListCheckpoints(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (!TryResolveFileDb(options.DbPath, out var fullDbPath, out var error))
            return WriteCommandError(options.Json, jsonOptions, error, CommandExitCodes.DatabaseError, "Use a filesystem database path, not a SQLite URI.", CommandErrorCodes.DbError);

        var result = ListCheckpoints(fullDbPath);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbCheckpointListJsonResult(
                    fullDbPath,
                    result.Entries,
                    result.Truncated,
                    CheckpointListEntryLimit,
                    CheckpointFileInspectLimit,
                    result.Diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointListJsonResult));
        }
        else
        {
            Console.WriteLine("Database checkpoints");
            Console.WriteLine($"  database: {fullDbPath}");
            if (result.Truncated)
                Console.WriteLine($"  truncated: yes (checkpoint directory limit {CheckpointListEntryLimit:N0}, file limit {CheckpointFileInspectLimit:N0} per checkpoint)");
            if (result.Entries.Count == 0)
            {
                Console.WriteLine("  checkpoints: none");
            }
            else
            {
                foreach (var entry in result.Entries)
                    Console.WriteLine($"  {entry.Name}  {entry.CreatedAtUtc}  {entry.Bytes:N0} bytes{(entry.FilesTruncated ? " (files truncated)" : string.Empty)}");
            }

            foreach (var diagnostic in result.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
        }

        return CommandExitCodes.Success;
    }

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
                return WriteCommandError(options.Json, jsonOptions, $"checkpoint not found: {FormatCheckpointNameForDiagnostic(options.Name)}", CommandExitCodes.NotFound, "Run `cdidx db checkpoints --list` to see available checkpoints.", CommandErrorCodes.DbNotFound);

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

        var pruneResult = PruneRestoreBackups(fullDbPath, options.RestoreBackupsKeep);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbRestoreBackupPruneJsonResult(
                    "success",
                    fullDbPath,
                    options.RestoreBackupsKeep,
                    pruneResult.Deleted,
                    pruneResult.Retained,
                    pruneResult.Truncated,
                    RestoreBackupPruneScanLimit,
                    pruneResult.Diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreBackupPruneJsonResult));
        }
        else
        {
            Console.WriteLine("Pruned database restore backups.");
            Console.WriteLine($"  database: {fullDbPath}");
            Console.WriteLine($"  keep    : {options.RestoreBackupsKeep:N0}");
            Console.WriteLine($"  deleted : {pruneResult.Deleted:N0}");
            Console.WriteLine($"  retained: {pruneResult.Retained:N0}");
            if (pruneResult.Truncated)
                Console.WriteLine($"  truncated: yes (restore backup scan limit {RestoreBackupPruneScanLimit:N0})");
            foreach (var diagnostic in pruneResult.Diagnostics)
                Console.Error.WriteLine($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
        }

        return CommandExitCodes.Success;
    }

    // PRAGMA integrity_check returns a single row `"ok"` when the file passes every consistency
    // probe, otherwise it returns up to N rows of corruption findings. The pragma itself only
    // reads the database, so a read-only connection is sufficient and avoids the WAL-mode
    // pragma side effects of the normal DbContext open path.
    // PRAGMA integrity_check は問題が無ければ 1 行の `"ok"` を、破損があれば最大 N 行の検出結果を返す。
    // 読み取りのみのため read-only 接続で十分で、DbContext の WAL モード設定副作用を避けられる。
    private static DbIntegrityCheckReadResult RunIntegrityCheckPragma(string dbPath, CancellationToken cancellationToken)
    {
        if (IntegrityCheckRowsForTesting != null)
            return BoundIntegrityRows(IntegrityCheckRowsForTesting(), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var connectionString = DbPathResolver.BuildSqliteConnectionString(dbPath, SqliteOpenMode.ReadOnly);

        using var connection = new SqliteConnection(connectionString);
        ReportMaintenanceProgress("integrity_check", "open_connection", dbPath);
        connection.Open();
        ApplyBusyTimeout(connection, cancellationToken);
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection, $"PRAGMA integrity_check({IntegrityCheckRowLimit + 1})");
        ReportMaintenanceProgress("integrity_check", "read_rows", dbPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        var rowsTruncated = false;
        var textTruncated = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= IntegrityCheckRowLimit)
            {
                rowsTruncated = true;
                break;
            }

            var raw = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var bounded = TruncateDiagnosticText(raw, IntegrityCheckTextLimit);
            textTruncated |= bounded.Truncated;
            rows.Add(bounded.Text);
        }
        return new DbIntegrityCheckReadResult(rows.Count > 0 ? rows : new List<string> { "ok" }, rowsTruncated, textTruncated);
    }

    private static DbSchemaReadResult ReadSchema(string dbPath, DbCommandOptions options, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(dbPath, writable: false, cancellationToken);
        ReportMaintenanceProgress("schema", "read_version", dbPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        var rawVersion = versionCmd.ExecuteScalar();
        var userVersion = rawVersion is long l ? (int)l : (rawVersion is int i ? i : 0);
        cancellationToken.ThrowIfCancellationRequested();
        ReportMaintenanceProgress("schema", "count_objects", dbPath);
        var objectTypeCounts = ReadSchemaObjectTypeCounts(connection, options);

        if (options.SchemaSummaryOnly)
        {
            return new DbSchemaReadResult(
                userVersion,
                [],
                objectTypeCounts,
                objectTypeCounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                EntriesTruncated: false,
                SqlTruncated: false);
        }

        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        var whereSql = BuildSchemaWhereSql(options);
        cmd.CommandText = $@"
            SELECT type, name, tbl_name, substr(sql, 1, @sql_limit)
            FROM sqlite_master
            WHERE {whereSql}
            ORDER BY type, name
            LIMIT @entry_limit";
        AddSchemaFilterParameters(cmd, options);
        SqliteCommandPolicy.AddLimit(cmd, "@sql_limit", options.SchemaSqlTextLimit + 1);
        SqliteCommandPolicy.AddLimit(cmd, "@entry_limit", options.SchemaEntryLimit + 1);
        ReportMaintenanceProgress("schema", "read_entries", dbPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        var entries = new List<DbSchemaEntryJsonResult>();
        var entriesTruncated = false;
        var sqlTruncated = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= options.SchemaEntryLimit)
            {
                entriesTruncated = true;
                break;
            }

            var rawSql = reader.IsDBNull(3) ? null : reader.GetString(3);
            var boundedSql = rawSql is null ? (Text: (string?)null, Truncated: false) : TruncateDiagnosticText(rawSql, options.SchemaSqlTextLimit);
            sqlTruncated |= boundedSql.Truncated;
            entries.Add(new DbSchemaEntryJsonResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                boundedSql.Text));
        }

        var emittedTypeCounts = entries
            .GroupBy(entry => entry.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var omittedTypeCounts = objectTypeCounts.ToDictionary(
            kv => kv.Key,
            kv => Math.Max(0, kv.Value - (emittedTypeCounts.TryGetValue(kv.Key, out var emitted) ? emitted : 0)),
            StringComparer.Ordinal);

        return new DbSchemaReadResult(userVersion, entries, objectTypeCounts, omittedTypeCounts, entriesTruncated, sqlTruncated);
    }

    private static Dictionary<string, int> ReadSchemaObjectTypeCounts(SqliteConnection connection, DbCommandOptions options)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["table"] = 0,
            ["index"] = 0,
            ["trigger"] = 0,
            ["view"] = 0,
        };

        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        var whereSql = BuildSchemaWhereSql(options);
        cmd.CommandText = $@"
            SELECT type, COUNT(*)
            FROM sqlite_master
            WHERE {whereSql}
            GROUP BY type";
        AddSchemaFilterParameters(cmd, options);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            if (counts.ContainsKey(type))
                counts[type] = SqliteCommandPolicy.ToInt32Scalar(reader.GetInt64(1), "schema object type count");
        }

        return counts;
    }

    private static string BuildSchemaWhereSql(DbCommandOptions options)
    {
        var clauses = new List<string> { "type IN ('table', 'index', 'trigger', 'view')" };
        if (options.SchemaType is not null)
            clauses.Add("type = @schema_type");
        if (options.SchemaName is not null)
            clauses.Add("name = @schema_name");
        if (!options.SchemaIncludeInternal)
            clauses.Add("name NOT LIKE 'sqlite!_%' ESCAPE '!'");
        return string.Join(" AND ", clauses);
    }

    private static void AddSchemaFilterParameters(SqliteCommand cmd, DbCommandOptions options)
    {
        if (options.SchemaType is not null)
            SqliteCommandPolicy.AddText(cmd, "@schema_type", options.SchemaType);
        if (options.SchemaName is not null)
            SqliteCommandPolicy.AddText(cmd, "@schema_name", options.SchemaName);
    }

    private static DbIntegrityCheckReadResult BoundIntegrityRows(IEnumerable<string> rawRows, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        var rowsTruncated = false;
        var textTruncated = false;
        foreach (var raw in rawRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= IntegrityCheckRowLimit)
            {
                rowsTruncated = true;
                break;
            }

            var bounded = TruncateDiagnosticText(raw, IntegrityCheckTextLimit);
            textTruncated |= bounded.Truncated;
            rows.Add(bounded.Text);
        }

        return new DbIntegrityCheckReadResult(rows.Count > 0 ? rows : new List<string> { "ok" }, rowsTruncated, textTruncated);
    }

    private static (string Text, bool Truncated) TruncateDiagnosticText(string text, int limit)
    {
        if (text.Length <= limit)
            return (text, false);
        return (text[..limit] + " [truncated]", true);
    }

    private static (int OrphanSymbolReferences, int OrphanReferenceLines, int OrphanSymbols, int Total, List<DbDiagnosticJsonResult> Warnings) PruneOrphans(string dbPath, bool apply, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(dbPath, writable: apply, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = apply ? connection.BeginTransaction() : null;
        var warnings = new List<DbDiagnosticJsonResult>();

        ReportMaintenanceProgress("prune", "count_symbol_references", dbPath);
        var orphanSymbolReferences = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM symbol_references sr
            LEFT JOIN files f ON f.id = sr.file_id
            LEFT JOIN reference_lines rl ON rl.id = sr.reference_line_id
            LEFT JOIN files rlf ON rlf.id = rl.file_id
            WHERE f.id IS NULL
               OR (sr.reference_line_id IS NOT NULL AND (rl.id IS NULL OR rlf.id IS NULL))", cancellationToken);
        ReportMaintenanceProgress("prune", "count_reference_lines", dbPath);
        var orphanReferenceLines = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM reference_lines rl
            LEFT JOIN files f ON f.id = rl.file_id
            WHERE f.id IS NULL", cancellationToken);
        ReportMaintenanceProgress("prune", "count_symbols", dbPath);
        var orphanSymbols = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM symbols s
            LEFT JOIN files f ON f.id = s.file_id
            WHERE f.id IS NULL", cancellationToken);

        if (apply)
        {
            ReportMaintenanceProgress("prune", "delete_symbol_references", dbPath);
            Execute(connection, transaction, @"
                DELETE FROM symbol_references
                WHERE file_id NOT IN (SELECT id FROM files)
                   OR (reference_line_id IS NOT NULL AND reference_line_id NOT IN (
                       SELECT rl.id
                       FROM reference_lines rl
                       INNER JOIN files f ON f.id = rl.file_id
                   ))", cancellationToken);
            ReportMaintenanceProgress("prune", "delete_reference_lines", dbPath);
            Execute(connection, transaction, "DELETE FROM reference_lines WHERE file_id NOT IN (SELECT id FROM files)", cancellationToken);
            ReportMaintenanceProgress("prune", "delete_symbols", dbPath);
            Execute(connection, transaction, "DELETE FROM symbols WHERE file_id NOT IN (SELECT id FROM files)", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("prune", "commit", dbPath);
            transaction!.Commit();
            ReportMaintenanceProgress("prune", "optimize", dbPath);
            Execute(connection, null, "PRAGMA optimize", cancellationToken);
            var walWarning = RunWalCheckpointTruncate(connection, cancellationToken);
            if (walWarning is not null)
                warnings.Add(walWarning);
        }

        var total = orphanSymbolReferences + orphanReferenceLines + orphanSymbols;
        return (orphanSymbolReferences, orphanReferenceLines, orphanSymbols, total, warnings);
    }

    private static SqliteConnection OpenConnection(string dbPath, bool writable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connectionString = DbPathResolver.BuildSqliteConnectionString(
            dbPath,
            writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly);
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            ApplyBusyTimeout(connection, cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ApplyBusyTimeout(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = DbPragmaPolicy.ReadBusyTimeoutPragmaSql(DbContext.BusyTimeoutEnvironmentVariable);
        cmd.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        var result = SqliteCommandPolicy.ReadInt32Scalar(cmd, "db maintenance row count");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static DbDiagnosticJsonResult? RunWalCheckpointTruncate(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("prune", "wal_checkpoint_truncate", connection.DataSource);
            using var cmd = SqliteConnectionPolicy.CreateCommand(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            DbContext.WalCheckpointTruncateExecutedForTesting?.Invoke(connection.DataSource);
            cmd.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new DbDiagnosticJsonResult(
                "wal_checkpoint_truncate_failed",
                "WAL checkpoint truncation failed after database prune committed.",
                ConsoleUi.FormatBoundedValue(connection.DataSource));
        }
    }

    private static DbDiagnosticJsonResult CreateCheckpointDiagnostic(string code, string message, string path)
        => new(code, message, ConsoleUi.FormatBoundedValue(path));

    private static bool IsRecoverableFilesystemException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsRecoverableRestoreException(Exception ex)
        => IsRecoverableFilesystemException(ex) || ex is InvalidOperationException;

    private static bool ValidateWritableFileDb(DbCommandOptions options, JsonSerializerOptions jsonOptions, string command, out string fullDbPath, out int exitCode)
    {
        exitCode = CommandExitCodes.Success;
        if (!TryResolveFileDb(options.DbPath, out fullDbPath, out var error))
        {
            WriteCommandError(options.Json, jsonOptions, error, CommandExitCodes.DatabaseError, "Use a filesystem database path, not a SQLite URI.", CommandErrorCodes.DbError);
            exitCode = CommandExitCodes.DatabaseError;
            return false;
        }

        if (!File.Exists(LongPath.EnsureWindowsPrefix(fullDbPath)))
        {
            WriteCommandError(
                options.Json,
                jsonOptions,
                $"database not found: {fullDbPath}",
                CommandExitCodes.NotFound,
                "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.",
                CommandErrorCodes.DbNotFound);
            exitCode = CommandExitCodes.NotFound;
            return false;
        }

        if (DbPathResolver.UriRequestsReadOnly(options.DbPath))
        {
            WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for {command}: {options.DbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path.",
                CommandErrorCodes.DbNotWritable);
            exitCode = CommandExitCodes.DatabaseError;
            return false;
        }

        return true;
    }

    private static bool TryResolveFileDb(string dbPath, out string fullDbPath, out string error)
    {
        fullDbPath = string.Empty;
        error = string.Empty;
        if (dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            error = $"database command requires a filesystem path: {dbPath}";
            return false;
        }

        fullDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        return true;
    }

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

    private static DbCheckpointListReadResult ListCheckpoints(string fullDbPath)
    {
        var root = GetCheckpointRoot(fullDbPath);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (!Directory.Exists(root))
            return new DbCheckpointListReadResult([], Truncated: false, diagnostics);

        var dbFileName = Path.GetFileName(fullDbPath);
        var entries = new List<DbCheckpointListEntryJsonResult>();
        var checkpointsTruncated = false;
        var directoriesInspected = 0;
        var directories = EnumerateCheckpointDirectories(root, diagnostics, CheckpointListEntryLimit + 1);
        checkpointsTruncated |= directories.Truncated;
        foreach (var path in directories.Items)
        {
            if (directoriesInspected >= CheckpointListEntryLimit)
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
        return new DbCheckpointListReadResult(entries, checkpointsTruncated || entries.Any(entry => entry.FilesTruncated), diagnostics);
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
            entries.Add(new DbRestoreBackupEntryJsonResult(
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
                : string.Compare(right.Name, left.Name, StringComparison.Ordinal);
        });
        return new DbRestoreBackupReadResult(entries, backupsTruncated, entries.Any(entry => entry.FilesTruncated), diagnostics);
    }

    private static DbRestoreBackupPruneResult PruneRestoreBackups(string fullDbPath, int keep)
    {
        var result = ListRestoreBackups(fullDbPath, RestoreBackupPruneScanLimit);
        var diagnostics = result.Diagnostics;
        if (result.DirectoryEnumerationTruncated)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_prune_truncated",
                "Restore backup pruning was skipped because backup enumeration reached the scan limit.",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return new DbRestoreBackupPruneResult(Deleted: 0, Retained: result.Entries.Count, Truncated: true, diagnostics);
        }

        var deleted = 0;
        foreach (var entry in result.Entries.Skip(keep))
        {
            if (TryDeleteRestoreBackupDirectory(fullDbPath, entry.BackupPath, diagnostics))
                deleted++;
        }

        var retained = result.Entries.Count - deleted;
        return new DbRestoreBackupPruneResult(deleted, retained, result.Truncated, diagnostics);
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

    private static string RestoreCheckpoint(string fullDbPath, string name, string checkpointPath)
    {
        ValidateCheckpointName(name);
        SqliteConnection.ClearAllPools();
        var checkpointDbPath = Path.Combine(checkpointPath, Path.GetFileName(fullDbPath));
        if (!File.Exists(LongPath.EnsureWindowsPrefix(checkpointDbPath)))
            throw new InvalidOperationException($"checkpoint is incomplete: {FormatCheckpointNameForDiagnostic(name)}");

        var restorePathSuffix = MakeRestorePathSuffix();
        var restoreTempPath = fullDbPath + ".restore-tmp-" + restorePathSuffix;
        var backupPath = fullDbPath + ".restore-backup-" + restorePathSuffix;
        DataDirectorySecurity.CreateSensitiveDirectory(restoreTempPath);
        try
        {
            CopyIfExists(checkpointDbPath, Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)), privateDestination: true);
            CopyIfExists(Path.Combine(checkpointPath, Path.GetFileName(fullDbPath) + "-wal"), Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-wal"), privateDestination: true);
            CopyIfExists(Path.Combine(checkpointPath, Path.GetFileName(fullDbPath) + "-shm"), Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-shm"), privateDestination: true);
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)))))
                throw new InvalidOperationException($"checkpoint staging failed: {FormatCheckpointNameForDiagnostic(name)}");

            DataDirectorySecurity.CreateSensitiveDirectory(backupPath);
            MoveIfExists(fullDbPath, Path.Combine(backupPath, Path.GetFileName(fullDbPath)), privateDestination: true);
            MoveIfExists(fullDbPath + "-wal", Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-wal"), privateDestination: true);
            MoveIfExists(fullDbPath + "-shm", Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-shm"), privateDestination: true);

            RestoreFailureAfterBackupForTesting?.Invoke();

            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)), fullDbPath, privateDestination: true);
            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-wal"), fullDbPath + "-wal", privateDestination: true);
            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-shm"), fullDbPath + "-shm", privateDestination: true);
        }
        catch (Exception primaryEx)
        {
            DbDiagnosticJsonResult? rollbackFailure = null;
            try
            {
                RestoreBackedUpFiles(fullDbPath, backupPath);
            }
            catch (Exception rollbackEx) when (IsRecoverableRestoreException(rollbackEx))
            {
                rollbackFailure = new DbDiagnosticJsonResult(
                    "restore_rollback_failed",
                    $"Failed to roll back database restore from backup ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).",
                    ConsoleUi.FormatBoundedValue(backupPath));
                CommandErrorWriter.WriteStderr($"Warning [{rollbackFailure.Code}]: {rollbackFailure.Message} Backup: {rollbackFailure.Path}");
            }

            throw new DbRestoreOperationException(primaryEx, checkpointPath, backupPath, rollbackFailure);
        }
        finally
        {
            TryDeleteTemporaryDirectory(
                restoreTempPath,
                "restore temporary directory",
                Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath("."),
                Path.GetFileName(fullDbPath) + ".restore-tmp-");
        }

        return backupPath;
    }

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
        if (!File.Exists(normalizedPath))
            return false;

        var attributes = File.GetAttributes(normalizedPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
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
        out string failureReason)
    {
        var options = new DirectoryCleanupBoundaryOptions(
            expectedNamePrefix,
            "target is outside the expected cleanup root",
            "target name does not match the expected temporary-directory prefix",
            "target is not a regular temporary directory");
        return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
            path,
            safeRoot,
            options,
            out fullPath,
            out failureReason);
    }

    internal static DbCommandOptions ParseArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var json = false;
        var integrityCheck = false;
        var schema = false;
        var prune = false;
        var pruneDryRun = false;
        var pruneApply = false;
        var checkpoint = false;
        var listCheckpoints = false;
        var restore = false;
        var restoreBackups = false;
        var restoreBackupsList = false;
        var restoreBackupsPrune = false;
        var restoreBackupsKeep = DefaultRestoreBackupKeepCount;
        var schemaSummaryOnly = false;
        var schemaEntryLimit = SchemaEntryLimit;
        var schemaSqlTextLimit = SchemaSqlTextLimit;
        bool? schemaIncludeInternal = null;
        var schemaSpecificOptionSeen = false;
        string? parsedSchemaType = null;
        string? parsedSchemaName = null;
        string? name = null;
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--db":
                    parseError = "--db requires a value";
                    break;
                case "--json":
                    json = true;
                    break;
                case "--integrity-check":
                    integrityCheck = true;
                    break;
                case "integrity":
                    integrityCheck = true;
                    break;
                case "schema":
                    schema = true;
                    break;
                case "--type" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    var schemaType = args[++i].Trim().ToLowerInvariant();
                    if (!SchemaObjectTypes.Contains(schemaType, StringComparer.Ordinal))
                        parseError = "--type must be one of table, index, trigger, or view";
                    else
                        parsedSchemaType = schemaType;
                    break;
                case "--type":
                    parseError = "--type requires a value";
                    break;
                case "--name" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    parsedSchemaName = args[++i];
                    break;
                case "--name":
                    parseError = "--name requires a value";
                    break;
                case "--summary-only":
                    schemaSpecificOptionSeen = true;
                    schemaSummaryOnly = true;
                    break;
                case "--limit" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out schemaEntryLimit)
                        || schemaEntryLimit < 0
                        || schemaEntryLimit > SchemaEntryLimit)
                    {
                        parseError = $"--limit must be an integer from 0 to {SchemaEntryLimit}";
                    }
                    break;
                case "--limit":
                    parseError = "--limit requires a value";
                    break;
                case "--max-sql-chars" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out schemaSqlTextLimit)
                        || schemaSqlTextLimit < 0
                        || schemaSqlTextLimit > SchemaSqlTextLimit)
                    {
                        parseError = $"--max-sql-chars must be an integer from 0 to {SchemaSqlTextLimit}";
                    }
                    break;
                case "--max-sql-chars":
                    parseError = "--max-sql-chars requires a value";
                    break;
                case "--include-internal":
                    schemaSpecificOptionSeen = true;
                    if (schemaIncludeInternal == false)
                        parseError = "--include-internal and --exclude-internal cannot be combined";
                    else
                        schemaIncludeInternal = true;
                    break;
                case "--exclude-internal":
                    schemaSpecificOptionSeen = true;
                    if (schemaIncludeInternal == true)
                        parseError = "--include-internal and --exclude-internal cannot be combined";
                    else
                        schemaIncludeInternal = false;
                    break;
                case "prune":
                    prune = true;
                    break;
                case "checkpoint":
                    checkpoint = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        name = args[++i];
                    break;
                case "checkpoints":
                    listCheckpoints = true;
                    break;
                case "restore":
                    restore = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        name = args[++i];
                    else
                        parseError = "restore requires a checkpoint name";
                    break;
                case "restore-backups":
                    restoreBackups = true;
                    break;
                case "--dry-run":
                    pruneDryRun = true;
                    break;
                case "--apply":
                    pruneApply = true;
                    break;
                case "--prune":
                    if (restoreBackups)
                        restoreBackupsPrune = true;
                    else
                        parseError = "--prune is only valid with `cdidx db restore-backups --prune`";
                    break;
                case "--keep" when i + 1 < args.Length:
                    if (!restoreBackups)
                    {
                        parseError = "--keep is only valid with `cdidx db restore-backups --prune --keep <n>`";
                        break;
                    }

                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out restoreBackupsKeep)
                        || restoreBackupsKeep < 0
                        || restoreBackupsKeep > MaxRestoreBackupKeepCount)
                    {
                        parseError = $"--keep must be an integer from 0 to {MaxRestoreBackupKeepCount}";
                    }
                    break;
                case "--keep":
                    parseError = "--keep requires a value";
                    break;
                case "--list":
                    if (listCheckpoints)
                        break;
                    if (restoreBackups)
                    {
                        restoreBackupsList = true;
                        break;
                    }

                    parseError = "--list is only valid with `cdidx db checkpoints --list`";
                    break;
                case "--help" or "-h":
                    return new DbCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json };
                default:
                    if (args[i].StartsWith('-'))
                        parseError = $"db does not support option: '{args[i]}'";
                    else
                        parseError = $"unknown db command or argument: '{args[i]}'";
                    break;
            }

            if (parseError != null)
                break;
        }

        if (parseError is null && restoreBackups && (pruneDryRun || pruneApply))
            parseError = "--dry-run and --apply are not supported with `cdidx db restore-backups`; use `--prune --keep <n>` to delete retained backups.";
        if (parseError is null && !schema && schemaSpecificOptionSeen)
            parseError = "--type, --name, --summary-only, --limit, --max-sql-chars, --include-internal, and --exclude-internal are only valid with `cdidx db schema`.";
        if (parseError is null && pruneDryRun && !prune && !checkpoint)
            parseError = "--dry-run is only valid with `cdidx db prune --dry-run` or `cdidx db checkpoint --dry-run`.";
        if (parseError is null && pruneApply && !prune)
            parseError = "--apply is only valid with `cdidx db prune --apply`.";

        return new DbCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            IntegrityCheck = integrityCheck,
            Schema = schema,
            Prune = prune,
            PruneDryRun = pruneDryRun,
            PruneApply = pruneApply,
            Checkpoint = checkpoint,
            ListCheckpoints = listCheckpoints,
            Restore = restore,
            RestoreBackups = restoreBackups,
            RestoreBackupsList = restoreBackupsList,
            RestoreBackupsPrune = restoreBackupsPrune,
            RestoreBackupsKeep = restoreBackupsKeep,
            SchemaSummaryOnly = schemaSummaryOnly,
            SchemaEntryLimit = schemaEntryLimit,
            SchemaSqlTextLimit = schemaSqlTextLimit,
            SchemaIncludeInternal = schemaIncludeInternal ?? true,
            SchemaType = parsedSchemaType,
            SchemaName = parsedSchemaName,
            CheckpointDryRun = checkpoint && pruneDryRun,
            Name = name,
            ParseError = parseError,
        };
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null, string? category = null)
    {
        return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, message, exitCode, hint, errorCode: errorCode, category: category);
    }

    private static int WriteMaintenanceCancelled(bool json, JsonSerializerOptions jsonOptions, string operation)
        => WriteCommandError(
            json,
            jsonOptions,
            $"{operation} cancelled before it could complete",
            CommandExitCodes.CancelledBySignal,
            "Retry the command after the cancelling operation completes.",
            CommandErrorCodes.Interrupted);

    private static void ReportMaintenanceProgress(string operation, string phase, string dbPath)
    {
        GlobalToolLog.Info($"db_maintenance_progress operation={operation} phase={phase} db_path={ConsoleUi.FormatBoundedValue(dbPath)}");
        MaintenanceProgressForTesting?.Invoke(operation, phase);
    }
}

internal sealed class DbCommandOptions
{
    public string DbPath { get; init; } = string.Empty;
    public bool Json { get; init; }
    public bool ShowHelp { get; init; }
    public bool IntegrityCheck { get; init; }
    public bool Schema { get; init; }
    public bool Prune { get; init; }
    public bool PruneDryRun { get; init; }
    public bool PruneApply { get; init; }
    public bool Checkpoint { get; init; }
    public bool ListCheckpoints { get; init; }
    public bool Restore { get; init; }
    public bool RestoreBackups { get; init; }
    public bool RestoreBackupsList { get; init; }
    public bool RestoreBackupsPrune { get; init; }
    public int RestoreBackupsKeep { get; init; } = DbCommandRunner.DefaultRestoreBackupKeepCount;
    public bool SchemaSummaryOnly { get; init; }
    public int SchemaEntryLimit { get; init; } = DbCommandRunner.SchemaEntryLimit;
    public int SchemaSqlTextLimit { get; init; } = DbCommandRunner.SchemaSqlTextLimit;
    public bool SchemaIncludeInternal { get; init; } = true;
    public string? SchemaType { get; init; }
    public string? SchemaName { get; init; }
    public bool CheckpointDryRun { get; init; }
    public string? Name { get; init; }
    public string? ParseError { get; init; }
}

internal sealed record DbCheckpointOperationResult(string Name, string CheckpointPath, List<string> Files, bool FilesTruncated, List<DbDiagnosticJsonResult> Diagnostics, long Bytes);

internal sealed record DbCheckpointListReadResult(List<DbCheckpointListEntryJsonResult> Entries, bool Truncated, List<DbDiagnosticJsonResult> Diagnostics);

internal sealed record DbRestoreBackupReadResult(
    List<DbRestoreBackupEntryJsonResult> Entries,
    bool DirectoryEnumerationTruncated,
    bool FileInspectionTruncated,
    List<DbDiagnosticJsonResult> Diagnostics)
{
    public bool Truncated => DirectoryEnumerationTruncated || FileInspectionTruncated;
}

internal sealed record DbRestoreBackupPruneResult(int Deleted, int Retained, bool Truncated, List<DbDiagnosticJsonResult> Diagnostics);

internal sealed class DbRestoreOperationException : Exception
{
    public DbRestoreOperationException(
        Exception innerException,
        string checkpointPath,
        string backupPath,
        DbDiagnosticJsonResult? rollbackFailure)
        : base("database restore failed", innerException)
    {
        CheckpointPath = checkpointPath;
        BackupPath = backupPath;
        RollbackFailure = rollbackFailure;
    }

    public string CheckpointPath { get; }

    public string BackupPath { get; }

    public DbDiagnosticJsonResult? RollbackFailure { get; }
}

internal sealed record DbIntegrityCheckReadResult(List<string> Rows, bool RowsTruncated, bool TextTruncated)
{
    public bool Truncated => RowsTruncated || TextTruncated;
}

internal sealed record DbSchemaReadResult(
    int UserVersion,
    List<DbSchemaEntryJsonResult> Entries,
    Dictionary<string, int> ObjectTypeCounts,
    Dictionary<string, int> ObjectTypeOmittedCounts,
    bool EntriesTruncated,
    bool SqlTruncated)
{
    public bool Truncated => EntriesTruncated || SqlTruncated;
}
