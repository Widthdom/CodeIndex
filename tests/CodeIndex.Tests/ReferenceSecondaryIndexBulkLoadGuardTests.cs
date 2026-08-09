using CodeIndex.Database;
using CodeIndex.Models;
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
    public void CanonicalSets_PartitionRawGraphAndRemainingIndexesExactly()
    {
        var rawNames = ReferenceSecondaryIndexSql.RawPersistenceRequired
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var graphNames = ReferenceSecondaryIndexSql.GraphFinalizationRequired
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var remainingNames = ReferenceSecondaryIndexSql.RemainingQuery
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var deferredNames = ReferenceSecondaryIndexSql.DeferredDuringBulkLoad
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            [
                "idx_symbol_refs_container_nocase_kind",
                "idx_symbol_refs_resolved_source_target_kind",
                "idx_symbol_refs_unresolved_mutual_folded",
            ],
            graphNames.Order(StringComparer.Ordinal));
        Assert.Empty(rawNames.Intersect(deferredNames));
        Assert.Empty(graphNames.Intersect(remainingNames));
        Assert.Contains("idx_symbol_ref_candidates_symbol", remainingNames);
        Assert.True(deferredNames.SetEquals(graphNames.Concat(remainingNames)));
        Assert.Equal(
            graphNames.Order(StringComparer.Ordinal),
            ReferenceSecondaryIndexBulkLoadGuard.GraphFinalizationIndexNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TransactionalComplete_RestoresGraphSubsetBeforeRemainingCanonicalSet()
    {
        var baseline = ReadReferenceIndexNames(_db.Connection);

        using var transaction = _writer.BeginTransaction();
        using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
            _writer,
            enabled: true);

        Assert.NotNull(guard);
        AssertDeferredIndexesAbsent(_db.Connection);
        AssertRawPersistenceIndexesPresent(_db.Connection);

        guard.PrepareForMutualRecursion();

        AssertGraphFinalizationIndexesPresent(_db.Connection);
        AssertRemainingQueryIndexesAbsent(_db.Connection);

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
    public void BulkLoad_RollsBackRetiredIndexesTransactionallyButNeverRestoresThemRecoverably()
    {
        CreateRetiredReferenceIndexes(_db.Connection);

        using (var transaction = _writer.BeginTransaction())
        {
            using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                       _writer,
                       enabled: true))
            {
                Assert.NotNull(guard);
                AssertRetiredIndexesAbsent(_db.Connection);
            }

            AssertRetiredIndexesAbsent(_db.Connection);
        }

        AssertRetiredIndexesPresent(_db.Connection);

        using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                   _writer,
                   enabled: true))
        {
            Assert.NotNull(guard);
            AssertRetiredIndexesAbsent(_db.Connection);
            guard.Complete();
        }

        AssertRetiredIndexesAbsent(_db.Connection);
        AssertDeferredIndexesPresent(_db.Connection);
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
    public void RecoverableCancelledComplete_AfterGraphRestoreRepairsRemainingIndexesDuringDispose()
    {
        using var cancellation = new CancellationTokenSource();

        using (var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                   _writer,
                   enabled: true))
        {
            Assert.NotNull(guard);
            guard.PrepareForMutualRecursion();
            AssertGraphFinalizationIndexesPresent(_db.Connection);
            AssertRemainingQueryIndexesAbsent(_db.Connection);

            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => guard.Complete(cancellation.Token));
            AssertRemainingQueryIndexesAbsent(_db.Connection);
        }

        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void RecoverableComplete_WithoutGraphRefreshRestoresAllIndexes()
    {
        using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
            _writer,
            enabled: true);
        Assert.NotNull(guard);
        AssertDeferredIndexesAbsent(_db.Connection);

        guard.Complete();

        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void StagedRefresh_PreservesMutualChangesCountAcrossRemainingIndexRestore()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/staged-mutual.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 2,
            Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "staged-mutual",
        });
        _writer.InsertReferences(
        [
            new ReferenceRecord { FileId = fileId, SymbolName = "Beta", ReferenceKind = "call", Line = 1, Column = 1, Context = "Beta();", ContainerName = "Alpha" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Alpha", ReferenceKind = "call", Line = 2, Column = 1, Context = "Alpha();", ContainerName = "Beta" },
        ],
        refreshMutualRecursionFlags: false);

        using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
            _writer,
            enabled: true);
        Assert.NotNull(guard);

        _writer.RefreshMutualRecursionFlags(
            stampReferenceIdentityContractReady: false,
            referenceSecondaryIndexBulkLoad: guard);

        Assert.Equal(2, ReadChanges(_db.Connection));
        AssertGraphFinalizationIndexesPresent(_db.Connection);
        AssertRemainingQueryIndexesAbsent(_db.Connection);

        guard.Complete();

        Assert.Equal(2, ReadChanges(_db.Connection));
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

    [Fact]
    public void InitializeSchema_RepairsRemainingIndexesAfterAbandonedGraphRestore()
    {
        var abandonedGuard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
            _writer,
            enabled: true);
        Assert.NotNull(abandonedGuard);
        abandonedGuard.PrepareForMutualRecursion();
        AssertGraphFinalizationIndexesPresent(_db.Connection);
        AssertRemainingQueryIndexesAbsent(_db.Connection);

        // Model termination between graph finalization and query-index restoration.
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

    private static void AssertGraphFinalizationIndexesPresent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var definition in ReferenceSecondaryIndexSql.GraphFinalizationRequired)
            Assert.Contains(definition.Name, names);
    }

    private static void AssertRemainingQueryIndexesAbsent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var definition in ReferenceSecondaryIndexSql.RemainingQuery)
            Assert.DoesNotContain(definition.Name, names);
    }

    private static long ReadChanges(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT changes()";
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertRetiredIndexesAbsent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var name in ReferenceSecondaryIndexSql.Retired)
            Assert.DoesNotContain(name, names);
    }

    private static void AssertRetiredIndexesPresent(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var name in ReferenceSecondaryIndexSql.Retired)
            Assert.Contains(name, names);
    }

    private static void CreateRetiredReferenceIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX idx_symbol_refs_name ON symbol_references(symbol_name);
            CREATE INDEX idx_symbol_refs_container ON symbol_references(container_name);
            CREATE INDEX idx_symbol_refs_name_nocase ON symbol_references(symbol_name COLLATE NOCASE);
            CREATE INDEX idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE);
            CREATE INDEX idx_symbol_refs_symbol_name_folded ON symbol_references(symbol_name_folded);
            CREATE INDEX idx_symbol_refs_container_name_folded ON symbol_references(container_name_folded);
            CREATE INDEX idx_symbol_refs_mutual_folded ON symbol_references(
                container_name_folded,
                symbol_name_folded,
                reference_kind,
                is_self_reference);
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadReferenceIndexNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND tbl_name IN ('symbol_references', 'symbol_reference_candidates')
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
