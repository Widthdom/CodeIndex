using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs `db` subcommands that operate directly on the SQLite file (integrity check, schema, prune).
/// SQLite ファイル本体に対する `db` サブコマンド（整合性チェック、schema、prune）を実行する。
/// </summary>
public static partial class DbCommandRunner
{
    private const string CheckpointsDirectorySuffix = ".checkpoints";
    private const string AutoCheckpointPrefix = "auto-";
    internal const int MaxCheckpointNameLength = 128;
    private const int CheckpointNameDiagnosticTextLimit = 80;
    internal const int CheckpointListEntryLimit = 100;
    internal const int CheckpointPruneScanLimit = 1_000;
    internal const int CheckpointFileInspectLimit = 32;
    internal const int CheckpointManifestByteLimit = 16 * 1024;
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
    private static readonly AsyncLocal<Func<string, long?>?> ScopedAvailableFreeSpaceForTesting = new();
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
    internal static Func<string, long?>? AvailableFreeSpaceForTesting
    {
        get => ScopedAvailableFreeSpaceForTesting.Value;
        set => ScopedAvailableFreeSpaceForTesting.Value = value;
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
                "Pass `integrity`, `--integrity-check`, `schema`, `prune --dry-run|--apply`, `checkpoint [name]`, `checkpoints --list|--delete|--prune`, `restore <name> [--dry-run]`, or `restore-backups --list|--prune --keep <n> [--dry-run]`.",
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
                "Run one of `cdidx db integrity`, `cdidx db --integrity-check`, `cdidx db schema`, `cdidx db prune --dry-run|--apply`, `cdidx db checkpoint [name]`, `cdidx db checkpoints --list|--delete|--prune`, `cdidx db restore <name> [--dry-run]`, or `cdidx db restore-backups --list|--prune --keep <n> [--dry-run]`.",
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
        {
            if (options.IntegrityCheck)
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    options.Json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.Create(
                        "db integrity",
                        dbPath,
                        options.ShowPaths,
                        MaintenanceDatabaseFailureKind.Missing));
            }

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database not found: {dbPath}",
                CommandExitCodes.NotFound,
                "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.",
                CommandErrorCodes.DbNotFound);
        }

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
                return RunCheckpoints(options, jsonOptions);

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

            if (!ok)
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    options.Json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.Create(
                        "db integrity",
                        dbPath,
                        options.ShowPaths,
                        MaintenanceDatabaseFailureKind.Corrupt,
                        details: issues,
                        detailsTruncated: result.Truncated));
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbIntegrityCheckJsonResult(
                        displayDbPath,
                        true,
                        "ok",
                        "integrity_ok",
                        new List<string>(),
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
                Console.WriteLine("  result  : ok");
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

            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromException(
                    "db integrity",
                    dbPath,
                    options.ShowPaths,
                    ex));
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
                var payload = JsonSerializer.SerializeToNode(
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
                    jsonContext.DbSchemaJsonResult)!.AsObject();
                AddDbSchemaOmissionMetadata(payload, schema, options);
                CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
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
                $"failed to read schema: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx db schema`. If this persists, rebuild with `cdidx index <projectPath> --rebuild`.",
                CommandErrorCodes.DbError);
        }
    }

    private static void AddDbSchemaOmissionMetadata(JsonObject payload, DbSchemaReadResult schema, DbCommandOptions options)
    {
        var emittedCount = schema.Entries.Count;
        var omittedCount = schema.ObjectTypeOmittedCounts.Values.Sum();
        payload["emitted_count"] = emittedCount;
        payload["omitted_count"] = omittedCount;

        var omittedBy = new JsonArray();
        if (options.SchemaSummaryOnly && omittedCount > 0)
        {
            payload["summary_only_omitted_count"] = omittedCount;
            omittedBy.Add("summary_only");
        }
        if (!options.SchemaSummaryOnly && schema.EntriesTruncated)
        {
            payload["row_limit_reached"] = true;
            payload["limit_omitted_count"] = omittedCount;
            omittedBy.Add("limit");
        }
        if (schema.SqlTruncated)
            omittedBy.Add("max_sql_chars");
        if (omittedBy.Count > 0)
            payload["omitted_by"] = omittedBy;
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
    public bool ShowPaths { get; init; }
    public bool IntegrityCheck { get; init; }
    public bool Schema { get; init; }
    public bool Prune { get; init; }
    public bool PruneDryRun { get; init; }
    public bool PruneApply { get; init; }
    public bool Checkpoint { get; init; }
    public bool ListCheckpoints { get; init; }
    public bool CheckpointsList { get; init; }
    public bool CheckpointsDelete { get; init; }
    public bool CheckpointsPrune { get; init; }
    public int CheckpointsKeep { get; init; } = DbCommandRunner.DefaultRestoreBackupKeepCount;
    public bool CheckpointsDryRun { get; init; }
    public bool Restore { get; init; }
    public bool RestoreDryRun { get; init; }
    public bool RestoreBackups { get; init; }
    public bool RestoreBackupsList { get; init; }
    public bool RestoreBackupsPrune { get; init; }
    public int RestoreBackupsKeep { get; init; } = DbCommandRunner.DefaultRestoreBackupKeepCount;
    public bool RestoreBackupsDryRun { get; init; }
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

internal sealed record DbCheckpointListReadResult(
    List<DbCheckpointListEntryJsonResult> Entries,
    bool DirectoryEnumerationTruncated,
    bool FileInspectionTruncated,
    List<DbDiagnosticJsonResult> Diagnostics)
{
    public bool Truncated => DirectoryEnumerationTruncated || FileInspectionTruncated;
}

internal sealed record DbRestoreBackupReadResult(
    List<DbRestoreBackupEntryJsonResult> Entries,
    bool DirectoryEnumerationTruncated,
    bool FileInspectionTruncated,
    List<DbDiagnosticJsonResult> Diagnostics)
{
    public bool Truncated => DirectoryEnumerationTruncated || FileInspectionTruncated;
}

internal sealed record DbCheckpointCleanupResult(
    int Deleted,
    int Retained,
    List<string> DeletedPaths,
    List<string> RetainedPaths,
    bool Truncated,
    List<DbDiagnosticJsonResult> Diagnostics);

internal sealed record DbRestorePreviewResult(
    bool Ready,
    bool ManifestValid,
    bool PathsValid,
    bool SpaceCheckAvailable,
    bool? SpaceSufficient,
    long RequiredSpaceBytes,
    long? AvailableSpaceBytes,
    List<string> Files,
    long Bytes,
    List<DbDiagnosticJsonResult> Diagnostics);

internal sealed record DbCheckpointPayloadValidationResult(
    bool PathsValid,
    List<string> Files,
    long Bytes);

internal sealed record DbRestoreBackupPruneResult(
    int Deleted,
    int Retained,
    List<string> DeletedPaths,
    List<string> RetainedPaths,
    bool Truncated,
    List<DbDiagnosticJsonResult> Diagnostics);

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
