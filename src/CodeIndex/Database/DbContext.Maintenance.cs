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
    private void NormalizeCodeIndexMetaKeys()
    {
        if (!TableExists("codeindex_meta"))
            return;

        using (var delete = SqliteConnectionPolicy.CreateCommand(_connection))
        {
            if (_activeMigrationTransaction != null)
                delete.Transaction = _activeMigrationTransaction;

            delete.CommandText = @"
                DELETE FROM codeindex_meta
                WHERE key IN ('hotspot_family_version', 'hotspot_family_marker_fingerprint')
                  AND value IS NULL";
            delete.ExecuteNonQuery();
        }

        using var stamp = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            stamp.Transaction = _activeMigrationTransaction;
        stamp.CommandText = @"
            INSERT INTO codeindex_meta (key, value) VALUES ('codeindex_meta_schema_version', @version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        SqliteCommandPolicy.Add(stamp, "@version", CodeIndexMetaSchemaVersion.ToString(CultureInfo.InvariantCulture));
        stamp.ExecuteNonQuery();
    }

    internal void MarkWriteWork(bool walCheckpointable = true)
    {
        if (!_isReadOnly && !_suppressWriteWorkTracking)
        {
            _hasWriteWork = true;
            if (walCheckpointable)
                _hasWalCheckpointableWriteWork = true;
        }
    }

    internal sealed record PlannerStatisticsMaintenanceFailure(string CommandText, SqliteException Exception);

    internal void SuppressPlannerStatisticsMaintenanceOnClose()
        => Volatile.Write(ref _suppressPlannerStatisticsMaintenanceOnClose, true);

    internal PlannerStatisticsMaintenanceFailure? RunPlannerStatisticsMaintenance(
        bool forceAnalyze,
        CancellationToken cancellationToken = default)
    {
        if (_isReadOnly)
            return null;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = forceAnalyze ? "ANALYZE" : "PRAGMA optimize";
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            _connection);
        try
        {
            PlannerStatisticsCommandCreatedForTesting?.Invoke(cmd);
            cmd.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            PlannerStatisticsCommandExecutedForTesting?.Invoke(_connection.DataSource, cmd.CommandText);
            if (!forceAnalyze)
                OptimizePragmaExecutedForTesting?.Invoke(_connection.DataSource);
            _hasWriteWork = false;
            return null;
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested && ex.SqliteErrorCode == 9)
        {
            throw new OperationCanceledException("SQLite planner maintenance was interrupted.", ex, cancellationToken);
        }
        catch (SqliteException ex)
        {
            // Planner statistics are an index-performance aid. If SQLite rejects ANALYZE /
            // optimize during cleanup (read-only handoff, transient filesystem state), keep
            // the completed index usable instead of converting success into failure.
            return new PlannerStatisticsMaintenanceFailure(cmd.CommandText, ex);
        }
    }

    private void RunOptimizeOnCloseIfNeeded()
    {
        if (!_hasWriteWork
            || _isReadOnly
            || _cancellation.IsCancellationRequested
            || Volatile.Read(ref _suppressPlannerStatisticsMaintenanceOnClose))
            return;

        try
        {
            RunPlannerStatisticsMaintenance(forceAnalyze: false, _cancellation);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Dispose-time maintenance is best effort and must not outlive or fail the
            // operation that owns this database context.
        }
    }

    public void Dispose()
    {
        DbSchemaCache? schemaCache;
        lock (_schemaCacheLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            schemaCache = _schemaCache;
            _schemaCache = null;
        }
        schemaCache?.Dispose();

        // Dispose cached prepared statements before closing the connection so each
        // SqliteCommand's finalizer does not race the connection teardown.
        // connection を閉じる前にキャッシュ済み command を dispose し、finalizer と
        // connection teardown の競合を防ぐ。
        _preparedCommands?.Dispose();
        _preparedCommands = null;
        var hadWriteWork = _hasWriteWork;
        var hadWalCheckpointableWriteWork = _hasWalCheckpointableWriteWork;
        RunOptimizeOnCloseIfNeeded();
        if (hadWalCheckpointableWriteWork)
            TryCheckpointWalTruncate();
        _connection.Dispose();
    }
}

/// <summary>
/// Captured information about a single failed step inside
/// <see cref="DbContext.TryMigrateForRead"/>. Surfaced via
/// <see cref="DbContext.LastMigrationFailure"/> so a later "no such column" error coming
/// out of a read path can be traced back to the specific step that did not run.
/// <see cref="DbContext.TryMigrateForRead"/> で失敗したステップの情報。
/// </summary>
public sealed record DbMigrationFailure(
    string Step,
    int SqliteErrorCode,
    string SqliteMessage,
    string SuggestedAction);

internal static class DbColumnEnsurer
{
    internal static void EnsureColumn(
        Func<bool> columnExists,
        Action? beginImmediate,
        Action? commit,
        Action? rollback,
        Action alterColumn)
    {
        if (columnExists())
            return;

        var hasTransactionHooks = beginImmediate != null && commit != null && rollback != null;
        var transactionStarted = false;
        try
        {
            if (hasTransactionHooks)
            {
                beginImmediate!();
                transactionStarted = true;
                if (columnExists())
                {
                    commit!();
                    transactionStarted = false;
                    return;
                }
            }

            alterColumn();
            if (transactionStarted)
            {
                commit!();
                transactionStarted = false;
            }
        }
        catch (SqliteException ex) when (IsDuplicateColumnRace(ex, columnExists))
        {
            // Another process or an earlier partial migration may have added the
            // column between PRAGMA inspection and ALTER. Re-check PRAGMA-derived
            // state and gate on SQLite's generic DDL error code so localized builds
            // or future wording changes still recover (#1532, #1690).
            // 列存在を PRAGMA 相当の状態で再確認し、SQLite の英語メッセージに依存せず
            // 「移行済み」を判定する (#1532)。
            if (transactionStarted)
            {
                try { rollback!(); } catch (SqliteException) { }
                transactionStarted = false;
            }
        }
        catch
        {
            if (transactionStarted)
            {
                try { rollback!(); } catch (SqliteException) { }
            }
            throw;
        }
    }

    internal static void EnsureColumn(Func<bool> columnExists, Action alterColumn)
        => EnsureColumn(columnExists, beginImmediate: null, commit: null, rollback: null, alterColumn);

    private static bool IsDuplicateColumnRace(SqliteException exception, Func<bool> columnExists)
    {
        if (!IsDuplicateColumnAddError(exception))
            return false;

        return columnExists();
    }

    private static bool IsDuplicateColumnAddError(SqliteException exception)
    {
        // SQLite reports duplicate-column ADD COLUMN as SQLITE_ERROR (1); callers
        // confirm the column exists before treating it as a recovered race.
        return exception.SqliteErrorCode == 1;
    }
}
