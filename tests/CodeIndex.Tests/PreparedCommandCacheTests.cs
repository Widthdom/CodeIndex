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
    public void GetOrAdd_TracksHitMissAndEvictionDiagnostics_Issue3795()
    {
        using var cache = new PreparedCommandCache(_db.Connection, capacity: 2);

        var first = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => c.Parameters.Add("@path", SqliteType.Text));
        var firstAgain = cache.GetOrAdd(
            "SELECT 1 FROM files WHERE path = @path",
            c => throw new InvalidOperationException("cache hit should not re-configure"));
        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE lang = @lang",
            c => c.Parameters.Add("@lang", SqliteType.Text));
        cache.GetOrAdd(
            "SELECT 1 FROM files WHERE size = @size",
            c => c.Parameters.Add("@size", SqliteType.Integer));

        Assert.Same(first, firstAgain);
        var diagnostics = cache.GetDiagnostics();
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(2, diagnostics.Capacity);
        Assert.Equal(1, diagnostics.HitCount);
        Assert.Equal(3, diagnostics.MissCount);
        Assert.Equal(1, diagnostics.EvictionCount);
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
    public void ReadCapacityFromEnvironment_UsesBoundedConfiguredCapacity_Issue3795()
    {
        using var env = EnvironmentVariableScope.Capture(PreparedCommandCache.CapacityEnvironmentVariable);

        env.Set(PreparedCommandCache.CapacityEnvironmentVariable, "4");
        Assert.Equal(4, PreparedCommandCache.ReadCapacityFromEnvironment());

        env.Set(PreparedCommandCache.CapacityEnvironmentVariable, (PreparedCommandCache.MaxCapacity + 1).ToString());
        Assert.Equal(PreparedCommandCache.DefaultCapacity, PreparedCommandCache.ReadCapacityFromEnvironment());

        env.Set(PreparedCommandCache.CapacityEnvironmentVariable, "not-a-number");
        Assert.Equal(PreparedCommandCache.DefaultCapacity, PreparedCommandCache.ReadCapacityFromEnvironment());
    }

    [Fact]
    public void DbContext_UsesConfiguredPreparedCommandCacheCapacity_Issue3795()
    {
        using var env = EnvironmentVariableScope.Capture(PreparedCommandCache.CapacityEnvironmentVariable);
        env.Set(PreparedCommandCache.CapacityEnvironmentVariable, "4");
        var dbPath = Path.Combine(Path.GetTempPath(), $"prepcache_capacity_{Guid.NewGuid():N}.db");
        DbContext? db = null;
        try
        {
            db = new DbContext(dbPath);
            db.InitializeSchema();

            Assert.Equal(4, db.PreparedCommands.Capacity);
        }
        finally
        {
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
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
        Assert.True(_db.PreparedCommands.MissCount > 0);
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
            Path = "src/outer.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "o",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using (var innerTxn = writer.BeginTransaction())
        {
            // Inner savepoint scope: re-lease the same cached UpsertFile command.
            // インナー savepoint 内で同じ cached command を再借用する。
            writer.UpsertFile(new FileRecord
            {
                Path = "src/inner.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Checksum = "i",
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
            Path = "src/outer2.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "o2",
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
            Path = "src/x.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "k1",
            Modified = modified,
        });
        writer.UpsertFile(new FileRecord
        {
            Path = "src/y.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "k2",
            Modified = modified,
        });

        // Same atomic lookup/touch command must be reused across distinct paths.
        // 異なる path に対しても atomic lookup/touch command が再利用される。
        Assert.NotNull(writer.GetUnchangedFileId("src/x.py", modified, "k1"));
        Assert.NotNull(writer.GetUnchangedFileId("src/y.py", modified, "k2"));
        Assert.Null(writer.GetUnchangedFileId("src/missing.py", modified, "k3"));
    }

    [Fact]
    public void DbWriter_WithCache_GetUnchangedFileIdByStatReusesCacheAcrossFiles()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        writer.UpsertFile(new FileRecord
        {
            Path = "src/stat-x.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Modified = modified,
        });
        writer.UpsertFile(new FileRecord
        {
            Path = "src/stat-y.py",
            Lang = "python",
            Size = 2,
            Lines = 1,
            Modified = modified,
        });

        Assert.NotNull(writer.GetUnchangedFileIdByStat("src/stat-x.py", modified, 1, "python"));
        Assert.NotNull(writer.GetUnchangedFileIdByStat("src/stat-y.py", modified, 2, "python"));
        Assert.Null(writer.GetUnchangedFileIdByStat("src/missing-stat.py", modified, 3, "python"));
        Assert.True(_db.PreparedCommands.HitCount > 0);
    }

    [Fact]
    public void DbWriter_WithCache_StaleIssueMetadataLookupReusesCacheAcrossFiles()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstFile = new FileRecord
        {
            Path = "src/legacy-a.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Modified = modified,
        };
        var secondFile = new FileRecord
        {
            Path = "src/legacy-b.py",
            Lang = "python",
            Size = 2,
            Lines = 1,
            Modified = modified,
        };
        var firstFileId = writer.UpsertFile(firstFile);
        var secondFileId = writer.UpsertFile(secondFile);
        writer.InsertIssues(firstFileId,
        [
            new FileIssue
            {
                Path = firstFile.Path,
                Kind = "replacement_char",
                Line = 1,
                Message = "legacy replacement_char row without metadata",
            },
        ]);
        writer.InsertIssues(secondFileId,
        [
            new FileIssue
            {
                Path = secondFile.Path,
                Kind = "non_utf8_likely",
                Line = 0,
                Message = "legacy non_utf8_likely row without metadata",
            },
        ]);
        var hitsBefore = _db.PreparedCommands.HitCount;

        Assert.Null(writer.GetUnchangedFileIdByStat(firstFile.Path, modified, firstFile.Size, "python"));
        Assert.Null(writer.GetUnchangedFileIdByStat(secondFile.Path, modified, secondFile.Size, "python"));

        Assert.True(_db.PreparedCommands.HitCount > hitsBefore);
    }

    [Fact]
    public void DbWriter_WithCache_HasReusableFileBlockingIssueForFileReusesCacheAcrossFiles()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reuse-a.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Modified = modified,
        });
        var secondFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reuse-b.py",
            Lang = "python",
            Size = 2,
            Lines = 1,
            Modified = modified,
        });

        var hitsBefore = _db.PreparedCommands.HitCount;

        Assert.False(writer.HasReusableFileBlockingIssueForFile(firstFileId, 10, 10, generatedExtractionSuppressed: false));
        Assert.False(writer.HasReusableFileBlockingIssueForFile(secondFileId, 10, 10, generatedExtractionSuppressed: false));

        Assert.True(_db.PreparedCommands.HitCount > hitsBefore);
    }

    [Fact]
    public void DbWriter_WithCache_HasAnyFilesWithLanguageReusesCacheAcrossLanguages()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        writer.UpsertFile(new FileRecord
        {
            Path = "src/lang-a.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = modified,
        });
        writer.UpsertFile(new FileRecord
        {
            Path = "src/lang-b.sql",
            Lang = "sql",
            Size = 2,
            Lines = 1,
            Modified = modified,
        });

        var hitsBefore = _db.PreparedCommands.HitCount;

        Assert.True(writer.HasAnyFilesWithLanguage("csharp"));
        Assert.True(writer.HasAnyFilesWithLanguage("sql"));
        Assert.False(writer.HasAnyFilesWithLanguage("python"));

        Assert.True(_db.PreparedCommands.HitCount > hitsBefore);
    }

    [Fact]
    public void DbWriter_WithCache_GetIndexedJavaScriptTypeScriptConfigPathsReusesCache()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        writer.UpsertFile(new FileRecord
        {
            Path = "tsconfig.json",
            Lang = "json",
            Size = 1,
            Lines = 1,
            Modified = modified,
        });
        writer.UpsertFile(new FileRecord
        {
            Path = "packages/app/jsconfig.build.json",
            Lang = "json",
            Size = 2,
            Lines = 1,
            Modified = modified,
        });

        var first = writer.GetIndexedJavaScriptTypeScriptConfigPaths();
        var hitsBefore = _db.PreparedCommands.HitCount;
        var second = writer.GetIndexedJavaScriptTypeScriptConfigPaths();

        Assert.Equal(first, second);
        Assert.Equal(
            new[] { "packages/app/jsconfig.build.json", "tsconfig.json" },
            second);
        Assert.True(_db.PreparedCommands.HitCount > hitsBefore);
    }

    [Fact]
    public void DbWriter_WithCache_InsertIssuesReusesDeleteAndInsertCommandsAcrossFiles()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/issues-a.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Modified = modified,
        });
        var secondFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/issues-b.py",
            Lang = "python",
            Size = 2,
            Lines = 1,
            Modified = modified,
        });
        var issues = new[]
        {
            new FileIssue
            {
                Path = "src/issues.py",
                Kind = "non_utf8_likely",
                Line = 0,
                Message = "non UTF-8 bytes",
                Origin = "validation",
                Severity = "warning",
            },
        };
        var hitsBefore = _db.PreparedCommands.HitCount;

        writer.InsertIssues(firstFileId, issues);
        writer.InsertIssues(secondFileId, issues);
        writer.InsertIssues(secondFileId, []);

        Assert.True(_db.PreparedCommands.HitCount >= hitsBefore + 3);
    }

    [Fact]
    public void DbWriter_WithCache_CSharpStaticInterfaceContractQueriesReuseCacheAcrossCalls()
    {
        var writer = new DbWriter(_db);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/IShape.cs",
            Lang = "csharp",
            Size = 120,
            Lines = 6,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertSymbols(new[]
        {
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "interface",
                Name = "IShape",
                Line = 1,
                StartLine = 1,
                EndLine = 6,
                Signature = "public interface IShape",
                ContainerQualifiedName = "Demo.IShape",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Create",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public static abstract IShape Create();",
                ContainerKind = "interface",
                ContainerName = "IShape",
                ContainerQualifiedName = "Demo.IShape",
            },
        });

        var first = writer.LoadCSharpStaticInterfaceContractSymbols();
        Assert.Contains(first, s => s.Kind == "interface" && s.Name == "IShape");
        Assert.Contains(first, s => s.Kind == "function" && s.Name == "Create");
        Assert.True(writer.HasCSharpStaticInterfaceContractSymbolsInPaths(
            new HashSet<string>(StringComparer.Ordinal) { "src/IShape.cs" }));

        var hitsBefore = _db.PreparedCommands.HitCount;

        var second = writer.LoadCSharpStaticInterfaceContractSymbols();
        Assert.True(writer.HasCSharpStaticInterfaceContractSymbolsInPaths(
            new HashSet<string>(StringComparer.Ordinal) { "src/IShape.cs" }));

        Assert.Equal(first.Count, second.Count);
        Assert.True(_db.PreparedCommands.HitCount >= hitsBefore + 2);
    }

    [Fact]
    public void DbWriter_WithCache_CSharpMetadataResolverReusesReadAndUpdateCommandsAcrossRuns()
    {
        var writer = new DbWriter(_db);
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/A/BaseAttribute.cs",
            Lang = "csharp",
            Size = 80,
            Lines = 4,
            Modified = modified,
        });
        var childFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/A/ChildAttribute.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 4,
            Modified = modified,
        });
        writer.InsertSymbols(new[]
        {
            new SymbolRecord
            {
                FileId = baseFileId,
                Kind = "class",
                Name = "BaseAttribute",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public class BaseAttribute : System.Attribute",
                ContainerQualifiedName = "A.BaseAttribute",
                IsMetadataTarget = true,
                MetadataTargetSource = SymbolRecord.MetadataTargetSourceExtractor,
            },
            new SymbolRecord
            {
                FileId = childFileId,
                Kind = "class",
                Name = "ChildAttribute",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public class ChildAttribute : BaseAttribute",
                ContainerQualifiedName = "A.ChildAttribute",
            },
        });

        writer.ResolveCSharpMetadataTargets();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.is_metadata_target, s.metadata_target_source
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'src/A/ChildAttribute.cs' AND s.name = 'ChildAttribute'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(SymbolRecord.MetadataTargetSourceResolver, reader.GetString(1));
        }

        var hitsBefore = _db.PreparedCommands.HitCount;

        writer.ResolveCSharpMetadataTargets();

        Assert.True(_db.PreparedCommands.HitCount >= hitsBefore + 3);
    }

    [Fact]
    public void DbWriter_WithCache_MetaHelpersReuseCommandsAcrossKeys()
    {
        var writer = new DbWriter(_db);
        var currentTypeScriptAugmentationVersion =
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

        writer.SetMeta(DbContext.TypeScriptAugmentationVersionMetaKey, currentTypeScriptAugmentationVersion);
        Assert.True(writer.TypeScriptAugmentationVersionMatchesCurrent());
        Assert.True(writer.HasMetaTable());

        var hitsBefore = _db.PreparedCommands.HitCount;

        writer.SetMeta("prepared_cache_meta_a", "1");
        writer.SetMeta(DbContext.TypeScriptAugmentationVersionMetaKey, currentTypeScriptAugmentationVersion);
        Assert.True(writer.TypeScriptAugmentationVersionMatchesCurrent());
        Assert.True(writer.HasMetaTable());

        Assert.True(_db.PreparedCommands.HitCount >= hitsBefore + 6);
    }

    [Fact]
    public void DbWriter_WithCache_GetUnchangedFileIdTouchUpdatesTimestamp()
    {
        // GetUnchangedFileId now performs lookup and timestamp touch in one
        // cached command. Confirm the touch still persists the new timestamp.
        // GetUnchangedFileId は lookup と timestamp touch を 1 つの cached command
        // で行うため、timestamp 更新が維持されることを確認する。
        var writer = new DbWriter(_db);
        var initial = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var touched = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        writer.UpsertFile(new FileRecord
        {
            Path = "src/touched.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "same_checksum",
            Modified = initial,
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
    public void DbWriter_WithCache_GetUnchangedFileIdDoesNotTouchWhenChecksumDrifts_Issue1735()
    {
        var writer = new DbWriter(_db);
        var initial = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var touched = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        writer.UpsertFile(new FileRecord
        {
            Path = "src/drift.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Checksum = "old_checksum",
            Modified = initial,
        });

        Assert.Null(writer.GetUnchangedFileId("src/drift.py", touched, "new_checksum"));

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT modified, checksum FROM files WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "src/drift.py");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(initial, reader.GetDateTime(0));
        Assert.Equal("old_checksum", reader.GetString(1));
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
