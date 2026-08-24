using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class CoreSecondaryIndexBulkLoadGuardTests : IDisposable
{
    private readonly string _dbDirectory;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public CoreSecondaryIndexBulkLoadGuardTests()
    {
        _dbDirectory = TestProjectHelper.CreateTempProject("core_index_bulk_load");
        var dbPath = Path.Combine(_dbDirectory, "codeindex.db");
        _db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void CanonicalSet_ContainsExactlyTheLanguageNeutralSecondaryIndexes()
    {
        var expected = new[]
        {
            "idx_chunks_file",
            "idx_chunks_file_end_start_nonnull",
            "idx_chunks_file_start_chunk_nonnull",
            "idx_file_issues_file_kind",
            "idx_files_checksum",
            "idx_files_generated",
            "idx_files_lang",
            "idx_files_lang_modified",
            "idx_files_modified",
            "idx_files_path_nocase",
            "idx_symbols_display_name_folded",
            "idx_symbols_file",
            "idx_symbols_file_kind",
            "idx_symbols_file_name_folded",
            "idx_symbols_file_name_nocase",
            "idx_symbols_kind",
            "idx_symbols_name",
            "idx_symbols_name_folded",
            "idx_symbols_name_folded_container_name_nocase",
            "idx_symbols_name_folded_container_qualified_name_nocase",
            "idx_symbols_name_nocase",
            "idx_symbols_start",
            "idx_symbols_visibility",
        };
        var definitions = CoreSecondaryIndexSql.All;
        var deferred = expected
            .Where(static name => name != "idx_symbols_file")
            .ToArray();

        Assert.Equal(23, definitions.Count);
        Assert.Equal(
            expected,
            definitions
                .Select(static definition => definition.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            deferred,
            CoreSecondaryIndexBulkLoadGuard.IndexNames.Order(StringComparer.Ordinal));
        Assert.Equal(expected, ReadCoreSecondaryIndexNames(_db.Connection));
        Assert.All(
            definitions,
            static definition => Assert.StartsWith(
                $"CREATE INDEX IF NOT EXISTS {definition.Name} ",
                definition.CreateSql,
                StringComparison.Ordinal));
    }

    [Fact]
    public void StartTransactional_RequiresCallerOwnedTransaction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CoreSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true));

        Assert.Contains("requires an active transaction", exception.Message, StringComparison.Ordinal);
        Assert.Equal(23, ReadCoreSecondaryIndexNames(_db.Connection).Length);
    }

    [Fact]
    public void TransactionalComplete_DefersOnlySecondaryIndexesAndRestoresPopulatedSchema()
    {
        var baseline = ReadCoreSecondaryIndexNames(_db.Connection);
        var previousHook = DbWriter.CoreSecondaryIndexBulkLoadStateForTesting;
        var phases = new List<string>();
        try
        {
            DbWriter.CoreSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                phases.Add(phase);
                Assert.Equal(
                    phase == "dropped" ? ["idx_symbols_file"] : baseline,
                    ReadCoreSecondaryIndexNames(connection));
                previousHook?.Invoke(connection, phase);
            };

            using var transaction = _writer.BeginTransaction();
            using var guard = CoreSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true);
            Assert.NotNull(guard);
            Assert.Equal(["idx_symbols_file"], ReadCoreSecondaryIndexNames(_db.Connection));
            Assert.Equal(1L, CountUniqueFileIndexes(_db.Connection));

            _ = _writer.InsertNewFile(new FileRecord
            {
                Path = "src/populated.cs",
                Lang = "csharp",
                Size = 32,
                Lines = 1,
                Checksum = "populated",
                Modified = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc),
            });

            guard.Complete();
            guard.Complete();
            Assert.Equal(baseline, ReadCoreSecondaryIndexNames(_db.Connection));
            transaction.Commit();

            Assert.Equal(["dropped", "restored"], phases);
        }
        finally
        {
            DbWriter.CoreSecondaryIndexBulkLoadStateForTesting = previousHook;
        }
    }

    [Fact]
    public void TransactionalCancellation_LeavesSchemaRecoveryToOuterRollback()
    {
        var baseline = ReadCoreSecondaryIndexNames(_db.Connection);
        using var cancellation = new CancellationTokenSource();

        using (var transaction = _writer.BeginTransaction())
        {
            using var guard = CoreSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true);
            Assert.NotNull(guard);
            Assert.Equal(["idx_symbols_file"], ReadCoreSecondaryIndexNames(_db.Connection));

            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                guard.Complete(cancellation.Token));
            Assert.Equal(["idx_symbols_file"], ReadCoreSecondaryIndexNames(_db.Connection));
        }

        Assert.Equal(baseline, ReadCoreSecondaryIndexNames(_db.Connection));
    }

    [Fact]
    public void Complete_ReassertsCallerOwnedTransactionBeforeRestoringIndexes()
    {
        CoreSecondaryIndexBulkLoadGuard? guard;
        using (var transaction = _writer.BeginTransaction())
        {
            guard = CoreSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true);
            Assert.NotNull(guard);
        }

        using (guard)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => guard!.Complete());
            Assert.Contains("requires an active transaction", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(23, ReadCoreSecondaryIndexNames(_db.Connection).Length);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_dbDirectory);
    }

    private static string[] ReadCoreSecondaryIndexNames(SqliteConnection connection)
    {
        var knownNames = CoreSecondaryIndexSql.All
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND name NOT LIKE 'sqlite_autoindex_%'
            ORDER BY name
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (knownNames.Contains(name))
                names.Add(name);
        }

        return names.ToArray();
    }

    private static long CountUniqueFileIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_index_list('files') WHERE origin = 'u'";
        return (long)command.ExecuteScalar()!;
    }
}
