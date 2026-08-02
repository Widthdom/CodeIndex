using System.Collections.Concurrent;
using System.Diagnostics;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_ParallelReusesBoundedSessionsAndMatchesSerialRecords_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_session_reuse_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = BuildIssue4872BatchInput(9);
        var (serialExitCode, serialStdout, serialStderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary"],
                _jsonOptions));

        using var firstWaveStarted = new CountdownEvent(3);
        var openedSessions = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber > 3)
                return;
            firstWaveStarted.Signal();
            if (!firstWaveStarted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The first parallel batch wave did not start.");
        };

        string parallelStdout;
        string parallelStderr;
        int parallelExitCode;
        try
        {
            (parallelExitCode, parallelStdout, parallelStderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "3"],
                    _jsonOptions));
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }

        Assert.Equal(CommandExitCodes.Success, serialExitCode);
        Assert.Equal(CommandExitCodes.Success, parallelExitCode);
        Assert.Equal(string.Empty, serialStderr);
        Assert.Equal(string.Empty, parallelStderr);
        Assert.Equal(3, Volatile.Read(ref openedSessions));

        var serialLines = ParseJsonLines(serialStdout);
        var parallelLines = ParseJsonLines(parallelStdout);
        try
        {
            Assert.Equal(10, serialLines.Count);
            Assert.Equal(serialLines.Count, parallelLines.Count);
            for (var index = 0; index < 9; index++)
            {
                var serialRecord = serialLines[index].RootElement;
                var parallelRecord = parallelLines[index].RootElement;
                Assert.Equal(index + 1, parallelRecord.GetProperty("line").GetInt32());
                Assert.Equal(
                    serialRecord.GetProperty("command").GetString(),
                    parallelRecord.GetProperty("command").GetString());
                Assert.Equal(
                    serialRecord.GetProperty("arguments").GetRawText(),
                    parallelRecord.GetProperty("arguments").GetRawText());
                Assert.Equal(
                    serialRecord.GetProperty("exit_code").GetInt32(),
                    parallelRecord.GetProperty("exit_code").GetInt32());
                Assert.Equal(
                    serialRecord.GetProperty("result").GetRawText(),
                    parallelRecord.GetProperty("result").GetRawText());
            }

            Assert.Equal(1, serialLines[^1].RootElement.GetProperty("parallelism").GetInt32());
            Assert.Equal(3, parallelLines[^1].RootElement.GetProperty("parallelism").GetInt32());
        }
        finally
        {
            foreach (var document in serialLines)
                document.Dispose();
            foreach (var document in parallelLines)
                document.Dispose();
        }
    }

    [Fact]
    public async Task RunBatch_ParallelRefreshesDetachedSnapshotsBetweenItems_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_snapshot_refresh_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                PRAGMA wal_checkpoint(TRUNCATE);
                UPDATE files SET checksum = 'initial-hot' WHERE path = 'src/Initial.cs';
                """;
            setup.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(dbPath + "-wal").Length > 0);
        var initialDbLength = new FileInfo(dbPath).Length;

        using var input = new InteractiveBatchTextReader();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var firstWaveCompleted = new CountdownEvent(2);
        using var cancellation = new CancellationTokenSource();
        var statusWaveTimeout = TimeSpan.FromMinutes(2);
        Task<int>? runTask = null;
        var openedSessions = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber <= 2)
                firstWaveCompleted.Signal();
        };

        try
        {
            runTask = Task.Run(() =>
            {
                using var capture = ConsoleCapture.Start(stdout, stderr, input);
                return QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token);
            });
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            Assert.True(
                firstWaveCompleted.Wait(statusWaveTimeout),
                "The first parallel batch wave did not complete.");

            using (var update = writer.CreateCommand())
            {
                update.CommandText = """
                    INSERT INTO files(path, lang, size, lines, checksum, modified)
                    VALUES ('src/Updated.cs', 'csharp', 1, 1, 'updated', CURRENT_TIMESTAMP);
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                update.ExecuteNonQuery();
            }
            Assert.Equal(initialDbLength, new FileInfo(dbPath).Length);
            Assert.Equal(0, new FileInfo(dbPath + "-wal").Length);

            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.Complete();

            var exitCode = await runTask.WaitAsync(statusWaveTimeout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            var lines = ParseJsonLines(stdout.ToString());
            try
            {
                Assert.Equal(5, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(1, lines[1].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(2, lines[2].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(2, lines[3].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(4, Volatile.Read(ref openedSessions));
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandCompletedForTesting = null;
            input.Complete();
            if (runTask is { IsCompleted: false })
            {
                cancellation.Cancel();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    [Fact]
    public async Task RunBatch_ParallelRefreshesDirectSessionsBetweenItems_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_direct_refresh_4872");
        var sourceDbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using (var source = new SqliteConnection(
                   $"Data Source={sourceDbPath};Mode=ReadWrite;Pooling=False"))
        {
            source.Open();
            using var checkpoint = source.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }

        var dbPath = Path.Combine(project.Root, "direct.db");
        File.Copy(sourceDbPath, dbPath);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var journalMode = writer.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", Assert.IsType<string>(journalMode.ExecuteScalar()));
        }
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }
        Assert.Equal("delete", ReadJournalMode(writer));

        using var input = new InteractiveBatchTextReader();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var firstWaveCompleted = new CountdownEvent(2);
        using var cancellation = new CancellationTokenSource();
        Task<int>? runTask = null;
        var openedSessions = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber <= 2)
                firstWaveCompleted.Signal();
        };

        try
        {
            runTask = Task.Run(() =>
            {
                using var capture = ConsoleCapture.Start(stdout, stderr, input);
                return QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token);
            });
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            Assert.True(
                firstWaveCompleted.Wait(TimeSpan.FromSeconds(60)),
                "The first parallel batch wave did not complete.");

            using (var update = writer.CreateCommand())
            {
                update.CommandText = """
                    INSERT INTO files(path, lang, size, lines, checksum, modified)
                    VALUES ('src/Updated.cs', 'csharp', 1, 1, 'updated', CURRENT_TIMESTAMP);
                    """;
                Assert.Equal(1, update.ExecuteNonQuery());
            }

            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.WriteLine("""{"command":"status","args":["--json"]}""");
            input.Complete();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            var lines = ParseJsonLines(stdout.ToString());
            try
            {
                Assert.Equal(5, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(1, lines[1].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(2, lines[2].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(2, lines[3].RootElement.GetProperty("result").GetProperty("files").GetInt32());
                Assert.Equal(4, Volatile.Read(ref openedSessions));
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandCompletedForTesting = null;
            input.Complete();
            if (runTask is { IsCompleted: false })
            {
                cancellation.Cancel();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    [Fact]
    public void RunBatch_InitialValidationFailuresDisposeDetachedSnapshots_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_validation_failure_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }

        var snapshotDirectories = new ConcurrentBag<string>();
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        var originalValidationHook = QueryCommandRunner.BatchParallelDatabaseValidatingForTesting;
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = snapshotDirectories.Add;
        QueryCommandRunner.BatchParallelDatabaseValidatingForTesting =
            () => throw new InvalidDataException("Injected parallel batch validation failure.");

        try
        {
            Assert.Throws<InvalidDataException>(() => CaptureConsoleWithInput(
                BuildIssue4872BatchInput(1),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions)));
        }
        finally
        {
            QueryCommandRunner.BatchParallelDatabaseValidatingForTesting = originalValidationHook;
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }

        Assert.NotEmpty(snapshotDirectories);
        Assert.All(snapshotDirectories, path => Assert.False(Directory.Exists(path), path));
    }

    [Fact]
    public void RunBatch_ReaderConstructionFailuresDisposeDetachedSnapshots_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_reader_failure_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(dbPath + "-wal").Length > 0);

        var snapshotDirectories = new ConcurrentBag<string>();
        var readerConstructionAttempts = 0;
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        var originalReaderFactory = QueryCommandRunner.BatchParallelReaderFactoryForTesting;
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = snapshotDirectories.Add;
        QueryCommandRunner.BatchParallelReaderFactoryForTesting = db =>
        {
            if (Interlocked.Increment(ref readerConstructionAttempts) == 1)
                return new DbReader(db);
            throw new InvalidDataException("Injected parallel batch reader construction failure.");
        };

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                BuildIssue4872BatchInput(6),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
        }
        finally
        {
            QueryCommandRunner.BatchParallelReaderFactoryForTesting = originalReaderFactory;
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }

        Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.True(Volatile.Read(ref readerConstructionAttempts) >= 3);
        Assert.True(snapshotDirectories.Count >= 3);
        Assert.All(snapshotDirectories, path => Assert.False(Directory.Exists(path), path));

        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(7, lines.Count);
            Assert.Equal(3, lines.Take(6).Count(
                line => line.RootElement.GetProperty("exit_code").GetInt32() == CommandExitCodes.RuntimeError));
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_ParallelSessionReuseKeepsTwelveItemCostWithinThreeItemRatio_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_ratio_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE batch_ratio_filler(payload BLOB NOT NULL);
                INSERT INTO batch_ratio_filler(payload) VALUES (zeroblob(16777216));
                """;
            command.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(dbPath + "-wal").Length >= 16_777_216);

        _ = MeasureIssue4872ParallelBatch(dbPath, commandCount: 3);
        var shortRuns = new TimeSpan[3];
        var longRuns = new TimeSpan[3];
        for (var iteration = 0; iteration < shortRuns.Length; iteration++)
        {
            shortRuns[iteration] = MeasureIssue4872ParallelBatch(dbPath, commandCount: 3);
            longRuns[iteration] = MeasureIssue4872ParallelBatch(dbPath, commandCount: 12);
        }

        var shortBaseline = shortRuns.Min();
        var longBaseline = longRuns.Min();
        var ratio = longBaseline.TotalMilliseconds / shortBaseline.TotalMilliseconds;
        Assert.True(
            ratio <= 3.0,
            $"Parallel batch session reuse regressed: 12-item/3-item ratio was {ratio:F2} "
            + $"({longBaseline.TotalMilliseconds:F1} ms / {shortBaseline.TotalMilliseconds:F1} ms).");
    }

    private TimeSpan MeasureIssue4872ParallelBatch(string dbPath, int commandCount)
    {
        using var firstWaveStarted = new CountdownEvent(3);
        QueryCommandRunner.BatchParallelSessionOpenedForTesting = static () =>
        {
            // Model an expensive large-index open/schema phase. This is workload cost,
            // not a synchronization delay; the countdown below owns worker coordination.
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        };
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber > 3)
                return;
            firstWaveStarted.Signal();
            if (!firstWaveStarted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The parallel batch benchmark wave did not start.");
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var (exitCode, _, stderr) = CaptureConsoleWithInput(
                BuildIssue4872RejectedBatchInput(commandCount),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "3"],
                    _jsonOptions));
            stopwatch.Stop();
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            return stopwatch.Elapsed;
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }
    }

    private static string BuildIssue4872BatchInput(int commandCount)
        => string.Join(
            '\n',
            Enumerable.Repeat(
                """{"command":"languages","args":["--format","count"]}""",
                commandCount)) + "\n";

    private static string BuildIssue4872RejectedBatchInput(int commandCount)
        => string.Join(
            '\n',
            Enumerable.Repeat(
                """{"command":"unknown"}""",
                commandCount)) + "\n";

    private static string ReadJournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
