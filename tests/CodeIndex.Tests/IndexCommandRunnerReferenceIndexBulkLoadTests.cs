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
        var snapshots = new ConcurrentQueue<ReferenceIndexStageSnapshot>();
        SqliteConnection? activeConnection = null;
        var missingConnectionObservations = 0;
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
                var connection = Volatile.Read(ref activeConnection);
                if (connection == null)
                    Interlocked.Increment(ref missingConnectionObservations);
                else
                    snapshots.Enqueue(CaptureReferenceIndexSnapshot("reference_graph", connection));
                previousGraphHook?.Invoke();
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
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "restored"));
            Assert.Equal(1, captured.Count(snapshot => snapshot.Stage == "reference_graph"));
            Assert.Equal("dropped", captured[0].Stage);
            Assert.Equal("restored", captured[^2].Stage);
            Assert.Equal("reference_graph", captured[^1].Stage);

            var requiredNames = GetRequiredReferenceIndexNames();
            var allNames = GetAllReferenceIndexNames();
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "dropped" or "insert_references"),
                snapshot => Assert.Equal(requiredNames, snapshot.Names));
            Assert.Equal(allNames, captured[^2].Names);
            Assert.Equal(allNames, captured[^1].Names);
            Assert.Equal(2, CountMutualRecursionReferences(dbPath));
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DbWriter.MutualRecursionRefreshForTesting = previousGraphHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FreshFullScan_FailureAfterReferenceIndexDropRollsBackSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_index_bulk_load_rollback");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        ReferenceIndexStageSnapshot? droppedSnapshot = null;
        var statePhases = new List<string>();
        try
        {
            WriteReferenceIndexCycleFixture(projectRoot);
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = (connection, phase) =>
            {
                statePhases.Add(phase);
                if (string.Equals(phase, "dropped", StringComparison.Ordinal))
                {
                    droppedSnapshot = CaptureReferenceIndexSnapshot(phase, connection);
                    previousStateHook?.Invoke(connection, phase);
                    throw new InvalidOperationException("Stop after dropping reference indexes.");
                }

                previousStateHook?.Invoke(connection, phase);
            };

            var exception = Assert.Throws<InvalidOperationException>(
                () => RunAndCaptureJson([projectRoot, "--json", "--quiet"]));

            Assert.Equal("Stop after dropping reference indexes.", exception.Message);
            Assert.Equal(["dropped"], statePhases);
            Assert.NotNull(droppedSnapshot);
            Assert.Equal(GetRequiredReferenceIndexNames(), droppedSnapshot!.Names);
            Assert.True(File.Exists(dbPath));

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Assert.Equal(
                GetAllReferenceIndexNames(),
                CaptureReferenceIndexSnapshot("after_rollback", connection).Names);
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
        var previousStateHook = DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting;
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var previousGraphHook = DbWriter.MutualRecursionRefreshForTesting;
        var snapshots = new ConcurrentQueue<ReferenceIndexStageSnapshot>();
        SqliteConnection? activeConnection = null;
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
                var connection = Volatile.Read(ref activeConnection);
                if (connection != null)
                    snapshots.Enqueue(CaptureReferenceIndexSnapshot("reference_graph", connection));
                previousGraphHook?.Invoke();
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
            Assert.Equal("restored", captured[^2].Stage);
            Assert.Equal("reference_graph", captured[^1].Stage);
            Assert.All(
                captured.Where(snapshot => snapshot.Stage is "dropped" or "insert_references"),
                snapshot => Assert.Equal(GetRequiredReferenceIndexNames(), snapshot.Names));
            Assert.Equal(GetAllReferenceIndexNames(), captured[^2].Names);
            Assert.Equal(GetAllReferenceIndexNames(), captured[^1].Names);
        }
        finally
        {
            DbWriter.ReferenceSecondaryIndexBulkLoadStateForTesting = previousStateHook;
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DbWriter.MutualRecursionRefreshForTesting = previousGraphHook;
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
            WHERE type = 'index' AND tbl_name = 'symbol_references'
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
