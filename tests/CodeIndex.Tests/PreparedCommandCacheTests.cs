using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for <see cref="PreparedCommandCache"/> and its integration with
/// <see cref="DbWriter"/> on hot per-file paths. Issue #1566.
/// <see cref="PreparedCommandCache"/> と <see cref="DbWriter"/> のホットパス
/// 統合テスト。Issue #1566.
/// </summary>
[Collection("SQLite pool sensitive")]
public class PreparedCommandCacheTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;

    public PreparedCommandCacheTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prepcache_test_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public void GetOrAdd_ReturnsSameCommandForSameSql()
    {
        using var cache = new PreparedCommandCache(_db.Connection);

        var first = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        var second = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => throw new InvalidOperationException("configureSchema must not be called on a cache hit"));

        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrAdd_DistinctSqlAddsDistinctCommands()
    {
        using var cache = new PreparedCommandCache(_db.Connection);

        var a = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        var b = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE lang = @lang",
            c => c.Parameters.Add("@lang", SqliteType.Text));

        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrAdd_EvictsLeastRecentlyUsedWhenOverCapacity()
    {
        using var cache = new PreparedCommandCache(_db.Connection, capacity: 2);

        var first = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE lang = @lang",
            c => c.Parameters.Add("@lang", SqliteType.Text));
        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE size = @size",
            c => c.Parameters.Add("@size", SqliteType.Integer));

        Assert.Equal(2, cache.Count);

        // Re-requesting the first SQL must rebuild (it was evicted as LRU tail).
        // 最も古いエントリは evict されているので、再要求時は別 instance になる。
        var rebuilt = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        Assert.NotSame(first, rebuilt);
    }

    [Fact]
    public void GetOrAdd_TouchOnHitDelaysEviction()
    {
        using var cache = new PreparedCommandCache(_db.Connection, capacity: 2);

        var first = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE lang = @lang",
            c => c.Parameters.Add("@lang", SqliteType.Text));

        // Touch the first entry so it becomes MRU; the lang entry must be evicted next.
        // first を touch して MRU に戻し、次は lang 側が evict されることを確認。
        var firstAgain = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => throw new InvalidOperationException("cache hit should not re-configure"));
        Assert.Same(first, firstAgain);

        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE size = @size",
            c => c.Parameters.Add("@size", SqliteType.Integer));

        var firstStillCached = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => throw new InvalidOperationException("first must still be cached after touch"));
        Assert.Same(first, firstStillCached);
    }

    [Fact]
    public void Dispose_ClearsCacheAndRejectsFurtherCalls()
    {
        var cache = new PreparedCommandCache(_db.Connection);

        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        Assert.Equal(1, cache.Count);

        cache.Dispose();

        Assert.Equal(0, cache.Count);
        Assert.Throws<ObjectDisposedException>(() =>
            cache.GetOrAdd("SELECT 1", c => { }));

        // Idempotent dispose: a second call must not throw.
        // Dispose は冪等。2 度目の呼び出しでも例外を投げない。
        cache.Dispose();
    }

    [Fact]
    public void GetOrAdd_RejectsInvalidArguments()
    {
        using var cache = new PreparedCommandCache(_db.Connection);

        Assert.Throws<ArgumentException>(() => cache.GetOrAdd("", c => { }));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd("SELECT 1", null!));
    }

    [Fact]
    public void Ctor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PreparedCommandCache(_db.Connection, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PreparedCommandCache(_db.Connection, capacity: -1));
    }

    [Fact]
    public void DbWriter_WithCache_ReusesCommandsAcrossUpsertCalls()
    {
        // The cache-aware constructor must lease the same SqliteCommand across
        // consecutive UpsertFile / GetUnchangedFileId calls so per-file paths
        // pay the parse/plan cost once.
        // cache 付きコンストラクタは、ファイル単位のホットパスで同一 SqliteCommand を
        // 借り続けるべき。
        var writer = new DbWriter(_db);
        var file = new FileRecord
        {
            Path = "src/a.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Checksum = "x",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var id1 = writer.UpsertFile(file);

        var file2 = new FileRecord
        {
            Path = "src/b.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Checksum = "y",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var id2 = writer.UpsertFile(file2);

        Assert.NotEqual(id1, id2);

        // The cache should hold prepared commands for the hot per-file SQLs.
        // ホットパス SQL に対応する prepared command が cache に積まれている。
        Assert.True(_db.PreparedCommands.Count > 0);
    }

    [Fact]
    public void DbWriter_WithCache_SurvivesTransactionBoundary()
    {
        // After an outer transaction commits, the cached command's Transaction
        // would otherwise point at the disposed SqliteTransaction. Re-leasing
        // must re-bind to the connection's current state so the next execute
        // does not throw TransactionConnectionMismatch.
        // 外部 transaction commit 後、cached command の Transaction は破棄済みを指す。
        // 借り直し時に再 bind して mismatch を起こさないことを確認する。
        var writer = new DbWriter(_db);

        using (var txn = writer.BeginTransaction())
        {
            writer.UpsertFile(new FileRecord
            {
                Path = "src/inside_txn.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Checksum = "c1",
                Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            txn.Commit();
        }

        // Subsequent call outside any transaction must still work.
        // 外側 transaction 終了後の呼び出しも例外なく成功する。
        var id = writer.UpsertFile(new FileRecord
        {
            Path = "src/outside_txn.py",
            Lang = "python",
            Size = 2,
            Lines = 1,
            Checksum = "c2",
            Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.True(id > 0);

        // And a fresh transaction afterward must also work — the cached command
        // must rebind to the new transaction.
        // その後の新規 transaction 内呼び出しも成功する。
        using (var txn = writer.BeginTransaction())
        {
            Assert.True(writer.HasFileAtPath("src/inside_txn.py"));
            txn.Commit();
        }
    }

    [Fact]
    public void DbWriter_WithCache_NestedSavepointStillBindsToOuterTransaction()
    {
        // Microsoft.Data.Sqlite does not create a new SqliteTransaction for SAVEPOINTs,
        // so the cached command's Transaction must continue pointing at the outermost
        // SqliteTransaction across nested BeginTransaction()s. Without this invariant,
        // re-leasing a cached command inside a nested savepoint would either null out
        // the txn (after the inner scope disposes) or mismatch the outer txn.
        // ネストされた BeginTransaction (SAVEPOINT) でも cached command の Transaction は
        // 最外 SqliteTransaction に紐付き続けるべき。
        var writer = new DbWriter(_db);

        using var outerTxn = writer.BeginTransaction();
        writer.UpsertFile(new FileRecord
        {
            Path = "src/outer.py", Lang = "python", Size = 1, Lines = 1, Checksum = "o",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using (var innerTxn = writer.BeginTransaction())
        {
            // Inner savepoint scope: re-lease the same cached UpsertFile command.
            // インナー savepoint 内で同じ cached command を再借用する。
            writer.UpsertFile(new FileRecord
            {
                Path = "src/inner.py", Lang = "python", Size = 1, Lines = 1, Checksum = "i",
                Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            Assert.True(writer.HasFileAtPath("src/inner.py"));
            innerTxn.Commit();
        }

        // After the inner savepoint releases, outer transaction is still live.
        // A subsequent cached-command lease must still bind to the outer txn.
        // インナー savepoint 解放後も outer transaction は活きており、cached command の
        // 再借用は outer txn にバインドされる。
        writer.UpsertFile(new FileRecord
        {
            Path = "src/outer2.py", Lang = "python", Size = 1, Lines = 1, Checksum = "o2",
            Modified = new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc),
        });
        outerTxn.Commit();

        Assert.True(writer.HasFileAtPath("src/outer.py"));
        Assert.True(writer.HasFileAtPath("src/inner.py"));
        Assert.True(writer.HasFileAtPath("src/outer2.py"));
    }

    [Fact]
    public void DbWriter_WithCache_GetUnchangedFileIdReusesCacheAcrossFiles()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        writer.UpsertFile(new FileRecord
        {
            Path = "src/x.py", Lang = "python", Size = 1, Lines = 1, Checksum = "k1",
            Modified = modified,
        });
        writer.UpsertFile(new FileRecord
        {
            Path = "src/y.py", Lang = "python", Size = 1, Lines = 1, Checksum = "k2",
            Modified = modified,
        });

        // Same SELECT command must be reused across distinct paths.
        // 異なる path に対しても SELECT command が再利用される。
        Assert.NotNull(writer.GetUnchangedFileId("src/x.py", modified, "k1"));
        Assert.NotNull(writer.GetUnchangedFileId("src/y.py", modified, "k2"));
        Assert.Null(writer.GetUnchangedFileId("src/missing.py", modified, "k3"));
    }

    [Fact]
    public void DbWriter_WithCache_GetUnchangedFileIdTouchUpdatesTimestamp()
    {
        // The slow-path UPDATE inside GetUnchangedFileId is now deferred until
        // after the SELECT reader is closed, so the cached SELECT command can
        // be reused. Confirm the touch still persists the new timestamp.
        // GetUnchangedFileId の slow-path UPDATE は SELECT reader を閉じた後に発行
        // されるよう変更したため、cached SELECT command の再利用と timestamp 更新が
        // 両立することを確認する。
        var writer = new DbWriter(_db);
        var initial = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var touched = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        writer.UpsertFile(new FileRecord
        {
            Path = "src/touched.py", Lang = "python", Size = 1, Lines = 1,
            Checksum = "same_checksum", Modified = initial,
        });

        // First call with a new timestamp + identical checksum triggers the touch.
        // タイムスタンプ違い・checksum 一致なら touch が走る。
        var id = writer.GetUnchangedFileId("src/touched.py", touched, "same_checksum");
        Assert.NotNull(id);

        // Second call now sees the touched timestamp and hits the fast path.
        // 2 回目は更新後 timestamp で fast-path を通る。
        var idFastPath = writer.GetUnchangedFileId("src/touched.py", touched, "same_checksum");
        Assert.Equal(id, idFastPath);

        // Verify the timestamp was actually persisted in the DB. SQLite stores
        // DateTime as TEXT, so read it back through a typed reader rather than
        // casting the scalar object directly.
        // SQLite は DateTime を TEXT で持つため、reader 経由で型付き取得する。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT modified FROM files WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "src/touched.py");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(touched, reader.GetDateTime(0));
    }

    [Fact]
    public void DbReader_WithCache_ReusesCSharpResolutionCommandsAcrossReaders()
    {
        var writer = new DbWriter(_db);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/pattern.cs",
            Lang = "csharp",
            Size = 80,
            Lines = 4,
            Checksum = "reader-cache",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertSymbols(new[]
        {
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Color",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "enum Color { Red }",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Red",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "Red",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Color",
            },
        });
        writer.InsertReferences(new[]
        {
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Red",
                ReferenceKind = "type_reference",
                Line = 4,
                Column = 18,
                Context = "case Red: break;",
            },
        });

        var firstReader = new DbReader(_db);
        firstReader.SearchReferences("Red", lang: "csharp", referenceKind: "type_reference", exact: true);
        var countAfterFirstReader = _db.PreparedCommands.Count;

        Assert.True(countAfterFirstReader > 0);

        var secondReader = new DbReader(_db);
        secondReader.SearchReferences("Red", lang: "csharp", referenceKind: "type_reference", exact: true);

        Assert.Equal(countAfterFirstReader, _db.PreparedCommands.Count);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}
