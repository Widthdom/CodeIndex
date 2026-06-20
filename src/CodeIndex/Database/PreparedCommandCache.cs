using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CodeIndex.Database;

/// <summary>
/// Bounded LRU cache of prepared <see cref="SqliteCommand"/> instances keyed by SQL text.
/// Lets hot read/write paths reuse one parsed/planned statement across calls instead of
/// constructing and disposing a fresh command per call. Issue #1566.
/// SQL テキストをキーにしたサイズ制限付き LRU の prepared <see cref="SqliteCommand"/> キャッシュ。
/// ホットな read/write パスで毎回 command を作り直す parse/plan コストを除き、index 実行時の
/// 統計的なオーバーヘッドを抑える。Issue #1566.
/// </summary>
/// <remarks>
/// Not thread-safe. SQLite connections (and the commands prepared against them) are not
/// thread-safe either, so callers should keep one cache per <see cref="SqliteConnection"/>
/// and serialize access through the owning <see cref="DbContext"/>.
/// スレッドセーフではない。SQLite の connection / command 自体がスレッドセーフでないため、
/// 1 connection あたり 1 キャッシュとし、DbContext 側で利用を直列化する想定。
/// </remarks>
internal sealed class PreparedCommandCache : IDisposable
{
    internal const int DefaultCapacity = 32;
    internal const int MaxCapacity = 512;
    internal const string CapacityEnvironmentVariable = "CDIDX_PREPARED_COMMAND_CACHE_CAPACITY";

    private readonly SqliteConnection _connection;
    private readonly int _capacity;
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private long _hitCount;
    private long _missCount;
    private long _evictionCount;
    private bool _disposed;

    public PreparedCommandCache(SqliteConnection connection, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _connection = connection;
        _capacity = capacity;
    }

    public int Count => _lru.Count;
    public int Capacity => _capacity;
    public long HitCount => _hitCount;
    public long MissCount => _missCount;
    public long EvictionCount => _evictionCount;

    internal static int ReadCapacityFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(CapacityEnvironmentVariable);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed <= MaxCapacity
                ? parsed
                : DefaultCapacity;
    }

    internal PreparedCommandCacheDiagnostics GetDiagnostics()
        => new(Count, Capacity, HitCount, MissCount, EvictionCount);

    /// <summary>
    /// Return a prepared <see cref="SqliteCommand"/> for <paramref name="sql"/>. On a miss
    /// the cache creates the command, invokes <paramref name="configureSchema"/> to bind
    /// typed parameter placeholders, and calls <see cref="SqliteCommand.Prepare"/>. The
    /// caller is responsible for assigning parameter values and (if a transaction is
    /// active on the connection) <see cref="SqliteCommand.Transaction"/> before executing.
    /// SQL に対応する prepared command を返す。未キャッシュ時のみ configureSchema が呼ばれ、
    /// 型付きパラメータを add してから <see cref="SqliteCommand.Prepare"/> される。値の代入
    /// と Transaction の同期は呼び出し側で行う。
    /// </summary>
    public SqliteCommand GetOrAdd(string sql, Action<SqliteCommand> configureSchema)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        ArgumentNullException.ThrowIfNull(configureSchema);
        if (_disposed)
            throw new ObjectDisposedException(nameof(PreparedCommandCache));

        if (_map.TryGetValue(sql, out var existing))
        {
            // LRU touch: move to front so least-recently-used falls out of the tail.
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            _hitCount++;
            return existing.Value.Command;
        }

        _missCount++;
        var cmd = _connection.CreateCommand();
        try
        {
            cmd.CommandText = sql;
            configureSchema(cmd);
            cmd.Prepare();
        }
        catch (Exception)
        {
            cmd.Dispose();
            throw;
        }

        var node = new LinkedListNode<Entry>(new Entry(sql, cmd));
        _lru.AddFirst(node);
        _map[sql] = node;

        if (_lru.Count > _capacity)
        {
            var tail = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(tail.Value.Sql);
            _evictionCount++;
            tail.Value.Command.Dispose();
        }

        return cmd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _lru)
            node.Command.Dispose();
        _lru.Clear();
        _map.Clear();
    }

    private sealed class Entry
    {
        public Entry(string sql, SqliteCommand command)
        {
            Sql = sql;
            Command = command;
        }

        public string Sql { get; }
        public SqliteCommand Command { get; }
    }

    internal sealed record PreparedCommandCacheDiagnostics(
        int Count,
        int Capacity,
        long HitCount,
        long MissCount,
        long EvictionCount);
}
