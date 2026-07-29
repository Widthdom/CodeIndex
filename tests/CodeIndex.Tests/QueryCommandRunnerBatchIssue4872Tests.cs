using System.Diagnostics;
using CodeIndex.Cli;
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
}
