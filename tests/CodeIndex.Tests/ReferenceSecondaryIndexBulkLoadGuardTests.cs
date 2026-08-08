using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class ReferenceSecondaryIndexBulkLoadGuardTests : IDisposable
{
    private readonly string _dbDirectory;
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public ReferenceSecondaryIndexBulkLoadGuardTests()
    {
        _dbDirectory = TestProjectHelper.CreateTempProject("reference_index_bulk_load");
        _dbPath = Path.Combine(_dbDirectory, "codeindex.db");
        _db = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void StartTransactional_RequiresCallerOwnedTransaction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(_writer, enabled: true));

        Assert.Contains("requires an active transaction", exception.Message, StringComparison.Ordinal);
        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void TransactionalComplete_DropsOnlyDeferredIndexesAndRestoresCanonicalSet()
    {
        var baseline = ReadReferenceIndexNames(_db.Connection);

        using var transaction = _writer.BeginTransaction();
        using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
            _writer,
            enabled: true);

        Assert.NotNull(guard);
        AssertDeferredIndexesAbsent(_db.Connection);
        AssertRawPersistenceIndexesPresent(_db.Connection);

        guard.Complete();

        Assert.Equal(
            baseline.Order(StringComparer.Ordinal),
            ReadReferenceIndexNames(_db.Connection).Order(StringComparer.Ordinal));
        transaction.Commit();
    }

    [Fact]
    public void TransactionalDispose_LeavesIndexesToCallerRollback()
    {
        var baseline = ReadReferenceIndexNames(_db.Connection);

        using (var transaction = _writer.BeginTransaction())
        {
            using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                       _writer,
                       enabled: true))
            {
                Assert.NotNull(guard);
                AssertDeferredIndexesAbsent(_db.Connection);
            }

            // Dispose must not rebuild a large index set while cancellation is unwinding.
            AssertDeferredIndexesAbsent(_db.Connection);
        }

        Assert.Equal(
            baseline.Order(StringComparer.Ordinal),
            ReadReferenceIndexNames(_db.Connection).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RecoverableDispose_RestoresDeferredIndexes()
    {
        using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                   _writer,
                   enabled: true))
        {
            Assert.NotNull(guard);
            AssertDeferredIndexesAbsent(_db.Connection);
            AssertRawPersistenceIndexesPresent(_db.Connection);
        }

        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void RecoverableCancelledComplete_RestoresDuringDispose()
    {
        using var cancellation = new CancellationTokenSource();

        using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                   _writer,
                   enabled: true))
        {
            Assert.NotNull(guard);
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => guard!.Complete(cancellation.Token));
            AssertDeferredIndexesAbsent(_db.Connection);
        }

        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void RecoverableDispose_RestoreFailureIsSurfaced()
    {
        var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
            _writer,
            enabled: true);
        Assert.NotNull(guard);

        using (var command = _db.Connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE symbol_references";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<SqliteException>(() => guard!.Dispose());

        Assert.Contains("no such table", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitializeSchema_RepairsIndexesAfterAbandonedRecoverableGuard()
    {
        var abandonedGuard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
            _writer,
            enabled: true);
        Assert.NotNull(abandonedGuard);
        AssertDeferredIndexesAbsent(_db.Connection);

        // Model process termination: the connection disappears without running guard Dispose.
        _db.Dispose();
        using (var recoveredDb = new DbContext(DbOpenIntent.WriteIndex, _dbPath))
        {
            recoveredDb.InitializeSchema();
            AssertDeferredIndexesPresent(recoveredDb.Connection);
        }

        GC.KeepAlive(abandonedGuard);
    }

    private static void AssertDeferredIndexesAbsent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var name in ReferenceSecondaryIndexBulkLoadGuard.IndexNames)
            Assert.DoesNotContain(name, names);
    }

    private static void AssertDeferredIndexesPresent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var name in ReferenceSecondaryIndexBulkLoadGuard.IndexNames)
            Assert.Contains(name, names);
    }

    private static void AssertRawPersistenceIndexesPresent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var definition in ReferenceSecondaryIndexSql.RawPersistenceRequired)
            Assert.Contains(definition.Name, names);
    }

    private static HashSet<string> ReadReferenceIndexNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND tbl_name = 'symbol_references'
              AND name NOT LIKE 'sqlite_autoindex_%'
            """;
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_dbDirectory);
    }
}
