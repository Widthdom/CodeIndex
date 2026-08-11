using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// One-shot proof that all fold-bearing tables were empty before an authoritative
    /// fresh-index run began. The proof is bound to one writer/connection and is invalidated
    /// when another connection commits before the readiness stamp is decided.
    /// authoritative fresh index 開始前に fold 対象3 table が空だったことを示す一回限りの証明。
    /// writer/connection に束縛し、stamp 決定前に別 connection が commit した場合は無効化する。
    /// </summary>
    internal sealed class AuthoritativeFreshFoldRowsClaim
    {
        private readonly DbWriter _owner;
        private readonly long _dataVersion;
        private int _consumedOrInvalidated;

        internal AuthoritativeFreshFoldRowsClaim(DbWriter owner, long dataVersion)
        {
            _owner = owner;
            _dataVersion = dataVersion;
        }

        internal void Invalidate()
            => Interlocked.Exchange(ref _consumedOrInvalidated, 1);

        internal bool TryConsume(DbWriter owner, long dataVersion)
        {
            if (Interlocked.CompareExchange(ref _consumedOrInvalidated, 1, 0) != 0)
                return false;

            return ReferenceEquals(_owner, owner) && _dataVersion == dataVersion;
        }
    }

    /// <summary>
    /// Claims the authoritative fresh-row shortcut only when files, symbols, and references
    /// are all empty in one BEGIN IMMEDIATE snapshot. PRAGMA data_version lets the eventual
    /// consumer distinguish this writer's own intervening commits from commits made by any
    /// other connection. Existing transactions fail closed because production callers claim
    /// before their first write scope.
    /// files/symbols/references が同じ BEGIN IMMEDIATE snapshot で全て空の場合だけ claim する。
    /// data_version により同じ writer 自身の commit は許可し、別 connection の commit は拒否する。
    /// </summary>
    internal AuthoritativeFreshFoldRowsClaim? TryClaimAuthoritativeFreshFoldRows(
        CancellationToken cancellationToken = default)
    {
        var gateLease = EnterTransactionGate(
            cancellationToken,
            "claim authoritative fresh fold rows");
        try
        {
            if (IsInTransaction())
                return null;

            var beganTransaction = false;
            try
            {
                Execute("BEGIN IMMEDIATE", cancellationToken);
                beganTransaction = true;

                cancellationToken.ThrowIfCancellationRequested();
                using var emptyCheck = _conn.CreateCommand();
                emptyCheck.CommandText =
                    """
                    SELECT CASE
                        WHEN EXISTS(SELECT 1 FROM files LIMIT 1)
                          OR EXISTS(SELECT 1 FROM symbols LIMIT 1)
                          OR EXISTS(SELECT 1 FROM symbol_references LIMIT 1)
                        THEN 0
                        ELSE 1
                    END
                    """;
                var allFoldRowTablesEmpty = Convert.ToInt64(
                    emptyCheck.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture) == 1;
                var dataVersion = ReadDataVersion();

                Execute("COMMIT");
                beganTransaction = false;
                return allFoldRowTablesEmpty
                    ? new AuthoritativeFreshFoldRowsClaim(this, dataVersion)
                    : null;
            }
            catch
            {
                if (beganTransaction)
                {
                    try { Execute("ROLLBACK"); }
                    catch (SqliteException) { /* best effort */ }
                }

                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    private bool TryConsumeAuthoritativeFreshFoldRowsClaim(
        AuthoritativeFreshFoldRowsClaim? claim)
        => claim?.TryConsume(this, ReadDataVersion()) == true;

    private long ReadDataVersion()
    {
        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
