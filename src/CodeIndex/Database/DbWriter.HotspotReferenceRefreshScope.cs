using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal static Action? HotspotAggregateReadinessCheckedForTesting { get; set; }
    internal static Action? HotspotAggregateRefreshStatementExecutingForTesting { get; set; }
    internal static Action<IReadOnlyCollection<long>>? DeferredHotspotDirtyFilesForTesting { get; set; }
    private const string DeferredHotspotDirtyFilesTable = HotspotReferenceAggregateSql.DeferredDirtyFilesTableName;
    private DeferredHotspotReferenceAggregateRefreshScope? _deferredHotspotReferenceRefresh;
    private DeferredHotspotReferenceTransactionFrame? _activeDeferredHotspotReferenceTransactionFrame;

    /// <summary>
    /// Defer per-file hotspot aggregate maintenance until a whole indexing batch has
    /// finished. Transaction/savepoint checkpoints retain only dirty IDs from successful
    /// mutations; <see cref="DeferredHotspotReferenceAggregateRefreshScope.Complete"/>
    /// performs one set-based refresh and restores trust in the same transaction.
    /// file ごとの hotspot aggregate 更新を indexing batch の終端まで遅延する。
    /// transaction/savepoint checkpoint により成功 mutation の dirty ID だけを保持し、
    /// Complete が一度の set-based refresh と trust 復元を同一 transaction で行う。
    /// </summary>
    internal DeferredHotspotReferenceAggregateRefreshScope BeginDeferredHotspotReferenceAggregateRefresh()
    {
        if (_deferredHotspotReferenceRefresh != null)
        {
            throw new InvalidOperationException(
                "A deferred hotspot reference aggregate refresh scope is already active for this writer.");
        }

        var scope = new DeferredHotspotReferenceAggregateRefreshScope(this);
        _deferredHotspotReferenceRefresh = scope;
        return scope;
    }

    private DeferredHotspotReferenceTransactionFrame? BeginDeferredHotspotReferenceTransactionFrame()
    {
        var scope = _deferredHotspotReferenceRefresh;
        if (scope == null || scope.IsCompleting || scope.IsCompleted)
            return null;

        var frame = new DeferredHotspotReferenceTransactionFrame(
            this,
            scope,
            _activeDeferredHotspotReferenceTransactionFrame);
        _activeDeferredHotspotReferenceTransactionFrame = frame;
        return frame;
    }

    private void EndDeferredHotspotReferenceTransactionFrame(
        DeferredHotspotReferenceTransactionFrame? frame,
        bool committed)
    {
        if (frame == null || !frame.TryFinish())
            return;

        if (ReferenceEquals(_activeDeferredHotspotReferenceTransactionFrame, frame))
            _activeDeferredHotspotReferenceTransactionFrame = frame.Parent;

        if (committed)
            frame.MergeCommittedState();
    }

    private bool TryDeferHotspotReferenceRefresh(
        IReadOnlyCollection<long> fileIds,
        bool requireDirtyFileIds)
    {
        var scope = _deferredHotspotReferenceRefresh;
        if (scope == null || scope.IsCompleting || scope.IsCompleted)
            return false;

        var frame = _activeDeferredHotspotReferenceTransactionFrame
            ?? throw new InvalidOperationException(
                "Deferred hotspot reference aggregate mutations require an active tracked transaction.");
        frame.EnsureMutationStarted();
        if (requireDirtyFileIds || fileIds.Count > 0)
            frame.AddDirtyFileIds(fileIds);
        return true;
    }

    private bool TryStartDeferredHotspotReferenceMutation()
        => TryDeferHotspotReferenceRefresh(Array.Empty<long>(), requireDirtyFileIds: false);

    private void TrackDeferredHotspotReferenceFiles(IReadOnlyCollection<long> fileIds)
    {
        if (fileIds.Count > 0)
            _ = TryDeferHotspotReferenceRefresh(fileIds, requireDirtyFileIds: true);
    }

    private void RefreshDeferredHotspotReferenceCounts(
        IReadOnlyCollection<long> fileIds,
        bool restoreReady,
        CancellationToken cancellationToken)
    {
        using var transaction = BeginTransaction(cancellationToken, "complete deferred hotspot reference refresh");
        if (fileIds.Count > 0)
        {
            DeferredHotspotDirtyFilesForTesting?.Invoke(fileIds);
            cancellationToken.ThrowIfCancellationRequested();
            using (var create = _conn.CreateCommand())
            {
                create.Transaction = _activeTransaction;
                create.CommandText = $"""
                    CREATE TEMP TABLE IF NOT EXISTS {DeferredHotspotDirtyFilesTable} (
                        file_id INTEGER PRIMARY KEY
                    ) WITHOUT ROWID;
                    DELETE FROM {DeferredHotspotDirtyFilesTable};
                    """;
                create.ExecuteNonQuery();
            }

            var fileIdList = fileIds as IReadOnlyList<long> ?? fileIds.ToList();
            for (var offset = 0; offset < fileIdList.Count; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, fileIdList.Count - offset);
                SqliteDynamicSql.EnsureParameterBudget(batchCount, "deferred hotspot dirty file insert");
                using var insert = _conn.CreateCommand();
                insert.Transaction = _activeTransaction;
                var values = new string[batchCount];
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    var parameterName = SqliteDynamicSql.BuildParameterName("file_id", parameterIndex);
                    values[parameterIndex] = $"({parameterName})";
                    insert.Parameters.Add(parameterName, SqliteType.Integer).Value = fileIdList[offset + parameterIndex];
                }
                insert.CommandText = $"INSERT OR IGNORE INTO {DeferredHotspotDirtyFilesTable}(file_id) VALUES {string.Join(", ", values)}";
                insert.ExecuteNonQuery();
            }

            ExecuteDeferredHotspotReferenceRefresh(cancellationToken);
            using var drop = _conn.CreateCommand();
            drop.Transaction = _activeTransaction;
            drop.CommandText = $"DROP TABLE {DeferredHotspotDirtyFilesTable}";
            drop.ExecuteNonQuery();
        }

        if (restoreReady)
            ApplyReadyBitToUserVersion(DbContext.HotspotReferenceAggregateFlags, _activeTransaction);
        transaction.Commit();
    }

    private void ExecuteDeferredHotspotReferenceRefresh(CancellationToken cancellationToken)
    {
        var refreshCheckpoint = HotspotAggregateRefreshExecutingForTesting;
        if (refreshCheckpoint != null)
        {
            var invoked = false;
            _conn.CreateFunction("hotspot_refresh_test_checkpoint", () =>
            {
                if (!invoked)
                {
                    invoked = true;
                    refreshCheckpoint();
                }
                return 0;
            });
        }

        using var cmd = _conn.CreateCommand();
        cmd.Transaction = _activeTransaction;
        cmd.CommandText = HotspotReferenceAggregateSql.BuildDeferredRefreshSql(
            includeTestCheckpoint: refreshCheckpoint != null);
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        HotspotAggregateRefreshStatementExecutingForTesting?.Invoke();
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "Deferred hotspot reference aggregate refresh was interrupted.",
                ex,
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal sealed class DeferredHotspotReferenceAggregateRefreshScope : IDisposable
    {
        private readonly DbWriter _writer;
        private readonly HashSet<long> _dirtyFileIds = [];
        private bool _hasReadinessBaseline;
        private bool _restoreReady;
        private bool _completed;
        private bool _disposed;

        internal DeferredHotspotReferenceAggregateRefreshScope(DbWriter writer)
        {
            _writer = writer;
        }

        internal bool IsCompleting { get; private set; }
        internal bool IsCompleted => _completed;

        internal void MergeCommitted(
            IReadOnlyCollection<long> dirtyFileIds,
            bool hasReadinessBaseline,
            bool restoreReady)
        {
            if (_completed)
                return;

            if (!_hasReadinessBaseline && hasReadinessBaseline)
            {
                _hasReadinessBaseline = true;
                _restoreReady = restoreReady;
            }
            _dirtyFileIds.UnionWith(dirtyFileIds);
        }

        internal void Complete(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
                return;

            var activeFrame = _writer._activeDeferredHotspotReferenceTransactionFrame;
            if (activeFrame is { Parent: not null })
            {
                throw new InvalidOperationException(
                    "Deferred hotspot reference aggregate refresh cannot complete inside a nested transaction frame.");
            }

            var dirtyFileIds = new HashSet<long>(_dirtyFileIds);
            var hasReadinessBaseline = _hasReadinessBaseline;
            var restoreReady = _restoreReady;
            if (activeFrame != null)
            {
                dirtyFileIds.UnionWith(activeFrame.DirtyFileIds);
                if (!hasReadinessBaseline && activeFrame.HasReadinessBaseline)
                {
                    hasReadinessBaseline = true;
                    restoreReady = activeFrame.RestoreReady;
                }
            }

            if (!hasReadinessBaseline && dirtyFileIds.Count == 0)
            {
                _completed = true;
                activeFrame?.Consume();
                return;
            }

            IsCompleting = true;
            try
            {
                _writer.RefreshDeferredHotspotReferenceCounts(
                    dirtyFileIds,
                    restoreReady: hasReadinessBaseline && restoreReady,
                    cancellationToken);
                _completed = true;
                _dirtyFileIds.Clear();
                activeFrame?.Consume();
            }
            finally
            {
                IsCompleting = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (ReferenceEquals(_writer._deferredHotspotReferenceRefresh, this))
                _writer._deferredHotspotReferenceRefresh = null;
        }
    }

    private sealed class DeferredHotspotReferenceTransactionFrame
    {
        private readonly DbWriter _writer;
        private readonly DeferredHotspotReferenceAggregateRefreshScope _scope;
        private readonly HashSet<long> _dirtyFileIds = [];
        private bool _mutationStarted;
        private bool _hasReadinessBaseline;
        private bool _restoreReady;
        private bool _finished;
        private bool _consumed;

        internal DeferredHotspotReferenceTransactionFrame(
            DbWriter writer,
            DeferredHotspotReferenceAggregateRefreshScope scope,
            DeferredHotspotReferenceTransactionFrame? parent)
        {
            _writer = writer;
            _scope = scope;
            Parent = parent;
        }

        internal DeferredHotspotReferenceTransactionFrame? Parent { get; }
        internal IReadOnlyCollection<long> DirtyFileIds => _dirtyFileIds;
        internal bool HasReadinessBaseline => _hasReadinessBaseline;
        internal bool RestoreReady => _restoreReady;

        internal void EnsureMutationStarted()
        {
            if (_mutationStarted)
                return;

            var wasReady = _writer.ClearHotspotReferenceAggregateReadyCore();
            _mutationStarted = true;
            if (!_hasReadinessBaseline)
            {
                _hasReadinessBaseline = true;
                _restoreReady = wasReady;
            }
        }

        internal void AddDirtyFileIds(IReadOnlyCollection<long> fileIds)
            => _dirtyFileIds.UnionWith(fileIds);

        internal bool TryFinish()
        {
            if (_finished)
                return false;
            _finished = true;
            return true;
        }

        internal void MergeCommittedState()
        {
            if (_consumed)
                return;

            if (Parent != null)
            {
                Parent.MergeFrom(this);
                return;
            }

            _scope.MergeCommitted(_dirtyFileIds, _hasReadinessBaseline, _restoreReady);
        }

        internal void Consume()
        {
            _consumed = true;
            _dirtyFileIds.Clear();
        }

        private void MergeFrom(DeferredHotspotReferenceTransactionFrame child)
        {
            if (!_hasReadinessBaseline && child._hasReadinessBaseline)
            {
                _hasReadinessBaseline = true;
                _restoreReady = child._restoreReady;
            }
            _dirtyFileIds.UnionWith(child._dirtyFileIds);
        }
    }
}
