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
    private const int DiagnosticBusyRetrySliceSeconds = 1;
    private const int DiagnosticBusyRetrySliceMilliseconds = 50;

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
        using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling: false,
            out _,
            out _);
        ReportMaintenanceProgress("integrity_check", "open_connection", dbPath);
        connection.Open();
        return RunReadOnlyDiagnosticWithCancellation(
            connection,
            cancellationToken,
            "integrity check",
            commandTimeoutSeconds =>
            {
                using var cmd = SqliteConnectionPolicy.CreateCommand(
                    connection,
                    IntegrityCheckCommandTextForTesting ?? $"PRAGMA integrity_check({IntegrityCheckRowLimit + 1})");
                cmd.CommandTimeout = commandTimeoutSeconds;
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
            });
    }

    private static CancellationTokenRegistration RegisterSqliteInterruptForCancellation(
        SqliteConnection connection,
        CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state =>
                {
                    var registeredConnection = (SqliteConnection)state!;
                    SQLitePCL.raw.sqlite3_interrupt(registeredConnection.Handle);
                },
                connection)
            : default;

    private static DbSchemaReadResult ReadSchema(string dbPath, DbCommandOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling: false,
            out _,
            out _);
        connection.Open();
        return RunReadOnlyDiagnosticWithCancellation(
            connection,
            cancellationToken,
            "schema read",
            commandTimeoutSeconds => ReadSchemaCore(
                connection,
                options,
                dbPath,
                cancellationToken,
                commandTimeoutSeconds));
    }

    private static DbSchemaReadResult ReadSchemaCore(
        SqliteConnection connection,
        DbCommandOptions options,
        string dbPath,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds)
    {
        ReportMaintenanceProgress("schema", "read_version", dbPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandTimeout = commandTimeoutSeconds;
        versionCmd.CommandText = "PRAGMA user_version";
        var rawVersion = versionCmd.ExecuteScalar();
        var userVersion = rawVersion is long l ? (int)l : (rawVersion is int i ? i : 0);
        cancellationToken.ThrowIfCancellationRequested();
        ReportMaintenanceProgress("schema", "count_objects", dbPath);
        var objectTypeCounts = ReadSchemaObjectTypeCounts(connection, options, commandTimeoutSeconds);

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
        cmd.CommandTimeout = commandTimeoutSeconds;
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

    private static T RunReadOnlyDiagnosticWithCancellation<T>(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        string operation,
        Func<int, T> read)
    {
        using var cancellationRegistration = RegisterSqliteInterruptForCancellation(connection, cancellationToken);
        try
        {
            ApplyBusyTimeout(
                connection,
                cancellationToken,
                cancellationToken.CanBeCanceled ? DiagnosticBusyRetrySliceMilliseconds : null);
            var commandTimeoutSeconds = cancellationToken.CanBeCanceled
                ? DiagnosticBusyRetrySliceSeconds
                : SqliteConnectionPolicy.DefaultCommandTimeoutSeconds;
            var maximumBusyWaitMs = SqliteConnectionPolicy.DefaultCommandTimeoutSeconds * 1000L;
            var busyWait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = read(commandTimeoutSeconds);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (SqliteException exception) when (
                    exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_BUSY
                    && busyWait.ElapsedMilliseconds < maximumBusyWaitMs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch (SqliteException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.SqliteErrorCode is SQLitePCL.raw.SQLITE_INTERRUPT or SQLitePCL.raw.SQLITE_BUSY)
        {
            throw new OperationCanceledException(
                $"The SQLite {operation} was interrupted by cancellation.",
                exception,
                cancellationToken);
        }
    }

    private static Dictionary<string, int> ReadSchemaObjectTypeCounts(
        SqliteConnection connection,
        DbCommandOptions options,
        int commandTimeoutSeconds)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["table"] = 0,
            ["index"] = 0,
            ["trigger"] = 0,
            ["view"] = 0,
        };

        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandTimeout = commandTimeoutSeconds;
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
            if (orphanSymbolReferences > 0 || orphanSymbols > 0)
            {
                Execute(
                    connection,
                    transaction,
                    $"DELETE FROM codeindex_meta WHERE key = '{DbContext.ReferenceIdentityContractVersionMetaKey}'",
                    cancellationToken);
            }
            if (orphanSymbolReferences > 0)
            {
                var userVersion = Count(connection, transaction, "PRAGMA user_version", cancellationToken);
                var nextUserVersion = userVersion & ~DbContext.HotspotReferenceAggregateReadyFlag;
                if (nextUserVersion != userVersion)
                {
                    Execute(
                        connection,
                        transaction,
                        $"PRAGMA user_version = {nextUserVersion}",
                        cancellationToken);
                }
            }
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
        var connection = writable
            ? new SqliteConnection(DbPathResolver.BuildSqliteConnectionString(dbPath, SqliteOpenMode.ReadWrite))
            : DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                dbPath,
                pooling: false,
                out _,
                out _);
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

    private static void ApplyBusyTimeout(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        int? busyTimeoutMs = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = busyTimeoutMs.HasValue
            ? DbPragmaPolicy.BusyTimeoutPragmaSql(busyTimeoutMs.Value)
            : DbPragmaPolicy.ReadBusyTimeoutPragmaSql(DbContext.BusyTimeoutEnvironmentVariable);
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

}
