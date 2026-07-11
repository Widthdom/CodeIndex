using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
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

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
