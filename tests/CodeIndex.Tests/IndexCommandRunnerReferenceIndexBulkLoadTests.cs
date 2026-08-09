using System.Collections.Concurrent;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Theory]
    [InlineData(63, 63, false)]
    [InlineData(64, 106, true)]
    [InlineData(64, 107, false)]
    [InlineData(599, 1_000, false)]
    [InlineData(600, 1_000, true)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    [InlineData(64, 0, false)]
    public void ShouldUseUpdateReferenceSecondaryIndexBulkLoad_UsesBoundedSixtyPercentThreshold(
        int targetCount,
        int indexedFileCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            IndexCommandRunner.ShouldUseUpdateReferenceSecondaryIndexBulkLoad(
                targetCount,
                indexedFileCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_FreshAndRebuildFullScan_DefersReferenceIndexesUntilGraphFinalization(bool rebuild)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_bulk_load");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var previousGraphHook = DbWriter.MutualRecursionRefreshForTesting;
        var previousScopeHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        var previousHotspotHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var snapshots = new ConcurrentQueue<ReferenceIndexStageSnapshot>();
        var scopeSnapshots = new ConcurrentQueue<DbWriter.ReferenceGraphRefreshScopeStats>();
        SqliteConnection? activeConnection = null;
        string[]? hotspotIndexNamesDuringRefresh = null;
        var missingConnectionObservations = 0;
        var refreshCount = 0;
        try
        {
            WriteReferenceIndexCycleFixture(projectRoot);
            if (rebuild)
            {
                var (seedExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
                Assert.Equal(CommandExitCodes.Success, seedExitCode);
            }

            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                Volatile.Write(ref activeConnection, connection);
                snapshots.Enqueue(CaptureReferenceIndexSnapshot(phase, connection));
                previousStateHook?.Invoke(connection, phase);
            };
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                previousStatementHook?.Invoke(statement);
                if (!string.Equals(statement.Operation, "insert_references", StringComparison.Ordinal))
                    return;

                var connection = Volatile.Read(ref activeConnection);
                if (connection == null)
                {
                    Interlocked.Increment(ref missingConnectionObservations);
                    return;
                }

                snapshots.Enqueue(CaptureReferenceIndexSnapshot("insert_references", connection));
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                Interlocked.Increment(ref refreshCount);
                previousGraphHook?.Invoke();
            };
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats =>
            {
                scopeSnapshots.Enqueue(stats);
                previousScopeHook?.Invoke(stats);
            };
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                var connection = Volatile.Read(ref activeConnection);
                Assert.NotNull(connection);
                hotspotIndexNamesDuringRefresh = ReadHotspotReferenceIndexNames(connection!);
                previousHotspotHook?.Invoke();
            };

            var args = rebuild
                ? new[] { projectRoot, "--rebuild", "--yes", "--json", "--quiet" }
                : new[] { projectRoot, "--json", "--quiet" };
            var (exitCode, json) = RunAndCaptureJson(args);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, missingConnectionObservations);

            var captured = snapshots.ToArray();
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "dropped"));
            Assert.Contains(captured, snapshot => snapshot.Stage == "insert_references");
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "deferred_graph_prepared"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "candidate_deferred"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "identity_started"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "graph_required_restored"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "mutual_started"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "restored"));
            Assert.Equal("dropped", captured[0].Stage);
            Assert.Equal("restored", captured[^1].Stage);
            Assert.Equal(
                ["dropped", "deferred_graph_prepared", "candidate_deferred", "identity_started", "graph_required_restored", "mutual_started", "restored"],
                captured
                    .Where(snapshot => snapshot.Stage != "insert_references")
                    .Select(snapshot => snapshot.Stage));

            var requiredNames = GetRequiredReferenceIndexNames();
            var initialBulkNames = GetInitialBulkPersistenceReferenceIndexNames();
            var deferredGraphNames = GetDeferredGraphPreparationReferenceIndexNames();
            var allNames = GetAllReferenceIndexNames();
            Assert.Equal(initialBulkNames, captured.First(snapshot => snapshot.Stage == "dropped").Names);
            Assert.All(
                captured.Where(snapshot => snapshot.Stage == "insert_references"),
                snapshot => Assert.Equal(initialBulkNames, snapshot.Names));
            Assert.Equal(
                allNames,
                captured.First(snapshot => snapshot.Stage == "deferred_graph_prepared").Names);
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "candidate_deferred" or "identity_started" or "graph_required_restored" or "mutual_started"),
                snapshot =>
                {
                    Assert.DoesNotContain("idx_symbol_ref_candidates_symbol", snapshot.Names);
                    Assert.Equal(deferredGraphNames, snapshot.Names);
                });
            Assert.Equal(allNames, captured[^1].Names);
            Assert.Empty(Assert.IsType<string[]>(hotspotIndexNamesDuringRefresh));
            using (var completedConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                completedConnection.Open();
                Assert.Equal(
                    GetHotspotReferenceIndexNames(),
                    ReadHotspotReferenceIndexNames(completedConnection));
            }
            Assert.Equal(1, refreshCount);
            var scope = Assert.Single(scopeSnapshots);
            Assert.True(scope.UsedFullRefresh);
            Assert.Equal(2, CountMutualRecursionReferences(dbPath));
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DbWriter.MutualRecursionRefreshForTesting = previousGraphHook;
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousScopeHook;
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousHotspotHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("dropped")]
    [InlineData("candidate_deferred")]
    [InlineData("graph_required_restored")]
    public void Run_FreshFullScan_FailureDuringStagedReferenceIndexLifecycleRollsBackSchema(
        string failurePhase)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_bulk_load_rollback");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        ReferenceIndexStageSnapshot? failureSnapshot = null;
        var statePhases = new List<string>();
        try
        {
            WriteReferenceIndexCycleFixture(projectRoot);
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                statePhases.Add(phase);
                if (string.Equals(phase, failurePhase, StringComparison.Ordinal))
                {
                    failureSnapshot = CaptureReferenceIndexSnapshot(phase, connection);
                    previousStateHook?.Invoke(connection, phase);
                    throw new InvalidOperationException($"Stop during {failurePhase}.");
                }

                previousStateHook?.Invoke(connection, phase);
            };

            var exception = Assert.Throws<InvalidOperationException>(
                () => RunAndCaptureJson([projectRoot, "--json", "--quiet"]));

            Assert.Equal($"Stop during {failurePhase}.", exception.Message);
            Assert.Equal(failurePhase, statePhases[^1]);
            Assert.NotNull(failureSnapshot);
            Assert.Equal(
                failurePhase == "dropped"
                    ? GetInitialBulkPersistenceReferenceIndexNames()
                    : GetDeferredGraphPreparationReferenceIndexNames(),
                failureSnapshot!.Names);
            Assert.True(File.Exists(dbPath));

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Assert.Equal(
                GetAllReferenceIndexNames(),
                CaptureReferenceIndexSnapshot("after_rollback", connection).Names);
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(0L, (long)countCommand.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_HighChurnExistingIndex_DefersReferenceIndexesUntilGraphFinalization(
        bool scopedUpdate)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_update_bulk_load");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var previousGraphHook = DbWriter.MutualRecursionRefreshForTesting;
        var previousScopeHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        var previousHotspotHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var snapshots = new ConcurrentQueue<ReferenceIndexStageSnapshot>();
        var scopeSnapshots = new ConcurrentQueue<DbWriter.ReferenceGraphRefreshScopeStats>();
        SqliteConnection? activeConnection = null;
        string[]? hotspotIndexNamesDuringRefresh = null;
        try
        {
            var relativePaths = WriteHighCardinalityTypeScriptReferenceFixture(projectRoot);

            var (seedExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, seedExitCode);
            foreach (var relativePath in relativePaths)
                File.AppendAllText(Path.Combine(projectRoot, relativePath), "\n// changed\n");

            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                Volatile.Write(ref activeConnection, connection);
                snapshots.Enqueue(CaptureReferenceIndexSnapshot(phase, connection));
                previousStateHook?.Invoke(connection, phase);
            };
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                previousStatementHook?.Invoke(statement);
                if (!string.Equals(statement.Operation, "insert_references", StringComparison.Ordinal))
                    return;

                var connection = Volatile.Read(ref activeConnection);
                if (connection != null)
                    snapshots.Enqueue(CaptureReferenceIndexSnapshot("insert_references", connection));
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                previousGraphHook?.Invoke();
            };
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats =>
            {
                scopeSnapshots.Enqueue(stats);
                previousScopeHook?.Invoke(stats);
            };
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                var connection = Volatile.Read(ref activeConnection);
                Assert.NotNull(connection);
                hotspotIndexNamesDuringRefresh = ReadHotspotReferenceIndexNames(connection!);
                previousHotspotHook?.Invoke();
            };

            var args = new List<string>(relativePaths.Length + 5) { projectRoot };
            if (scopedUpdate)
            {
                args.Add("--files");
                args.AddRange(relativePaths);
            }
            args.Add("--json");
            args.Add("--quiet");
            var (exitCode, json) = RunAndCaptureJson(args.ToArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            var captured = snapshots.ToArray();
            Assert.Equal("dropped", captured[0].Stage);
            Assert.Contains(captured, snapshot => snapshot.Stage == "insert_references");
            Assert.Equal("restored", captured[^1].Stage);
            Assert.Equal(
                ["dropped", "deferred_graph_prepared", "candidate_deferred", "identity_started", "graph_required_restored", "mutual_started", "restored"],
                captured
                    .Where(snapshot => snapshot.Stage != "insert_references")
                    .Select(snapshot => snapshot.Stage));
            Assert.Equal(
                GetInitialBulkPersistenceReferenceIndexNames(),
                captured.First(snapshot => snapshot.Stage == "dropped").Names);
            Assert.All(
                captured.Where(snapshot => snapshot.Stage == "insert_references"),
                snapshot => Assert.Equal(GetInitialBulkPersistenceReferenceIndexNames(), snapshot.Names));
            Assert.Equal(
                GetAllReferenceIndexNames(),
                captured.First(snapshot => snapshot.Stage == "deferred_graph_prepared").Names);
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "candidate_deferred" or "identity_started" or "graph_required_restored" or "mutual_started"),
                snapshot =>
                {
                    Assert.DoesNotContain("idx_symbol_ref_candidates_symbol", snapshot.Names);
                    Assert.Equal(GetDeferredGraphPreparationReferenceIndexNames(), snapshot.Names);
                });
            Assert.Equal(GetAllReferenceIndexNames(), captured[^1].Names);
            Assert.Empty(Assert.IsType<string[]>(hotspotIndexNamesDuringRefresh));
            using (var completedConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                completedConnection.Open();
                Assert.Equal(
                    GetHotspotReferenceIndexNames(),
                    ReadHotspotReferenceIndexNames(completedConnection));
            }
            var scope = Assert.Single(scopeSnapshots);
            Assert.True(scope.UsedFullRefresh);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DbWriter.MutualRecursionRefreshForTesting = previousGraphHook;
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousScopeHook;
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousHotspotHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_HighCardinalityNoOpScopedUpdate_KeepsCandidateReverseIndex()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_noop_update");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousGraphHook = DbWriter.MutualRecursionRefreshForTesting;
        var previousSnapshotReadHook = DbWriter.ReusableStatSnapshotReadForTesting;
        var previousSnapshotFilterModeHook = DbWriter.ReusableStatSnapshotFilterModeForTesting;
        var snapshots = new List<ReferenceIndexStageSnapshot>();
        var snapshotFilterModes = new List<string>();
        var graphRefreshCount = 0;
        var snapshotReadCount = 0;
        try
        {
            var relativePaths = WriteHighCardinalityTypeScriptReferenceFixture(projectRoot);

            var (seedExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, seedExitCode);
            using (var seededConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                seededConnection.Open();
                using var countCommand = seededConnection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM symbol_reference_candidates";
                Assert.True((long)countCommand.ExecuteScalar()! > 0);
            }

            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                snapshots.Add(CaptureReferenceIndexSnapshot(phase, connection));
                previousStateHook?.Invoke(connection, phase);
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                graphRefreshCount++;
                previousGraphHook?.Invoke();
            };
            DbWriter.ReusableStatSnapshotReadForTesting = () =>
            {
                snapshotReadCount++;
                previousSnapshotReadHook?.Invoke();
            };
            DbWriter.ReusableStatSnapshotFilterModeForTesting = mode =>
            {
                snapshotFilterModes.Add(mode);
                previousSnapshotFilterModeHook?.Invoke(mode);
            };

            var args = new List<string>(relativePaths.Length + 4)
            {
                projectRoot,
                "--files",
            };
            args.AddRange(relativePaths);
            args.Add("--json");
            args.Add("--quiet");
            var (exitCode, json) = RunAndCaptureJson(args.ToArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, graphRefreshCount);
            Assert.Empty(snapshots);
            Assert.Equal(1, snapshotReadCount);
            Assert.Equal(["candidate_paths"], snapshotFilterModes);
            using var completedConnection = new SqliteConnection($"Data Source={dbPath}");
            completedConnection.Open();
            Assert.Equal(
                GetAllReferenceIndexNames(),
                CaptureReferenceIndexSnapshot("completed", completedConnection).Names);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DbWriter.MutualRecursionRefreshForTesting = previousGraphHook;
            DbWriter.ReusableStatSnapshotReadForTesting = previousSnapshotReadHook;
            DbWriter.ReusableStatSnapshotFilterModeForTesting = previousSnapshotFilterModeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_HighCardinalityStatPreflightRace_UsesAuthoritativeFileLoop()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_preflight_race");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousLookupHook = IndexedFileStatReuse.LookupForTesting;
        var referenceIndexPhases = new List<string>();
        var firstPathLookupCount = 0;
        try
        {
            var relativePaths = WriteHighCardinalityTypeScriptReferenceFixture(projectRoot);
            var racedRelativePath = relativePaths[0];
            var racedAbsolutePath = Path.Combine(projectRoot, racedRelativePath);
            var (seedExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, seedExitCode);

            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                referenceIndexPhases.Add(phase);
                previousStateHook?.Invoke(connection, phase);
            };
            IndexedFileStatReuse.LookupForTesting = relativePath =>
            {
                previousLookupHook?.Invoke(relativePath);
                if (!string.Equals(relativePath, racedRelativePath, StringComparison.Ordinal)
                    || ++firstPathLookupCount != 2)
                {
                    return;
                }

                File.AppendAllText(
                    racedAbsolutePath,
                    "export function racedAfterPreflight(): number { return source_00(); }\n");
            };

            var args = new List<string>(relativePaths.Length + 4)
            {
                projectRoot,
                "--files",
            };
            args.AddRange(relativePaths);
            args.Add("--json");
            args.Add("--quiet");
            var (exitCode, json) = RunAndCaptureJson(args.ToArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(2, firstPathLookupCount);
            Assert.Empty(referenceIndexPhases);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var symbolCommand = connection.CreateCommand();
            symbolCommand.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'racedAfterPreflight'";
            Assert.Equal(1L, (long)symbolCommand.ExecuteScalar()!);
            Assert.Equal(
                GetAllReferenceIndexNames(),
                CaptureReferenceIndexSnapshot("completed", connection).Names);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            IndexedFileStatReuse.LookupForTesting = previousLookupHook;
            DeleteDirectory(projectRoot);
        }
    }

    private static ReferenceIndexStageSnapshot CaptureReferenceIndexSnapshot(
        string stage,
        SqliteConnection connection)
    {
        var knownNames = GetAllReferenceIndexNames().ToHashSet(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND tbl_name IN ('symbol_references', 'symbol_reference_candidates')
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

        return new ReferenceIndexStageSnapshot(stage, names.ToArray());
    }

    private static string[] GetRequiredReferenceIndexNames()
        => ReferenceSecondaryIndexSql.RawPersistenceRequired
            .Select(static definition => definition.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetAllReferenceIndexNames()
        => GetRequiredReferenceIndexNames()
            .Concat(ReferenceSecondaryIndexBulkLoadGuard.IndexNames)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetInitialBulkPersistenceReferenceIndexNames()
        => GetRequiredReferenceIndexNames()
            .Concat(ReferenceSecondaryIndexBulkLoadGuard.CandidatePopulationIndexNames)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetGraphFinalizationReferenceIndexNames()
        => GetRequiredReferenceIndexNames()
            .Concat(ReferenceSecondaryIndexBulkLoadGuard.GraphFinalizationIndexNames)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetDeferredGraphPreparationReferenceIndexNames()
        => GetRequiredReferenceIndexNames()
            .Concat(ReferenceSecondaryIndexBulkLoadGuard.DeferredGraphPreparationIndexNames)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetHotspotReferenceIndexNames()
        => HotspotReferenceAggregateSql.Indexes
            .Select(static index => index.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadHotspotReferenceIndexNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND tbl_name = 'hotspot_reference_counts'
              AND name NOT LIKE 'sqlite_autoindex_%'
            ORDER BY name
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static void WriteReferenceIndexCycleFixture(string projectRoot)
    {
        File.WriteAllText(
            Path.Combine(projectRoot, "cycle_a.cs"),
            "public static class BulkCycleA { public static void CallA() { CallB(); } }\n");
        File.WriteAllText(
            Path.Combine(projectRoot, "cycle_b.cs"),
            "public static class BulkCycleB { public static void CallB() { CallA(); } }\n");
        for (var index = 0;
             index < IndexCommandRunner.UpdateReferenceSecondaryIndexBulkLoadMinimumTargetCount;
             index++)
        {
            File.WriteAllText(
                Path.Combine(projectRoot, $"augmentation_{index:D2}.ts"),
                $"export function bulkTarget{index:D2}(): number {{ return {index}; }}\n"
                + $"export function bulkCaller{index:D2}(): number {{ return bulkTarget{index:D2}(); }}\n");
        }
    }

    private static string[] WriteHighCardinalityTypeScriptReferenceFixture(string projectRoot)
    {
        var relativePaths = Enumerable
            .Range(0, IndexCommandRunner.UpdateReferenceSecondaryIndexBulkLoadMinimumTargetCount)
            .Select(index => $"source_{index:D2}.ts")
            .ToArray();
        foreach (var relativePath in relativePaths)
        {
            var stem = Path.GetFileNameWithoutExtension(relativePath);
            File.WriteAllText(
                Path.Combine(projectRoot, relativePath),
                $"export function {stem}(): number {{ return 1; }}\n"
                + $"export function call_{stem}(): number {{ return {stem}(); }}\n");
        }

        return relativePaths;
    }

    private sealed record ReferenceIndexStageSnapshot(string Stage, string[] Names);
}
