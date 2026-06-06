using System.Text.Json;
using CodeIndex.Database;
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
    internal const int IntegrityCheckRowLimit = 100;
    internal const int IntegrityCheckTextLimit = 4096;
    internal const int SchemaEntryLimit = 200;
    internal const int SchemaSqlTextLimit = 8192;
    private static readonly char[] InvalidCheckpointNameChars = Path.GetInvalidFileNameChars();
    internal static Action? RestoreFailureAfterBackupForTesting { get; set; }
    internal static Action<string>? DeleteTemporaryDirectoryForTesting { get; set; }
    internal static Func<IEnumerable<string>>? IntegrityCheckRowsForTesting { get; set; }

    public static int Run(string[] cmdArgs, JsonSerializerOptions jsonOptions)
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

        if (!options.IntegrityCheck && !options.Schema && !options.Prune && !options.Checkpoint && !options.ListCheckpoints && !options.Restore)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db requires a mode flag",
                CommandExitCodes.UsageError,
                "Pass `--integrity-check`, `schema`, `prune --dry-run|--apply`, `checkpoint [name]`, `checkpoints --list`, or `restore <name>`.",
                CommandErrorCodes.UsageError);

        if ((options.IntegrityCheck ? 1 : 0)
            + (options.Schema ? 1 : 0)
            + (options.Prune ? 1 : 0)
            + (options.Checkpoint ? 1 : 0)
            + (options.ListCheckpoints ? 1 : 0)
            + (options.Restore ? 1 : 0) > 1)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db accepts exactly one mode",
                CommandExitCodes.UsageError,
                "Run one of `cdidx db --integrity-check`, `cdidx db schema`, `cdidx db prune --dry-run|--apply`, `cdidx db checkpoint [name]`, `cdidx db checkpoints --list`, or `cdidx db restore <name>`.",
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

        if (options.Schema)
            return RunSchema(options, jsonOptions, dbPath, isUri);

        if (options.Prune)
            return RunPrune(options, jsonOptions, dbPath, isUri);

        if (options.Checkpoint)
            return RunCheckpoint(options, jsonOptions);

        if (options.ListCheckpoints)
            return RunListCheckpoints(options, jsonOptions);

        if (options.Restore)
            return RunRestore(options, jsonOptions);

        return RunIntegrityCheck(options, jsonOptions, dbPath, isUri);
    }

    public static int RunIntegrityCheck(string[] cmdArgs, JsonSerializerOptions jsonOptions) => Run(cmdArgs, jsonOptions);

    internal static string CreateAutomaticCheckpoint(string dbPath)
    {
        var fullDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var name = AutoCheckpointPrefix + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return CreateCheckpoint(fullDbPath, name).CheckpointPath;
    }

    private static int RunIntegrityCheck(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri)
    {
        try
        {
            var result = RunIntegrityCheckPragma(dbPath);
            var issues = result.Rows;
            var ok = issues.Count == 1 && string.Equals(issues[0], "ok", StringComparison.Ordinal);
            var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
            var displayDbPath = DbPathResolver.FormatDbPathForDisplay(dbPath);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbIntegrityCheckJsonResult(
                        displayDbPath,
                        ok,
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
                    Console.Error.WriteLine($"Error [{CommandErrorCodes.DbIntegrityFailed}]: SQLite reported integrity_check failures.");
                    Console.Error.WriteLine("Hint: rebuild with `cdidx index <projectPath> --rebuild` to discard the corrupted DB and start fresh.");
                }
            }

            return ok ? CommandExitCodes.Success : CommandExitCodes.DatabaseError;
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

    private static int RunSchema(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri)
    {
        try
        {
            var schema = ReadSchema(dbPath);
            var fullPath = DbPathResolver.FormatDbPathForDisplay(dbPath);
            if (options.Json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbSchemaJsonResult(
                        fullPath,
                        schema.UserVersion,
                        schema.Entries,
                        schema.Truncated,
                        schema.EntriesTruncated,
                        schema.SqlTruncated,
                        SchemaEntryLimit,
                        SchemaSqlTextLimit),
                    jsonContext.DbSchemaJsonResult));
            }
            else
            {
                Console.WriteLine("Database schema");
                Console.WriteLine($"  database    : {fullPath}");
                Console.WriteLine($"  user_version: {schema.UserVersion}");
                if (schema.Truncated)
                    Console.WriteLine($"  truncated   : yes (entry limit {SchemaEntryLimit:N0}, SQL text limit {SchemaSqlTextLimit:N0} chars)");
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

    private static int RunPrune(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri)
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
            var result = PruneOrphans(dbPath, apply: options.PruneApply);
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
                        result.Total),
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
            }

            return CommandExitCodes.Success;
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
                        CheckpointFileInspectLimit),
                    CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointJsonResult));
            }
            else
            {
                Console.WriteLine("Created database checkpoint.");
                Console.WriteLine($"  database  : {fullDbPath}");
                Console.WriteLine($"  name      : {result.Name}");
                Console.WriteLine($"  checkpoint: {result.CheckpointPath}");
                Console.WriteLine($"  files     : {ConsoleUi.Counted(result.Files.Count, "file")}{(result.FilesTruncated ? " (truncated)" : string.Empty)}");
            }

            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to create database checkpoint: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Ensure the database and checkpoint directory are writable, then retry `cdidx db checkpoint`.",
                CommandErrorCodes.DbError);
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
                    CheckpointFileInspectLimit),
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
        }

        return CommandExitCodes.Success;
    }

    private static int RunRestore(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
            return WriteCommandError(options.Json, jsonOptions, "restore requires a checkpoint name", CommandExitCodes.UsageError, "Use `cdidx db restore <name> --db <path>`.", CommandErrorCodes.UsageError);
        if (!ValidateWritableFileDb(options, jsonOptions, "restore", out var fullDbPath, out var validationExitCode))
            return validationExitCode;

        try
        {
            var checkpointPath = GetCheckpointPath(fullDbPath, options.Name);
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
        catch (Exception ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to restore database checkpoint: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Ensure no cdidx writer is running, then retry `cdidx db restore <name>`.",
                CommandErrorCodes.DbError);
        }
    }

    // PRAGMA integrity_check returns a single row `"ok"` when the file passes every consistency
    // probe, otherwise it returns up to N rows of corruption findings. The pragma itself only
    // reads the database, so a read-only connection is sufficient and avoids the WAL-mode
    // pragma side effects of the normal DbContext open path.
    // PRAGMA integrity_check は問題が無ければ 1 行の `"ok"` を、破損があれば最大 N 行の検出結果を返す。
    // 読み取りのみのため read-only 接続で十分で、DbContext の WAL モード設定副作用を避けられる。
    private static DbIntegrityCheckReadResult RunIntegrityCheckPragma(string dbPath)
    {
        if (IntegrityCheckRowsForTesting != null)
            return BoundIntegrityRows(IntegrityCheckRowsForTesting());

        var connectionString = DbPathResolver.BuildSqliteConnectionString(dbPath, SqliteOpenMode.ReadOnly);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA integrity_check({IntegrityCheckRowLimit + 1})";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        var rowsTruncated = false;
        var textTruncated = false;
        while (reader.Read())
        {
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

    private static DbSchemaReadResult ReadSchema(string dbPath)
    {
        using var connection = OpenConnection(dbPath, writable: false);
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        var rawVersion = versionCmd.ExecuteScalar();
        var userVersion = rawVersion is long l ? (int)l : (rawVersion is int i ? i : 0);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT type, name, tbl_name, substr(sql, 1, @sql_limit)
            FROM sqlite_master
            WHERE type IN ('table', 'index', 'trigger', 'view')
            ORDER BY type, name
            LIMIT @entry_limit";
        cmd.Parameters.AddWithValue("@sql_limit", SchemaSqlTextLimit + 1);
        cmd.Parameters.AddWithValue("@entry_limit", SchemaEntryLimit + 1);
        using var reader = cmd.ExecuteReader();
        var entries = new List<DbSchemaEntryJsonResult>();
        var entriesTruncated = false;
        var sqlTruncated = false;
        while (reader.Read())
        {
            if (entries.Count >= SchemaEntryLimit)
            {
                entriesTruncated = true;
                break;
            }

            var rawSql = reader.IsDBNull(3) ? null : reader.GetString(3);
            var boundedSql = rawSql is null ? (Text: (string?)null, Truncated: false) : TruncateDiagnosticText(rawSql, SchemaSqlTextLimit);
            sqlTruncated |= boundedSql.Truncated;
            entries.Add(new DbSchemaEntryJsonResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                boundedSql.Text));
        }

        return new DbSchemaReadResult(userVersion, entries, entriesTruncated, sqlTruncated);
    }

    private static DbIntegrityCheckReadResult BoundIntegrityRows(IEnumerable<string> rawRows)
    {
        var rows = new List<string>();
        var rowsTruncated = false;
        var textTruncated = false;
        foreach (var raw in rawRows)
        {
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

    private static (int OrphanSymbolReferences, int OrphanReferenceLines, int OrphanSymbols, int Total) PruneOrphans(string dbPath, bool apply)
    {
        using var connection = OpenConnection(dbPath, writable: apply);
        using var transaction = apply ? connection.BeginTransaction() : null;

        var orphanSymbolReferences = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM symbol_references sr
            LEFT JOIN files f ON f.id = sr.file_id
            LEFT JOIN reference_lines rl ON rl.id = sr.reference_line_id
            LEFT JOIN files rlf ON rlf.id = rl.file_id
            WHERE f.id IS NULL
               OR (sr.reference_line_id IS NOT NULL AND (rl.id IS NULL OR rlf.id IS NULL))");
        var orphanReferenceLines = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM reference_lines rl
            LEFT JOIN files f ON f.id = rl.file_id
            WHERE f.id IS NULL");
        var orphanSymbols = Count(connection, transaction, @"
            SELECT COUNT(*)
            FROM symbols s
            LEFT JOIN files f ON f.id = s.file_id
            WHERE f.id IS NULL");

        if (apply)
        {
            Execute(connection, transaction, @"
                DELETE FROM symbol_references
                WHERE file_id NOT IN (SELECT id FROM files)
                   OR (reference_line_id IS NOT NULL AND reference_line_id NOT IN (
                       SELECT rl.id
                       FROM reference_lines rl
                       INNER JOIN files f ON f.id = rl.file_id
                   ))");
            Execute(connection, transaction, "DELETE FROM reference_lines WHERE file_id NOT IN (SELECT id FROM files)");
            Execute(connection, transaction, "DELETE FROM symbols WHERE file_id NOT IN (SELECT id FROM files)");
            transaction!.Commit();
            Execute(connection, null, "PRAGMA optimize");
            RunWalCheckpointTruncate(connection);
        }

        var total = orphanSymbolReferences + orphanReferenceLines + orphanSymbols;
        return (orphanSymbolReferences, orphanReferenceLines, orphanSymbols, total);
    }

    private static SqliteConnection OpenConnection(string dbPath, bool writable)
    {
        var connectionString = DbPathResolver.BuildSqliteConnectionString(
            dbPath,
            writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly);
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static int Count(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void RunWalCheckpointTruncate(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            DbContext.WalCheckpointTruncateExecutedForTesting?.Invoke(connection.DataSource);
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // WAL truncation is opportunistic cleanup. Prune has already committed.
        }
    }

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
            DataDirectorySecurity.WritePrivateText(Path.Combine(tempPath, "manifest.txt"), $"name={name}{Environment.NewLine}created_at_utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}db={fullDbPath}{Environment.NewLine}");
            Directory.Move(tempPath, checkpointPath);
        }
        catch
        {
            TryDeleteTemporaryDirectory(tempPath, "checkpoint temporary directory");
            throw;
        }

        var files = EnumerateCheckpointFileNames(checkpointPath);
        return new DbCheckpointOperationResult(name, checkpointPath, files.Items, files.Truncated);
    }

    private static DbCheckpointListReadResult ListCheckpoints(string fullDbPath)
    {
        var root = GetCheckpointRoot(fullDbPath);
        if (!Directory.Exists(root))
            return new DbCheckpointListReadResult([], Truncated: false);

        var dbFileName = Path.GetFileName(fullDbPath);
        var entries = new List<DbCheckpointListEntryJsonResult>();
        var checkpointsTruncated = false;
        var directoriesInspected = 0;
        foreach (var path in Directory.EnumerateDirectories(root))
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

            var info = new DirectoryInfo(path);
            var bytes = SumCheckpointBytes(path);
            entries.Add(new DbCheckpointListEntryJsonResult(
                info.Name,
                path,
                info.CreationTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                bytes.Bytes,
                bytes.Truncated));
        }

        entries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return new DbCheckpointListReadResult(entries, checkpointsTruncated || entries.Any(entry => entry.FilesTruncated));
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointFileNames(string checkpointPath)
    {
        var files = new List<string>();
        var truncated = false;
        foreach (var file in Directory.EnumerateFiles(checkpointPath))
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

        files.Sort(StringComparer.Ordinal);
        return (files, truncated);
    }

    private static (long Bytes, bool Truncated) SumCheckpointBytes(string checkpointPath)
    {
        long bytes = 0;
        var filesSeen = 0;
        foreach (var file in Directory.EnumerateFiles(checkpointPath))
        {
            if (filesSeen >= CheckpointFileInspectLimit)
                return (bytes, Truncated: true);

            bytes += new FileInfo(file).Length;
            filesSeen++;
        }

        return (bytes, Truncated: false);
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
        catch
        {
            RestoreBackedUpFiles(fullDbPath, backupPath);
            throw;
        }
        finally
        {
            TryDeleteTemporaryDirectory(restoreTempPath, "restore temporary directory");
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
        => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    private static string MakeRestorePathSuffix()
        => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");

    private static string GetCheckpointRoot(string fullDbPath)
        => fullDbPath + CheckpointsDirectorySuffix;

    private static string GetCheckpointPath(string fullDbPath, string name)
    {
        ValidateCheckpointName(name);
        return Path.Combine(GetCheckpointRoot(fullDbPath), name);
    }

    private static void CopyIfExists(string source, string destination, bool privateDestination = false)
    {
        if (File.Exists(LongPath.EnsureWindowsPrefix(source)))
        {
            File.Copy(LongPath.EnsureWindowsPrefix(source), LongPath.EnsureWindowsPrefix(destination), overwrite: false);
            if (privateDestination)
                DataDirectorySecurity.ApplyPrivateFileMode(destination);
        }
    }

    private static void MoveIfExists(string source, string destination, bool privateDestination = false)
    {
        if (File.Exists(LongPath.EnsureWindowsPrefix(source)))
        {
            File.Move(LongPath.EnsureWindowsPrefix(source), LongPath.EnsureWindowsPrefix(destination));
            if (privateDestination)
                DataDirectorySecurity.ApplyPrivateFileMode(destination);
        }
    }

    private static void RestoreBackedUpFiles(string fullDbPath, string backupPath)
    {
        if (!Directory.Exists(backupPath))
            return;

        DeleteIfExists(fullDbPath);
        DeleteIfExists(fullDbPath + "-wal");
        DeleteIfExists(fullDbPath + "-shm");
        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath)), fullDbPath, privateDestination: true);
        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-wal"), fullDbPath + "-wal", privateDestination: true);
        MoveIfExists(Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-shm"), fullDbPath + "-shm", privateDestination: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(LongPath.EnsureWindowsPrefix(path)))
            File.Delete(LongPath.EnsureWindowsPrefix(path));
    }

    private static void TryDeleteTemporaryDirectory(string path, string cleanupDescription)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            if (DeleteTemporaryDirectoryForTesting != null)
                DeleteTemporaryDirectoryForTesting(path);
            else
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
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
                case "schema":
                    schema = true;
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
                case "--dry-run":
                    pruneDryRun = true;
                    break;
                case "--apply":
                    pruneApply = true;
                    break;
                case "--list":
                    if (listCheckpoints)
                        break;
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
            Name = name,
            ParseError = parseError,
        };
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
        else
        {
            var prefix = errorCode is null ? "Error" : $"Error [{errorCode}]";
            Console.Error.WriteLine($"{prefix}: {message}");
            if (hint != null)
                Console.Error.WriteLine($"Hint: {hint}");
        }
        return exitCode;
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
    public string? Name { get; init; }
    public string? ParseError { get; init; }
}

internal sealed record DbCheckpointOperationResult(string Name, string CheckpointPath, List<string> Files, bool FilesTruncated);

internal sealed record DbCheckpointListReadResult(List<DbCheckpointListEntryJsonResult> Entries, bool Truncated);

internal sealed record DbIntegrityCheckReadResult(List<string> Rows, bool RowsTruncated, bool TextTruncated)
{
    public bool Truncated => RowsTruncated || TextTruncated;
}

internal sealed record DbSchemaReadResult(
    int UserVersion,
    List<DbSchemaEntryJsonResult> Entries,
    bool EntriesTruncated,
    bool SqlTruncated)
{
    public bool Truncated => EntriesTruncated || SqlTruncated;
}
