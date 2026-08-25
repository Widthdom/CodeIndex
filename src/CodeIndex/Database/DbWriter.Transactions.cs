using System.Diagnostics;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static readonly AsyncLocal<Action?> ScopedBeforePassiveWalCheckpointForTesting = new();
    internal static Action? BeforePassiveWalCheckpointForTesting
    {
        get => ScopedBeforePassiveWalCheckpointForTesting.Value;
        set => ScopedBeforePassiveWalCheckpointForTesting.Value = value;
    }
    private static readonly AsyncLocal<Action?> ScopedBeforeRollbackTerminalStateForTesting = new();
    internal static Action? BeforeRollbackTerminalStateForTesting
    {
        get => ScopedBeforeRollbackTerminalStateForTesting.Value;
        set => ScopedBeforeRollbackTerminalStateForTesting.Value = value;
    }

    internal long CaptureDurableCommitGeneration()
        => Interlocked.Read(ref _durableCommitGeneration);

    internal bool HasDurableCommitSince(long generation)
        => Interlocked.Read(ref _durableCommitGeneration) != generation;

    private void RecordDurableCommit()
        => Interlocked.Increment(ref _durableCommitGeneration);

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
                var savepointDepth = _transactionDepth;
                var name = $"sp_{savepointDepth}";
                var reuseControlStatements = savepointDepth == 1;
                if (reuseControlStatements)
                    ExecuteReusableControlStatement(FirstNestedSavepointSql);
                else
                    Execute($"SAVEPOINT {name}");
                IncrementTransactionDepth();
                return new TransactionScope(
                    name,
                    _conn,
                    this,
                    gateLease,
                    reuseControlStatements);
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
        private readonly bool _reuseControlStatements;
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

        internal TransactionScope(
            string savepointName,
            SqliteConnection conn,
            DbWriter writer,
            TransactionGateLease transactionGateLease = default,
            bool reuseControlStatements = false)
        {
            _savepointName = savepointName;
            _conn = conn;
            _writer = writer;
            _deferredHotspotReferenceFrame = writer.BeginDeferredHotspotReferenceTransactionFrame();
            _transactionGateLease = transactionGateLease;
            _reuseControlStatements = reuseControlStatements;
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

            var commitBoundaryCompleted = false;
            try
            {
                if (_transaction != null)
                {
                    _transaction.Commit();
                    commitBoundaryCompleted = true;
                    // SQLite has durably committed at this point. Publish that boundary before
                    // any post-commit bookkeeping or WAL checkpoint can fail so cleanup guards
                    // never mistake committed rows for a rolled-back attempt.
                    // この時点で SQLite commit は durable。後続 bookkeeping / WAL checkpoint
                    // が失敗しても cleanup guard が rollback 済みと誤認しないよう先に通知する。
                    _writer.RecordDurableCommit();
                    // Clear the writer's cached active-transaction reference immediately after
                    // the real transaction commit. Otherwise a subsequent RentCommand would
                    // bind a cached command to the now-detached transaction.
                    // real transaction の commit 直後に writer 側の active transaction 参照を解除する。
                    _writer._activeTransaction = null;
                    _writer.EndDeferredHotspotReferenceTransactionFrame(
                        _deferredHotspotReferenceFrame,
                        committed: true);
                    _writer._markWriteWork?.Invoke();
                    BeforePassiveWalCheckpointForTesting?.Invoke();
                    _writer.RunPassiveWalCheckpoint();
                }
                else
                {
                    ExecuteSavepointControlStatement(SavepointControlOperation.Release);
                    commitBoundaryCompleted = true;
                    _writer.EndDeferredHotspotReferenceTransactionFrame(
                        _deferredHotspotReferenceFrame,
                        committed: true);
                }
                SetState(StateCommitted);
            }
            catch
            {
                // Keep the scope finalizing until every post-commit action has either completed
                // or failed. A concurrent Dispose must not release the transaction gate while
                // bookkeeping or the passive WAL checkpoint is still using this writer. Once
                // the SQLite commit/RELEASE boundary succeeded, publish the terminal state so
                // Dispose never attempts a fictitious rollback of committed work.
                // post-commit action の完了または失敗確定までは finalizing を維持し、並行
                // Dispose が writer 使用中に transaction gate を解放しないようにする。
                // SQLite commit/RELEASE 済みなら terminal state を公開し、架空 rollback を防ぐ。
                SetState(commitBoundaryCompleted ? StateCommitted : StateActive);
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

            var rollbackBoundaryCompleted = false;
            try
            {
                if (_transaction != null)
                {
                    _transaction.Rollback();
                    rollbackBoundaryCompleted = true;
                    // Clear the detached transaction reference before publishing a terminal
                    // state. Concurrent Dispose can release the gate as soon as it observes
                    // StateRolledBack; no old finalizer may then overwrite a successor's live
                    // transaction reference.
                    // terminal state 公開前に detach 済み transaction 参照を消す。並行
                    // Dispose が gate 解放後、旧 finalizer が後続 transaction を null で
                    // 上書きしてはならない。
                    _writer._activeTransaction = null;
                }
                else
                {
                    ExecuteSavepointControlStatement(SavepointControlOperation.Rollback);
                    rollbackBoundaryCompleted = true;
                }
                _writer.EndDeferredHotspotReferenceTransactionFrame(
                    _deferredHotspotReferenceFrame,
                    committed: false);
                _writer.NotifyTypeScriptAugmentationTransactionRolledBack();
                BeforeRollbackTerminalStateForTesting?.Invoke();
                SetState(StateRolledBack);
            }
            catch
            {
                SetState(rollbackBoundaryCompleted ? StateRolledBack : StateActive);
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
                        // Dispose owns the transaction resources and gate. It must not time out
                        // and release either while Commit/Rollback is still finalizing on another
                        // thread; those finalizers always publish a terminal/active state.
                        // Dispose は transaction resource と gate の owner。別 thread の
                        // Commit/Rollback が finalizing 中に timeout して解放してはいけない。
                        WaitForStateTransition("dispose", state, waitUntilFinalized: true);
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
                            ExecuteSavepointControlStatement(SavepointControlOperation.Rollback);
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
                        _writer.NotifyTypeScriptAugmentationTransactionRolledBack();
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

        private void WaitForStateTransition(
            string operation,
            int observedState,
            bool waitUntilFinalized = false)
        {
            var timeout = GetTransactionStateContentionTimeout();
            var stopwatch = Stopwatch.StartNew();
            lock (_stateWaitLock)
            {
                while (IsFinalizingState(Volatile.Read(ref _state)))
                {
                    if (waitUntilFinalized)
                    {
                        Monitor.Wait(_stateWaitLock, TransactionStateContentionWaitInterval);
                        continue;
                    }

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

        private void ExecuteSavepointControlStatement(SavepointControlOperation operation)
        {
            if (_conn is null)
                throw new InvalidOperationException("Savepoint transaction scope is missing its SQLite connection.");

            if (_reuseControlStatements)
            {
                _writer.ExecuteReusableControlStatement(operation switch
                {
                    SavepointControlOperation.Release => ReleaseFirstNestedSavepointSql,
                    SavepointControlOperation.Rollback => RollbackFirstNestedSavepointSql,
                    _ => throw new ArgumentOutOfRangeException(nameof(operation)),
                });
            }
            else
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = operation switch
                {
                    SavepointControlOperation.Release => $"RELEASE SAVEPOINT {_savepointName}",
                    SavepointControlOperation.Rollback => $"ROLLBACK TO SAVEPOINT {_savepointName}",
                    _ => throw new ArgumentOutOfRangeException(nameof(operation)),
                };
                cmd.ExecuteNonQuery();
            }
            _writer._markWriteWork?.Invoke();
        }

        private enum SavepointControlOperation
        {
            Release,
            Rollback,
        }
    }
}
