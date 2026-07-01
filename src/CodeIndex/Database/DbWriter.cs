using Microsoft.Data.Sqlite;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

/// <summary>
/// Handles INSERT/UPSERT operations to the database with batch commits.
/// Transaction scopes on a writer are serialized; nested scopes are supported on the
/// owning thread and other threads wait until the active scope is disposed.
/// バッチコミットによるINSERT/UPSERT処理を担当する。
/// writer 上の transaction scope は直列化され、同一所有スレッドのネストのみ許可する。
/// </summary>
public class DbWriter
{
    private const string FoldBackfillPhaseMetaKey = "fold_backfill_phase";
    private const string FoldBackfillLastSymbolIdMetaKey = "fold_backfill_last_symbol_id";
    private const string FoldBackfillLastReferenceIdMetaKey = "fold_backfill_last_reference_id";

    public const string FtsIncrementalWritesSinceOptimizeMetaKey = "fts_incremental_writes_since_optimize";
    public const string FtsLastOptimizedAtMetaKey = "fts_last_optimized_at";
    public const int DefaultFtsOptimizeIncrementalWriteThreshold = 25;

    private readonly SqliteConnection _conn;
    private readonly PreparedCommandCache? _commandCache;
    private readonly Action? _markWriteWork;
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillRowUpdatedForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedBatchRowSkipWarningForTesting = new();
    private static readonly AsyncLocal<Action<DbWriterBatchProgress>?> ScopedBatchProgressCheckpointForTesting = new();
    internal static Action? FoldBackfillRowUpdatedForTesting
    {
        get => ScopedFoldBackfillRowUpdatedForTesting.Value;
        set => ScopedFoldBackfillRowUpdatedForTesting.Value = value;
    }

    internal static Action<string>? BatchRowSkipWarningForTesting
    {
        get => ScopedBatchRowSkipWarningForTesting.Value;
        set => ScopedBatchRowSkipWarningForTesting.Value = value;
    }

    internal static Action<DbWriterBatchProgress>? BatchProgressCheckpointForTesting
    {
        get => ScopedBatchProgressCheckpointForTesting.Value;
        set => ScopedBatchProgressCheckpointForTesting.Value = value;
    }

    // Transaction ownership (#4154): the semaphore is held for the outermost writer
    // transaction lifetime. Same-stack nested calls from the owning thread and
    // AsyncLocal token skip the semaphore and become SAVEPOINTs; other flows wait even
    // when ExecutionContext copied the token into Task.Run.
    private readonly object _transactionStateLock = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly AsyncLocal<Guid?> _currentTransactionGateToken = new();
    private static readonly TimeSpan DefaultTransactionStateContentionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransactionStateContentionWaitInterval = TimeSpan.FromMilliseconds(50);
    private const int BatchSize = 500;
    private const int DeleteFilesBatchSize = 500;
    private const int SqliteConstraintErrorCode = 19;
    private const int TypeScriptModuleSyntaxFallbackMaxBytes = (int)FileIndexer.DefaultMaxFileSizeBytes;
    private const int TypeScriptModuleSyntaxFallbackMaxLines = 16384;
    private static readonly BoundedRegex CSharpExternAliasSignatureRegex = new(
        @"^\s*extern\s+alias\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpGlobalUsingSignatureRegex = new(
        @"^\s*global\s+using\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingStaticSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+static\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingAliasSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+(?<alias>@?\w+)\s*=\s*(?<target>[^;]+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private int _rowSkipSavepointCounter;
    private long _batchRowsSkipped;
    private int _transactionDepth;
    private int _transactionOwnerThreadId;
    private Guid _transactionOwnerToken;
    private string? _transactionOwnerOperation;
    private DateTimeOffset _transactionOwnerAcquiredAtUtc;
    private bool? _hasIssueMetadataColumns;
    internal static TimeSpan? TransactionStateContentionTimeoutForTesting { get; set; }
    // Outermost SqliteTransaction currently held open by this writer (null when no
    // transaction is active OR after the outermost transaction has been committed /
    // rolled back). Tracked so cached prepared commands can be re-pointed at the live
    // transaction on every lease — SqliteCommand validates Transaction against the
    // connection's current transaction at execute time and would throw
    // TransactionRequired / TransactionConnectionMismatch after a transaction boundary
    // if we kept a stale reference. Cleared on Commit / Rollback (and at depth-0
    // Dispose as a safety net) so a subsequent lease outside any transaction sets the
    // cached command's Transaction back to null. Issue #1566.
    // 現在 writer が保持している最外 SqliteTransaction。キャッシュ済み prepared command の
    // Transaction を毎回その時点の active transaction に同期させるため保持する。
    // Commit / Rollback / Dispose で必ず null に戻し、トランザクション外で借用したときに
    // cached command の Transaction を null に再同期できるようにする。Issue #1566.
    private SqliteTransaction? _activeTransaction;
    internal SqliteConnection Connection => _conn;
    public long BatchRowsSkipped => Volatile.Read(ref _batchRowsSkipped);

    internal sealed record DbWriterBatchProgress(string Operation, int RowsProcessed, int RowsTotal);

    public DbWriter(SqliteConnection connection)
        : this(connection, commandCache: null, markWriteWork: null)
    {
    }

    /// <summary>
    /// Construct a writer that shares its owning <see cref="DbContext"/>'s
    /// <see cref="PreparedCommandCache"/>. Hot per-file paths (`GetUnchangedFileId`,
    /// `UpsertFile`, file-data cleanup) then reuse one prepared statement per SQL text
    /// instead of constructing a fresh command per call. Issue #1566.
    /// 所属 <see cref="DbContext"/> の <see cref="PreparedCommandCache"/> を共有する writer
    /// を構築する。ファイル単位のホットパスは SQL ごとに 1 つの prepared statement を再利用する。
    /// Issue #1566.
    /// </summary>
    public DbWriter(DbContext context)
        : this(
            (context ?? throw new ArgumentNullException(nameof(context))).Connection,
            context.IsReadOnly ? null : context.PreparedCommands,
            () => context.MarkWriteWork())
    {
    }

    internal DbWriter(SqliteConnection connection, PreparedCommandCache? commandCache, Action? markWriteWork)
    {
        _conn = connection;
        _commandCache = commandCache;
        _markWriteWork = markWriteWork;
    }

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

    /// <summary>
    /// Check if a file needs re-indexing by comparing modified time and checksum.
    /// 更新日時とチェックサムを比較してファイルの再インデックスが必要か判定する。
    /// Returns the existing file ID if unchanged, or null if indexing is needed.
    /// If the timestamp differs but the checksum matches, updates the timestamp
    /// in the DB and returns the ID (content unchanged, e.g. after git checkout).
    /// 変更なしなら既存ファイルIDを返し、インデックスが必要ならnullを返す。
    /// タイムスタンプが異なってもチェックサムが一致すればDB側を更新しIDを返す。
    /// </summary>
    public long? GetUnchangedFileId(string relativePath, DateTime modified, string? checksum = null, long? size = null, int? lines = null, bool allowReuse = true, string? language = null, bool? generated = null)
    {
        if (!allowReuse)
            return null;
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;
        if (HasStaleIssueMetadata(relativePath))
            return null;

        // Keep the unchanged check and timestamp touch in one SQLite statement so
        // concurrent row drift cannot slip between a SELECT and a later UPDATE (#1735).
        var cmd = RentCommand(
            @"UPDATE files
              SET modified = CASE
                  WHEN modified <> @modified
                       AND @checksum IS NOT NULL
                       AND checksum = @checksum
                  THEN @modified
                  ELSE modified
              END,
                  generated = CASE
                  WHEN @generated IS NOT NULL THEN @generated
                  ELSE generated
              END
              WHERE path = @path
                AND (
                    (@checksum IS NOT NULL AND checksum = @checksum AND (@lines IS NULL OR lines = @lines))
                    OR (@checksum IS NULL AND modified = @modified AND (@size IS NULL OR size = @size) AND (@lines IS NULL OR lines = @lines))
                )
              RETURNING id",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@generated", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@checksum"].Value = checksum is null ? DBNull.Value : checksum;
            cmd.Parameters["@size"].Value = size.HasValue ? size.Value : DBNull.Value;
            cmd.Parameters["@lines"].Value = lines.HasValue ? lines.Value : DBNull.Value;
            cmd.Parameters["@generated"].Value = generated.HasValue ? (generated.Value ? 1 : 0) : DBNull.Value;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Read-only unchanged lookup for callers that have only filesystem stat data.
    /// filesystem stat だけを持つ呼び出し元向けの read-only 変更なし判定。
    /// </summary>
    public long? GetUnchangedFileIdByStat(
        string relativePath,
        DateTime modified,
        long size,
        string? language,
        bool allowReuse = true)
    {
        if (!allowReuse)
            return null;
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;
        if (HasStaleIssueMetadata(relativePath))
            return null;

        var cmd = RentCommand(
            @"SELECT id
              FROM files
              WHERE path = @path
                AND modified = @modified
                AND size = @size
              LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@size"].Value = size;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private bool HasStaleIssueMetadata(string relativePath)
    {
        if (!HasIssueMetadataColumns())
        {
            return false;
        }

        var isSolutionFile = string.Equals(Path.GetExtension(relativePath), ".sln", StringComparison.OrdinalIgnoreCase);
        var cmd = RentCommand(
            @"
            SELECT 1
            FROM file_issues i
            JOIN files f ON i.file_id = f.id
            WHERE f.path = @path
              AND (
                  (i.kind IN ('replacement_char', 'non_utf8_likely', 'bom', 'utf16_bom')
                      AND (i.origin IS NULL OR i.severity IS NULL))
                  OR (i.kind = 'bom' AND @is_solution_file = 1)
              )
            LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@is_solution_file", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@is_solution_file"].Value = isSolutionFile ? 1 : 0;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private bool HasIssueMetadataColumns() =>
        _hasIssueMetadataColumns ??= TableExists("file_issues")
            && ColumnExists("file_issues", "origin")
            && ColumnExists("file_issues", "severity");

    /// <summary>
    /// Check whether the DB currently contains any indexed files for the given language.
    /// 指定言語の indexed file が DB に存在するか確認する。
    /// </summary>
    public bool HasAnyFilesWithLanguage(string lang)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM files WHERE lang = @lang LIMIT 1",
            static c => c.Parameters.Add("@lang", SqliteType.Text));
        try
        {
            cmd.Parameters["@lang"].Value = lang;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int CountSymbolsForFile(long fileId)
    {
        var cmd = RentCommand(
            "SELECT COUNT(*) FROM symbols WHERE file_id = @file_id",
            static c => c.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "symbols count for file");
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int CountReferencesForFile(long fileId)
    {
        var cmd = RentCommand(
            "SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id",
            static c => c.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "symbol reference count for file");
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasExtractionCapViolationForFile(long fileId, int maxSymbolsPerFile, int maxReferencesPerFile)
        => HasReusableFileBlockingIssueForFile(fileId, maxSymbolsPerFile, maxReferencesPerFile, generatedExtractionSuppressed: null);

    public bool HasReusableFileBlockingIssueForFile(
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool? generatedExtractionSuppressed)
    {
        var cmd = RentCommand(
            @"
            SELECT CASE WHEN
                (SELECT COUNT(*) FROM symbols WHERE file_id = @file_id) > @max_symbols
                OR EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = @file_id
                      AND kind = 'symbol_count_exceeded'
                )
                OR (SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id) > @max_references
                OR EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = @file_id
                      AND kind = 'reference_count_exceeded'
                )
                OR (
                    @generated_suppressed IS NOT NULL
                    AND (
                        EXISTS (
                            SELECT 1
                            FROM file_issues
                            WHERE file_id = @file_id
                              AND kind = @generated_issue_kind
                        )
                    ) <> @generated_suppressed
                )
            THEN 1 ELSE 0 END",
            static c =>
            {
                c.Parameters.Add("@file_id", SqliteType.Integer);
                c.Parameters.Add("@max_symbols", SqliteType.Integer);
                c.Parameters.Add("@max_references", SqliteType.Integer);
                c.Parameters.Add("@generated_suppressed", SqliteType.Integer);
                c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            cmd.Parameters["@generated_suppressed"].Value = generatedExtractionSuppressed.HasValue
                ? (generatedExtractionSuppressed.Value ? 1 : 0)
                : DBNull.Value;
            cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "reusable file blocking issue") != 0;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasIssueForFile(long fileId, string kind)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM file_issues WHERE file_id = @file_id AND kind = @kind LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@file_id", SqliteType.Integer);
                c.Parameters.Add("@kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            cmd.Parameters["@kind"].Value = kind;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public IReadOnlyList<string> GetIndexedLanguages()
    {
        var languages = new List<string>();
        if (!TableExists("files"))
            return languages;

        var cmd = RentCommand(
            @"
            SELECT DISTINCT f.lang
            FROM files f
            WHERE f.lang IS NOT NULL
              AND f.lang <> ''
              AND EXISTS (SELECT 1 FROM symbols s WHERE s.file_id = f.id)
            ORDER BY f.lang",
            static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                languages.Add(reader.GetString(0));
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return languages;
    }

    /// <summary>
    /// Clean up existing file data (FTS, chunks, symbols) before re-indexing.
    /// 再インデックス前に既存ファイルデータ（FTS、チャンク、シンボル）を削除する。
    /// </summary>
    public void CleanExistingFileData(string relativePath)
    {
        var cmd = RentCommand(
            "SELECT id FROM files WHERE path = @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        object? result;
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            result = cmd.ExecuteScalar();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        if (result != null)
            DeleteFileData((long)result);
    }

    /// <summary>
    /// Purge stale DB rows for deleted/renamed files that still share the current file's checksum.
    /// 現在のファイルと同じ checksum を持つ削除/rename 済みの古いDB行を削除する。
    /// </summary>
    public int PurgeStaleFilesSharingChecksum(string projectRoot, string retainedRelativePath, string? checksum)
    {
        if (string.IsNullOrEmpty(checksum))
            return 0;

        var staleIds = new List<long>();
        var cmd = RentCommand(
            "SELECT id, path FROM files WHERE checksum = @checksum AND path <> @path",
            static c =>
            {
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@path", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@checksum"].Value = checksum;
            cmd.Parameters["@path"].Value = retainedRelativePath;
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var relativePath = reader.GetString(1);
                var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                    staleIds.Add(id);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return DeleteStaleFileIds(staleIds);
    }

    /// <summary>
    /// Purge stale DB rows that look like an extension-changing rename in the same directory.
    /// 同一ディレクトリ・同一stemの拡張子変更リネームに見える古いDB行を削除する。
    /// </summary>
    public int PurgeStaleFilesSharingDirectoryAndStem(string projectRoot, string retainedRelativePath)
    {
        var retainedDirectory = GetRelativeDirectory(retainedRelativePath);
        var retainedStem = GetRelativeFileStem(retainedRelativePath);
        if (retainedStem.Length == 0)
            return 0;

        var staleIds = new List<long>();
        var cmd = RentCommand(
            "SELECT id, path FROM files WHERE path <> @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        try
        {
            cmd.Parameters["@path"].Value = retainedRelativePath;
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var relativePath = reader.GetString(1);
                if (!string.Equals(GetRelativeDirectory(relativePath), retainedDirectory, StringComparison.Ordinal)
                    || !string.Equals(GetRelativeFileStem(relativePath), retainedStem, StringComparison.Ordinal))
                {
                    continue;
                }

                var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                    staleIds.Add(id);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return DeleteStaleFileIds(staleIds);
    }

    private int DeleteStaleFileIds(IReadOnlyCollection<long> staleIds)
    {
        if (staleIds.Count == 0)
            return 0;

        using var txn = !IsInTransaction() ? BeginTransaction() : null;
        DeleteFilesByIdBatched(staleIds);
        txn?.Commit();

        return staleIds.Count;
    }

    private void DeleteFilesByIdBatched(IEnumerable<long> fileIds, int batchSize = DeleteFilesBatchSize)
    {
        var batch = new List<long>(batchSize);
        foreach (var id in fileIds)
        {
            batch.Add(id);
            if (batch.Count == batchSize)
            {
                DeleteFileIdBatch(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            DeleteFileIdBatch(batch);
    }

    private void DeleteFileIdBatch(IReadOnlyList<long> fileIds)
    {
        DeleteCrossFileReferencesToSymbolsDefinedOnlyByFiles(fileIds);

        using var deleteCmd = _conn.CreateCommand();
        var parameters = SqliteDynamicSql.AddParameters(deleteCmd, "id", fileIds, SqliteType.Integer, "file id delete batch");
        deleteCmd.CommandText = $"DELETE FROM files WHERE id IN ({string.Join(", ", parameters)})";
        deleteCmd.ExecuteNonQuery();
    }

    private void DeleteCrossFileReferencesToSymbolsDefinedOnlyByFiles(IReadOnlyList<long> fileIds)
    {
        using var deleteCmd = _conn.CreateCommand();
        var parameters = SqliteDynamicSql.AddParameters(deleteCmd, "id", fileIds, SqliteType.Integer, "cross-file reference delete batch");
        var idList = string.Join(", ", parameters);
        deleteCmd.CommandText = $@"
            DELETE FROM symbol_references
            WHERE file_id NOT IN ({idList})
              AND symbol_name IS NOT NULL
              AND symbol_name <> ''
              AND EXISTS (
                  SELECT 1
                  FROM symbols deleted_symbols
                  WHERE deleted_symbols.file_id IN ({idList})
                    AND deleted_symbols.name = symbol_references.symbol_name
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbols retained_symbols
                  WHERE retained_symbols.file_id NOT IN ({idList})
                    AND retained_symbols.name = symbol_references.symbol_name
              )";
        deleteCmd.ExecuteNonQuery();
    }

    private static string GetRelativeDirectory(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : normalized[..slashIndex];
    }

    private static string GetRelativeFileStem(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        var fileName = slashIndex < 0 ? normalized : normalized[(slashIndex + 1)..];
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex <= 0 ? fileName : fileName[..dotIndex];
    }

    /// <summary>
    /// Check whether a file row already exists for the given relative path.
    /// 指定した相対パスの file 行が既に存在するか確認する。
    /// </summary>
    public bool HasFileAtPath(string relativePath)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM files WHERE path = @path LIMIT 1",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public IReadOnlyList<string> GetIndexedJavaScriptTypeScriptConfigPaths()
    {
        var cmd = RentCommand(
            """
            SELECT path
            FROM files
            WHERE lower(path) = 'jsconfig.json'
               OR lower(path) = 'tsconfig.json'
               OR lower(path) LIKE '%/jsconfig.json'
               OR lower(path) LIKE '%/tsconfig.json'
               OR lower(path) LIKE 'jsconfig.%.json'
               OR lower(path) LIKE 'tsconfig.%.json'
               OR lower(path) LIKE '%/jsconfig.%.json'
               OR lower(path) LIKE '%/tsconfig.%.json'
            ORDER BY path
            """,
            static _ => { });
        try
        {
            var paths = new List<string>();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                paths.Add(reader.GetString(0));
            return paths;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Upsert a file record and return its ID.
    /// Uses ON CONFLICT DO UPDATE to preserve the existing file ID (avoids
    /// unnecessary AUTOINCREMENT growth from INSERT OR REPLACE's delete+insert).
    /// Cleans up old chunks/symbols before re-indexing unless the caller knows
    /// the path cannot already exist in the current database.
    /// ファイルレコードをUPSERTしてIDを返す。
    /// ON CONFLICT DO UPDATEで既存IDを保持する（INSERT OR REPLACEの
    /// delete+insertによる不要なAUTOINCREMENT増加を回避）。
    /// 呼び出し元が現在のDBに同じ path が存在しないと保証できる場合を除き、
    /// 再インデックス前に古いチャンク/シンボルをクリーンアップする。
    /// </summary>
    public long UpsertFile(FileRecord file, bool cleanExistingData = true)
    {
        if (cleanExistingData)
        {
            // Clean up old chunks/symbols so new ones can be inserted
            // 新しいチャンク/シンボル挿入のため古いデータをクリーンアップ
            CleanExistingFileData(file.Path);
        }

        // ON CONFLICT DO UPDATE preserves the existing row ID
        // ON CONFLICT DO UPDATEで既存の行IDを保持する
        var cmd = RentCommand(
            @"
            INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (@path, @lang, @size, @lines, @checksum, @modified, @generated, CURRENT_TIMESTAMP)
            ON CONFLICT(path) DO UPDATE SET
                lang = excluded.lang,
                size = excluded.size,
                lines = excluded.lines,
                checksum = excluded.checksum,
                modified = excluded.modified,
                generated = excluded.generated,
                indexed_at = CURRENT_TIMESTAMP
            RETURNING id",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@lang", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@generated", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = file.Path;
            cmd.Parameters["@lang"].Value = (object?)file.Lang ?? DBNull.Value;
            cmd.Parameters["@size"].Value = file.Size;
            cmd.Parameters["@lines"].Value = file.Lines;
            cmd.Parameters["@checksum"].Value = (object?)file.Checksum ?? DBNull.Value;
            cmd.Parameters["@modified"].Value = file.Modified;
            cmd.Parameters["@generated"].Value = file.Generated ? 1 : 0;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("SQLite RETURNING id produced no row for file upsert.");
            return reader.GetInt64(0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete old chunks and symbols for a file before re-indexing.
    /// 再インデックス前にファイルの古いチャンクとシンボルを削除する。
    /// </summary>
    public void DeleteFileData(long fileId)
    {
        // FTS cleanup is handled automatically by fts_chunks_ad trigger on chunk deletion
        // FTSクリーンアップはチャンク削除時にfts_chunks_adトリガーで自動処理される
        ExecuteFileIdDelete("DELETE FROM chunks WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM symbols WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM symbol_references WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM reference_lines WHERE file_id = @fid", fileId);
    }

    private void ExecuteFileIdDelete(string sql, long fileId)
    {
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@fid", SqliteType.Integer));
        try
        {
            cmd.Parameters["@fid"].Value = fileId;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Insert chunks in batches (FTS index is populated automatically by triggers).
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// チャンクをバッチ挿入する（FTSインデックスはトリガーにより自動で反映される）。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertChunks(IReadOnlyList<ChunkRecord> chunks)
        => InsertChunks(chunks, CancellationToken.None);

    public void InsertChunks(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 5);
        for (int i = 0; i < chunks.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_chunks", i, chunks.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, chunks.Count);
            try
            {
                // Only create a batch transaction when not already inside an outer transaction
                // 外部トランザクション内でない場合のみバッチトランザクションを作成
                using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert chunks") : null;
                InsertChunkBatch(chunks, i, end);
                transaction?.Commit();
            }
            catch (SqliteException batchException) when (IsRowSkippableSqliteException(batchException))
            {
                InsertChunksWithRowSkip(chunks, i, end, batchException, cancellationToken);
            }
        }
        CheckBatchCancellationAndReportProgress("insert_chunks", chunks.Count, chunks.Count, cancellationToken);
    }

    /// <summary>
    /// Insert symbols in batches.
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// シンボルをバッチ挿入する。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertSymbols(IReadOnlyList<SymbolRecord> symbols)
        => InsertSymbols(symbols, CancellationToken.None);

    public void InsertSymbols(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return;

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 19);
        for (int i = 0; i < symbols.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_symbols", i, symbols.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, symbols.Count);
            var foldedNameCache = CreateFoldedNameCache(end - i, namesPerRow: 1);
            try
            {
                // Only create a batch transaction when not already inside an outer transaction
                // 外部トランザクション内でない場合のみバッチトランザクションを作成
                using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert symbols") : null;
                InsertSymbolBatch(symbols, i, end, foldedNameCache);
                transaction?.Commit();
            }
            catch (SqliteException batchException) when (IsRowSkippableSqliteException(batchException))
            {
                InsertSymbolsWithRowSkip(symbols, i, end, batchException, cancellationToken);
            }
        }
        CheckBatchCancellationAndReportProgress("insert_symbols", symbols.Count, symbols.Count, cancellationToken);
    }

    private void InsertChunksWithRowSkip(IReadOnlyList<ChunkRecord> chunks, int start, int end, SqliteException batchException, CancellationToken cancellationToken)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert chunks row skip") : null;
        for (int i = start; i < end; i++)
        {
            CheckBatchCancellationAndReportProgress("insert_chunks_row_skip", i, end, cancellationToken);
            var chunk = chunks[i];
            ExecuteWithRowSavepoint(
                () => InsertChunkBatch(chunks, i, i + 1),
                ex => WarnSkippedBatchRow($"chunk file_id={chunk.FileId} chunk_index={chunk.ChunkIndex}", batchException, ex));
        }

        transaction?.Commit();
    }

    private void InsertSymbolsWithRowSkip(IReadOnlyList<SymbolRecord> symbols, int start, int end, SqliteException batchException, CancellationToken cancellationToken)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert symbols row skip") : null;
        var foldedNameCache = CreateFoldedNameCache(end - start, namesPerRow: 1);
        for (int i = start; i < end; i++)
        {
            CheckBatchCancellationAndReportProgress("insert_symbols_row_skip", i, end, cancellationToken);
            var symbol = symbols[i];
            ExecuteWithRowSavepoint(
                () => InsertSymbolBatch(symbols, i, i + 1, foldedNameCache),
                ex => WarnSkippedBatchRow($"symbol file_id={symbol.FileId} name={symbol.Name} line={symbol.Line}", batchException, ex));
        }

        transaction?.Commit();
    }

    private void CheckBatchCancellationAndReportProgress(
        string operation,
        int rowsProcessed,
        int rowsTotal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rowsTotal <= BatchSize && BatchProgressCheckpointForTesting == null)
            return;

        var progress = new DbWriterBatchProgress(operation, rowsProcessed, rowsTotal);
        BatchProgressCheckpointForTesting?.Invoke(progress);
        GlobalToolLog.Info(
            "db_writer_batch_checkpoint"
            + $" operation={operation}"
            + $" rows_processed={rowsProcessed.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $" rows_total={rowsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private void ExecuteWithRowSavepoint(Action insertRow, Action<Exception> onSkip)
    {
        var savepointName = $"row_skip_{Interlocked.Increment(ref _rowSkipSavepointCounter)}";
        Execute($"SAVEPOINT {savepointName}");
        try
        {
            insertRow();
            Execute($"RELEASE SAVEPOINT {savepointName}");
        }
        catch (Exception ex) when (ex is SqliteException)
        {
            Execute($"ROLLBACK TO SAVEPOINT {savepointName}");
            Execute($"RELEASE SAVEPOINT {savepointName}");
            if (IsRowSkippableSqliteException((SqliteException)ex))
                onSkip(ex);
            else
                throw;
        }
    }

    private static bool IsRowSkippableSqliteException(SqliteException ex)
        => ex.SqliteErrorCode == SqliteConstraintErrorCode;

    private void WarnSkippedBatchRow(string rowIdentifier, Exception batchException, Exception rowException)
    {
        Interlocked.Increment(ref _batchRowsSkipped);
        var message = BuildBatchRowSkipWarning(rowIdentifier, batchException, rowException);
        var testSink = BatchRowSkipWarningForTesting;
        if (testSink != null)
            testSink(message);
        else
            CommandErrorWriter.WriteStderr(message);
    }

    internal static string BuildBatchRowSkipWarningForTesting(string rowIdentifier, Exception batchException, Exception rowException)
        => BuildBatchRowSkipWarning(rowIdentifier, batchException, rowException);

    private static string BuildBatchRowSkipWarning(string rowIdentifier, Exception batchException, Exception rowException)
        => $"Warning: skipped failed batch row ({FormatBatchRowSkipDiagnosticValue(rowIdentifier)}); batch_error={FormatBatchRowSkipException(batchException)}; row_error={FormatBatchRowSkipException(rowException)}";

    private static string FormatBatchRowSkipException(Exception ex)
        => FormatBatchRowSkipDiagnosticValue(DiagnosticRedactor.FormatExceptionMessage(ex));

    private static string FormatBatchRowSkipDiagnosticValue(string? value)
    {
        return ConsoleUi.FormatBoundedValue(value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private void InsertChunkBatch(IReadOnlyList<ChunkRecord> chunks, int start, int end)
    {
        using var cmd = _conn.CreateCommand();
        var sql = CreateBatchSqlBuilder(end - start, estimatedCharsPerRow: 64);
        sql.Append("INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content) VALUES ");
        for (int j = start; j < end; j++)
        {
            var chunk = chunks[j];
            if (j > start)
                sql.Append(", ");
            var suffix = j - start;
            sql.Append($"(@fid{suffix}, @idx{suffix}, @start{suffix}, @end{suffix}, @content{suffix})");
            cmd.Parameters.Add($"@fid{suffix}", SqliteType.Integer).Value = chunk.FileId;
            cmd.Parameters.Add($"@idx{suffix}", SqliteType.Integer).Value = chunk.ChunkIndex;
            cmd.Parameters.Add($"@start{suffix}", SqliteType.Integer).Value = chunk.StartLine;
            cmd.Parameters.Add($"@end{suffix}", SqliteType.Integer).Value = chunk.EndLine;
            cmd.Parameters.Add($"@content{suffix}", SqliteType.Text).Value = chunk.Content;
        }

        cmd.CommandText = sql.ToString();
        cmd.ExecuteNonQuery();
    }

    private void InsertSymbolBatch(IReadOnlyList<SymbolRecord> symbols, int start, int end, Dictionary<string, string?> foldedNameCache)
    {
        using var cmd = _conn.CreateCommand();
        var sql = CreateBatchSqlBuilder(end - start, estimatedCharsPerRow: 320);
        sql.Append(@"
                INSERT INTO symbols (
                    file_id, kind, sub_kind, name, line, start_line, start_column, end_line,
                    body_start_line, body_end_line, signature,
                    container_kind, container_name, container_qualified_name, family_key,
                    visibility, return_type,
                    is_metadata_target, metadata_target_source,
                    name_folded
                )
                VALUES ");

        for (int j = start; j < end; j++)
        {
            var symbol = symbols[j];
            ValidateSymbolKinds(symbol);
            var startLine = symbol.StartLine > 0 ? symbol.StartLine : symbol.Line;
            var endLine = symbol.EndLine > 0 ? symbol.EndLine : startLine;
            if (j > start)
                sql.Append(", ");
            var suffix = j - start;
            sql.Append($@"(
                    @fid{suffix}, @kind{suffix}, @subKind{suffix}, @name{suffix}, @line{suffix}, @startLine{suffix}, @startColumn{suffix}, @endLine{suffix},
                    @bodyStartLine{suffix}, @bodyEndLine{suffix}, @signature{suffix},
                    @containerKind{suffix}, @containerName{suffix}, @containerQualifiedName{suffix}, @familyKey{suffix},
                    @visibility{suffix}, @returnType{suffix},
                    @isMetadataTarget{suffix}, @metadataTargetSource{suffix},
                    @nameFolded{suffix}
                )");
            cmd.Parameters.Add($"@fid{suffix}", SqliteType.Integer).Value = symbol.FileId;
            cmd.Parameters.Add($"@kind{suffix}", SqliteType.Text).Value = symbol.Kind;
            cmd.Parameters.Add($"@subKind{suffix}", SqliteType.Text).Value = (object?)symbol.SubKind ?? DBNull.Value;
            cmd.Parameters.Add($"@name{suffix}", SqliteType.Text).Value = symbol.Name;
            cmd.Parameters.Add($"@line{suffix}", SqliteType.Integer).Value = symbol.Line;
            cmd.Parameters.Add($"@startLine{suffix}", SqliteType.Integer).Value = startLine;
            cmd.Parameters.Add($"@startColumn{suffix}", SqliteType.Integer).Value = (object?)symbol.StartColumn ?? DBNull.Value;
            cmd.Parameters.Add($"@endLine{suffix}", SqliteType.Integer).Value = endLine;
            cmd.Parameters.Add($"@bodyStartLine{suffix}", SqliteType.Integer).Value = (object?)symbol.BodyStartLine ?? DBNull.Value;
            cmd.Parameters.Add($"@bodyEndLine{suffix}", SqliteType.Integer).Value = (object?)symbol.BodyEndLine ?? DBNull.Value;
            cmd.Parameters.Add($"@signature{suffix}", SqliteType.Text).Value = (object?)symbol.Signature ?? DBNull.Value;
            cmd.Parameters.Add($"@containerKind{suffix}", SqliteType.Text).Value = (object?)symbol.ContainerKind ?? DBNull.Value;
            cmd.Parameters.Add($"@containerName{suffix}", SqliteType.Text).Value = (object?)symbol.ContainerName ?? DBNull.Value;
            cmd.Parameters.Add($"@containerQualifiedName{suffix}", SqliteType.Text).Value = (object?)symbol.ContainerQualifiedName ?? DBNull.Value;
            cmd.Parameters.Add($"@familyKey{suffix}", SqliteType.Text).Value = (object?)symbol.FamilyKey ?? DBNull.Value;
            cmd.Parameters.Add($"@visibility{suffix}", SqliteType.Text).Value = (object?)symbol.Visibility ?? DBNull.Value;
            cmd.Parameters.Add($"@returnType{suffix}", SqliteType.Text).Value = (object?)symbol.ReturnType ?? DBNull.Value;
            cmd.Parameters.Add($"@isMetadataTarget{suffix}", SqliteType.Integer).Value = symbol.IsMetadataTarget.HasValue
                ? (symbol.IsMetadataTarget.Value ? 1 : 0)
                : (object)DBNull.Value;
            cmd.Parameters.Add($"@metadataTargetSource{suffix}", SqliteType.Text).Value = (object?)symbol.MetadataTargetSource ?? DBNull.Value;
            cmd.Parameters.Add($"@nameFolded{suffix}", SqliteType.Text).Value = FoldedNameDbValue(symbol.Name, foldedNameCache);
        }

        cmd.CommandText = sql.ToString();
        cmd.ExecuteNonQuery();
    }

    public List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(IReadOnlySet<string>? excludedPaths = null)
    {
        var symbols = new List<SymbolRecord>();
        const string sql = @"
            SELECT
                f.path,
                s.file_id, s.kind, s.name, s.line,
                COALESCE(s.start_line, s.line) AS start_line,
                s.start_column,
                COALESCE(s.end_line, COALESCE(s.start_line, s.line)) AS end_line,
                s.body_start_line, s.body_end_line,
                s.signature,
                s.container_kind, s.container_name, s.container_qualified_name,
                s.family_key, s.visibility, s.return_type,
                s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND (
                    s.kind = 'interface'
                    OR (
                        s.container_kind = 'interface'
                        AND s.kind IN ('function', 'operator', 'property')
                        AND s.signature LIKE '%static%'
                        AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')
                    )
              )";

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (excludedPaths?.Contains(path) == true)
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = reader.GetInt64(1),
                    Kind = reader.GetString(2),
                    Name = reader.GetString(3),
                    Line = reader.GetInt32(4),
                    StartLine = reader.GetInt32(5),
                    StartColumn = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    EndLine = reader.GetInt32(7),
                    BodyStartLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    BodyEndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    Signature = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ContainerKind = reader.IsDBNull(11) ? null : reader.GetString(11),
                    ContainerName = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ContainerQualifiedName = reader.IsDBNull(13) ? null : reader.GetString(13),
                    FamilyKey = reader.IsDBNull(14) ? null : reader.GetString(14),
                    Visibility = reader.IsDBNull(15) ? null : reader.GetString(15),
                    ReturnType = reader.IsDBNull(16) ? null : reader.GetString(16),
                    IsMetadataTarget = reader.IsDBNull(17) ? null : reader.GetInt32(17) != 0,
                });
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return symbols;
    }

    public bool HasCSharpStaticInterfaceContractSymbolsInPaths(IReadOnlySet<string> paths)
    {
        if (paths.Count == 0)
            return false;

        const string sql = @"
            SELECT f.path
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.container_kind = 'interface'
              AND s.kind IN ('function', 'operator', 'property')
              AND s.signature LIKE '%static%'
              AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')";

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (paths.Contains(reader.GetString(0)))
                    return true;
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return false;
    }

    /// <summary>
    /// Insert indexed references in batches.
    /// インデックス済み参照をバッチ挿入する。
    /// </summary>
    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags = true)
        => InsertReferences(references, refreshMutualRecursionFlags, CancellationToken.None);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, CancellationToken cancellationToken)
        => InsertReferences(references, refreshMutualRecursionFlags: true, cancellationToken);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags, CancellationToken cancellationToken)
    {
        if (references.Count == 0) return;

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 13);
        for (int i = 0; i < references.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_references", i, references.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, references.Count);
            var foldedNameCache = CreateFoldedNameCache(end - i, namesPerRow: 2);
            // Always open a chunk-scoped transaction or SAVEPOINT so reference_lines and
            // symbol_references share one rollback boundary; without it a mid-chunk failure
            // under an outer transaction would orphan committed reference_lines (#1518).
            using var transaction = BeginTransaction(cancellationToken, "insert references");
            var referenceLineIds = UpsertReferenceLines(references, i, end, cancellationToken);

            using var cmd = _conn.CreateCommand();
            var sql = CreateBatchSqlBuilder(end - i, estimatedCharsPerRow: 256);
            sql.Append(@"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion
                )
                VALUES ");

            for (int j = i; j < end; j++)
            {
                var reference = references[j];
                ValidateReferenceKinds(reference);
                var referenceLineId = referenceLineIds[(reference.FileId, reference.Line, reference.Context)];

                if (j > i)
                    sql.Append(", ");
                var suffix = j - i;
                sql.Append($@"(
                    @fid{suffix}, @symbolName{suffix}, @referenceKind{suffix}, @line{suffix}, @columnNumber{suffix},
                    @context{suffix}, @referenceLineId{suffix}, @containerKind{suffix}, @containerName{suffix},
                    @symbolNameFolded{suffix}, @containerNameFolded{suffix}, @isSelfReference{suffix},
                    @isMutualRecursion{suffix}
                )");
                cmd.Parameters.Add($"@fid{suffix}", SqliteType.Integer).Value = reference.FileId;
                cmd.Parameters.Add($"@symbolName{suffix}", SqliteType.Text).Value = reference.SymbolName;
                cmd.Parameters.Add($"@referenceKind{suffix}", SqliteType.Text).Value = reference.ReferenceKind;
                cmd.Parameters.Add($"@line{suffix}", SqliteType.Integer).Value = reference.Line;
                cmd.Parameters.Add($"@columnNumber{suffix}", SqliteType.Integer).Value = reference.Column;
                cmd.Parameters.Add($"@context{suffix}", SqliteType.Text).Value = DBNull.Value;
                cmd.Parameters.Add($"@referenceLineId{suffix}", SqliteType.Integer).Value = referenceLineId;
                cmd.Parameters.Add($"@containerKind{suffix}", SqliteType.Text).Value = (object?)reference.ContainerKind ?? DBNull.Value;
                cmd.Parameters.Add($"@containerName{suffix}", SqliteType.Text).Value = (object?)reference.ContainerName ?? DBNull.Value;
                cmd.Parameters.Add($"@symbolNameFolded{suffix}", SqliteType.Text).Value = FoldedNameDbValue(reference.SymbolName, foldedNameCache);
                cmd.Parameters.Add($"@containerNameFolded{suffix}", SqliteType.Text).Value = FoldedNameDbValue(reference.ContainerName, foldedNameCache);
                cmd.Parameters.Add($"@isSelfReference{suffix}", SqliteType.Integer).Value = reference.IsSelfReference ? 1 : 0;
                cmd.Parameters.Add($"@isMutualRecursion{suffix}", SqliteType.Integer).Value = reference.IsMutualRecursion ? 1 : 0;
            }

            cmd.CommandText = sql.ToString();
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }

        CheckBatchCancellationAndReportProgress("insert_references", references.Count, references.Count, cancellationToken);
        if (refreshMutualRecursionFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMutualRecursionFlags();
        }
    }

    private static void ValidateSymbolKinds(SymbolRecord symbol)
    {
        if (!SymbolKindCatalog.IsValidSymbolKind(symbol.Kind))
            throw new ArgumentException($"Unknown symbol kind '{symbol.Kind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(symbol));

        if (symbol.ContainerKind != null && !SymbolKindCatalog.IsValidSymbolKind(symbol.ContainerKind))
            throw new ArgumentException($"Unknown symbol container kind '{symbol.ContainerKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(symbol));
    }

    private static void ValidateReferenceKinds(ReferenceRecord reference)
    {
        if (!SymbolKindCatalog.IsValidReferenceKind(reference.ReferenceKind))
            throw new ArgumentException($"Unknown reference kind '{reference.ReferenceKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(reference));

        if (reference.ContainerKind != null && !SymbolKindCatalog.IsValidSymbolKind(reference.ContainerKind))
            throw new ArgumentException($"Unknown reference container kind '{reference.ContainerKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(reference));
    }

    private Dictionary<(long FileId, int Line, string Context), long> UpsertReferenceLines(IReadOnlyList<ReferenceRecord> references, int start, int end, CancellationToken cancellationToken)
    {
        var batchCount = end - start;
        var referenceLineKeys = new HashSet<(long FileId, int Line, string Context)>(batchCount);
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[i];
            referenceLineKeys.Add((reference.FileId, reference.Line, reference.Context));
        }

        var rows = referenceLineKeys.ToArray();
        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("upsert_reference_lines", i, rows.Length, cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Length);
            using var cmd = _conn.CreateCommand();
            var sql = CreateBatchSqlBuilder(batchEnd - i, estimatedCharsPerRow: 64);
            sql.Append("INSERT INTO reference_lines (file_id, line, context) VALUES ");
            for (int j = i; j < batchEnd; j++)
            {
                if (j > i)
                    sql.Append(", ");
                var suffix = j - i;
                var (fileId, line, context) = rows[j];
                sql.Append($"(@fid{suffix}, @line{suffix}, @context{suffix})");
                cmd.Parameters.Add($"@fid{suffix}", SqliteType.Integer).Value = fileId;
                cmd.Parameters.Add($"@line{suffix}", SqliteType.Integer).Value = line;
                cmd.Parameters.Add($"@context{suffix}", SqliteType.Text).Value = context;
            }

            sql.Append(" ON CONFLICT(file_id, line, context) DO NOTHING");
            cmd.CommandText = sql.ToString();
            cmd.ExecuteNonQuery();
        }

        var lineIds = new Dictionary<(long FileId, int Line, string Context), long>(rows.Length);
        int keysPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += keysPerStatement)
        {
            CheckBatchCancellationAndReportProgress("lookup_reference_lines", i, rows.Length, cancellationToken);
            int keyEnd = Math.Min(i + keysPerStatement, rows.Length);
            using var cmd = _conn.CreateCommand();
            var predicates = CreateBatchSqlBuilder(keyEnd - i, estimatedCharsPerRow: 96);
            for (int j = i; j < keyEnd; j++)
            {
                if (j > i)
                    predicates.Append(" OR ");

                var suffix = j - i;
                var (fileId, line, context) = rows[j];
                predicates.Append($"(file_id = @lookupFid{suffix} AND line = @lookupLine{suffix} AND context = @lookupContext{suffix})");
                cmd.Parameters.Add($"@lookupFid{suffix}", SqliteType.Integer).Value = fileId;
                cmd.Parameters.Add($"@lookupLine{suffix}", SqliteType.Integer).Value = line;
                cmd.Parameters.Add($"@lookupContext{suffix}", SqliteType.Text).Value = context;
            }

            cmd.CommandText = $@"
                SELECT id, file_id, line, context
                FROM reference_lines
                WHERE {predicates}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var fileId = reader.GetInt64(1);
                var line = reader.GetInt32(2);
                var context = reader.GetString(3);
                var key = (fileId, line, context);
                if (referenceLineKeys.Contains(key))
                    lineIds[key] = id;
            }
        }

        return lineIds;
    }

    internal void RefreshMutualRecursionFlags()
    {
        var cmd = RentCommand(
            @"
            UPDATE symbol_references AS r
            SET is_mutual_recursion = CASE
                WHEN r.is_self_reference = 0
                 AND r.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                 AND r.container_name IS NOT NULL
                 AND r.container_name <> ''
                 AND r.symbol_name IS NOT NULL
                 AND r.symbol_name <> ''
                 AND (
                    (
                        r.container_name_folded IS NOT NULL
                        AND r.container_name_folded <> ''
                        AND r.symbol_name_folded IS NOT NULL
                        AND r.symbol_name_folded <> ''
                        AND EXISTS (
                            SELECT 1
                            FROM symbol_references AS reverse
                            WHERE reverse.is_self_reference = 0
                              AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                              AND reverse.container_name_folded = r.symbol_name_folded
                              AND reverse.symbol_name_folded = r.container_name_folded
                        )
                    )
                    OR (
                        (r.container_name_folded IS NULL OR r.symbol_name_folded IS NULL)
                        AND EXISTS (
                            SELECT 1
                            FROM symbol_references AS reverse
                            WHERE reverse.is_self_reference = 0
                              AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                              AND reverse.container_name = r.symbol_name COLLATE NOCASE
                              AND reverse.symbol_name = r.container_name COLLATE NOCASE
                        )
                    )
                 )
                THEN 1
                ELSE 0
            END",
            static _ => { });
        try
        {
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int RebuildTypeScriptAugmentationReferences(string? projectRoot = null)
    {
        using var transaction = BeginTransaction();

        var deleteCmd = RentCommand(
            "DELETE FROM symbol_references WHERE reference_kind = 'augmentation'",
            static _ => { });
        try
        {
            deleteCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(deleteCmd);
        }

        var references = new List<ReferenceRecord>();
        var cmd = RentCommand(
            @"
                SELECT s.file_id,
                       f.path,
                       s.name,
                       s.line,
                       s.start_column,
                       s.signature,
                       s.kind,
                       s.container_name,
                       s.visibility
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'typescript'
                  AND s.name IS NOT NULL
                  AND s.name <> ''
                  AND s.kind = 'interface'
                ORDER BY s.name, s.file_id, s.line",
            static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            var declarations = new List<(long FileId, string Path, string Name, int Line, int Column, string Signature, string Kind, string ContainerName, string? Visibility)>();
            while (reader.Read())
            {
                declarations.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? 1 : Math.Max(1, reader.GetInt32(3)),
                    reader.IsDBNull(4) ? 1 : Math.Max(1, reader.GetInt32(4) + 1),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            var moduleFileIds = FindTypeScriptModuleFileIds(projectRoot, declarations);
            foreach (var group in declarations.GroupBy(d => (d.Name, ScopeKey: BuildTypeScriptScopeKey(d.FileId, d.Path, d.Signature, d.ContainerName, moduleFileIds))))
            {
                if (group.Count() < 2)
                    continue;

                foreach (var declaration in group)
                {
                    references.Add(new ReferenceRecord
                    {
                        FileId = declaration.FileId,
                        SymbolName = declaration.Name,
                        ReferenceKind = "augmentation",
                        Line = declaration.Line,
                        Column = declaration.Column,
                        Context = declaration.Signature,
                        ContainerKind = declaration.Kind == "interface" ? "interface" : "type",
                        ContainerName = declaration.Name,
                    });
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        InsertReferences(references);
        SetMeta(
            DbContext.TypeScriptAugmentationVersionMetaKey,
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        transaction.Commit();
        return references.Count;
    }

    private static HashSet<long> FindTypeScriptModuleFileIds(
        string? projectRoot,
        IReadOnlyList<(long FileId, string Path, string Name, int Line, int Column, string Signature, string Kind, string ContainerName, string? Visibility)> declarations)
    {
        var moduleFileIds = new HashSet<long>();
        foreach (var declaration in declarations)
        {
            if (declaration.Visibility == "export"
                || declaration.Signature.StartsWith("export ", StringComparison.Ordinal)
                || declaration.Signature.StartsWith("import ", StringComparison.Ordinal))
            {
                moduleFileIds.Add(declaration.FileId);
            }
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
            return moduleFileIds;

        foreach (var file in declarations.GroupBy(static d => (d.FileId, d.Path)))
        {
            if (moduleFileIds.Contains(file.Key.FileId))
                continue;

            var absolutePath = Path.Combine(projectRoot, file.Key.Path.Replace('/', Path.DirectorySeparatorChar));
            if (TypeScriptFileHasModuleSyntax(absolutePath))
                moduleFileIds.Add(file.Key.FileId);
        }

        return moduleFileIds;
    }

    private static string BuildTypeScriptScopeKey(long fileId, string path, string signature, string containerName, HashSet<long> moduleFileIds)
    {
        if (!moduleFileIds.Contains(fileId))
            return "global:" + containerName;
        if (containerName == "global" || signature.StartsWith("declare global ", StringComparison.Ordinal))
            return "global:";
        if (IsAmbientModuleContainer(containerName))
            return "ambient-module:" + containerName;
        return "module:" + path + ":" + containerName;
    }

    private static bool IsAmbientModuleContainer(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;
        return containerName[0] is '"' or '\'' || containerName.Contains('/', StringComparison.Ordinal);
    }

    private static bool TypeScriptFileHasModuleSyntax(string absolutePath)
    {
        try
        {
            var content = DataDirectorySecurity.ReadTextWithinLimit(
                absolutePath,
                TypeScriptModuleSyntaxFallbackMaxBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (content is null)
                return false;

            var lineCount = 0;
            foreach (var line in EnumerateLines(content))
            {
                lineCount++;
                if (lineCount > TypeScriptModuleSyntaxFallbackMaxLines)
                    return false;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("import ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export{", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsExpectedTypeScriptFallbackReadException(ex)) { }

        return false;
    }

    internal static bool TypeScriptFileHasModuleSyntaxForTests(string absolutePath) =>
        TypeScriptFileHasModuleSyntax(absolutePath);

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return text[start..].TrimEnd('\r');
                yield break;
            }

            yield return text[start..newline].TrimEnd('\r');
            start = newline + 1;
        }
    }

    private static bool IsExpectedTypeScriptFallbackReadException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    /// <summary>
    /// Insert file validation issues.
    /// ファイル検証問題を挿入する。
    /// </summary>
    public void InsertIssues(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues)
    {
        // Always delete existing issues — if the file is now clean, old issues must be removed
        // 常に既存問題を削除 — ファイルが修正済みなら古い問題を残さない
        var delCmd = RentCommand(
            "DELETE FROM file_issues WHERE file_id = @fid",
            static c => c.Parameters.Add("@fid", SqliteType.Integer));
        try
        {
            delCmd.Parameters["@fid"].Value = fileId;
            delCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(delCmd);
        }

        if (issues.Count == 0) return;

        var cmd = RentCommand(
            "INSERT INTO file_issues (file_id, kind, line, message, origin, severity) VALUES (@fid, @kind, @line, @message, @origin, @severity)",
            static c =>
            {
                c.Parameters.Add("@fid", SqliteType.Integer);
                c.Parameters.Add("@kind", SqliteType.Text);
                c.Parameters.Add("@line", SqliteType.Integer);
                c.Parameters.Add("@message", SqliteType.Text);
                c.Parameters.Add("@origin", SqliteType.Text);
                c.Parameters.Add("@severity", SqliteType.Text);
            });
        try
        {
            var pFid = cmd.Parameters["@fid"];
            var pKind = cmd.Parameters["@kind"];
            var pLine = cmd.Parameters["@line"];
            var pMessage = cmd.Parameters["@message"];
            var pOrigin = cmd.Parameters["@origin"];
            var pSeverity = cmd.Parameters["@severity"];

            foreach (var issue in issues)
            {
                pFid.Value = fileId;
                pKind.Value = issue.Kind;
                pLine.Value = issue.Line;
                pMessage.Value = issue.Message;
                pOrigin.Value = issue.Origin ?? (object)DBNull.Value;
                pSeverity.Value = issue.Severity ?? (object)DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete a file and its associated data by relative path. Returns true if found.
    /// CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically.
    /// 相対パスでファイルと関連データを削除する。見つかればtrueを返す。
    /// chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する。
    /// </summary>
    public bool DeleteFileByPath(string relativePath)
    {
        var cmd = RentCommand(
            "DELETE FROM files WHERE path = @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            return cmd.ExecuteNonQuery() > 0;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Remove files from DB that no longer exist on disk (e.g. after branch switch).
    /// ディスク上に存在しなくなったファイルをDBから削除する（ブランチ切り替え対応）。
    /// </summary>
    /// <param name="projectRoot">Absolute path to project root / プロジェクトルートの絶対パス</param>
    /// <param name="preservedMissingPaths">
    /// Relative DB paths that are intentionally absent from disk /
    /// ディスク上に意図的に存在しないDB相対パス
    /// </param>
    /// <returns>Number of stale files removed / 削除された古いファイル数</returns>
    public int PurgeStaleFiles(
        string projectRoot,
        Action? beforeCommit = null,
        IReadOnlySet<string>? preservedMissingPaths = null)
    {
        // Collect all paths currently in DB / DB内の全パスを収集
        var dbPaths = LoadCurrentFilePaths();

        // Identify stale files (no longer on disk) / ディスク上に存在しないファイルを特定
        var staleIds = new List<long>();
        foreach (var (id, relativePath) in dbPaths)
        {
            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            // Wrap with the Windows extended-length prefix before File.Exists so deep monorepo
            // paths (>= 248 chars) are not silently classified as stale and DELETED from the DB.
            // Without this wrap, the FileIndexer walker can index a long path successfully and
            // the next index run will purge it. See LongPath.cs and #1547.
            if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath))
                && (preservedMissingPaths == null || !preservedMissingPaths.Contains(relativePath)))
                staleIds.Add(id);
        }

        if (staleIds.Count == 0)
            return 0;

        return DeleteFilesById(staleIds, beforeCommit);
    }

    /// <summary>
    /// Remove files from DB that are not part of the current authoritative full-scan set.
    /// This covers both deleted files and files that still exist on disk but are no longer indexable.
    /// 現在の authoritative な full-scan 結果に含まれないファイルをDBから削除する。
    /// ディスク上から消えたファイルだけでなく、存在はするがインデックス対象外になったファイルも含む。
    /// </summary>
    public int PurgeFilesOutsideRetainedSet(IReadOnlySet<string> retainedRelativePaths)
    {
        var staleIds = new List<long>();
        foreach (var (id, path) in LoadCurrentFilePaths())
        {
            if (!retainedRelativePaths.Contains(path))
                staleIds.Add(id);
        }

        return DeleteFilesById(staleIds);
    }

    /// <summary>
    /// Remove files from DB that are outside the retained set, but only when their immediate
    /// parent directory completed its own file listing authoritatively OR they sit anywhere
    /// under a directory that the scanner skipped because of file attributes such as symlink /
    /// reparse point or Windows Hidden/System. The pruned-directory case lets us authoritatively
    /// purge deep descendants indexed by earlier runs, because the current scan affirmatively
    /// refused to enter that subtree. Used by partial full scans so unreadable descendants do not block stale-file
    /// cleanup for already-listed siblings, while still protecting unreadable subtrees from
    /// speculative deletes.
    /// retained set の外にある DB ファイルを削除するが、即時親ディレクトリ自身の file listing が
    /// authoritative に完了した場合、または symlink / reparse point や Windows Hidden/System などの
    /// file attribute で scanner が skip したディレクトリ配下に入っている場合に限定する。後者は
    /// 「今回のスキャンが subtree 全体への進入を明示的に拒否した」ことを根拠に、過去の実行で
    /// 作られた深い子孫も authoritative に purge できる。partial full scan では、unreadable descendant のせいで既に列挙済み sibling
    /// の stale cleanup が止まらないようにしつつ、unreadable subtree 自体は推測ベースで削除しない。
    /// </summary>
    public int PurgeFilesOutsideRetainedSetWithinListedDirectories(
        IReadOnlySet<string> retainedRelativePaths,
        IReadOnlySet<string> listedDirectories,
        IReadOnlySet<string> attributePrunedDirectories)
    {
        var staleIds = new List<long>();
        foreach (var (id, path) in LoadCurrentFilePaths())
        {
            if (retainedRelativePaths.Contains(path))
                continue;

            if (HasListedParentDirectory(path, listedDirectories)
                || IsUnderAttributePrunedDirectory(path, attributePrunedDirectories))
                staleIds.Add(id);
        }

        if (staleIds.Count == 0)
            return 0;

        return DeleteFilesById(staleIds);
    }

    private List<(long id, string path)> LoadCurrentFilePaths()
    {
        var cmd = RentCommand("SELECT id, path FROM files", static _ => { });
        try
        {
            var dbPaths = new List<(long id, string path)>();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                dbPaths.Add((reader.GetInt64(0), reader.GetString(1)));
            return dbPaths;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private int DeleteFilesById(IReadOnlyList<long> fileIds, Action? beforeCommit = null)
    {
        if (fileIds.Count == 0)
            return 0;

        // Delete all stale files in a single transaction for atomicity and performance.
        // アトミック性とパフォーマンスのため、全古いファイルを1トランザクションで削除。
        // CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically.
        // chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する。
        using var txn = BeginTransaction();
        var deleteCmd = RentCommand(
            "DELETE FROM files WHERE id = @id",
            static c => c.Parameters.Add("@id", SqliteType.Integer));
        try
        {
            var pId = deleteCmd.Parameters["@id"];
            if (_commandCache is null)
                deleteCmd.Prepare();
            foreach (var id in fileIds)
            {
                pId.Value = id;
                deleteCmd.ExecuteNonQuery();
            }
            beforeCommit?.Invoke();
            txn.Commit();
            return fileIds.Count;
        }
        finally
        {
            ReleaseCommand(deleteCmd);
        }
    }

    private static bool HasListedParentDirectory(string path, IReadOnlySet<string> listedDirectories)
    {
        var directory = GetDirectoryPath(path);
        return listedDirectories.Contains(directory);
    }

    // True when any proper ancestor directory of `path` is in the attribute-pruned set.
    // We walk parents via LastIndexOf('/') rather than building a substring prefix test so that
    // "sub/parent_loop" only matches "sub/parent_loop/..." and never a sibling like "sub/parent_loop_x/...".
    // path のいずれかの真の祖先ディレクトリが attribute-pruned 集合に含まれるかを判定する。
    // 単純な prefix 比較だと "sub/parent_loop" が "sub/parent_loop_x/..." まで巻き込むので、
    // LastIndexOf('/') で親を辿り、ディレクトリ境界に揃った一致のみを拾う。
    private static bool IsUnderAttributePrunedDirectory(string path, IReadOnlySet<string> attributePrunedDirectories)
    {
        if (attributePrunedDirectories.Count == 0)
            return false;

        var directory = GetDirectoryPath(path);
        while (directory.Length > 0)
        {
            if (attributePrunedDirectories.Contains(directory))
                return true;

            var separatorIndex = directory.LastIndexOf('/');
            directory = separatorIndex >= 0 ? directory[..separatorIndex] : string.Empty;
        }
        return false;
    }

    private static string GetDirectoryPath(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex >= 0 ? path[..separatorIndex] : string.Empty;
    }

    /// <summary>
    /// Count symbol_references for files whose language is no longer graph-supported.
    /// Used by update mode to demote readiness before the first real mutation commits.
    /// グラフ非対応になった言語の symbol_references 件数を数える。
    /// update mode が最初の実 mutation 前に readiness を落とす判断に使う。
    /// </summary>
    /// <param name="supportedLanguages">Currently supported languages / 現在対応している言語</param>
    /// <returns>Number of stale reference rows present / 存在している古い参照行数</returns>
    public int CountUnsupportedReferences(IReadOnlyCollection<string> supportedLanguages)
    {
        if (supportedLanguages.Count == 0)
            return 0;

        var values = SnapshotSupportedLanguages(supportedLanguages);
        var inParams = BuildSupportedLanguageParameterNames(values.Count);
        var cmd = RentCommand(
            $@"
            SELECT COUNT(*)
            FROM symbol_references
            WHERE file_id IN (
                SELECT f.id FROM files f
                WHERE f.lang IS NOT NULL
                  AND f.lang NOT IN ({string.Join(", ", inParams)})
            )",
            c => AddSupportedLanguageParameters(c, values.Count));
        try
        {
            BindSupportedLanguageParameterValues(cmd, values);
            var raw = cmd.ExecuteScalar();
            return raw is long l ? (int)l : (raw is int i ? i : 0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete symbol_references for files whose language is no longer graph-supported.
    /// Prevents stale call edges from surviving after a language is removed from graph support.
    /// グラフ非対応になった言語のファイルから symbol_references を削除する。
    /// グラフ対応が外された言語の古いコールエッジが残存するのを防止する。
    /// </summary>
    /// <param name="supportedLanguages">Currently supported languages / 現在対応している言語</param>
    /// <returns>Number of stale reference rows deleted / 削除された古い参照行数</returns>
    public int PurgeUnsupportedReferences(IReadOnlyCollection<string> supportedLanguages)
    {
        if (supportedLanguages.Count == 0)
            return 0;

        var values = SnapshotSupportedLanguages(supportedLanguages);
        var inParams = BuildSupportedLanguageParameterNames(values.Count);
        var cmd = RentCommand(
            $@"
            DELETE FROM symbol_references
            WHERE file_id IN (
                SELECT f.id FROM files f
                WHERE f.lang IS NOT NULL
                  AND f.lang NOT IN ({string.Join(", ", inParams)})
            )",
            c => AddSupportedLanguageParameters(c, values.Count));
        try
        {
            BindSupportedLanguageParameterValues(cmd, values);
            return cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete all reference graph rows while preserving files, chunks, symbols, and issues.
    /// files / chunks / symbols / issues は残し、参照グラフ行だけを全削除する。
    /// </summary>
    public int PurgeAllReferences()
    {
        var referenceCmd = RentCommand("DELETE FROM symbol_references", static _ => { });
        int deletedReferences;
        try
        {
            deletedReferences = referenceCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(referenceCmd);
        }

        var lineCmd = RentCommand("DELETE FROM reference_lines", static _ => { });
        try
        {
            lineCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(lineCmd);
        }

        return deletedReferences;
    }

    /// <summary>
    /// Get total counts for the summary output.
    /// サマリー出力用の合計件数を取得する。
    /// </summary>
    public (long files, long chunks, long symbols, long references) GetCounts()
    {
        long files = ExecuteScalar("SELECT COUNT(*) FROM files");
        long chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        long symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        long references = ExecuteScalar("SELECT COUNT(*) FROM symbol_references");
        return (files, chunks, symbols, references);
    }

    /// <summary>
    /// Optimize FTS5 index to merge internal b-tree segments for better query performance.
    /// FTS5インデックスを最適化して内部b-treeセグメントを統合し、クエリ性能を改善する。
    /// </summary>
    public void OptimizeFts()
    {
        Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('optimize')");
        SetMeta(FtsIncrementalWritesSinceOptimizeMetaKey, "0");
        SetMeta(FtsLastOptimizedAtMetaKey, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    public int GetFtsIncrementalWritesSinceOptimize()
    {
        var raw = GetMetaString(FtsIncrementalWritesSinceOptimizeMetaKey);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    public int RecordFtsIncrementalWrite()
    {
        var value = GetFtsIncrementalWritesSinceOptimize() + 1;
        SetMeta(FtsIncrementalWritesSinceOptimizeMetaKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return value;
    }

    public void MarkBatchInProgress() => SetMeta(DbContext.BatchInProgressMetaKey, "true");

    public void ClearBatchInProgress() => SetMeta(DbContext.BatchInProgressMetaKey, "false");

    public bool OptimizeFtsIfIncrementalWriteThresholdReached(int threshold = DefaultFtsOptimizeIncrementalWriteThreshold)
    {
        if (threshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        if (GetFtsIncrementalWritesSinceOptimize() < threshold)
            return false;

        OptimizeFts();
        return true;
    }

    // End-of-successful-index trust markers. The ready bits live in PRAGMA user_version so
    // that a reader can tell which subset of the index has been fully populated:
    //   bit 0 (GraphReadyFlag)  — symbol_references fully backfilled
    //   bit 1 (IssuesReadyFlag) — file_issues produced by ValidateContent
    //   bit 2 (FoldReadyFlag)   — name_folded columns populated for Unicode --exact (#86)
    // CLI and MCP full-scan indexing set graph + fold; CLI additionally sets issues (MCP
    // now persists file_issues too after bdbb2bd, so both can stamp it). The index runner
    // ClearReadyFlags() first so partial / aborted runs demote trust until a successful
    // end-of-run commit. Fold is only stamped after a full scan because a partial update
    // leaves legacy rows without folded values.
    // CLI / MCP 共に full-scan で graph + fold を立てる。fold は部分更新では立てない。
    public void MarkGraphReady() => SetReadyBit(DbContext.GraphReadyFlag);
    public void MarkIssuesReady() => SetReadyBit(DbContext.IssuesReadyFlag);

    /// <summary>
    /// Stamp FoldReadyFlag AND write the current <see cref="NameFold.Version"/> plus the
    /// runtime-sensitive <see cref="NameFold.Fingerprint"/> into `codeindex_meta`.
    /// Readers require the bit, a version match, and a fingerprint match before trusting
    /// folded columns, so both intentional fold changes and runtime ICU / invariant-casing
    /// drift degrade safely to NOCASE until `--rebuild`. Issue #97.
    ///
    /// Re-verifies <see cref="AllFoldedColumnsBackfilled"/> inside a BEGIN IMMEDIATE
    /// transaction so a concurrent writer cannot insert NULL-folded rows between the
    /// caller's pre-check and this stamp. Returns false (and writes nothing) when the
    /// re-verify fails, so callers can surface a friendly retry message instead of
    /// silently advertising fold-trust to readers. Issue #1535.
    /// FoldReady bit + fold_key_version + fold_key_fingerprint を書く。runtime drift を含む
    /// silent mismatch を防ぎ、ズレた場合は `--rebuild` まで NOCASE fallback に降格する。
    /// BEGIN IMMEDIATE で囲んだうえで再検証し、concurrent writer による NULL 行差し込みで
    /// fold_ready が嘘になるのを防ぐ。Issue #1535。
    /// </summary>
    /// <returns>True when the bit was actually stamped; false when re-verification failed.</returns>
    public bool MarkFoldReady(bool stampCurrentSymbolExtractorVersions = false)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            bool ownTransaction = !IsInTransaction();
            if (ownTransaction)
                Execute("BEGIN IMMEDIATE");
            try
            {
                if (stampCurrentSymbolExtractorVersions)
                    StampSymbolExtractorVersions();

                if (!AllFoldedColumnsBackfilledCore(
                        requireCurrentSymbolExtractorVersions: false,
                        requireCurrentFoldKeys: true))
                {
                    if (ownTransaction)
                    {
                        Execute("COMMIT");
                        ownTransaction = false;
                    }
                    return false;
                }

                ApplyReadyBitToUserVersion(DbContext.FoldReadyFlag, ownTransaction ? null : _activeTransaction);

                SetMeta("fold_key_version", NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SetMeta("fold_key_fingerprint", NameFold.Fingerprint());
                StampSymbolExtractorVersions();

                if (ownTransaction)
                {
                    Execute("COMMIT");
                    ownTransaction = false;
                }
                return true;
            }
            catch (Exception)
            {
                if (ownTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    public void StampSymbolExtractorVersions()
    {
        foreach (var lang in GetIndexedLanguages())
        {
            SetMeta(
                DbContext.GetSymbolExtractorVersionMetaKey(lang),
                SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Stamp the current C# symbol-name contract version. Readers and indexers use this to
    /// detect canonical-name upgrades such as operator/conversion/indexer renames.
    /// C# canonical symbol name 契約の current version を stamp する。
    /// </summary>
    public void MarkCSharpSymbolNameContractReady()
    {
        SetMeta(
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stamp the current SQL graph storage contract version. Readers use this to distinguish
    /// pre-fix SQL graph rows (stale call columns / symbol names) from rows rewritten by the
    /// current extractor/name-resolution contract.
    /// SQL graph 保存契約の current version を stamp する。
    /// </summary>
    public void MarkSqlGraphContractReady()
    {
        SetMeta(
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void ClearSqlGraphContractReady()
    {
        SetMeta(DbContext.SqlGraphContractVersionMetaKey, null);
    }

    /// <summary>
    /// Stamp the current authoritative version for hotspot family grouping semantics.
    /// Only fully authoritative DB states should call this; mixed legacy/current DBs must
    /// stay unstamped so readers degrade to conservative same-file counting.
    /// hotspots family grouping の current authoritative version を stamp する。
    /// </summary>
    public void MarkHotspotFamilyReady(string lang, string? markerFingerprint = null)
    {
        SetMeta(
            DbContext.GetHotspotFamilyVersionMetaKey(lang),
            DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), markerFingerprint);
        // Clear the superseded global keys so mixed-version DBs don't leave confusing stale metadata behind.
        // 廃止した global key を掃除し、混在 DB に紛らわしい古い metadata を残さない。
        SetMeta(DbContext.HotspotFamilyVersionMetaKey, null);
        SetMeta(DbContext.HotspotFamilyMarkerFingerprintMetaKey, null);
    }

    public void MarkHotspotFamilyMarkerFingerprintIncomplete(string lang, string? markerFingerprint)
    {
        SetMeta(
            DbContext.GetHotspotFamilyVersionMetaKey(lang),
            DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(
            DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang),
            DbContext.BuildIncompleteHotspotFamilyMarkerFingerprint(markerFingerprint));
        SetMeta(DbContext.HotspotFamilyVersionMetaKey, null);
        SetMeta(DbContext.HotspotFamilyMarkerFingerprintMetaKey, null);
    }

    /// <summary>
    /// Demote hotspot-family trust. Called at the start of any indexing run that may leave
    /// a mixed legacy/current symbol set so readers fall back conservatively unless the run
    /// completes and restamps the current version.
    /// hotspot-family trust を縮退させる。index 開始時に呼び、成功時だけ再 stamp する。
    /// </summary>
    public void ClearHotspotFamilyReady()
    {
        if (!TableExists("codeindex_meta"))
            return;

        SetMeta(DbContext.HotspotFamilyVersionMetaKey, null);
        SetMeta(DbContext.HotspotFamilyMarkerFingerprintMetaKey, null);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            SetMeta(DbContext.GetHotspotFamilyVersionMetaKey(lang), null);
            SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), null);
        }
    }

    /// <summary>
    /// Stamp the per-language metadata-target version once the writer's resolver has finished
    /// classifying every class-like row for that language. Readers consult this stamp before
    /// trusting `symbols.is_metadata_target`. Issue #435.
    /// 言語別 metadata-target version を stamp する。reader はこの stamp 一致時のみ
    /// `symbols.is_metadata_target` を信頼する。Issue #435。
    /// </summary>
    public void MarkMetadataTargetReady(string lang)
    {
        SetMeta(
            DbContext.GetMetadataTargetVersionMetaKey(lang),
            DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool TypeScriptAugmentationVersionMatchesCurrent()
    {
        return string.Equals(
            GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey),
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    public void ClearTypeScriptAugmentationReady()
    {
        SetMeta(DbContext.TypeScriptAugmentationVersionMetaKey, null);
    }

    /// <summary>
    /// Stamp the cdidx version string that wrote the most recent successful end-of-index
    /// pass. Readers compare this against their own binary version (and each persisted
    /// contract version) to surface forward-compatibility warnings when an older cdidx
    /// opens a DB last written by a newer cdidx. Issue #1515.
    /// 成功 index 末尾で書き込みを行った cdidx の version を stamp する。reader は自身の
    /// version と各 contract version と突き合わせて forward-compat 警告を出す。Issue #1515。
    /// </summary>
    public void WriteCdidxWriterVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;
        SetMeta(DbContext.CdidxWriterVersionMetaKey, version);
    }

    public void ClearLastFailedIndexRunMetadata()
    {
        SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunModeMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunStartedAtMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunDurationMsMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunFilesProcessedMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunFilesTotalMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunErrorCodeMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunReasonMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunProgressPersistedMetaKey, null);
        SetMeta(DbContext.LastFailedIndexRunRecoveryHintMetaKey, null);
    }

    /// <summary>
    /// Stamp unknown-extension scan coverage from the latest successful full-worktree scan.
    /// Stores the total count plus a bounded path sample so status callers can identify the
    /// first files that need a language mapping or ignore rule without unbounded metadata.
    /// 未知拡張子の scan coverage を保存する。件数と上限付き path sample を status で返す。
    /// </summary>
    public void WriteUnknownExtensionFileMetadata(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sample = JsonStringListCodec.TakeSerializableSample(
            paths,
            DbContext.UnknownExtensionFilePathSampleLimit);
        var classification = UnknownExtensionClassifier.Classify(paths);
        SetMeta(
            DbContext.UnknownExtensionFileCountMetaKey,
            paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(
            DbContext.UnknownExtensionFilePathsMetaKey,
            JsonStringListCodec.Serialize(sample));
        SetMeta(
            DbContext.UnknownExtensionFilesTruncatedMetaKey,
            (paths.Count > sample.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(
            DbContext.UnknownExtensionFilePathLimitMetaKey,
            DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(
            DbContext.UnknownExtensionExtensionCountsMetaKey,
            UnknownExtensionClassifier.SerializeCounts(classification.ExtensionCounts));
        SetMeta(
            DbContext.UnknownExtensionCategoryCountsMetaKey,
            UnknownExtensionClassifier.SerializeCounts(classification.CategoryCounts));
        SetMeta(
            DbContext.UnknownExtensionGroupsMetaKey,
            UnknownExtensionClassifier.SerializeGroups(classification.Groups));
    }

    /// <summary>
    /// Demote metadata-target trust for every known language. Called at the start of any
    /// indexing run that may leave the resolver output partially stale so readers fall back
    /// to the legacy heuristic until a successful run restamps the current version.
    /// metadata-target trust を全言語まとめて縮退させる。index 開始時に呼び、成功時のみ
    /// 再 stamp する。Issue #435。
    /// </summary>
    public void ClearMetadataTargetReady()
    {
        if (!TableExists("codeindex_meta"))
            return;

        SetMeta(DbContext.GetMetadataTargetVersionMetaKey("csharp"), null);
    }

    /// <summary>
    /// Recompute `symbols.is_metadata_target` for every C# class-like row from extractor-owned
    /// direct facts plus a fixed-point resolver for transitive `System.Attribute` inheritance.
    /// Out-of-repo bases whose name ends with `Attribute` (the BCL convention) remain a bounded
    /// resolver fallback. Non-target rows are written as 0 so reader switching does not confuse
    /// "no resolver pass yet" with "resolver decided not a target". Issue #3524.
    /// C# class-like 行の `is_metadata_target` を extractor 由来の直接 fact と transitive
    /// resolver から再計算する。リポ外で末尾が `Attribute` の base 型は bounded fallback として
    /// target 扱いを残す。target でない行は明示的に 0 で書き、reader で「未解決」と区別する。
    /// </summary>
    public void ResolveCSharpMetadataTargets()
    {
        var rows = LoadCSharpClassRows();
        if (rows.Count == 0)
            return;

        // Fully-qualified-name index: `Namespace.TypeName` -> ids. Used when the base type
        // in a signature is qualified (`: A.BaseAttr`) so we do not resolve against an
        // unrelated same-simple-name class in another namespace. A LIST is required here
        // because C# `partial class` can split a single logical type across multiple
        // rows (one row per declaration site): with a single-id map, whichever row was
        // inserted first wins and any sibling partial carrying the real `: Attribute`
        // base list is dropped, making metadata-target resolution file-order dependent.
        // Issue #435 codex review iter 2.
        // 完全修飾名 `Namespace.TypeName` -> ids の索引。`partial class` で同一 FQN が複数行に
        // 分割されても、どのファイルが先に読まれても解決が安定するように List で保持する。
        var qualifiedToIds = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        // Scope-aware simple-name index: (enclosing scope, simple name) -> ids. Unqualified
        // bases must resolve through the deriving class's own namespace / nesting chain so
        // a non-attribute impostor in an UNRELATED namespace does not falsely promote the
        // deriving class to `is_metadata_target=1` just because another namespace happens
        // to contain a same-named real attribute. A global simple-name bucket was the
        // earlier design and was rejected in #435 codex review iter 4 with a reproducible
        // false-positive: `A.BaseAttr : Attribute` + `B.BaseAttr : BaseService` + deriving
        // `namespace B { class FooAttribute : BaseAttr {} }` previously returned a false
        // metadata edge for `[Foo] class Svc {}`. Issue #435 codex review iter 4.
        // スコープ対応の単純名索引。(外側スコープ, 単純名) -> ids。非修飾基底は deriving の
        // 名前空間 / 入れ子チェーンを辿って解決し、無関係な名前空間に同名の本物 attribute が
        // 存在するだけで非 attribute impostor が `is_metadata_target=1` に昇格するのを防ぐ。
        var scopeNameToIds = new Dictionary<(string Scope, string Name), List<long>>();
        var rowScope = new Dictionary<long, string>();
        var rowFileId = new Dictionary<long, long>();
        var bases = new Dictionary<long, List<string>>();
        foreach (var row in rows)
        {
            foreach (var fq in EnumerateQualifiedKeys(row.QualifiedName, row.Name))
            {
                if (!qualifiedToIds.TryGetValue(fq, out var qbucket))
                {
                    qbucket = new List<long>();
                    qualifiedToIds[fq] = qbucket;
                }
                qbucket.Add(row.Id);
            }
            string scope = GetEnclosingScope(row.QualifiedName, row.Name);
            rowScope[row.Id] = scope;
            rowFileId[row.Id] = row.FileId;
            var scopeKey = (scope, row.Name);
            if (!scopeNameToIds.TryGetValue(scopeKey, out var sbucket))
            {
                sbucket = new List<long>();
                scopeNameToIds[scopeKey] = sbucket;
            }
            sbucket.Add(row.Id);
            bases[row.Id] = ParseCSharpBaseIdentifiers(row.Signature);
        }

        // Per-file import tables so unqualified bases that come from `using Namespace;` /
        // `using Alias = FQN;` directives resolve to the right in-repo class, and repo-wide
        // aggregated `global using` so C# 10+ global directives still widen every file's
        // lookup set. Aliases can also target a qualified type (`using AliasAttr = A.BaseAttr;`)
        // whose target itself lives in a sibling file. Issue #435 codex review iter 5.
        // ファイル別 import テーブル。非修飾基底が `using Namespace;` や `using Alias = FQN;` 経由
        // で別ファイルの実体に解決される C# の一般パターンをカバーする。`global using` は全ファイルで
        // 集約して、ファイルを跨ぐ拡張も拾う。Issue #435 codex review iter 5。
        var (perFileImports, globalImports) = LoadCSharpImportsByFile();

        var extractorTargets = rows
            .Where(row => row.ExtractorMetadataTarget)
            .Select(row => row.Id)
            .ToHashSet();
        var targets = new HashSet<long>(extractorTargets);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
            {
                if (targets.Contains(row.Id))
                    continue;
                FileImportSet? fileImports = null;
                if (rowFileId.TryGetValue(row.Id, out var fid) && perFileImports.TryGetValue(fid, out var perFile))
                    fileImports = perFile;
                if (IsMetadataTargetByBases(bases[row.Id], rowScope[row.Id], targets, scopeNameToIds, qualifiedToIds, fileImports, globalImports))
                {
                    targets.Add(row.Id);
                    changed = true;
                }
            }
        }

        using var txn = !IsInTransaction() ? BeginTransaction() : null;
        bool hasMetadataTargetSource = ColumnExists("symbols", "metadata_target_source");
        string updateSql = hasMetadataTargetSource
            ? "UPDATE symbols SET is_metadata_target = @flag, metadata_target_source = @source WHERE id = @id"
            : "UPDATE symbols SET is_metadata_target = @flag WHERE id = @id";
        var update = RentCommand(
            updateSql,
            c =>
            {
                c.Parameters.Add("@flag", SqliteType.Integer);
                if (hasMetadataTargetSource)
                    c.Parameters.Add("@source", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            if (_commandCache == null)
                update.Prepare();

            var pFlag = update.Parameters["@flag"];
            var pSource = hasMetadataTargetSource ? update.Parameters["@source"] : null;
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                bool target = targets.Contains(row.Id);
                pFlag.Value = target ? 1 : 0;
                if (pSource != null)
                {
                    pSource.Value = extractorTargets.Contains(row.Id)
                        ? SymbolRecord.MetadataTargetSourceExtractor
                        : target
                            ? SymbolRecord.MetadataTargetSourceResolver
                            : DBNull.Value;
                }
                pId.Value = row.Id;
                update.ExecuteNonQuery();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }
        txn?.Commit();
    }

    private List<CSharpClassRow> LoadCSharpClassRows()
    {
        var rows = new List<CSharpClassRow>();
        if (!ColumnExists("symbols", "signature") || !ColumnExists("symbols", "is_metadata_target"))
            return rows;
        bool hasQualified = ColumnExists("symbols", "container_qualified_name");
        bool hasMetadataTargetSource = ColumnExists("symbols", "metadata_target_source");

        string sql = hasQualified
            ? $@"SELECT s.id, s.file_id, s.name, s.signature, s.container_qualified_name,
                    s.is_metadata_target, {(hasMetadataTargetSource ? "s.metadata_target_source" : "NULL")}
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL"
            : $@"SELECT s.id, s.file_id, s.name, s.signature, NULL,
                    s.is_metadata_target, {(hasMetadataTargetSource ? "s.metadata_target_source" : "NULL")}
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL";
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                bool extractorMetadataTarget = !reader.IsDBNull(5)
                    && reader.GetInt64(5) == 1
                    && !reader.IsDBNull(6)
                    && string.Equals(reader.GetString(6), SymbolRecord.MetadataTargetSourceExtractor, StringComparison.Ordinal);
                rows.Add(new CSharpClassRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    extractorMetadataTarget));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return rows;
    }

    private sealed record CSharpClassRow(
        long Id,
        long FileId,
        string Name,
        string? Signature,
        string? QualifiedName,
        bool ExtractorMetadataTarget);

    // Per-file import set for C# unqualified-base resolution. `Namespaces` lists each
    // `using Foo.Bar;` target so `class X : Base` can probe `Foo.Bar.Base` in the qualified
    // index; `Aliases` maps `using Alias = Foo.Bar.Type;` directives so `class X : Alias`
    // resolves to `Foo.Bar.Type`. `using static Foo.Bar;` and `extern alias Foo;` are out
    // of scope — they do not introduce a plain namespace-prefix lookup that a C# base
    // clause would use. Issue #435 codex review iter 5.
    // C# 非修飾基底解決用のファイル別 import セット。`Namespaces` は `using Foo.Bar;` の集合。
    // `Aliases` は `using Alias = Foo.Bar.Type;` のエイリアス -> ターゲット写像。`using static`
    // と `extern alias` は base 句が引けない文脈なので対象外。Issue #435 codex review iter 5。
    private sealed class FileImportSet
    {
        public List<string> Namespaces { get; } = new();
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
    }

    // Load `symbols.kind='import'` rows for every C# file and partition each row into either
    // a namespace import or an alias import. `global using` directives (C# 10+) are aggregated
    // into a repo-wide set because they widen the import lookup in every file, even ones that
    // do not contain them literally. The split is driven by the stored signature — `using X =
    // Y.Z;` contains `=` before the terminating `;`, which distinguishes alias form from plain
    // namespace form even when both names tokenise as a single identifier.
    // `symbols.kind='import'` 行を C# ファイル別に読み、namespace 用 / alias 用に分ける。
    // `global using`（C# 10+）はリポジトリ全体に効くので、別途集約した集合として返す。判定は
    // 保存済み signature から行い、`=` があれば alias と認識する。
    private (Dictionary<long, FileImportSet> PerFile, FileImportSet Global) LoadCSharpImportsByFile()
    {
        var perFile = new Dictionary<long, FileImportSet>();
        var global = new FileImportSet();
        if (!TableExists("symbols"))
            return (perFile, global);

        const string sql = @"SELECT s.file_id, s.name, s.signature
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp' AND s.kind = 'import' AND s.name IS NOT NULL";
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                long fileId = reader.GetInt64(0);
                string rawName = reader.GetString(1);
                string? signature = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!perFile.TryGetValue(fileId, out var bag))
                {
                    bag = new FileImportSet();
                    perFile[fileId] = bag;
                }
                RegisterCSharpImport(bag, global, rawName, signature);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return (perFile, global);
    }

    private static void RegisterCSharpImport(FileImportSet perFile, FileImportSet global, string rawName, string? signature)
    {
        string name = rawName.Trim();
        if (name.Length == 0)
            return;
        // `extern alias X;` surfaces as an `import` row too (see SymbolExtractor). We skip
        // it — extern aliases map to assemblies, not to a type/namespace the writer has
        // indexed, and the qualified-name index is unaware of the alias identity.
        // `extern alias X;` も import 行として現れるがアセンブリ別名でしかなく resolver 側の
        // qualified 索引には載らないので対象外。
        if (signature != null && signature.IndexOf("extern", StringComparison.Ordinal) >= 0
            && CSharpExternAliasSignatureRegex.IsMatch(signature))
        {
            return;
        }
        bool isGlobal = signature != null
            && CSharpGlobalUsingSignatureRegex.IsMatch(signature);
        bool isStatic = signature != null
            && CSharpUsingStaticSignatureRegex.IsMatch(signature);
        // `using static Foo.Bar;` imports the static members of `Foo.Bar` into the file's
        // scope — NOT a namespace that a base clause `class X : Base` could pull from.
        // Drop it so we don't confuse the alias/namespace paths.
        // `using static` は静的メンバーを取り込むだけで base 句の解決経路には使えない。
        if (isStatic)
            return;
        string? aliasTarget = null;
        string? aliasName = null;
        if (signature != null)
        {
            // `@?\w+` so verbatim alias names (`using @AliasAttr = A.BaseAttr;`) are captured
            // just like the SymbolExtractor side; the leading `@` is stripped below before
            // the alias enters the per-file map.
            // SymbolExtractor 側と同じく verbatim 識別子も `@?\w+` で受け、下の正規化で
            // 先頭 `@` を剥がしてから alias map に載せる。
            var m = CSharpUsingAliasSignatureRegex.Match(signature);
            if (m.Success)
            {
                aliasName = m.Groups["alias"].Value.Trim();
                aliasTarget = m.Groups["target"].Value.Trim();
            }
        }
        if (aliasName != null && aliasTarget != null && aliasName.Length > 0 && aliasTarget.Length > 0)
        {
            // Normalize `global::` prefix off the alias target so the downstream qualified
            // lookup sees the same key shape (`A.BaseAttr`) regardless of source syntax.
            // Then strip the C# verbatim `@` prefix from each identifier segment in both
            // the alias name and target — `using @AliasAttr = @Foo.@Bar.BaseAttr;` must
            // resolve identically to `using AliasAttr = Foo.Bar.BaseAttr;` because the two
            // forms are semantically equivalent in C#. Issue #435 codex review iter 6.
            // alias の target 先頭の `global::` は剥がして qualified 索引のキー形に合わせる。
            // さらに alias 名・target の各 dotted segment 先頭の verbatim `@` も剥がし、
            // `using @AliasAttr = @Foo.@Bar.BaseAttr;` が非 verbatim 形と同じキーで解決されるよう
            // 整える（C# では両者は同義）。Issue #435 codex review iter 6.
            if (aliasTarget.StartsWith("global::", StringComparison.Ordinal))
                aliasTarget = aliasTarget.Substring("global::".Length);
            aliasName = StripCSharpVerbatimPrefixes(aliasName);
            aliasTarget = StripCSharpVerbatimPrefixes(aliasTarget);
            if (aliasName.Length == 0 || aliasTarget.Length == 0)
                return;
            perFile.Aliases[aliasName] = aliasTarget;
            if (isGlobal)
                global.Aliases[aliasName] = aliasTarget;
            return;
        }
        // Fall-through: plain `using Foo.Bar;`. `name` is captured as `Foo.Bar` by the
        // SymbolExtractor regex, so we can use it directly. Trailing `global::` can sneak
        // through in exotic files (`using global::System.Linq;`) — strip it for parity
        // with the alias path so every downstream probe sees one consistent prefix.
        // Strip the C# verbatim `@` prefix from each dotted segment too so `using @Foo.@Bar;`
        // resolves identically to `using Foo.Bar;` (semantically equivalent in C#).
        // Issue #435 codex review iter 6.
        // 通常の `using Foo.Bar;` は name 側に `Foo.Bar` が入っているのでそれを使う。
        // 稀な `using global::X;` も prefix を剥がして qualified 索引と揃える。さらに
        // `using @Foo.@Bar;` のような verbatim 表記も先頭 `@` を剥がし、非 verbatim 形と
        // 同じキーで解決されるよう整える。Issue #435 codex review iter 6.
        string ns = name;
        if (ns.StartsWith("global::", StringComparison.Ordinal))
            ns = ns.Substring("global::".Length);
        ns = StripCSharpVerbatimPrefixes(ns);
        if (ns.Length == 0)
            return;
        perFile.Namespaces.Add(ns);
        if (isGlobal)
            global.Namespaces.Add(ns);
    }

    // Strip the C# verbatim-identifier `@` prefix from each identifier segment of a
    // qualified name. Segment boundaries are the start of the string, every `.`, and
    // every `::` (the alias-qualifier boundary that produces `global::Foo`,
    // `Alias::Foo`, etc.). `@Foo.@Bar.BaseAttr` → `Foo.Bar.BaseAttr`;
    // `global::@Foo.@Bar.BaseAttr` → `global::Foo.Bar.BaseAttr`; `Foo.Bar` → unchanged.
    // Runs on the writer side so every qualified-index key and every scope/import entry
    // shares one canonical form regardless of whether the source used verbatim syntax.
    // The `@` escape is purely syntactic in C# (`@class` is the identifier `class`
    // escaping a keyword), so stripping it never changes identity. Issue #435 codex
    // review iter 6 + iter 7 (the `::` boundary was missing in iter 6 so
    // `global::@Foo.@Bar.BaseAttr` stayed as `global::@Foo.Bar.BaseAttr` and did not
    // match the canonical qualified index key).
    // 修飾名の各識別子セグメント先頭に付く C# verbatim 識別子 `@` を剥がす。セグメント境界は
    // 文字列の先頭、`.`、`::`（`global::Foo` や `Alias::Foo` を作る alias 修飾境界）。
    // `@Foo.@Bar.BaseAttr` → `Foo.Bar.BaseAttr`、`global::@Foo.@Bar.BaseAttr`
    // → `global::Foo.Bar.BaseAttr`、`Foo.Bar` → そのまま。書き込み側で正規化することで、
    // qualified 索引キーと scope / import エントリをソース表記に依らない単一の canonical 形に
    // 統一する。`@` エスケープは C# では純粋に構文上のものなので（`@class` は識別子
    // `class`）、剥がしても同一性は変わらない。Issue #435 codex review iter 6 + iter 7
    // （iter 6 は `::` 境界を処理していなかったため `global::@Foo.@Bar.BaseAttr` が
    // `global::@Foo.Bar.BaseAttr` のまま残り、canonical な qualified 索引キーと一致しなかった）。
    private static string StripCSharpVerbatimPrefixes(string qualified)
    {
        if (qualified.Length == 0 || qualified.IndexOf('@') < 0)
            return qualified;
        var sb = new System.Text.StringBuilder(qualified.Length);
        bool atBoundary = true;
        for (int i = 0; i < qualified.Length; i++)
        {
            char c = qualified[i];
            if (atBoundary && c == '@'
                && i + 1 < qualified.Length
                && IsCSharpIdentifierStartChar(qualified[i + 1]))
            {
                // Skip the verbatim prefix; the next iteration emits the escaped identifier.
                atBoundary = false;
                continue;
            }
            sb.Append(c);
            if (c == '.')
            {
                atBoundary = true;
            }
            else if (c == ':' && i + 1 < qualified.Length && qualified[i + 1] == ':')
            {
                sb.Append(':');
                i++;
                atBoundary = true;
            }
            else
            {
                atBoundary = false;
            }
        }
        return sb.Length == qualified.Length ? qualified : sb.ToString();
    }

    private static bool IsCSharpIdentifierStartChar(char c) =>
        c == '_' || char.IsLetter(c);

    // Yield every qualified-name variant that callers might write against this class:
    // `Namespace.TypeName`, `global::Namespace.TypeName`, and (for nested classes whose
    // `container_qualified_name` is itself `Outer.Inner.Name`) each dotted tail so
    // `class Foo : Outer.Inner.BaseAttr` can match. Issue #435 codex review.
    // 修飾名ルックアップで match させたい表記をすべて列挙する。`Namespace.TypeName`、
    // `global::Namespace.TypeName`、および container が `Outer.Inner.Name` のような入れ子のとき
    // `Inner.Name` のような dotted tail も入れる。
    private static IEnumerable<string> EnumerateQualifiedKeys(string? containerQualifiedName, string simpleName)
    {
        var container = containerQualifiedName?.Trim();
        if (string.IsNullOrEmpty(container))
            yield break;
        // container_qualified_name in our extractor already includes the simple type name
        // at the tail, e.g. `A.FooAttribute` for `namespace A { class FooAttribute { } }`.
        // Some callers may also reference the type via `global::A.FooAttribute`, so emit
        // both forms. Defensive check: if the tail segment does not match simpleName, also
        // append simpleName as an extra candidate so we still index a usable qualified key.
        // container_qualified_name は末尾に自身の単純名を含む想定（例: `A.FooAttribute`）。
        // `global::` 付きでも参照され得るため両形を yield する。末尾が simpleName と一致しない
        // 非想定 DB でも simpleName を補った候補を 1 つ追加で出し、ルックアップ漏れを防ぐ。
        string fq = container;
        int lastDot = fq.LastIndexOf('.');
        string tail = lastDot >= 0 ? fq.Substring(lastDot + 1) : fq;
        if (!string.Equals(tail, simpleName, StringComparison.Ordinal))
            fq = container + "." + simpleName;

        yield return fq;
        yield return "global::" + fq;
        // Also yield dotted suffixes so `Outer.Inner.Name` can match a base reference of
        // just `Inner.Name`. Skip the leaf-only `Name` form — that overlaps with the
        // simple-name map and we do not want qualified-base lookup to silently resolve
        // an unqualified match. / `Outer.Inner.Name` の末尾 `Inner.Name` のような表記を
        // qualified ルックアップで当てるために dotted suffix も yield する。`Name` 単独は
        // simple-name map 側と重複するので除外する。
        int searchFrom = 0;
        while (true)
        {
            int dot = fq.IndexOf('.', searchFrom);
            if (dot < 0) break;
            var suffix = fq.Substring(dot + 1);
            if (suffix.IndexOf('.') < 0) break; // leaf-only — skip
            yield return suffix;
            searchFrom = dot + 1;
        }
    }

    // Derive the deriving class's enclosing scope from its container_qualified_name.
    // For `namespace A.B { class Foo { } }` the QualifiedName is `A.B.Foo`, and stripping
    // the trailing simple name yields `A.B`. Nested types (`namespace A { class Outer {
    // class Inner { } } }` → `A.Outer.Inner`) yield `A.Outer`. Top-level non-namespaced
    // types yield `""`. A null / empty QualifiedName also yields `""`, which matches
    // the implicit "global" scope bucket populated in `ResolveCSharpMetadataTargets`.
    // Issue #435 codex review iter 4.
    // 非修飾基底解決で使う deriving の外側スコープを container_qualified_name から導く。
    // `namespace A.B { class Foo { } }` の QualifiedName `A.B.Foo` からは末尾の Foo を
    // 除いた `A.B` を返し、ネストした `A.Outer.Inner` は `A.Outer`、トップレベル型や
    // null / 空の場合は `""`（グローバルスコープ）を返す。
    private static string GetEnclosingScope(string? qualifiedName, string simpleName)
    {
        var fq = qualifiedName?.Trim();
        if (string.IsNullOrEmpty(fq))
            return string.Empty;
        int lastDot = fq.LastIndexOf('.');
        string tail = lastDot >= 0 ? fq.Substring(lastDot + 1) : fq;
        if (string.Equals(tail, simpleName, StringComparison.Ordinal))
            return lastDot >= 0 ? fq.Substring(0, lastDot) : string.Empty;
        // container_qualified_name does not end with the row's simple name (unexpected
        // shape from older extractors). Treat the whole container as the enclosing scope
        // so at least exact-same-scope matches still work; the chain walk will still
        // climb outward. / 想定外の container 形状では container 全体をスコープ扱いする。
        return fq;
    }

    private static bool IsMetadataTargetByBases(
        List<string> baseIdentifiers,
        string derivingScope,
        HashSet<long> resolvedTargets,
        Dictionary<(string Scope, string Name), List<long>> scopeNameToIds,
        Dictionary<string, List<long>> qualifiedToIds,
        FileImportSet? fileImports,
        FileImportSet? globalImports)
    {
        foreach (var rawBaseName in baseIdentifiers)
        {
            if (rawBaseName.Length == 0)
                continue;
            // Normalize verbatim `@` prefixes in the base identifier so `class Foo : @BaseAttr`
            // and `class Foo : @Bar.@BaseAttr` share the same lookup key with their
            // non-verbatim counterparts. Import maps are already normalized by
            // `RegisterCSharpImport`, so we only need to canonicalize the deriving side here.
            // Issue #435 codex review iter 6.
            // base 識別子側の verbatim `@` も剥がし、import map と揃える（import 側は
            // `RegisterCSharpImport` で正規化済み）。Issue #435 codex review iter 6.
            var baseName = StripCSharpVerbatimPrefixes(rawBaseName);
            if (baseName.Length == 0)
                continue;
            // Direct System.Attribute / Attribute reference / 直接 Attribute 派生
            if (baseName == "Attribute"
                || baseName == "System.Attribute"
                || baseName == "global::System.Attribute"
                || baseName == "global::Attribute")
                return true;

            // Split qualified vs unqualified. Qualified bases (containing `.` or `::`)
            // resolve against the fully-qualified index so we do not leak into unrelated
            // same-simple-name classes in another namespace. Unqualified bases resolve
            // against the deriving class's own scope chain (same namespace / nesting
            // chain only) — NOT against a global simple-name bucket, because that bucket
            // would false-match a real attribute in an unrelated namespace when the
            // deriving file has the same simple name for a non-attribute class. Issue
            // #435 codex review iter 4.
            // 修飾名（`.` または `::` を含む）は完全修飾索引で解決し、別名前空間の同名 class
            // に解決してしまうのを防ぐ。非修飾名は deriving 自身のスコープチェーン（同一
            // 名前空間 / 入れ子チェーン）のみで解決し、グローバル単純名索引は使わない。
            bool isQualified = baseName.IndexOf('.') >= 0 || baseName.IndexOf("::", StringComparison.Ordinal) >= 0;
            var head = baseName;
            int lastDot = head.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < head.Length)
                head = head.Substring(lastDot + 1);

            if (isQualified)
            {
                // Normalize `global::` prefix — always try both forms against the qualified index.
                // `global::` を剥がした形と元の両方で修飾索引を引く。
                var normalized = baseName.StartsWith("global::", StringComparison.Ordinal)
                    ? baseName.Substring("global::".Length)
                    : baseName;
                // Alias expansion for qualified bases: `using Alias = A.B;` followed by
                // `class Foo : Alias.C` must resolve to `A.B.C` per C# lookup rules. The
                // earlier unqualified alias path only handles `class Foo : Alias` — it
                // cannot see `Alias.C` because `Alias.C` was already routed into the
                // qualified branch by the `.` check. Without this expansion the resolver
                // silently drops every `class FooAttribute : Alias.MetaBase` pattern
                // where `MetaBase : Attribute` lives under the alias target namespace.
                // File-local aliases take precedence over global usings per C# rules.
                // Alias target strings in the import map are already canonicalized (no
                // `global::`, no verbatim `@`), so we only need to splice the first
                // segment of the qualified base with the alias target.
                // Issue #435 codex review iter 8.
                // 修飾基底の alias 展開: `using Alias = A.B;` の下で `class Foo : Alias.C` は
                // `A.B.C` に解決される。非修飾 alias 経路（上方）は `class Foo : Alias` しか
                // 扱えないため、この展開が無いと `class FooAttribute : Alias.MetaBase` のような
                // 実運用パターンで `MetaBase : Attribute` が同 repo にあっても edge が落ちる。
                // alias target は RegisterCSharpImport 時に canonical 化済みなので、qualified
                // の先頭セグメントを alias target に差し替えるだけで良い。Issue #435 iter 8。
                string? aliasExpanded = ExpandCSharpAliasQualifiedBase(normalized, fileImports)
                                      ?? ExpandCSharpAliasQualifiedBase(normalized, globalImports);
                // If the alias itself points to `System.Attribute` (e.g.
                // `using Sys = System; class Foo : Sys.Attribute`), honor the direct-attr rule.
                // alias 展開先が BCL `Attribute` そのものなら直接 attribute とみなす。
                if (aliasExpanded == "Attribute" || aliasExpanded == "System.Attribute")
                    return true;
                if (qualifiedToIds.TryGetValue(baseName, out var qids)
                    || qualifiedToIds.TryGetValue(normalized, out qids)
                    || (aliasExpanded != null && qualifiedToIds.TryGetValue(aliasExpanded, out qids)))
                {
                    bool anyResolved = false;
                    foreach (var qid in qids)
                    {
                        if (resolvedTargets.Contains(qid))
                        {
                            anyResolved = true;
                            break;
                        }
                    }
                    if (anyResolved)
                        return true;
                    // Matched specific qualified in-repo classes but none (yet) resolved.
                    // Wait for the next iteration instead of falling to the BCL heuristic —
                    // promoting it would contradict the user's explicit qualified reference.
                    // A list is needed here because `partial class` can split a single FQN
                    // across multiple rows; if only the declaration carrying `: Attribute`
                    // is the real target, we must still iterate to promote it.
                    // 修飾名で具体的な class 群に当たったが未確定。次回反復に委ねる。partial
                    // で同一 FQN が複数行に分かれている場合も、どれか 1 つでも target になれば
                    // この if で拾えるよう list で保持している。
                    continue;
                }
                // Qualified base did not match any in-repo class — treat as external and
                // fall through to the BCL suffix fallback below without consulting the
                // simple-name map (which could false-match an unrelated class).
                // 修飾名が repo 内で見つからない場合は外部基底として扱い、単純名索引は引かず
                // 末尾サフィックス規約のフォールバックに任せる。
                if (head.Length > "Attribute".Length && head.EndsWith("Attribute", StringComparison.Ordinal))
                    return true;
                continue;
            }

            // Scope-aware unqualified resolution: walk the deriving class's scope chain
            // from innermost outward, stopping at the first level that has a same-name
            // row. Only that bucket is consulted — we do NOT fall back to a global
            // simple-name bucket, because that would false-promote when a same-named
            // real attribute happens to live in an unrelated namespace. The chain walk
            // also naturally handles nested types (e.g. `Outer.Inner : Base` checks
            // `Outer` before `""`) and top-level types (scope starts at `""`). Issue
            // #435 codex review iter 4.
            // 非修飾基底の解決は deriving のスコープチェーンを内側から外側へ辿り、最初に
            // 同名行が見つかった階層のバケットだけで判定する。グローバル単純名へのフォール
            // バックは行わない（無関係な名前空間の本物 attribute で偽昇格するため）。
            List<long>? scopedIds = null;
            string? scope = derivingScope;
            while (scope != null)
            {
                if (scopeNameToIds.TryGetValue((scope, head), out var found))
                {
                    scopedIds = found;
                    break;
                }
                if (scope.Length == 0)
                    break;
                int lastDotInScope = scope.LastIndexOf('.');
                scope = lastDotInScope >= 0 ? scope.Substring(0, lastDotInScope) : string.Empty;
            }

            if (scopedIds != null)
            {
                bool anyResolved = false;
                foreach (var id in scopedIds)
                {
                    if (resolvedTargets.Contains(id))
                    {
                        anyResolved = true;
                        break;
                    }
                }
                if (anyResolved)
                    return true;
                // Same-scope in-repo class exists but is not (yet) a target — wait for
                // the next fixed-point iteration. Don't fall through to the BCL
                // heuristic because that would incorrectly promote a non-attribute
                // in-repo class that literally shadows the base name.
                // 同スコープに in-repo class があるなら BCL ヒューリスティックに落とさず、次回反復に委ねる。
                continue;
            }

            // Import-aware fallback: the deriving file may bring the base type into scope via
            // `using Namespace;` (plain namespace import) or `using Alias = FQN;` (alias
            // import). The C# compiler considers these before concluding a base is external,
            // and production codebases routinely split `A.BaseAttr : Attribute` and
            // `B.FooAttribute : BaseAttr` across sibling files with a `using A;` at the top.
            // Without this path, iter 4's strict same-scope rule false-negatives every such
            // file and emits zero metadata edges. Issue #435 codex review iter 5.
            // ファイルが持つ `using Namespace;` / `using Alias = FQN;` を経由した解決。C# の
            // 一般的な `using A; class FooAttribute : BaseAttr {}` パターンで、`A.BaseAttr :
            // Attribute` が別ファイルにある場合に、これが無いと iter 4 は false-negative になる。
            bool anyImportInRepoMatch = false;
            // 1. Alias imports: `using AliasAttr = A.BaseAttr;` → probe qualified index with
            //    the alias target. Alias matches take precedence over namespace imports per
            //    C# lookup rules.
            // 1. alias import: `using AliasAttr = A.BaseAttr;` は qualified 索引を target で引く。
            if (TryResolveAliasImport(head, fileImports, qualifiedToIds, resolvedTargets, out var aliasMatched, out var aliasResolved))
            {
                if (aliasResolved)
                    return true;
                if (aliasMatched)
                    anyImportInRepoMatch = true;
            }
            if (TryResolveAliasImport(head, globalImports, qualifiedToIds, resolvedTargets, out aliasMatched, out aliasResolved))
            {
                if (aliasResolved)
                    return true;
                if (aliasMatched)
                    anyImportInRepoMatch = true;
            }
            // 2. Namespace imports: for every `using Ns;` probe `Ns.head` in the qualified
            //    index. A single file often has several namespace imports; any one that hits
            //    an in-repo class is enough to stop the BCL suffix fallback from firing.
            // 2. namespace import: `using Ns;` ごとに `Ns.head` を qualified 索引で引く。
            if (TryResolveNamespaceImport(head, fileImports, qualifiedToIds, resolvedTargets, out var nsMatched, out var nsResolved))
            {
                if (nsResolved)
                    return true;
                if (nsMatched)
                    anyImportInRepoMatch = true;
            }
            if (TryResolveNamespaceImport(head, globalImports, qualifiedToIds, resolvedTargets, out nsMatched, out nsResolved))
            {
                if (nsResolved)
                    return true;
                if (nsMatched)
                    anyImportInRepoMatch = true;
            }

            if (anyImportInRepoMatch)
            {
                // An import resolved to a concrete in-repo class that is not (yet) a
                // target — wait for the next fixed-point iteration. Falling through to the
                // BCL suffix heuristic would contradict the user's explicit import and
                // false-promote when the imported class is genuinely not an Attribute.
                // import 経由で in-repo class には当たったが未確定。次回反復に委ねる。
                continue;
            }

            // No in-scope same-name row AND no import match — treat as external and use the
            // BCL suffix fallback. Intentionally does NOT consult a global simple-name
            // bucket; that was the iter 4 false-positive. / スコープチェーンにも import にも
            // 同名行が無ければ外部基底として扱い、末尾サフィックス規約のみにフォールバックする。
            if (head.Length > "Attribute".Length && head.EndsWith("Attribute", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Expand a qualified C# base name against `using Alias = Target;` entries so that
    // `Alias.C` and `Alias::C` both resolve to `Target.C`. The C# spec allows either
    // `.` (member access) or `::` (qualified-alias-member, §7.8) as the alias
    // separator for using-alias directives when the alias names a namespace.
    // Returns null when the first segment is not an alias in the given import set.
    // Alias targets are pre-canonicalized by `RegisterCSharpImport` (no `global::`,
    // no verbatim `@`); a leading `global::` in the stored target is still stripped
    // defensively for older migrations. The rest of the qualified name after the
    // alias separator is spliced with `.` so `Alias::Outer.Inner` collapses to
    // `Target.Outer.Inner` — that matches how `qualifiedToIds` keys are stored.
    // Issue #435 codex review iter 8 + iter 9 (`::` separator).
    // qualified 基底名を alias import で展開。`Alias.C` と `Alias::C` のいずれも
    // `Target.C` に書き換える。C# の仕様では using alias が名前空間を指す場合、
    // alias 区切りとして `.`（メンバ アクセス）または `::`（qualified-alias-member、
    // §7.8）が使える。先頭セグメントが alias でなければ null。alias target は登録時
    // に canonical 化済み（`global::` なし・`@` なし）だが、旧マイグレーション対応で
    // `global::` を剥がす。alias 区切り以降は `.` で繋ぎ直すので `Alias::Outer.Inner`
    // も `Target.Outer.Inner` に畳める — `qualifiedToIds` のキー形式に合わせる。
    // Issue #435 iter 8 + iter 9（`::` 区切り）。
    private static string? ExpandCSharpAliasQualifiedBase(string qualified, FileImportSet? imports)
    {
        if (imports == null)
            return null;
        if (qualified.Length == 0)
            return null;
        // Find the earliest alias separator: either `.` or `::`, whichever comes first.
        // alias 区切り（`.` または `::`）の先頭出現位置を採用する。
        int firstDot = qualified.IndexOf('.');
        int firstColonColon = qualified.IndexOf("::", StringComparison.Ordinal);
        int boundary;
        int sepLen;
        if (firstDot < 0 && firstColonColon < 0)
            return null;
        if (firstDot < 0)
        {
            boundary = firstColonColon;
            sepLen = 2;
        }
        else if (firstColonColon < 0)
        {
            boundary = firstDot;
            sepLen = 1;
        }
        else if (firstDot < firstColonColon)
        {
            boundary = firstDot;
            sepLen = 1;
        }
        else
        {
            boundary = firstColonColon;
            sepLen = 2;
        }
        if (boundary <= 0)
            return null;
        string prefix = qualified.Substring(0, boundary);
        if (!imports.Aliases.TryGetValue(prefix, out var target))
            return null;
        if (target.StartsWith("global::", StringComparison.Ordinal))
            target = target.Substring("global::".Length);
        if (target.Length == 0)
            return null;
        string suffix = qualified.Substring(boundary + sepLen);
        return suffix.Length == 0 ? target : target + "." + suffix;
    }

    private static bool TryResolveAliasImport(
        string head,
        FileImportSet? imports,
        Dictionary<string, List<long>> qualifiedToIds,
        HashSet<long> resolvedTargets,
        out bool matchedAnyInRepoClass,
        out bool resolvedToTarget)
    {
        matchedAnyInRepoClass = false;
        resolvedToTarget = false;
        if (imports == null)
            return false;
        if (!imports.Aliases.TryGetValue(head, out var target))
            return false;
        // Alias may point to BCL `Attribute` directly — honor the direct-attribute rule.
        // alias の先が BCL Attribute そのものなら直接 attribute とみなす。
        if (target == "System.Attribute" || target == "Attribute"
            || target == "global::System.Attribute" || target == "global::Attribute")
        {
            resolvedToTarget = true;
            return true;
        }
        if (target.StartsWith("global::", StringComparison.Ordinal))
            target = target.Substring("global::".Length);
        if (qualifiedToIds.TryGetValue(target, out var ids))
        {
            matchedAnyInRepoClass = true;
            foreach (var id in ids)
            {
                if (resolvedTargets.Contains(id))
                {
                    resolvedToTarget = true;
                    return true;
                }
            }
        }
        // Alias target did not match an in-repo class. If the target's simple-name tail
        // ends with `Attribute` we still trust the BCL convention for an external base.
        // alias 先が repo 内に無くても simple tail が `Attribute` で終わるなら BCL 規約で attribute 扱い。
        int lastDotInTarget = target.LastIndexOf('.');
        string tail = lastDotInTarget >= 0 ? target.Substring(lastDotInTarget + 1) : target;
        if (tail.Length > "Attribute".Length && tail.EndsWith("Attribute", StringComparison.Ordinal))
        {
            resolvedToTarget = true;
            return true;
        }
        return true;
    }

    private static bool TryResolveNamespaceImport(
        string head,
        FileImportSet? imports,
        Dictionary<string, List<long>> qualifiedToIds,
        HashSet<long> resolvedTargets,
        out bool matchedAnyInRepoClass,
        out bool resolvedToTarget)
    {
        matchedAnyInRepoClass = false;
        resolvedToTarget = false;
        if (imports == null)
            return false;
        bool any = false;
        foreach (var ns in imports.Namespaces)
        {
            if (ns.Length == 0)
                continue;
            var key = ns + "." + head;
            if (!qualifiedToIds.TryGetValue(key, out var ids))
                continue;
            any = true;
            matchedAnyInRepoClass = true;
            foreach (var id in ids)
            {
                if (resolvedTargets.Contains(id))
                {
                    resolvedToTarget = true;
                    return true;
                }
            }
        }
        return any;
    }

    /// <summary>
    /// Extract base-type head identifiers from a C# class signature, respecting generic depth
    /// so that `Foo<Bar, Baz> : IBase, IOther<Bar>` yields ["IBase", "IOther"]. Stops at the
    /// first `where` clause (generic constraints are not bases) and trims modifiers like
    /// `public sealed`.
    /// C# class signature から基底/インターフェース識別子の頭を抜き出す。`<...>` の depth を
    /// 数えて generic argument 内の `,` を区切りに誤認しないようにし、`where` 制約は除外する。
    /// </summary>
    internal static List<string> ParseCSharpBaseIdentifiers(string? signature)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(signature))
            return result;

        int colonIdx = FindBaseListColon(signature);
        if (colonIdx < 0)
            return result;

        int start = colonIdx + 1;
        int genericDepth = 0;
        var current = new System.Text.StringBuilder();
        for (int i = start; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                genericDepth++;
                current.Append(c);
                continue;
            }
            if (c == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                current.Append(c);
                continue;
            }
            if (c == '{')
                break;
            if (genericDepth == 0 && c == ',')
            {
                AddBaseIfPresent(result, current.ToString());
                current.Clear();
                continue;
            }
            // `where T : ...` ends the base list
            if (genericDepth == 0 && (c == 'w' || c == 'W'))
            {
                if (LooksLikeWhereKeyword(signature, i))
                {
                    AddBaseIfPresent(result, current.ToString());
                    current.Clear();
                    return result;
                }
            }
            current.Append(c);
        }
        AddBaseIfPresent(result, current.ToString());
        return result;
    }

    private static int FindBaseListColon(string signature)
    {
        int genericDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<') { genericDepth++; continue; }
            if (c == '>') { if (genericDepth > 0) genericDepth--; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { if (parenDepth > 0) parenDepth--; continue; }
            if (c == '{')
                return -1;
            // `class Foo<T> where T : IBar {}` has no base list — only a generic constraint.
            // If we reach a top-level `where` before finding `:`, treat that `:` as a
            // constraint separator, not a base list opener. Issue #435 codex review.
            // `class Foo<T> where T : IBar {}` のように base list を持たない場合、ここで遭遇する
            // `:` は generic constraint の区切りなので base list colon として採用しない。
            if (genericDepth == 0 && parenDepth == 0 && (c == 'w' || c == 'W')
                && LooksLikeWhereKeyword(signature, i))
            {
                return -1;
            }
            if (c == ':' && genericDepth == 0 && parenDepth == 0)
            {
                // Skip `::` namespace alias separator / `::` 名前空間エイリアスは除外
                if (i + 1 < signature.Length && signature[i + 1] == ':')
                {
                    i++;
                    continue;
                }
                if (i > 0 && signature[i - 1] == ':')
                    continue;
                return i;
            }
        }
        return -1;
    }

    private static bool LooksLikeWhereKeyword(string signature, int i)
    {
        if (i + 5 > signature.Length)
            return false;
        if (string.Compare(signature, i, "where", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (i > 0)
        {
            char prev = signature[i - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_')
                return false;
        }
        if (i + 5 < signature.Length)
        {
            char next = signature[i + 5];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        return true;
    }

    private static void AddBaseIfPresent(List<string> result, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return;
        // Take the head identifier (everything before `<` or whitespace) but preserve
        // any namespace prefix so the caller can treat `System.Attribute` directly.
        // `<` 以前と空白以前を頭とし、`System.Attribute` などの名前空間付きはそのまま残す。
        int cut = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '<' || char.IsWhiteSpace(c))
            {
                cut = i;
                break;
            }
        }
        var head = trimmed.Substring(0, cut);
        if (head.Length > 0)
            result.Add(head);
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Upsert a metadata key/value into `codeindex_meta`.
    /// codeindex_meta への key/value の upsert。
    /// </summary>
    public void SetMeta(string key, string? value)
    {
        if (!HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT set_meta_atomic");
            try
            {
                SetMetaCore(key, value);
                Execute("RELEASE SAVEPOINT set_meta_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaCore(key, value);
    }

    private void SetMetaCore(string key, string? value)
    {
        var cmd = RentCommand(
            @"INSERT INTO codeindex_meta (key, value) VALUES (@key, @value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            static c =>
            {
                c.Parameters.Add("@key", SqliteType.Text);
                c.Parameters.Add("@value", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@value"].Value = (object?)value ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private string? GetMetaString(string key)
    {
        var cmd = RentCommand(
            "SELECT value FROM codeindex_meta WHERE key = @key",
            static c => c.Parameters.Add("@key", SqliteType.Text));
        try
        {
            cmd.Parameters["@key"].Value = key;
            return cmd.ExecuteScalar() as string;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
    public void ClearReadyFlags() => Execute("PRAGMA user_version = 0");

    public bool HasMetaTable() => TableExists("codeindex_meta");

    private bool TableExists(string name)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            static c => c.Parameters.Add("@name", SqliteType.Text));
        try
        {
            cmd.Parameters["@name"].Value = name;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// True only when every existing row in symbols / symbol_references has a populated folded
    /// value for each source name that is itself non-NULL. Callers use this before stamping
    /// `FoldReadyFlag` on a full scan because the default incremental path skips unchanged files
    /// — their pre-#86 rows still carry NULL folded columns, so a naive stamp would flip readers
    /// onto the folded equality path and silently miss those legacy rows. Codex #86 review.
    /// full scan 成功時でも、incremental で skip された legacy 行が NULL のまま残っていれば
    /// fold-ready にしてはならない。stamp 前にこの実検証を通す。
    /// </summary>
    public bool AllFoldedColumnsBackfilled(
        bool requireCurrentSymbolExtractorVersions = false,
        bool requireCurrentFoldKeys = false)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(
        bool requireCurrentSymbolExtractorVersions,
        bool requireCurrentFoldKeys)
    {
        if (requireCurrentSymbolExtractorVersions && !SymbolExtractorVersionsMatchCurrent())
            return false;

        var cmd = RentCommand(
            @"
            SELECT
                (SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE container_name IS NOT NULL AND container_name_folded IS NULL)",
            static _ => { });
        try
        {
            var raw = cmd.ExecuteScalar();
            long missing = raw is long l ? l : (raw is int i ? i : 0);
            if (missing != 0)
                return false;
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return !requireCurrentFoldKeys || AllFoldedColumnValuesMatchCurrentFold();
    }

    public bool AllFoldedColumnValuesMatchCurrentFold()
    {
        var symbols = RentCommand(
            "SELECT name, name_folded FROM symbols WHERE name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = symbols.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var expected = NameFold.Fold(reader.GetString(0));
                var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return false;
            }
        }
        finally
        {
            ReleaseCommand(symbols);
        }

        var references = RentCommand(
            @"
                SELECT symbol_name, symbol_name_folded, container_name, container_name_folded
                FROM symbol_references
                WHERE symbol_name IS NOT NULL OR container_name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = references.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                {
                    var expected = NameFold.Fold(reader.GetString(0));
                    var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }

                if (!reader.IsDBNull(2))
                {
                    var expected = NameFold.Fold(reader.GetString(2));
                    var actual = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }
            }
        }
        finally
        {
            ReleaseCommand(references);
        }

        return true;
    }

    public bool AllFoldedColumnsBackfilled(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (requireCurrentSymbolExtractorLanguages.Count > 0
            && !SymbolExtractorVersionsMatchCurrent(requireCurrentSymbolExtractorLanguages))
        {
            return false;
        }

        return AllFoldedColumnsBackfilledCore(
            requireCurrentSymbolExtractorVersions: false,
            requireCurrentFoldKeys: false);
    }

    public bool SymbolExtractorVersionsMatchCurrent()
    {
        foreach (var lang in GetIndexedLanguages())
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    public bool SymbolExtractorVersionsMatchCurrent(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    private bool SymbolExtractorVersionMatchesCurrent(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return true;

        var stored = GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey(lang));
        if (stored == null)
            return true;

        var current = SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return stored == current;
    }

    /// <summary>
    /// Recompute persisted folded-name keys from existing symbol / reference rows without
    /// reparsing source files. This is used to upgrade legacy DBs (NULL folded columns) and
    /// to refresh stored keys after a future <see cref="NameFold.Version"/> bump.
    /// ソース再解析なしで既存行から folded key を再計算する。legacy DB の NULL 埋めと、
    /// 将来の <see cref="NameFold.Version"/> 変更時の key 再生成に使う。
    /// </summary>
    /// <param name="rewriteAll">
    /// When true, rewrite every non-null source name even if the folded column is already
    /// populated. Needed when the stored fold metadata does not match the current binary/runtime.
    /// true のとき、既に埋まっている folded 列も含めて全行再計算する（fold metadata 不一致時）。
    /// </param>
    /// <returns>Counts of symbol rows and reference rows rewritten.</returns>
    public (int Symbols, int SymbolReferences) BackfillFoldedColumns(
        bool rewriteAll = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foldBackfillPhase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll, cancellationToken);
        if (rewriteAll && foldBackfillPhase != "references")
        {
            SetMeta(FoldBackfillPhaseMetaKey, "references");
            SetMeta(FoldBackfillLastReferenceIdMetaKey, "0");
        }

        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rewriteAll)
            ClearFoldBackfillCheckpoint();

        return (symbols, symbolReferences);
    }

    public (int Symbols, int SymbolReferences) CountBackfillFoldedColumns(bool rewriteAll = false)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;

        var symbolsSql = rewriteAll && phase != "references"
            ? "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId"
            : rewriteAll
            ? "SELECT 0"
            : "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var symbolsUsesCheckpoint = rewriteAll && phase != "references";
        var symbols = RentCommand(
            symbolsSql,
            symbolsUsesCheckpoint
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });

        var referencesSql = rewriteAll
            ? @"SELECT COUNT(*)
                FROM symbol_references
                WHERE id > @lastReferenceId
                  AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)"
            : @"SELECT COUNT(*)
                FROM symbol_references
                WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                   OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var references = RentCommand(
            referencesSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });

        try
        {
            if (symbolsUsesCheckpoint)
                symbols.Parameters["@lastSymbolId"].Value = lastSymbolId;
            if (rewriteAll)
                references.Parameters["@lastReferenceId"].Value = phase == "references" ? lastReferenceId : 0;

            return (ToInt32Count(symbols.ExecuteScalar()), ToInt32Count(references.ExecuteScalar()));
        }
        finally
        {
            ReleaseCommand(references);
            ReleaseCommand(symbols);
        }
    }

    private static int ToInt32Count(object? value)
    {
        var count = value is long l ? l : (value is int i ? i : 0);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private int BackfillSymbolFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        if (phase == "references")
            return 0;

        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var rows = new List<(long Id, string Name)>();
        var selectSql = rewriteAll
            ? "SELECT id, name FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId ORDER BY id"
            : "SELECT id, name FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastSymbolId"].Value = lastSymbolId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            "UPDATE symbols SET name_folded = @folded WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@folded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pFolded = update.Parameters["@folded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pFolded.Value = (object?)NameFold.Fold(row.Name) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastSymbolIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private int BackfillReferenceFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;
        var rows = new List<(long Id, string? SymbolName, string? ContainerName)>();
        var selectSql = rewriteAll
            ? @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE id > @lastReferenceId
                      AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)
                    ORDER BY id"
            : @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                       OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastReferenceId"].Value = lastReferenceId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            @"UPDATE symbol_references
                               SET symbol_name_folded = @symbolNameFolded,
                                   container_name_folded = @containerNameFolded
                               WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@symbolNameFolded", SqliteType.Text);
                c.Parameters.Add("@containerNameFolded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pSymbolNameFolded = update.Parameters["@symbolNameFolded"];
            var pContainerNameFolded = update.Parameters["@containerNameFolded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pSymbolNameFolded.Value = (object?)NameFold.Fold(row.SymbolName) ?? DBNull.Value;
                pContainerNameFolded.Value = (object?)NameFold.Fold(row.ContainerName) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastReferenceIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private long GetFoldBackfillCheckpoint(string key)
    {
        var value = GetMetaString(key);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private void ClearFoldBackfillCheckpoint()
    {
        SetMeta(FoldBackfillPhaseMetaKey, null);
        SetMeta(FoldBackfillLastSymbolIdMetaKey, null);
        SetMeta(FoldBackfillLastReferenceIdMetaKey, null);
    }

    private static object FoldedNameDbValue(string? name, Dictionary<string, string?> cache)
    {
        if (name == null)
            return DBNull.Value;

        if (!cache.TryGetValue(name, out var folded))
        {
            folded = NameFold.Fold(name);
            cache[name] = folded;
        }

        return (object?)folded ?? DBNull.Value;
    }

    private static Dictionary<string, string?> CreateFoldedNameCache(int rowCount, int namesPerRow)
    {
        if (rowCount <= 0 || namesPerRow <= 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var capacity = rowCount > int.MaxValue / namesPerRow
            ? int.MaxValue
            : rowCount * namesPerRow;
        return new Dictionary<string, string?>(capacity, StringComparer.Ordinal);
    }

    private static StringBuilder CreateBatchSqlBuilder(int rowCount, int estimatedCharsPerRow)
    {
        const int BaseCapacity = 256;
        if (rowCount <= 0 || estimatedCharsPerRow <= 0)
            return new StringBuilder(BaseCapacity);

        var rowCapacity = rowCount > (int.MaxValue - BaseCapacity) / estimatedCharsPerRow
            ? int.MaxValue - BaseCapacity
            : rowCount * estimatedCharsPerRow;
        return new StringBuilder(BaseCapacity + rowCapacity);
    }

    private static int GetRowsPerInsertStatement(int columnCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));

        return Math.Max(1, Math.Min(BatchSize, SqliteDynamicSql.MaxSqlVariables / columnCount));
    }

    private void SetReadyBit(int flag)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            // The ready bits share a single PRAGMA user_version word, so two parallel
            // cdidx writers (e.g. CI + a local rebuild) can each read the same prior
            // value, OR in their own flag, and the slower writer's PRAGMA write clobbers
            // the faster writer's flag. Wrap the read-modify-write in BEGIN IMMEDIATE so
            // SQLite's reserved write lock serialises it across processes (issue #1513).
            // Use raw BEGIN/COMMIT instead of a provider-managed transaction object here:
            // PRAGMA user_version updates are connection-level metadata, and keeping this
            // path to plain SQL avoids provider transaction state leaking across pooled
            // connections under highly parallel release tests.
            bool ownTransaction = !IsInTransaction();
            bool beganTransaction = false;
            if (ownTransaction)
            {
                Execute("BEGIN IMMEDIATE");
                beganTransaction = true;
            }
            var transaction = ownTransaction ? null : _activeTransaction;
            try
            {
                ApplyReadyBitToUserVersion(flag, transaction);
                if (ownTransaction)
                {
                    Execute("COMMIT");
                    beganTransaction = false;
                }
            }
            catch (Exception)
            {
                if (beganTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    private void ApplyReadyBitToUserVersion(int flag, SqliteTransaction? transaction)
    {
        int current;
        using (var read = _conn.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA user_version";
            var raw = read.ExecuteScalar();
            current = raw is long l ? (int)l : (raw is int i ? i : 0);
        }

        int next = current | flag;
        if (next != current)
            Execute($"PRAGMA user_version = {next}", transaction);
    }

    private static IReadOnlyList<string> SnapshotSupportedLanguages(IReadOnlyCollection<string> supportedLanguages)
        => supportedLanguages as IReadOnlyList<string> ?? supportedLanguages.ToList();

    private static List<string> BuildSupportedLanguageParameterNames(int count)
    {
        SqliteDynamicSql.EnsureParameterBudget(count, "supported language filters");
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
            names.Add(SqliteDynamicSql.BuildParameterName("lang", i));
        return names;
    }

    private static void AddSupportedLanguageParameters(SqliteCommand cmd, int count)
    {
        SqliteDynamicSql.EnsureParameterBudget(count, "supported language filters");
        for (var i = 0; i < count; i++)
            cmd.Parameters.Add(SqliteDynamicSql.BuildParameterName("lang", i), SqliteType.Text);
    }

    private static void BindSupportedLanguageParameterValues(SqliteCommand cmd, IReadOnlyList<string> supportedLanguages)
    {
        for (var i = 0; i < supportedLanguages.Count; i++)
            cmd.Parameters[SqliteDynamicSql.BuildParameterName("lang", i)].Value = supportedLanguages[i];
    }

    private bool IsInTransaction() => _transactionDepth > 0;

    private long ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
