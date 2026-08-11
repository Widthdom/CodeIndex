using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string AuthoritativeFreshRowsEmptySql =
        """
        SELECT CASE
            WHEN EXISTS(SELECT 1 FROM files LIMIT 1)
              OR EXISTS(SELECT 1 FROM symbols LIMIT 1)
              OR EXISTS(SELECT 1 FROM symbol_references LIMIT 1)
            THEN 0
            ELSE 1
        END
        """;

    private static readonly AsyncLocal<Action?>
        ScopedFreshFoldBeginImmediateCompletedForTesting = new();

    internal static Action? FreshFoldBeginImmediateCompletedForTesting
    {
        get => ScopedFreshFoldBeginImmediateCompletedForTesting.Value;
        set => ScopedFreshFoldBeginImmediateCompletedForTesting.Value = value;
    }

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
                BeginImmediateForAuthoritativeFreshClaim(cancellationToken);
                beganTransaction = true;

                cancellationToken.ThrowIfCancellationRequested();
                using var emptyCheck = _conn.CreateCommand();
                emptyCheck.CommandText = AuthoritativeFreshRowsEmptySql;
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

    /// <summary>
    /// Execute BEGIN with cancellation-aware SQLite interruption but without a post-success
    /// token check. The caller records cleanup ownership immediately after this method returns,
    /// then performs the post-BEGIN cancellation check inside its guarded try/catch.
    /// cancellation-aware な SQLite interrupt 付きで BEGIN を実行するが、成功後の token check は
    /// caller が cleanup ownership を記録した直後、guarded try/catch 内で行う。
    /// </summary>
    private void BeginImmediateForAuthoritativeFreshClaim(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            command.ExecuteNonQuery();
            FreshFoldBeginImmediateCompletedForTesting?.Invoke();
        }
        catch (SqliteException exception) when (IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(
                "SQLite authoritative-fresh claim was interrupted.",
                exception,
                cancellationToken);
        }
    }

    /// <summary>
    /// Revalidate the fresh-reference shortcut after the authoritative CLI write transaction
    /// has begun. The transaction snapshot/lock closes the pre-write gap; a false result makes
    /// reference insertion and final resolution use their ordinary full-refresh defaults.
    /// authoritative CLI write transaction 開始後に fresh-reference shortcut を再検証する。
    /// transaction snapshot / lock で write 前の gap を閉じ、false なら通常の full-refresh
    /// default で reference insert と最終 resolution を行う。
    /// </summary>
    internal bool CanUseFreshReferenceResolutionDefaultsInCurrentTransaction(
        CancellationToken cancellationToken = default)
    {
        if (!IsInTransaction())
        {
            throw new InvalidOperationException(
                "Fresh reference resolution defaults must be revalidated inside the active write transaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = AuthoritativeFreshRowsEmptySql;
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            var tablesAreEmpty = Convert.ToInt64(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            cancellationToken.ThrowIfCancellationRequested();
            return tablesAreEmpty;
        }
        catch (SqliteException exception) when (IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(
                "SQLite fresh reference resolution revalidation was interrupted.",
                exception,
                cancellationToken);
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
