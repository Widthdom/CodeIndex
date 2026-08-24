using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string FirstNestedSavepointSql = "SAVEPOINT sp_1";
    private const string ReleaseFirstNestedSavepointSql = "RELEASE SAVEPOINT sp_1";
    private const string RollbackFirstNestedSavepointSql = "ROLLBACK TO SAVEPOINT sp_1";

    /// <summary>
    /// Lease a command for <paramref name="sql"/>. When the writer is wired to a
    /// <see cref="PreparedCommandCache"/> the cache returns a reused prepared command
    /// (with parameter placeholders already added by <paramref name="configureSchema"/>),
    /// otherwise a fresh per-call command is constructed for backwards compatibility
    /// with callers built against the legacy <see cref="SqliteConnection"/>-only ctor.
    /// Always pair with <see cref="ReleaseCommand"/> in a try/finally so the per-call
    /// path disposes its command.
    /// SQL に対する command を借りる。キャッシュ付きならパラメータプレースホルダ追加済みの
    /// prepared command を再利用し、未付与なら毎回 fresh な command を生成する。
    /// 必ず try/finally で <see cref="ReleaseCommand"/> と対にする。
    /// </summary>
    private SqliteCommand RentCommand(string sql, Action<SqliteCommand> configureSchema)
    {
        if (_commandCache != null)
        {
            var cached = _commandCache.GetOrAdd(sql, configureSchema);
            // Re-sync the transaction reference because the cached command may have been
            // bound to a previous (now-committed/rolled-back) transaction. SqliteCommand
            // throws TransactionRequired / TransactionConnectionMismatch on execute when
            // its Transaction does not equal the connection's current transaction.
            // キャッシュ済み command は前回別 transaction にバインドされている可能性があるため、
            // SqliteCommand の transaction 整合性検証を満たすよう毎回同期する。
            cached.Transaction = _activeTransaction;
            return cached;
        }

        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        configureSchema(cmd);
        return cmd;
    }

    private void ReleaseCommand(SqliteCommand cmd)
    {
        if (_commandCache == null)
            cmd.Dispose();
    }

    /// <summary>
    /// Execute one member of the writer's fixed, bounded control-statement set through the
    /// prepared-command cache. Callers must pass only compile-time-stable SQL; dynamic
    /// savepoint names and row-dependent statements stay on the per-call command path.
    /// writer 内の固定・有界な control statement だけを prepared-command cache 経由で
    /// 実行する。呼び出し側は compile-time で固定した SQL だけを渡し、動的な
    /// savepoint 名や row-dependent statement は従来の per-call command 経路に残す。
    /// </summary>
    private void ExecuteReusableControlStatement(string sql)
    {
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException("SQLite write maintenance was interrupted.", exception, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Execute(string sql, SqliteTransaction? transaction)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void RunPassiveWalCheckpoint()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
        cmd.ExecuteNonQuery();
    }
}
