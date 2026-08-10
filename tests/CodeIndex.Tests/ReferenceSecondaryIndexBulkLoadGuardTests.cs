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
        const string candidateReverseIndexName = "idx_symbol_ref_candidates_symbol";
        var rawNames = ReferenceSecondaryIndexSql.RawPersistenceRequired
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var graphNames = ReferenceSecondaryIndexSql.GraphFinalizationRequired
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var deferredGraphPreparationNames = ReferenceSecondaryIndexSql.DeferredGraphPreparation
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var candidatePopulationNames = ReferenceSecondaryIndexSql.CandidatePopulationDeferred
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
        Assert.Contains(candidateReverseIndexName, remainingNames);
        Assert.Equal([candidateReverseIndexName], candidatePopulationNames);
        Assert.DoesNotContain(candidateReverseIndexName, deferredGraphPreparationNames);
        Assert.True(
            deferredGraphPreparationNames.SetEquals(
                graphNames.Concat(remainingNames.Where(
                    name => !string.Equals(
                        name,
                        candidateReverseIndexName,
                        StringComparison.Ordinal)))));
        Assert.True(deferredNames.SetEquals(graphNames.Concat(remainingNames)));
        Assert.Equal(
            graphNames.Order(StringComparer.Ordinal),
            ReferenceSecondaryIndexBulkLoadGuard.GraphFinalizationIndexNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            deferredGraphPreparationNames.Order(StringComparer.Ordinal),
            ReferenceSecondaryIndexBulkLoadGuard.DeferredGraphPreparationIndexNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            candidatePopulationNames.Order(StringComparer.Ordinal),
            ReferenceSecondaryIndexBulkLoadGuard.CandidatePopulationIndexNames.Order(StringComparer.Ordinal));
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
        AssertInitialBulkPersistenceSchema(_db.Connection);
        AssertRawPersistenceIndexesPresent(_db.Connection);

        guard.PrepareForCandidatePopulation();
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
    public void TransactionalPrepareForDeferredGraphRefresh_KeepsCandidateUntilPopulationStarts()
    {
        const string candidateReverseIndexName = "idx_symbol_ref_candidates_symbol";
        var baseline = ReadReferenceIndexNames(_db.Connection);
        var expectedPreparedNames = ReferenceSecondaryIndexSql.RawPersistenceRequired
            .Concat(ReferenceSecondaryIndexSql.DeferredGraphPreparation)
            .Select(static definition => definition.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var transaction = _writer.BeginTransaction();
        using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
            _writer,
            enabled: true);

        Assert.NotNull(guard);
        AssertInitialBulkPersistenceSchema(_db.Connection);

        guard.PrepareForDeferredGraphRefresh();

        var preparedNames = ReadReferenceIndexNames(_db.Connection);
        Assert.Equal(baseline.Order(StringComparer.Ordinal), preparedNames.Order(StringComparer.Ordinal));
        Assert.Contains(candidateReverseIndexName, preparedNames);

        guard.PrepareForCandidatePopulation();

        preparedNames = ReadReferenceIndexNames(_db.Connection);
        Assert.Equal(expectedPreparedNames, preparedNames.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(candidateReverseIndexName, preparedNames);

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
                AssertInitialBulkPersistenceSchema(_db.Connection);
            }

            // Dispose must not rebuild a large index set while cancellation is unwinding.
            AssertInitialBulkPersistenceSchema(_db.Connection);
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
            AssertInitialBulkPersistenceSchema(_db.Connection);
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
            AssertInitialBulkPersistenceSchema(_db.Connection);
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
            guard.PrepareForCandidatePopulation();
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
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var phases = new List<string>();
        try
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                phases.Add(phase);
                previousStateHook?.Invoke(connection, phase);
            };
            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                _writer,
                enabled: true);
            Assert.NotNull(guard);
            AssertInitialBulkPersistenceSchema(_db.Connection);

            guard.Complete();

            Assert.Equal(["dropped", "restored"], phases);
            AssertDeferredIndexesPresent(_db.Connection);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
        }
    }

    [Fact]
    public void FreshPlannerStatistics_RunOnceAfterCandidateDropAndAnalyzeOnlyGraphTables()
    {
        SeedPlannerStatisticsFixture();
        ResetPlannerStatistics();
        var previousBulkStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousStatisticsHook = DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting;
        var previousFinalMaintenanceHook = DbContext.PlannerStatisticsCommandCreatedForTesting;
        var lifecycle = new List<string>();
        string[]? tablesBeforeAnalyze = null;
        string[]? tablesAfterAnalyze = null;
        string[]? indexesBeforeAnalyze = null;
        string[]? indexesAfterAnalyze = null;
        var finalMaintenanceHookCalls = 0;
        try
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                lifecycle.Add(phase);
                previousBulkStateHook?.Invoke(connection, phase);
            };
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = (connection, phase) =>
            {
                lifecycle.Add(phase);
                if (phase == "post_load_statistics_started")
                {
                    tablesBeforeAnalyze = ReadPlannerStatisticTables(connection);
                    indexesBeforeAnalyze = ReadPlannerStatisticIndexes(connection);
                    Assert.DoesNotContain(
                        "idx_symbol_ref_candidates_symbol",
                        ReadReferenceIndexNames(connection));
                }
                else if (phase == "post_load_statistics_completed")
                {
                    tablesAfterAnalyze = ReadPlannerStatisticTables(connection);
                    indexesAfterAnalyze = ReadPlannerStatisticIndexes(connection);
                }
                previousStatisticsHook?.Invoke(connection, phase);
            };
            DbContext.PlannerStatisticsCommandCreatedForTesting = command =>
            {
                finalMaintenanceHookCalls++;
                previousFinalMaintenanceHook?.Invoke(command);
            };

            using var transaction = _writer.BeginTransaction();
            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true,
                refreshPlannerStatisticsBeforeCandidatePopulation: true);
            Assert.NotNull(guard);

            guard.PrepareForDeferredGraphRefresh();
            guard.PrepareForCandidatePopulation();
            guard.PrepareForCandidatePopulation();
            guard.ReportIdentityRefreshStarted();
            guard.Complete();
            transaction.Commit();

            Assert.Equal(
                [
                    "dropped",
                    "deferred_graph_prepared",
                    "candidate_deferred",
                    "post_load_statistics_started",
                    "post_load_statistics_completed",
                    "candidate_deferred",
                    "identity_started",
                    "restored",
                ],
                lifecycle);
            Assert.Empty(Assert.IsType<string[]>(tablesBeforeAnalyze));
            Assert.Empty(Assert.IsType<string[]>(indexesBeforeAnalyze));
            Assert.Equal(
                ["files", "symbol_references", "symbols"],
                Assert.IsType<string[]>(tablesAfterAnalyze));
            var analyzedIndexes = Assert.IsType<string[]>(indexesAfterAnalyze);
            Assert.All(
                ReferenceSecondaryIndexSql.DeferredGraphPreparation,
                definition => Assert.Contains(definition.Name, analyzedIndexes));
            Assert.DoesNotContain("idx_symbol_ref_candidates_symbol", analyzedIndexes);
            Assert.DoesNotContain("symbol_reference_candidates", tablesAfterAnalyze);
            Assert.Equal(0, finalMaintenanceHookCalls);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousBulkStateHook;
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = previousStatisticsHook;
            DbContext.PlannerStatisticsCommandCreatedForTesting = previousFinalMaintenanceHook;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FreshPlannerStatistics_DisabledGuardOrFlagLeavesStatisticsUntouched(
        bool guardEnabled,
        bool statisticsEnabled)
    {
        SeedPlannerStatisticsFixture();
        ResetPlannerStatistics();
        var previousStatisticsHook = DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting;
        var phases = new List<string>();
        try
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = (connection, phase) =>
            {
                phases.Add(phase);
                previousStatisticsHook?.Invoke(connection, phase);
            };

            using var transaction = _writer.BeginTransaction();
            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                guardEnabled,
                refreshPlannerStatisticsBeforeCandidatePopulation: statisticsEnabled);
            if (guard != null)
            {
                guard.PrepareForDeferredGraphRefresh();
                guard.PrepareForCandidatePopulation();
                guard.Complete();
            }
            transaction.Commit();

            Assert.Empty(phases);
            Assert.Empty(ReadPlannerStatisticTables(_db.Connection));
        }
        finally
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = previousStatisticsHook;
        }
    }

    [Fact]
    public void FreshPlannerStatistics_OuterRollbackRemovesAnalyzeResults()
    {
        SeedPlannerStatisticsFixture();
        ResetPlannerStatistics();

        using (var transaction = _writer.BeginTransaction())
        {
            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true,
                refreshPlannerStatisticsBeforeCandidatePopulation: true);
            Assert.NotNull(guard);

            guard.PrepareForDeferredGraphRefresh();
            guard.PrepareForCandidatePopulation();

            Assert.Equal(
                ["files", "symbol_references", "symbols"],
                ReadPlannerStatisticTables(_db.Connection));
        }

        Assert.Empty(ReadPlannerStatisticTables(_db.Connection));
        AssertDeferredIndexesPresent(_db.Connection);
    }

    [Fact]
    public void FreshPlannerStatistics_NonCancellationSqliteFailureRollsBackAndGraphContinues()
    {
        SeedPlannerStatisticsFixture();
        ResetPlannerStatistics();
        var previousStatisticsHook = DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting;
        var phases = new List<string>();
        try
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = (connection, phase) =>
            {
                phases.Add(phase);
                if (phase == "post_load_statistics_started")
                {
                    ExecuteNonQuery(connection, """
                        INSERT INTO codeindex_meta(key, value)
                        VALUES ('fresh_statistics_savepoint_probe', 'pending');
                        ANALYZE main.files;
                        """);
                    throw new SqliteException("forced fresh statistics failure", 1);
                }
                previousStatisticsHook?.Invoke(connection, phase);
            };

            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartRecoverable(
                _writer,
                enabled: true,
                refreshPlannerStatisticsBeforeCandidatePopulation: true);
            Assert.NotNull(guard);
            guard.PrepareForDeferredGraphRefresh();

            _writer.RefreshMutualRecursionFlags(
                stampReferenceIdentityContractReady: false,
                referenceSecondaryIndexBulkLoad: guard);

            Assert.Equal(
                ["post_load_statistics_started", "post_load_statistics_failed"],
                phases);
            Assert.Equal(
                0,
                ReadScalarLong(
                    _db.Connection,
                    "SELECT COUNT(*) FROM codeindex_meta WHERE key = 'fresh_statistics_savepoint_probe'"));
            Assert.Empty(ReadPlannerStatisticTables(_db.Connection));
            Assert.Equal(
                1,
                ReadScalarLong(
                    _db.Connection,
                    "SELECT COUNT(*) FROM symbol_references WHERE resolution_state IS NOT NULL"));
            guard.Complete();
        }
        finally
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = previousStatisticsHook;
        }
    }

    [Fact]
    public void FreshPlannerStatistics_CancellationPropagatesAndRollsBackSavepoint()
    {
        SeedPlannerStatisticsFixture();
        ResetPlannerStatistics();
        var previousStatisticsHook = DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting;
        using var cancellation = new CancellationTokenSource();
        var phases = new List<string>();
        try
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = (connection, phase) =>
            {
                phases.Add(phase);
                if (phase == "post_load_statistics_started")
                    cancellation.Cancel();
                previousStatisticsHook?.Invoke(connection, phase);
            };

            using var transaction = _writer.BeginTransaction();
            using var guard = ReferenceSecondaryIndexBulkLoadGuard.StartTransactional(
                _writer,
                enabled: true,
                refreshPlannerStatisticsBeforeCandidatePopulation: true);
            Assert.NotNull(guard);
            guard.PrepareForDeferredGraphRefresh();

            var exception = Assert.Throws<OperationCanceledException>(
                () => guard.PrepareForCandidatePopulation(cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(["post_load_statistics_started"], phases);
            Assert.Empty(ReadPlannerStatisticTables(_db.Connection));
        }
        finally
        {
            DbWriter.FreshBulkLoadPlannerStatisticsStateForTesting = previousStatisticsHook;
        }
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
        AssertInitialBulkPersistenceSchema(_db.Connection);

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
        abandonedGuard.PrepareForCandidatePopulation();
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

    private static void AssertInitialBulkPersistenceSchema(SqliteConnection connection)
    {
        var names = ReadReferenceIndexNames(connection);
        foreach (var definition in ReferenceSecondaryIndexSql.DeferredGraphPreparation)
            Assert.DoesNotContain(definition.Name, names);
        foreach (var definition in ReferenceSecondaryIndexSql.CandidatePopulationDeferred)
            Assert.Contains(definition.Name, names);
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

    private void SeedPlannerStatisticsFixture()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fresh-statistics.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 3,
            Modified = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "fresh-statistics",
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
            },
        ]);
        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Run",
                ReferenceKind = "call",
                Line = 2,
                Column = 1,
                Context = "Run();",
                ContainerName = "Run",
            },
        ],
        refreshMutualRecursionFlags: false);
    }

    private void ResetPlannerStatistics()
    {
        ExecuteNonQuery(_db.Connection, """
            ANALYZE main.files;
            ANALYZE main.symbols;
            ANALYZE main.symbol_references;
            DELETE FROM sqlite_stat1;
            """);
    }

    private static string[] ReadPlannerStatisticTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT tbl FROM sqlite_stat1 ORDER BY tbl";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables.ToArray();
    }

    private static string[] ReadPlannerStatisticIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx FROM sqlite_stat1 WHERE idx IS NOT NULL ORDER BY idx";
        using var reader = command.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
            indexes.Add(reader.GetString(0));
        return indexes.ToArray();
    }

    private static long ReadScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
