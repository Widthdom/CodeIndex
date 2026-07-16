using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for the DB-path-scoped schema cache that backs DbReader's
/// PRAGMA table_info / PRAGMA index_list / sqlite_master lookups (issues #1565 / #1701 / #4554).
/// </summary>
[Collection("SQLite pool sensitive")]
public sealed class DbSchemaCacheTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;

    public DbSchemaCacheTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_schema_cache_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public void DbContext_SchemaCache_IsLazySingleton()
    {
        var first = _db.SchemaCache;
        var second = _db.SchemaCache;

        Assert.Same(first, second);
    }

    [Fact]
    public void GetColumns_ReturnsSameInstanceForRepeatedLookup()
    {
        var first = _db.SchemaCache.GetColumns("files");
        var second = _db.SchemaCache.GetColumns("files");

        Assert.Same(first, second);
        Assert.Contains("path", first);
        Assert.Contains("checksum", first);
    }

    [Fact]
    public async Task GetSchemaSets_FrozenSnapshotsRejectConcurrentMutation()
    {
        var columnSnapshot = _db.SchemaCache.GetColumns("files");
        var indexSnapshot = _db.SchemaCache.GetIndexes("symbols");
        var mutableColumns = Assert.IsAssignableFrom<ISet<string>>(columnSnapshot);
        var mutableIndexes = Assert.IsAssignableFrom<ISet<string>>(indexSnapshot);

        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                mutableColumns.Add("injected_column_by_caller");
            });
            Assert.Throws<NotSupportedException>(() =>
            {
                mutableIndexes.Add("injected_index_by_caller");
            });
            Assert.Same(columnSnapshot, _db.SchemaCache.GetColumns("files"));
            Assert.Same(indexSnapshot, _db.SchemaCache.GetIndexes("symbols"));
            Assert.Contains("path", columnSnapshot);
            Assert.Contains("idx_symbols_name_nocase", indexSnapshot);
        }));

        await Task.WhenAll(attempts);
        Assert.DoesNotContain("injected_column_by_caller", columnSnapshot);
        Assert.DoesNotContain("injected_index_by_caller", indexSnapshot);
    }

    [Fact]
    public void GetIndexes_ReturnsSameInstanceForRepeatedLookup()
    {
        var first = _db.SchemaCache.GetIndexes("symbols");
        var second = _db.SchemaCache.GetIndexes("symbols");

        Assert.Same(first, second);
        Assert.Contains("idx_symbols_name_nocase", first);
    }

    [Fact]
    public void GetIndexes_ReturnsEmptyForMissingTable()
    {
        var indexes = _db.SchemaCache.GetIndexes("nonexistent_table_for_test");

        Assert.Empty(indexes);
    }

    [Fact]
    public void HasTable_CachesPositiveAndNegativeResults()
    {
        Assert.True(_db.SchemaCache.HasTable("symbols"));
        Assert.False(_db.SchemaCache.HasTable("nonexistent_table_for_test"));

        // Repeated calls must not error and must match.
        Assert.True(_db.SchemaCache.HasTable("symbols"));
        Assert.False(_db.SchemaCache.HasTable("nonexistent_table_for_test"));
    }

    [Fact]
    public void Refresh_ForcesReload()
    {
        // Prime the cache.
        var beforeColumns = _db.SchemaCache.GetColumns("files");

        // Explicit Refresh must drop cached instances even when nothing has
        // structurally changed, so callers that suspect external mutation can
        // still force a reload.
        _db.RefreshSchemaCache();

        var afterColumns = _db.SchemaCache.GetColumns("files");
        Assert.NotSame(beforeColumns, afterColumns);
        Assert.Equal(beforeColumns, afterColumns);
    }

    [Fact]
    public void TryMigrateForRead_AutoRefreshesCache()
    {
        // Prime the cache with the freshly-initialized schema.
        var beforeColumns = _db.SchemaCache.GetColumns("files");

        // Drop a column-bearing table and recreate it with a different shape so
        // a follow-up TryMigrateForRead has migration steps to run. We do this
        // by deleting the codeindex_meta table (one TryMigrateForRead step
        // re-creates it) and confirming the cache sees the post-migration shape.
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE codeindex_meta";
            cmd.ExecuteNonQuery();
        }

        // Sanity: the cache served before the drop; this call would resolve from
        // the connection now and capture the missing state.
        Assert.False(_db.SchemaCache.HasTable("codeindex_meta"));

        _db.TryMigrateForRead();

        Assert.True(_db.SchemaCache.HasTable("codeindex_meta"));
        // The files columns may legitimately remain identical, but the instance
        // must be a fresh one (cache cleared by TryMigrateForRead).
        var afterColumns = _db.SchemaCache.GetColumns("files");
        Assert.NotSame(beforeColumns, afterColumns);
        Assert.Equal(beforeColumns, afterColumns);
    }

    [Fact]
    public void DbReader_FromDbContext_ReusesSharedSchemaCacheInstances()
    {
        // Multiple DbReader constructions on the same DbContext should share
        // the same underlying schema HashSet instances rather than each one
        // performing its own PRAGMA scan.
        _ = new DbReader(_db);
        var fromCacheBefore = _db.SchemaCache.GetColumns("files");
        _ = new DbReader(_db);
        var fromCacheAfter = _db.SchemaCache.GetColumns("files");

        Assert.Same(fromCacheBefore, fromCacheAfter);
    }

    [Fact]
    public void DbReader_SeparateDbContextsForSamePath_ReuseSharedSchemaCacheInstances()
    {
        // A writable open may run SQLite maintenance that changes
        // schema_version. Once two separately-opened contexts observe the
        // same generation, they should reuse the process-level snapshot.
        using var secondDb = new DbContext(_dbPath);
        _ = new DbReader(secondDb);
        var secondContextColumns = secondDb.SchemaCache.GetColumns("files");

        using var thirdDb = new DbContext(_dbPath);
        _ = new DbReader(thirdDb);
        var thirdContextColumns = thirdDb.SchemaCache.GetColumns("files");

        Assert.Same(secondContextColumns, thirdContextColumns);
    }

    [Fact]
    public void DbContext_DisposeRetainsActiveSharedStateAndReleasesLastOwner()
    {
        using var secondDb = new DbContext(_dbPath);
        var secondSnapshot = secondDb.SchemaCache.GetColumns("files");
        var firstSnapshot = _db.SchemaCache.GetColumns("files");
        var activeStateCount = DbSchemaCache.SharedStateCountForTest;
        Assert.Same(firstSnapshot, secondSnapshot);
        Assert.Equal(activeStateCount, DbSchemaCache.SharedStateCountForTest);

        secondDb.Dispose();
        Assert.Equal(activeStateCount, DbSchemaCache.SharedStateCountForTest);
        Assert.Same(firstSnapshot, _db.SchemaCache.GetColumns("files"));

        _db.Dispose();
        Assert.Equal(activeStateCount - 1, DbSchemaCache.SharedStateCountForTest);
        Assert.Throws<ObjectDisposedException>(() => _ = _db.SchemaCache);
    }

    [Fact]
    public void DbContext_ManyDatabasePathsDoNotAccumulateSharedStates()
    {
        const int databaseCount = 32;
        var baseline = DbSchemaCache.SharedStateCountForTest;
        var paths = Enumerable.Range(0, databaseCount)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"codeindex_schema_cache_lifecycle_{Guid.NewGuid():N}.db"))
            .ToArray();

        try
        {
            foreach (var path in paths)
            {
                using (var db = new DbContext(path))
                {
                    _ = db.SchemaCache.GetColumns("files");
                    Assert.Equal(baseline + 1, DbSchemaCache.SharedStateCountForTest);
                }

                Assert.Equal(baseline, DbSchemaCache.SharedStateCountForTest);
            }
        }
        finally
        {
            foreach (var path in paths)
                TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void GetColumns_AutoRefreshesWhenSecondConnectionMutatesSchema()
    {
        // Simulates the MCP `WithDbReader` scenario flagged by the adversarial
        // review (#1565): the cache lives on a long-running shared DbContext,
        // and an external process / second connection (e.g. `cdidx index`)
        // mutates the schema. Without `PRAGMA schema_version` polling the
        // cache would serve stale results until the server restarted.
        var beforeColumns = _db.SchemaCache.GetColumns("files");
        Assert.DoesNotContain("external_test_column", beforeColumns);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        using (var external = new SqliteConnection(connectionString))
        {
            external.Open();
            using var cmd = external.CreateCommand();
            cmd.CommandText = "ALTER TABLE files ADD COLUMN external_test_column TEXT";
            cmd.ExecuteNonQuery();
        }

        var afterColumns = _db.SchemaCache.GetColumns("files");
        Assert.Contains("external_test_column", afterColumns);
        Assert.NotSame(beforeColumns, afterColumns);
    }

    [Fact]
    public void HasTable_AutoRefreshesWhenSecondConnectionAddsTable()
    {
        Assert.False(_db.SchemaCache.HasTable("external_test_table"));

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        using (var external = new SqliteConnection(connectionString))
        {
            external.Open();
            using var cmd = external.CreateCommand();
            cmd.CommandText = "CREATE TABLE external_test_table (id INTEGER PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }

        Assert.True(_db.SchemaCache.HasTable("external_test_table"));
    }

    [Fact]
    public void GetIndexes_AutoRefreshesWhenSecondConnectionCreatesIndex()
    {
        // Capture the pre-existing index set so the assertion isn't sensitive
        // to other indexes the schema migration may already have created.
        var beforeIndexes = _db.SchemaCache.GetIndexes("symbols");
        Assert.DoesNotContain("idx_external_test_symbols", beforeIndexes);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        using (var external = new SqliteConnection(connectionString))
        {
            external.Open();
            using var cmd = external.CreateCommand();
            cmd.CommandText = "CREATE INDEX idx_external_test_symbols ON symbols(name)";
            cmd.ExecuteNonQuery();
        }

        var afterIndexes = _db.SchemaCache.GetIndexes("symbols");
        Assert.Contains("idx_external_test_symbols", afterIndexes);
        Assert.NotSame(beforeIndexes, afterIndexes);
    }

    [Fact]
    public void StaleSentinel_DiscardsEntriesLoadedDuringFailedVersionWindow()
    {
        // Codex review (#1565, second pass): if `PRAGMA schema_version` fails
        // (transient lock during external DDL), entries the cache loads in
        // that window have no version guarantee. The next successful version
        // read MUST clear them, regardless of whether the read returns a new
        // value or a "first observation" value — otherwise they would be
        // version-stamped as current and outlive the DDL.

        // Prime the cache so we have an entry that simulates "loaded during
        // failure window" (the production path would set this via the
        // exception branch, but we use the internal test seam to keep the
        // test free of contrived locking choreography).
        var stale = _db.SchemaCache.GetColumns("files");
        _db.SchemaCache.MarkVersionStaleForTest();

        // Next lookup: EnsureFreshUnlocked sees the stale sentinel and must
        // drop the existing entry even though no DDL has actually happened
        // and the schema_version equals whatever was last observed.
        var refreshed = _db.SchemaCache.GetColumns("files");

        Assert.NotSame(stale, refreshed);
        Assert.Equal(stale, refreshed);

        // Sentinel must clear after the successful read so further calls in
        // the steady state do not pay the discard cost on every lookup.
        var steady = _db.SchemaCache.GetColumns("files");
        Assert.Same(refreshed, steady);
    }

    [Fact]
    public void DbReader_LegacyConstructor_StillWorksWithoutCache()
    {
        // The (SqliteConnection, bool) overload is the legacy path used by
        // write-side signal readers; it must keep functioning without a cache.
        var reader = new DbReader(_db.Connection, _db.IsReadOnly);

        Assert.NotNull(reader);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteFile(_dbPath);
    }
}
