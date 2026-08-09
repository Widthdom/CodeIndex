using System.Collections.Concurrent;
using CodeIndex.Cli;
using CodeIndex.Database;
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
        var snapshots = new ConcurrentQueue<ReferenceIndexStageSnapshot>();
        var scopeSnapshots = new ConcurrentQueue<DbWriter.ReferenceGraphRefreshScopeStats>();
        SqliteConnection? activeConnection = null;
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

            var args = rebuild
                ? new[] { projectRoot, "--rebuild", "--yes", "--json", "--quiet" }
                : new[] { projectRoot, "--json", "--quiet" };
            var (exitCode, json) = RunAndCaptureJson(args);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, missingConnectionObservations);

            var captured = snapshots.ToArray();
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "dropped"));
            Assert.Equal(2, captured.Count(snapshot => snapshot.Stage == "insert_references"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "identity_started"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "graph_required_restored"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "mutual_started"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "restored"));
            Assert.Equal("dropped", captured[0].Stage);
            Assert.Equal("restored", captured[^1].Stage);
            Assert.Equal(
                ["dropped", "identity_started", "graph_required_restored", "mutual_started", "restored"],
                captured
                    .Where(snapshot => snapshot.Stage != "insert_references")
                    .Select(snapshot => snapshot.Stage));

            var requiredNames = GetRequiredReferenceIndexNames();
            var graphNames = GetGraphFinalizationReferenceIndexNames();
            var allNames = GetAllReferenceIndexNames();
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "dropped" or "insert_references" or "identity_started"),
                snapshot => Assert.Equal(requiredNames, snapshot.Names));
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "graph_required_restored" or "mutual_started"),
                snapshot => Assert.Equal(graphNames, snapshot.Names));
            Assert.Equal(allNames, captured[^1].Names);
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
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("dropped")]
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
                    ? GetRequiredReferenceIndexNames()
                    : GetGraphFinalizationReferenceIndexNames(),
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
            var relativePaths = Enumerable
                .Range(
                    0,
                    IndexCommandRunner.UpdateReferenceSecondaryIndexBulkLoadMinimumTargetCount)
                .Select(index => $"source_{index:D2}.py")
                .ToArray();
            foreach (var relativePath in relativePaths)
            {
                var stem = Path.GetFileNameWithoutExtension(relativePath);
                File.WriteAllText(
                    Path.Combine(projectRoot, relativePath),
                    $"def {stem}():\n    return 1\n\ndef call_{stem}():\n    return {stem}()\n");
            }

            var (seedExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, seedExitCode);
            foreach (var relativePath in relativePaths)
                File.AppendAllText(Path.Combine(projectRoot, relativePath), "\n# changed\n");

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
                ["dropped", "identity_started", "graph_required_restored", "mutual_started", "restored"],
                captured
                    .Where(snapshot => snapshot.Stage != "insert_references")
                    .Select(snapshot => snapshot.Stage));
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "dropped" or "insert_references" or "identity_started"),
                snapshot => Assert.Equal(GetRequiredReferenceIndexNames(), snapshot.Names));
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "graph_required_restored" or "mutual_started"),
                snapshot => Assert.Equal(GetGraphFinalizationReferenceIndexNames(), snapshot.Names));
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

    private static string[] GetGraphFinalizationReferenceIndexNames()
        => GetRequiredReferenceIndexNames()
            .Concat(ReferenceSecondaryIndexBulkLoadGuard.GraphFinalizationIndexNames)
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
    }

    private sealed record ReferenceIndexStageSnapshot(string Stage, string[] Names);
}
