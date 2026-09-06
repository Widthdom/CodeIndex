using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_DbDiagnosticsMatchStandaloneAndPreserveDatabaseProvenance_Issue5282()
    {
        using var parent = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_diagnostics_parent_5282");
        using var child = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_diagnostics_child_5282");
        var parentDbPath = TestProjectHelper.CreateProjectDb(parent.Root);
        var childDbPath = TestProjectHelper.CreateProjectDb(child.Root);
        SetBatchDbUserVersionIssue5282(parentDbPath, 5282);
        SetBatchDbUserVersionIssue5282(childDbPath, 5283);

        var (schemaExitCode, schemaStdout, schemaStderr) = CaptureConsole(() =>
            DbCommandRunner.Run(
                ["schema", "--summary-only", "--db", parentDbPath, "--json"],
                _jsonOptions));
        var (integrityExitCode, integrityStdout, integrityStderr) = CaptureConsole(() =>
            DbCommandRunner.Run(
                ["integrity", "--db", childDbPath, "--json"],
                _jsonOptions));
        using var schemaDocument = JsonDocument.Parse(schemaStdout);
        using var integrityDocument = JsonDocument.Parse(integrityStdout);
        Assert.Equal(CommandExitCodes.Success, schemaExitCode);
        Assert.Equal(CommandExitCodes.Success, integrityExitCode);
        Assert.Equal(string.Empty, schemaStderr);
        Assert.Equal(string.Empty, integrityStderr);

        var input = string.Join(
            "\n",
            JsonSerializer.Serialize(new[] { "db", "schema", "--summary-only", "--json" }),
            JsonSerializer.Serialize(new[] { "db", "integrity", "--db", childDbPath, "--json" }),
            string.Empty);

        foreach (var parallelArgs in new[]
                 {
                     Array.Empty<string>(),
                     new[] { "--parallel", "2" },
                 })
        {
            var batchArgs = new[] { "--db", parentDbPath, "--json-summary" }
                .Concat(parallelArgs)
                .ToArray();
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(batchArgs, _jsonOptions));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(3, lines.Count);
                Assert.Equal("db", lines[0].RootElement.GetProperty("command").GetString());
                Assert.Equal("db", lines[1].RootElement.GetProperty("command").GetString());
                Assert.Equal(
                    schemaDocument.RootElement.GetRawText(),
                    lines[0].RootElement.GetProperty("result").GetRawText());
                Assert.Equal(
                    integrityDocument.RootElement.GetRawText(),
                    lines[1].RootElement.GetProperty("result").GetRawText());
                Assert.Equal(
                    parallelArgs.Length == 0 ? 1 : 2,
                    lines[2].RootElement.GetProperty("parallelism").GetInt32());
                Assert.Equal(0, lines[2].RootElement.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
    }

    [Fact]
    public void RunBatch_DbDiagnosticsKeepInputOrderWhenParallelCompletionReverses_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_order_5282");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var integrityCompleted = new ManualResetEventSlim();
        var completions = new ConcurrentQueue<string>();
        DbCommandRunner.MaintenanceProgressForTesting = (operation, phase) =>
        {
            if (operation == "schema" && phase == "start"
                && !integrityCompleted.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("The parallel integrity diagnostic did not complete.");
            }

            if (phase != "complete")
                return;
            completions.Enqueue(operation);
            if (operation == "integrity_check")
                integrityCompleted.Set();
        };

        try
        {
            var input = string.Join(
                "\n",
                JsonSerializer.Serialize(new[] { "db", "schema", "--summary-only", "--json" }),
                JsonSerializer.Serialize(new[] { "db", "integrity", "--json" }),
                string.Empty);
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(new[] { "integrity_check", "schema" }, completions.ToArray());
                Assert.Equal("schema", lines[0].RootElement.GetProperty("arguments")[0].GetString());
                Assert.Equal("integrity", lines[1].RootElement.GetProperty("arguments")[0].GetString());
                Assert.Equal("batch_summary", lines[2].RootElement.GetProperty("record").GetString());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            DbCommandRunner.MaintenanceProgressForTesting = null;
        }
    }

    [Fact]
    public void RunBatch_DbDiagnosticsPreserveStructuredMissingAndCorruptErrors_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_errors_5282");
        var parentDbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var missingDbPath = TestProjectHelper.CreateTempDbPath("cdidx_batch_db_missing_5282");
        var corruptDbPath = TestProjectHelper.CreateTempDbPath("cdidx_batch_db_corrupt_5282");
        WriteCorruptBatchDbIssue5282(corruptDbPath);

        try
        {
            var input = string.Join(
                "\n",
                JsonSerializer.Serialize(new[] { "db", "schema", "--db", missingDbPath, "--json" }),
                JsonSerializer.Serialize(new[] { "db", "integrity", "--db", corruptDbPath, "--json" }),
                string.Empty);
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", parentDbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.NotEqual(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(3, lines.Count);
                var missingError = lines[0].RootElement.GetProperty("error");
                Assert.Equal(CommandErrorCodes.DbNotFound, missingError.GetProperty("error_code").GetString());
                Assert.NotEqual("batch_command_not_allowed", missingError.GetProperty("category").GetString());
                var corruptError = lines[1].RootElement.GetProperty("error");
                Assert.Equal(CommandErrorCodes.DbNotDatabase, corruptError.GetProperty("error_code").GetString());
                Assert.Equal("database_not_a_database", corruptError.GetProperty("category").GetString());
                Assert.False(lines[0].RootElement.TryGetProperty("raw_streams", out _));
                Assert.False(lines[1].RootElement.TryGetProperty("raw_streams", out _));
                Assert.Equal(2, lines[2].RootElement.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            File.Delete(corruptDbPath);
        }
    }

    [Fact]
    public void RunBatch_RejectsDbAliasesUnknownModesAndMutations_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_policy_5282");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var rejected = new[]
        {
            new[] { "db" },
            new[] { "db", "--integrity-check", "--json" },
            new[] { "db", "integrity", "--integrity-check", "--json" },
            new[] { "db", "schema", "unknown", "--json" },
            new[] { "db", "schema", "integrity", "--json" },
            new[] { "db", "schema", "--apply", "--json" },
            new[] { "db", "integrity", "--dry-run", "--json" },
            new[] { "db", "prune", "--dry-run", "--json" },
            new[] { "db", "prune", "--apply", "--json" },
            new[] { "db", "checkpoint", "blocked", "--json" },
            new[] { "db", "checkpoints", "--list", "--json" },
            new[] { "db", "restore", "blocked", "--json" },
            new[] { "db", "restore-backups", "--list", "--json" },
        };
        var input = string.Join("\n", rejected.Select(args => JsonSerializer.Serialize(args))) + "\n";
        var checkpointDirectory = dbPath + ".checkpoints";
        var originalLength = new FileInfo(dbPath).Length;
        Assert.False(Directory.Exists(checkpointDirectory));

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", "3"],
                _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(rejected.Length + 1, lines.Count);
            for (var i = 0; i < rejected.Length; i++)
            {
                Assert.Equal("error", lines[i].RootElement.GetProperty("status").GetString());
                Assert.Equal(
                    "batch_command_not_allowed",
                    lines[i].RootElement.GetProperty("error").GetProperty("category").GetString());
            }
            Assert.Equal(rejected.Length, lines[^1].RootElement.GetProperty("command_failures").GetInt32());
            Assert.False(Directory.Exists(checkpointDirectory));
            Assert.Equal(originalLength, new FileInfo(dbPath).Length);
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_DbSchemaHonorsAggregateOutputBudget_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_budget_5282");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = JsonSerializer.Serialize(new[] { "db", "schema", "--json" }) + "\n";

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--max-output-chars", "4096"],
                _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, lines.Count);
            Assert.Equal(
                "batch_output_limit",
                lines[0].RootElement.GetProperty("error").GetProperty("category").GetString());
            Assert.True(lines[0].RootElement.GetProperty("arguments_omitted").GetBoolean());
            Assert.True(lines[1].RootElement.GetProperty("output_limit_reached").GetBoolean());
            Assert.True(stdout.Length <= 4096);
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_DbIntegrityObservesCallerCancellation_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_cancel_5282");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        DbCommandRunner.MaintenanceProgressForTesting = (operation, phase) =>
        {
            if (operation == "integrity_check" && phase == "start")
                cancellation.Cancel();
        };

        try
        {
            var input = JsonSerializer.Serialize(new[] { "db", "integrity", "--json" }) + "\n";
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(2, lines.Count);
                Assert.Equal(
                    CommandErrorCodes.Interrupted,
                    lines[0].RootElement.GetProperty("error").GetProperty("error_code").GetString());
                Assert.Equal(
                    CommandExitCodes.CancelledBySignal,
                    lines[1].RootElement.GetProperty("exit_code").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            DbCommandRunner.MaintenanceProgressForTesting = null;
        }
    }

    [Fact]
    public void RunBatch_DbIntegrityInterruptsExecutingSqliteScan_Issue5282()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_interrupt_5282");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        DbCommandRunner.IntegrityCheckCommandTextForTesting = """
            WITH RECURSIVE numbers(value) AS (
                VALUES(0)
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 100000000
            )
            SELECT CASE WHEN SUM(value) >= 0 THEN 'ok' ELSE 'failed' END FROM numbers;
            """;
        DbCommandRunner.MaintenanceProgressForTesting = (operation, phase) =>
        {
            if (operation == "integrity_check" && phase == "read_rows")
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        };

        try
        {
            var input = JsonSerializer.Serialize(new[] { "db", "integrity", "--json" }) + "\n";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token));
            stopwatch.Stop();
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(2, lines.Count);
                Assert.Equal(
                    CommandErrorCodes.Interrupted,
                    lines[0].RootElement.GetProperty("error").GetProperty("error_code").GetString());
                Assert.Equal(
                    CommandExitCodes.CancelledBySignal,
                    lines[1].RootElement.GetProperty("exit_code").GetInt32());
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"SQLite interruption took {stopwatch.Elapsed}.");
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            DbCommandRunner.IntegrityCheckCommandTextForTesting = null;
            DbCommandRunner.MaintenanceProgressForTesting = null;
        }
    }

    [Fact]
    public void RunBatch_DbDiagnosticsInterruptBusyWait_Issue5282()
    {
        using var parent = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_busy_parent_5282");
        using var child = TestProjectHelper.CreateTempProjectScope("cdidx_batch_db_busy_child_5282");
        using var env = EnvironmentVariableScope.Capture(DbContext.BusyTimeoutEnvironmentVariable);
        env.Set(DbContext.BusyTimeoutEnvironmentVariable, "3000");
        var parentDbPath = TestProjectHelper.CreateProjectDb(parent.Root);
        var childDbPath = TestProjectHelper.CreateProjectDb(child.Root);
        SqliteConnection.ClearAllPools();
        using var lockConnection = new SqliteConnection(
            DbPathResolver.BuildSqliteConnectionString(childDbPath, SqliteOpenMode.ReadWrite));
        lockConnection.Open();
        using (var lockCommand = lockConnection.CreateCommand())
        {
            lockCommand.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;";
            lockCommand.ExecuteNonQuery();
        }

        try
        {
            foreach (var (command, cancellationPhase) in new[]
                     {
                         ("schema", "read_version"),
                         ("integrity", "read_rows"),
                     })
            {
                using var cancellation = new CancellationTokenSource();
                DbCommandRunner.MaintenanceProgressForTesting = (operation, phase) =>
                {
                    if (operation == (command == "schema" ? "schema" : "integrity_check")
                        && phase == cancellationPhase)
                    {
                        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                    }
                };
                var input = JsonSerializer.Serialize(new[] { "db", command, "--db", childDbPath, "--json" }) + "\n";
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                    input,
                    () => QueryCommandRunner.RunBatch(
                        ["--db", parentDbPath, "--json-summary"],
                        _jsonOptions,
                        cancellationToken: cancellation.Token));
                stopwatch.Stop();
                var lines = ParseJsonLines(stdout);
                try
                {
                    Assert.True(cancellation.IsCancellationRequested);
                    Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    Assert.Equal(2, lines.Count);
                    Assert.Equal(
                        CommandErrorCodes.Interrupted,
                        lines[0].RootElement.GetProperty("error").GetProperty("error_code").GetString());
                    Assert.True(
                        stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                        $"The {command} busy wait took {stopwatch.Elapsed}.");
                }
                finally
                {
                    foreach (var document in lines)
                        document.Dispose();
                }
            }
        }
        finally
        {
            DbCommandRunner.MaintenanceProgressForTesting = null;
            using var rollback = lockConnection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
    }

    private static void SetBatchDbUserVersionIssue5282(string dbPath, int userVersion)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {userVersion};";
        command.ExecuteNonQuery();
    }

    private static void WriteCorruptBatchDbIssue5282(string dbPath)
    {
        var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        var bytes = new byte[4096];
        Array.Copy(header, bytes, header.Length);
        Array.Fill(bytes, (byte)0xFF, header.Length, bytes.Length - header.Length);
        File.WriteAllBytes(dbPath, bytes);
    }
}
