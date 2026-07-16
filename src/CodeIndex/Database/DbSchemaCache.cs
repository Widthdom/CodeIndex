using Microsoft.Data.Sqlite;
using System.Collections.Frozen;

namespace CodeIndex.Database;

/// <summary>
/// Process-level cache for SQLite schema discovery (`PRAGMA table_info`,
/// `PRAGMA index_list`, and `sqlite_master` table existence checks).
///
/// `DbReader` discovered these on every construction, so MCP sessions that
/// create or reuse `DbContext` instances for the same DB path were re-running
/// the same PRAGMAs per invocation (issues #1565 / #1701). Caching them at a
/// DB-path level pays the scan once for the live cache owners and serves
/// subsequent `DbReader` instances from immutable in-memory snapshots. Shared
/// state is removed after its final owning `DbContext` is disposed.
///
/// In-process migrations (`InitializeSchema`, `TryMigrateForRead`, `DropAll`)
/// call <see cref="Refresh"/> directly. To also catch DDL run through a
/// different connection — for example, a long-running MCP server while
/// `cdidx index` rebuilds the same DB out of band — every lookup first
/// consults SQLite's `PRAGMA schema_version`, which is bumped by SQLite on
/// every schema mutation regardless of which connection issued it. A change
/// auto-clears the cache before the lookup is served, so the next reader
/// observes the new shape without restart.
/// </summary>
public sealed class DbSchemaCache : IDisposable
{
    private sealed class SharedState
    {
        public readonly object Lock = new();
        public readonly Dictionary<string, FrozenSet<string>> Columns = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, FrozenSet<string>> Indexes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> TableExists = new(StringComparer.OrdinalIgnoreCase);
        public long? LastSchemaVersion;
        public bool VersionStale;
        public int ReferenceCount;
    }

    private static readonly object SharedStatesLock = new();
    private static readonly Dictionary<string, SharedState> SharedStates = new(StringComparer.Ordinal);

    private readonly SqliteConnection _connection;
    private readonly string? _sharedCacheKey;
    private readonly SharedState? _sharedState;
    private readonly object _lock = new();
    private readonly Dictionary<string, FrozenSet<string>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrozenSet<string>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _tableExists = new(StringComparer.OrdinalIgnoreCase);
    private long? _lastSchemaVersion;
    private bool _disposed;
    // Set when `EnsureFreshUnlocked` could not read `PRAGMA schema_version`
    // and we therefore cannot trust that entries loaded during this window are
    // current. The next successful version read must drop cached entries
    // unconditionally, regardless of whether the read returns a "new" or
    // "first-observation" value — otherwise stale data loaded between a failed
    // check and the next successful one would be version-stamped as current
    // and outlive the migration.
    // PRAGMA schema_version 取得に失敗した直後はキャッシュ整合性が信頼できないため、
    // この sentinel が立っている間に追加されたエントリは次の成功時に必ず破棄する。
    private bool _versionStale;

    public DbSchemaCache(SqliteConnection connection, string? sharedCacheKey = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (!string.IsNullOrWhiteSpace(sharedCacheKey))
        {
            _sharedCacheKey = sharedCacheKey;
            _sharedState = AcquireSharedState(sharedCacheKey);
        }
    }

    public FrozenSet<string> GetColumns(string tableName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            EnsureFreshUnlocked();
            if (_sharedState != null)
            {
                lock (_sharedState.Lock)
                {
                    if (_sharedState.Columns.TryGetValue(tableName, out var sharedCached))
                        return sharedCached;
                    var sharedFresh = LoadColumns(_connection, tableName);
                    _sharedState.Columns[tableName] = sharedFresh;
                    return sharedFresh;
                }
            }
            if (_columns.TryGetValue(tableName, out var cached))
                return cached;
            var fresh = LoadColumns(_connection, tableName);
            _columns[tableName] = fresh;
            return fresh;
        }
    }

    public FrozenSet<string> GetIndexes(string tableName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            EnsureFreshUnlocked();
            if (_sharedState != null)
            {
                lock (_sharedState.Lock)
                {
                    if (_sharedState.Indexes.TryGetValue(tableName, out var sharedCached))
                        return sharedCached;
                    var sharedFresh = LoadIndexes(_connection, tableName, HasTableUnlocked(tableName));
                    _sharedState.Indexes[tableName] = sharedFresh;
                    return sharedFresh;
                }
            }
            if (_indexes.TryGetValue(tableName, out var cached))
                return cached;
            var fresh = LoadIndexes(_connection, tableName, HasTableUnlocked(tableName));
            _indexes[tableName] = fresh;
            return fresh;
        }
    }

    public bool HasTable(string tableName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            EnsureFreshUnlocked();
            return HasTableUnlocked(tableName);
        }
    }

    /// <summary>
    /// Drop all cached schema state. Call after DDL that adds or removes
    /// tables, columns, or indexes so subsequent reads observe the new shape.
    /// </summary>
    public void Refresh()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ClearUnlocked();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_sharedState != null && _sharedCacheKey != null)
                ReleaseSharedState(_sharedCacheKey, _sharedState);
        }
    }

    private void ClearUnlocked()
    {
        if (_sharedState != null)
        {
            lock (_sharedState.Lock)
            {
                ClearSharedUnlocked(_sharedState);
            }
        }
        _columns.Clear();
        _indexes.Clear();
        _tableExists.Clear();
        _lastSchemaVersion = null;
        _versionStale = false;
    }

    /// <summary>
    /// Detect schema mutations performed through any connection to the same
    /// database file by comparing `PRAGMA schema_version`. SQLite bumps this
    /// counter on every CREATE / DROP / ALTER, so a long-lived MCP `DbContext`
    /// that out-of-process tooling has touched will reload cached entries on
    /// the next lookup instead of serving stale results until restart.
    /// </summary>
    private void EnsureFreshUnlocked()
    {
        long current;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA schema_version";
            var raw = cmd.ExecuteScalar();
            current = raw switch
            {
                long l => l,
                int i => i,
                _ => 0L,
            };
        }
        catch (SqliteException)
        {
            // Could not read schema_version (e.g. transient lock during DDL on
            // another connection). Drop whatever was cached and mark the
            // cache stale so the next successful version read clears anything
            // we load during this failure window — that load may have read the
            // DB mid-DDL and would otherwise get version-stamped as current.
            // PRAGMA schema_version 取得に失敗した場合は安全側に倒し、現エントリを破棄して
            // sentinel を立てる。失敗中に読み込んだ値は次回成功時に必ず破棄される。
            if (_sharedState != null)
            {
                lock (_sharedState.Lock)
                {
                    _sharedState.Columns.Clear();
                    _sharedState.Indexes.Clear();
                    _sharedState.TableExists.Clear();
                    _sharedState.VersionStale = true;
                }
            }
            else
            {
                _columns.Clear();
                _indexes.Clear();
                _tableExists.Clear();
                _versionStale = true;
            }
            return;
        }

        if (_sharedState != null)
        {
            lock (_sharedState.Lock)
            {
                EnsureSharedFreshUnlocked(_sharedState, current);
            }
            return;
        }

        // Always clear if the prior failure path set the stale sentinel, even
        // when the version equals the last known value — entries cached after
        // the failed read carry no version guarantee at all.
        if (_versionStale)
        {
            _columns.Clear();
            _indexes.Clear();
            _tableExists.Clear();
            _lastSchemaVersion = current;
            _versionStale = false;
            return;
        }

        if (_lastSchemaVersion is null)
        {
            _lastSchemaVersion = current;
            return;
        }
        if (_lastSchemaVersion.Value == current)
            return;

        _columns.Clear();
        _indexes.Clear();
        _tableExists.Clear();
        _lastSchemaVersion = current;
    }

    /// <summary>
    /// Test seam: simulates the post-failed-`PRAGMA schema_version` state by
    /// pretending the cache holds entries that were populated during a
    /// failure window. Production code never calls this; only the regression
    /// test for the stale-sentinel path uses it.
    /// </summary>
    internal void MarkVersionStaleForTest()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_sharedState != null)
            {
                lock (_sharedState.Lock)
                {
                    _sharedState.VersionStale = true;
                }
                return;
            }
            _versionStale = true;
        }
    }

    private bool HasTableUnlocked(string tableName)
    {
        if (_sharedState != null)
        {
            lock (_sharedState.Lock)
            {
                if (_sharedState.TableExists.TryGetValue(tableName, out var sharedCached))
                    return sharedCached;
                var sharedExists = QueryHasTable(_connection, tableName);
                _sharedState.TableExists[tableName] = sharedExists;
                return sharedExists;
            }
        }
        if (_tableExists.TryGetValue(tableName, out var cached))
            return cached;
        var exists = QueryHasTable(_connection, tableName);
        _tableExists[tableName] = exists;
        return exists;
    }

    internal static FrozenSet<string> LoadColumns(SqliteConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            columns.Add(reader.GetString(1));
        return columns.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static FrozenSet<string> LoadIndexes(SqliteConnection conn, string tableName, bool tableExists)
    {
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!tableExists)
            return indexes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.IndexListPragmaSql(tableName);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (!reader.IsDBNull(1))
                indexes.Add(reader.GetString(1));
        }
        return indexes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool QueryHasTable(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name";
        SqliteCommandPolicy.Add(cmd, "@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static void EnsureSharedFreshUnlocked(SharedState state, long current)
    {
        if (state.VersionStale)
        {
            state.Columns.Clear();
            state.Indexes.Clear();
            state.TableExists.Clear();
            state.LastSchemaVersion = current;
            state.VersionStale = false;
            return;
        }

        if (state.LastSchemaVersion is null)
        {
            state.LastSchemaVersion = current;
            return;
        }

        if (state.LastSchemaVersion.Value == current)
            return;

        state.Columns.Clear();
        state.Indexes.Clear();
        state.TableExists.Clear();
        state.LastSchemaVersion = current;
    }

    private static void ClearSharedUnlocked(SharedState state)
    {
        state.Columns.Clear();
        state.Indexes.Clear();
        state.TableExists.Clear();
        state.LastSchemaVersion = null;
        state.VersionStale = false;
    }

    private static SharedState AcquireSharedState(string key)
    {
        lock (SharedStatesLock)
        {
            if (!SharedStates.TryGetValue(key, out var state))
            {
                state = new SharedState();
                SharedStates.Add(key, state);
            }

            checked
            {
                state.ReferenceCount++;
            }
            return state;
        }
    }

    private static void ReleaseSharedState(string key, SharedState state)
    {
        lock (SharedStatesLock)
        {
            state.ReferenceCount--;
            if (state.ReferenceCount != 0)
                return;

            if (SharedStates.TryGetValue(key, out var current) && ReferenceEquals(current, state))
                SharedStates.Remove(key);

            lock (state.Lock)
            {
                ClearSharedUnlocked(state);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbSchemaCache));
    }

    internal static int SharedStateCountForTest
    {
        get
        {
            lock (SharedStatesLock)
            {
                return SharedStates.Count;
            }
        }
    }
}
