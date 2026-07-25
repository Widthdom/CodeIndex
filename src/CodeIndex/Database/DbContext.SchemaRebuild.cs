using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

public partial class DbContext : IDisposable
{
    private void RunWithForeignKeysDisabledForMigration(string operation, Action action)
    {
        if (IsSqliteTransactionActive())
        {
            AssertForeignKeyMode(operation, expected: 0);
            ForeignKeysDisabledForTesting?.Invoke(operation);
            action();
            return;
        }

        var foreignKeys = ReadPragmaLong("foreign_keys");
        ExceptionDispatchInfo? operationFailure = null;
        try
        {
            SetForeignKeyModeAndVerify(operation, expected: 0);
            ForeignKeysDisabledForTesting?.Invoke(operation);
            action();
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            ForeignKeysRestoringForTesting?.Invoke(operation, foreignKeys);
            SetForeignKeyModeAndVerify(operation, foreignKeys);
        }
        catch (Exception ex)
        {
            throw new CodeIndexException(
                code: CommandErrorCodes.DbError,
                category: CodeIndexExceptionCategory.Database,
                message: $"Failed to restore PRAGMA foreign_keys after {operation}.",
                path: _connection.DataSource,
                hint: "Close other database connections, restore write access if needed, and rerun the command before trusting further migration work.",
                innerException: ex);
        }

        operationFailure?.Throw();
    }

    private void SetForeignKeyModeAndVerify(string operation, long expected)
    {
        Execute($"PRAGMA foreign_keys={expected}");
        AssertForeignKeyMode(operation, expected);
    }

    private void AssertForeignKeyMode(string operation, long expected)
    {
        var effective = ReadPragmaLong("foreign_keys");
        if (effective == expected)
            return;

        throw new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: $"PRAGMA foreign_keys remained {effective} while schema migration operation '{operation}' required {expected}.",
            path: _connection.DataSource,
            hint: "Finish or roll back the external transaction, then rerun the migration on a writable database connection.");
    }

    private bool IsSqliteTransactionActive()
        => SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0;

    private void InvokeForeignKeyValidationBeforeCheckForTesting(string phase)
    {
        var boundedPhase = DiagnosticRedactor.BoundDiagnosticText(phase, MigrationDiagnosticTextLimit);
        ForeignKeyValidationBeforeCheckForTesting?.Invoke(_connection, boundedPhase);
    }

    private void ValidateForeignKeysAfterMigration(string phase)
    {
        var boundedPhase = DiagnosticRedactor.BoundDiagnosticText(phase, MigrationDiagnosticTextLimit);
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "PRAGMA foreign_key_check";

        var violations = new List<string>();
        var violationCount = 0;
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            violationCount++;
            if (violations.Count < MigrationForeignKeyViolationSampleLimit)
                violations.Add(FormatForeignKeyViolation(reader));
        }

        if (violationCount == 0)
            return;

        var sample = string.Join("; ", violations);
        var truncated = violationCount > violations.Count
            ? $" (showing {violations.Count.ToString(CultureInfo.InvariantCulture)})"
            : string.Empty;
        throw new CodeIndexException(
            code: CommandErrorCodes.DbIntegrityFailed,
            category: CodeIndexExceptionCategory.Database,
            message: $"Foreign key validation failed after schema migration phase '{boundedPhase}' with {violationCount.ToString(CultureInfo.InvariantCulture)} violation(s){truncated}: {sample}.",
            hint: "Run `cdidx db --integrity-check --db <db>` and rebuild the index on writable storage if violations persist.");
    }

    private static string FormatForeignKeyViolation(SqliteDataReader reader)
    {
        var table = FormatForeignKeyCheckValue(reader.IsDBNull(0) ? "<unknown>" : reader.GetString(0));
        var rowId = reader.IsDBNull(1)
            ? "<null>"
            : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var parent = FormatForeignKeyCheckValue(reader.IsDBNull(2) ? "<unknown>" : reader.GetString(2));
        var fkId = reader.IsDBNull(3)
            ? "<null>"
            : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return $"table={table}, rowid={rowId}, parent={parent}, fkid={fkId}";
    }

    private static string FormatForeignKeyCheckValue(string value)
        => DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(value, redactPaths: true),
            MigrationDiagnosticTextLimit);

    private bool TableCheckContainsAll(string tableName, IEnumerable<string> allowedValues)
    {
        var createSql = GetTableCreateSql(tableName);
        if (createSql == null)
            return true;

        if (!createSql.Contains("CHECK", StringComparison.OrdinalIgnoreCase))
            return true;

        return allowedValues.All(value => createSql.Contains($"'{value.Replace("'", "''")}'", StringComparison.Ordinal));
    }

    private string? GetTableCreateSql(string tableName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table";
        SqliteCommandPolicy.AddText(cmd, "@table", tableName);
        return cmd.ExecuteScalar() as string;
    }

    private string BuildRebuildSelectProjection(string sourceTableName, string columns)
    {
        var existingColumns = LoadColumnNames(sourceTableName);
        var projectedColumns = columns
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => existingColumns.Contains(column) ? column : $"NULL AS {column}");
        return string.Join(", ", projectedColumns);
    }

    private HashSet<string> LoadColumnNames(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private void RebuildTableWithCurrentKindChecks(string tableName, string oldTableName, string createSql, string columns)
    {
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedOldTableName = SqliteIdentifier.Quote(oldTableName);
        Execute($"DROP TABLE IF EXISTS {quotedOldTableName}");
        Execute($"ALTER TABLE {quotedTableName} RENAME TO {quotedOldTableName}");
        Execute(createSql);
        var sourceColumns = BuildRebuildSelectProjection(oldTableName, columns);
        Execute($"INSERT INTO {quotedTableName} ({columns}) SELECT {sourceColumns} FROM {quotedOldTableName}");
        Execute($"DROP TABLE {quotedOldTableName}");
    }

    private void RebuildTableWithRequiredFileId(string tableName, string createSql, string columns)
    {
        if (ColumnIsNotNull(tableName, "file_id"))
            return;

        var oldTableName = $"_{tableName}_nullable_file_id";
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedOldTableName = SqliteIdentifier.Quote(oldTableName);
        Execute($"DROP TABLE IF EXISTS {quotedOldTableName}");
        Execute(DropAllFtsChunksSyncTriggersSql);
        if (string.Equals(tableName, "chunks", StringComparison.Ordinal))
        {
            Execute("DROP TABLE IF EXISTS fts_chunks");
            Execute($"DROP TABLE IF EXISTS {FtsChunksTrigramTableName}");
            _rebuildFtsAfterSchemaMigration = true;
            _rebuildTrigramFtsAfterSchemaMigration = true;
        }
        Execute($"DELETE FROM {quotedTableName} WHERE file_id IS NULL");
        Execute($"ALTER TABLE {quotedTableName} RENAME TO {quotedOldTableName}");
        Execute(createSql);
        var sourceColumns = BuildRebuildSelectProjection(oldTableName, columns);
        Execute($"INSERT INTO {quotedTableName} ({columns}) SELECT {sourceColumns} FROM {quotedOldTableName}");
        if (!string.Equals(tableName, "reference_lines", StringComparison.Ordinal))
            Execute($"DROP TABLE {quotedOldTableName}");
    }

    private bool ColumnIsNotNull(string tableName, string columnName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) != 0;
        }
        return false;
    }

    /// <summary>
    /// Delete all data for a full rebuild.
    /// 全データを削除して完全再構築する。
    /// </summary>
    public void DropAll()
    {
        // A rebuild that produces zero files must still invalidate an outstanding resource cursor.
        // 0 件になる rebuild でも既存の resource cursor を必ず無効化する。
        // A fresh database has no cursor to invalidate and creates this table after DropAll.
        // fresh database には無効化対象がなく、この table は DropAll 後に作成される。
        if (TableExists("codeindex_meta"))
            Execute(IncrementResourceListGenerationSql);
        Execute(DropAllFtsChunksSyncTriggersSql);
        Execute($"DROP TABLE IF EXISTS {FtsChunksTrigramTableName}");
        Execute("DROP TABLE IF EXISTS fts_chunks");
        Execute("DROP TABLE IF EXISTS file_issues");
        Execute("DROP TABLE IF EXISTS hotspot_reference_counts");
        Execute("DROP TABLE IF EXISTS symbol_references");
        Execute("DROP TABLE IF EXISTS reference_lines");
        Execute("DROP TABLE IF EXISTS symbols");
        Execute("DROP TABLE IF EXISTS chunks");
        Execute("DROP TABLE IF EXISTS files");
        _schemaCache?.Refresh();
    }

    private void Execute(string sql)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = sql;
        using var cancellationRegistration = _cancellation.CanBeCanceled
            ? _cancellation.UnsafeRegister(
                static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
                _connection)
            : default;
        _cancellation.ThrowIfCancellationRequested();
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (
            _cancellation.IsCancellationRequested && exception.SqliteErrorCode == 9)
        {
            throw new OperationCanceledException(
                "SQLite schema or aggregate maintenance was interrupted.",
                exception,
                _cancellation);
        }
        _cancellation.ThrowIfCancellationRequested();
        MarkWriteWork(walCheckpointable: false);
    }

    private void EnsureForeignKeysEnabled()
    {
        Execute("PRAGMA foreign_keys=ON");
        var fkResult = ExecuteScalar("PRAGMA foreign_keys");
        if (fkResult != "1")
            CommandErrorWriter.WriteStderr("Warning: foreign_keys pragma not enabled");
    }

    /// <summary>
    /// Latest opportunistic-migration failure captured by <see cref="TryMigrateForRead"/>.
    /// Null when the most recent migration attempt completed every step (or was skipped on a
    /// read-only connection). Callers can surface this to explain a later "no such column"
    /// error coming out of a read path.
    /// 直前の <see cref="TryMigrateForRead"/> 実行で発生した部分マイグレーション失敗の情報。
    /// 全ステップ完了時、または読み取り専用接続でスキップされた場合は null。
    /// </summary>
}
