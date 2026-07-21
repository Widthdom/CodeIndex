using System.Diagnostics;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Begin a transaction or savepoint for grouping multiple operations atomically.
    /// SQLite does not support nested BEGIN TRANSACTION, so nested calls use SAVEPOINT.
    /// 複数操作をアトミックにまとめるためのトランザクションまたはセーブポイントを開始する。
    /// SQLiteはネストされたBEGIN TRANSACTIONをサポートしないため、ネスト時はSAVEPOINTを使用する。
    /// </summary>
    public TransactionScope BeginTransaction()
        => BeginTransaction(CancellationToken.None, operation: null);

    public TransactionScope BeginTransaction(CancellationToken cancellationToken)
        => BeginTransaction(cancellationToken, operation: null);

    internal TransactionScope BeginTransaction(CancellationToken cancellationToken, string? operation)
    {
        var gateLease = EnterTransactionGate(cancellationToken, operation);
        try
        {
            if (_transactionDepth == 0)
            {
                var txn = _conn.BeginTransaction();
                SetTransactionDepth(1);
                _activeTransaction = txn;
                return new TransactionScope(txn, this, gateLease);
            }
            else
            {
                // Nested: use SAVEPOINT instead of BEGIN TRANSACTION
                // ネスト: BEGIN TRANSACTIONの代わりにSAVEPOINTを使用
                var name = $"sp_{_transactionDepth}";
                Execute($"SAVEPOINT {name}");
                IncrementTransactionDepth();
                return new TransactionScope(name, _conn, this, gateLease);
            }
        }
        catch
        {
            gateLease.Dispose();
            throw;
        }
    }

    private TransactionGateLease EnterTransactionGate(CancellationToken cancellationToken = default, string? operation = null)
    {
        var timeout = GetTransactionStateContentionTimeout();
        var stopwatch = Stopwatch.StartNew();
        var operationLabel = string.IsNullOrWhiteSpace(operation) ? "transaction" : operation.Trim();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_transactionStateLock)
            {
                if (_transactionDepth > 0 &&
                    _transactionOwnerThreadId == Environment.CurrentManagedThreadId &&
                    _transactionOwnerToken != Guid.Empty &&
                    _currentTransactionGateToken.Value == _transactionOwnerToken)
                    return TransactionGateLease.None;
            }

            var waitMilliseconds = GetTransactionStateContentionWaitMilliseconds(timeout, stopwatch);
            if (waitMilliseconds <= 0)
                throw new InvalidOperationException(BuildTransactionGateTimeoutMessage(operationLabel, stopwatch));

            if (!_transactionGate.Wait(waitMilliseconds, cancellationToken))
                continue;

            var ownsSemaphore = true;
            lock (_transactionStateLock)
            {
                if (_transactionDepth == 0)
                {
                    var previousToken = _currentTransactionGateToken.Value;
                    var token = Guid.NewGuid();
                    _transactionOwnerThreadId = Environment.CurrentManagedThreadId;
                    _transactionOwnerToken = token;
                    _transactionOwnerOperation = operationLabel;
                    _transactionOwnerAcquiredAtUtc = DateTimeOffset.UtcNow;
                    _currentTransactionGateToken.Value = token;
                    ownsSemaphore = false;
                    return new TransactionGateLease(this, token, previousToken);
                }
            }

            if (ownsSemaphore)
                _transactionGate.Release();
            WaitForTransactionDepthToClear(cancellationToken, operationLabel);
        }
    }

    private void SetTransactionDepth(int depth)
    {
        lock (_transactionStateLock)
        {
            _transactionDepth = depth;
            Monitor.PulseAll(_transactionStateLock);
        }
    }

    private void IncrementTransactionDepth()
    {
        lock (_transactionStateLock)
        {
            _transactionDepth++;
            Monitor.PulseAll(_transactionStateLock);
        }
    }

    private int DecrementTransactionDepth()
    {
        lock (_transactionStateLock)
        {
            if (_transactionDepth > 0)
                _transactionDepth--;
            Monitor.PulseAll(_transactionStateLock);
            return _transactionDepth;
        }
    }

    private void WaitForTransactionDepthToClear(CancellationToken cancellationToken, string operation)
    {
        var timeout = GetTransactionStateContentionTimeout();
        var stopwatch = Stopwatch.StartNew();
        lock (_transactionStateLock)
        {
            while (_transactionDepth > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waitMilliseconds = GetTransactionStateContentionWaitMilliseconds(timeout, stopwatch);
                if (waitMilliseconds <= 0)
                {
                    throw new InvalidOperationException(BuildTransactionGateTimeoutMessage(operation, stopwatch));
                }

                Monitor.Wait(_transactionStateLock, waitMilliseconds);
            }
        }
    }

    private static TimeSpan GetTransactionStateContentionTimeout()
        => TransactionStateContentionTimeoutForTesting ?? DefaultTransactionStateContentionTimeout;

    private static int GetTransactionStateContentionWaitMilliseconds(TimeSpan timeout, Stopwatch stopwatch)
    {
        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return 0;

        var wait = remaining < TransactionStateContentionWaitInterval
            ? remaining
            : TransactionStateContentionWaitInterval;
        return Math.Max(1, (int)Math.Ceiling(wait.TotalMilliseconds));
    }

    private string BuildTransactionGateTimeoutMessage(string operation, Stopwatch stopwatch)
    {
        lock (_transactionStateLock)
        {
            var heldMs = _transactionOwnerAcquiredAtUtc == default
                ? 0
                : Math.Max(0, (long)(DateTimeOffset.UtcNow - _transactionOwnerAcquiredAtUtc).TotalMilliseconds);
            return "Timed out waiting for DbWriter transaction gate"
                + $"; waiter_operation={FormatTransactionGateDiagnosticValue(operation)}"
                + $"; waiter_thread_id={Environment.CurrentManagedThreadId}"
                + $"; owner_thread_id={_transactionOwnerThreadId}"
                + $"; owner_operation={FormatTransactionGateDiagnosticValue(_transactionOwnerOperation)}"
                + $"; transaction_depth={_transactionDepth}"
                + $"; held_ms={heldMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + $"; waited_ms={((long)stopwatch.Elapsed.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
        }
    }

    private static string FormatTransactionGateDiagnosticValue(string? value)
        => ConsoleUi.FormatBoundedValue(string.IsNullOrWhiteSpace(value) ? "unknown" : value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

    private void ExitTransactionGate(Guid token, Guid? previousToken)
    {
        lock (_transactionStateLock)
        {
            if (_transactionOwnerToken == token)
            {
                _transactionOwnerThreadId = 0;
                _transactionOwnerToken = Guid.Empty;
                _transactionOwnerOperation = null;
                _transactionOwnerAcquiredAtUtc = default;
            }
        }
        if (_currentTransactionGateToken.Value == token)
            _currentTransactionGateToken.Value = previousToken;
        _transactionGate.Release();
    }

    internal readonly struct TransactionGateLease
    {
        public static readonly TransactionGateLease None = new(null, Guid.Empty, null);

        private readonly DbWriter? _writer;
        private readonly Guid _token;
        private readonly Guid? _previousToken;

        public TransactionGateLease(DbWriter? writer, Guid token, Guid? previousToken)
        {
            _writer = writer;
            _token = token;
            _previousToken = previousToken;
        }

        public void Dispose()
        {
            _writer?.ExitTransactionGate(_token, _previousToken);
        }
    }

    /// <summary>
    /// RAII wrapper for transactions and savepoints.
    /// Ensures _transactionDepth is decremented and uncommitted changes are rolled back on Dispose.
    /// トランザクションとセーブポイントのRAIIラッパー。
    /// Dispose時に_transactionDepthを確実に減算し、未コミットの変更をロールバックする。
    /// </summary>
    public sealed class TransactionScope : IDisposable
    {
        private readonly SqliteTransaction? _transaction;
        private readonly string? _savepointName;
        private readonly SqliteConnection? _conn;
        private readonly DbWriter _writer;
        private readonly DeferredHotspotReferenceTransactionFrame? _deferredHotspotReferenceFrame;
        private readonly TransactionGateLease _transactionGateLease;
        private readonly object _stateWaitLock = new();
        private const int StateActive = 0;
        private const int StateCommitting = 1;
        private const int StateCommitted = 2;
        private const int StateRollingBack = 3;
        private const int StateRolledBack = 4;
        private int _state;
        private int _disposeStarted;

        // Real transaction / 実トランザクション
        internal TransactionScope(SqliteTransaction transaction, DbWriter writer)
            : this(transaction, writer, TransactionGateLease.None)
        {
        }

        internal TransactionScope(SqliteTransaction transaction, DbWriter writer, TransactionGateLease transactionGateLease = default)
        {
            _transaction = transaction;
            _writer = writer;
            _deferredHotspotReferenceFrame = writer.BeginDeferredHotspotReferenceTransactionFrame();
            _transactionGateLease = transactionGateLease;
        }

        // Savepoint / セーブポイント
        internal TransactionScope(string savepointName, SqliteConnection conn, DbWriter writer)
            : this(savepointName, conn, writer, TransactionGateLease.None)
        {
        }

        internal TransactionScope(string savepointName, SqliteConnection conn, DbWriter writer, TransactionGateLease transactionGateLease = default)
        {
            _savepointName = savepointName;
            _conn = conn;
            _writer = writer;
            _deferredHotspotReferenceFrame = writer.BeginDeferredHotspotReferenceTransactionFrame();
            _transactionGateLease = transactionGateLease;
        }

        public void Commit()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state == StateCommitted)
                    return;
                if (state == StateRolledBack)
                    throw new InvalidOperationException("Cannot commit a transaction scope that has already been rolled back.");
                if (state == StateRollingBack)
                {
                    WaitForStateTransition("commit", state);
                    continue;
                }

                if (state != StateActive)
                    throw new InvalidOperationException("Cannot commit a transaction scope while it is being finalized.");

                if (Interlocked.CompareExchange(ref _state, StateCommitting, StateActive) == StateActive)
                    break;
            }

            try
            {
                if (_transaction != null)
                {
                    _transaction.Commit();
                    _writer._markWriteWork?.Invoke();
                    _writer.RunPassiveWalCheckpoint();
                }
                else
                {
                    ExecuteSql($"RELEASE SAVEPOINT {_savepointName}");
                }
                _writer.EndDeferredHotspotReferenceTransactionFrame(
                    _deferredHotspotReferenceFrame,
                    committed: true);
                // Mark committed after success so Dispose() will rollback if Commit/Release throws.
                // コミット/リリース成功後に committed に遷移し、失敗時は Dispose() でロールバックされるようにする。
                SetState(StateCommitted);
                // Clear the writer's cached active-transaction reference immediately after a
                // real-transaction commit. Otherwise a subsequent RentCommand between Commit
                // and Dispose would bind a cached prepared command to the now-committed (and
                // detached from the connection) SqliteTransaction and throw at execute time.
                // Savepoint Commit (RELEASE) does not affect the outer SqliteTransaction.
                // real transaction の commit 直後に writer 側の active transaction 参照を解除する。
                // commit と Dispose の間に RentCommand が走った場合、commit 済み(connection から
                // 外れている) transaction を cached command に再バインドして execute 時に例外を
                // 投げるため。savepoint の RELEASE は外側 SqliteTransaction に影響しない。
                if (_transaction != null)
                    _writer._activeTransaction = null;
            }
            catch
            {
                SetState(StateActive);
                throw;
            }
        }

        public void Rollback()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state == StateRolledBack)
                    return;
                if (state == StateCommitted)
                    throw new InvalidOperationException("Cannot roll back a transaction scope that has already been committed.");
                if (state == StateCommitting || state == StateRollingBack)
                {
                    WaitForStateTransition("rollback", state);
                    continue;
                }

                if (state != StateActive)
                    throw new InvalidOperationException("Cannot roll back a transaction scope while it is being finalized.");

                if (Interlocked.CompareExchange(ref _state, StateRollingBack, StateActive) == StateActive)
                    break;
            }

            try
            {
                if (_transaction != null)
                    _transaction.Rollback();
                else
                    ExecuteSql($"ROLLBACK TO SAVEPOINT {_savepointName}");
                _writer.EndDeferredHotspotReferenceTransactionFrame(
                    _deferredHotspotReferenceFrame,
                    committed: false);
                SetState(StateRolledBack);
                // Same rationale as Commit: drop the stale reference so cached commands
                // re-bind correctly after the transaction boundary.
                // Commit と同じ理由で stale 参照を解除する。
                if (_transaction != null)
                    _writer._activeTransaction = null;
            }
            catch
            {
                SetState(StateActive);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            try
            {
                // Rollback uncommitted changes / 未コミットの変更をロールバック
                while (true)
                {
                    var state = Volatile.Read(ref _state);
                    if (state == StateCommitted || state == StateRolledBack)
                        break;
                    if (state == StateCommitting || state == StateRollingBack)
                    {
                        WaitForStateTransition("dispose", state);
                        continue;
                    }

                    if (state != StateActive)
                        break;

                    if (Interlocked.CompareExchange(ref _state, StateRollingBack, StateActive) != StateActive)
                        continue;

                    try
                    {
                        if (_transaction != null)
                            _transaction.Rollback();
                        else
                            ExecuteSql($"ROLLBACK TO SAVEPOINT {_savepointName}");
                        SetState(StateRolledBack);
                    }
                    catch (Exception ex)
                    {
                        // Best effort during dispose / Dispose中はベストエフォート
                        GlobalToolLog.Error($"transaction_scope_dispose_rollback_failed {GlobalToolLog.FormatExceptionChain(ex)}");
                        SetState(StateRolledBack);
                    }
                    finally
                    {
                        _writer.EndDeferredHotspotReferenceTransactionFrame(
                            _deferredHotspotReferenceFrame,
                            committed: false);
                    }
                    break;
                }
            }
            finally
            {
                try
                {
                    _transaction?.Dispose();
                    var transactionDepth = _writer.DecrementTransactionDepth();
                    // Safety net: even if Commit/Rollback was bypassed (e.g. uncommitted scope
                    // disposed after an exception), make sure the outer-transaction reference is
                    // cleared before the next RentCommand sees it.
                    // 安全弁: Commit/Rollback を経由せず Dispose された場合でも active reference を解除。
                    if (transactionDepth == 0)
                        _writer._activeTransaction = null;
                }
                finally
                {
                    _transactionGateLease.Dispose();
                }
            }
        }

        private void SetState(int state)
        {
            lock (_stateWaitLock)
            {
                Volatile.Write(ref _state, state);
                Monitor.PulseAll(_stateWaitLock);
            }
        }

        private void WaitForStateTransition(string operation, int observedState)
        {
            var timeout = GetTransactionStateContentionTimeout();
            var stopwatch = Stopwatch.StartNew();
            lock (_stateWaitLock)
            {
                while (IsFinalizingState(Volatile.Read(ref _state)))
                {
                    var waitMilliseconds = GetTransactionStateContentionWaitMilliseconds(timeout, stopwatch);
                    if (waitMilliseconds <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Timed out waiting for transaction scope state transition during {operation}; state={FormatState(observedState)}.");
                    }

                    Monitor.Wait(_stateWaitLock, waitMilliseconds);
                }
            }
        }

        private static bool IsFinalizingState(int state)
            => state == StateCommitting || state == StateRollingBack;

        private static string FormatState(int state)
            => state switch
            {
                StateActive => "active",
                StateCommitting => "committing",
                StateCommitted => "committed",
                StateRollingBack => "rolling_back",
                StateRolledBack => "rolled_back",
                _ => "unknown",
            };

        private void ExecuteSql(string sql)
        {
            if (_conn is null)
                throw new InvalidOperationException("Savepoint transaction scope is missing its SQLite connection.");

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            _writer._markWriteWork?.Invoke();
        }
    }
}
