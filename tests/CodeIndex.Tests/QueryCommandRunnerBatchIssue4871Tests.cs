using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_AllJsonSummaryFailuresUseTypedEnvelopesInSerialAndParallel_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_typed_errors_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = """
        42
        [1]
        []
        {"command":"status","args":"--json"}
        ["unknown"]
        ["search","missing-symbol-4871","--json","--strict-not-found"]

        """;
        var expectedCategories = new[]
        {
            "invalid_batch_input_shape",
            "invalid_batch_argument_type",
            "invalid_batch_input_shape",
            "invalid_batch_command_object",
            "batch_command_not_allowed",
            "batch_child_not_found",
        };

        foreach (var batchArgs in new[]
                 {
                     new[] { "--db", dbPath, "--json-summary" },
                     new[] { "--db", dbPath, "--json-summary", "--parallel", "3" },
                 })
        {
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(batchArgs, _jsonOptions));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(expectedCategories.Length + 1, lines.Count);

                for (var index = 0; index < expectedCategories.Length; index++)
                {
                    var record = lines[index].RootElement;
                    Assert.Equal("1", record.GetProperty("api_version").GetString());
                    Assert.Equal("error", record.GetProperty("status").GetString());
                    Assert.Equal(index + 1, record.GetProperty("line").GetInt32());
                    Assert.False(record.TryGetProperty("stdout", out _));
                    Assert.False(record.TryGetProperty("stderr", out _));
                    Assert.False(record.TryGetProperty("raw_streams", out _));

                    var error = record.GetProperty("error");
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error_code").GetString()));
                    Assert.Equal(expectedCategories[index], error.GetProperty("category").GetString());
                }

                var summary = lines[^1].RootElement;
                Assert.Equal("batch_summary", summary.GetProperty("record").GetString());
                Assert.Equal(expectedCategories.Length, summary.GetProperty("input_lines_read").GetInt32());
                Assert.Equal(2, summary.GetProperty("commands_processed").GetInt32());
                Assert.Equal(4, summary.GetProperty("line_errors").GetInt32());
                Assert.Equal(2, summary.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
    }

    [Fact]
    public void RunBatch_RawFailureStreamsRequireExplicitJsonSummaryOptIn_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_raw_streams_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            "[\"unknown\"]\n",
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--include-raw-streams"],
                _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, lines.Count);
            var record = lines[0].RootElement;
            Assert.Equal(
                "batch_command_not_allowed",
                record.GetProperty("error").GetProperty("category").GetString());
            var rawStreams = record.GetProperty("raw_streams");
            Assert.Equal(string.Empty, rawStreams.GetProperty("stdout").GetString());
            Assert.Contains(
                "batch only supports query and read-only discovery commands",
                rawStreams.GetProperty("stderr").GetString());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }

        var (invalidExitCode, _, invalidStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--include-raw-streams"],
                _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, invalidExitCode);
        Assert.Contains("--include-raw-streams requires --json-summary", invalidStderr);
    }

    [Fact]
    public void RunBatch_ParallelTimeoutUsesTypedErrorAndPreservesInputOrder_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_timeout_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber == 1)
                throw new TimeoutException("untrusted timeout detail");
        };

        try
        {
            var input = """
            {"command":"status","args":["--json"]}
            {"command":"languages","args":["--format","count"]}

            """;
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(3, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("line").GetInt32());
                Assert.Equal(
                    "batch_command_timeout",
                    lines[0].RootElement.GetProperty("error").GetProperty("category").GetString());
                Assert.DoesNotContain("untrusted timeout detail", stdout);
                Assert.Equal(2, lines[1].RootElement.GetProperty("line").GetInt32());
                Assert.Equal("ok", lines[1].RootElement.GetProperty("status").GetString());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }
    }

    [Fact]
    public void RunBatch_ParallelCancellationUsesTypedErrorsAndRestoresConsole_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_cancel_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        using var bothStarted = new CountdownEvent(2);
        using var cancellationTriggered = new ManualResetEventSlim();
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            bothStarted.Signal();
            if (!bothStarted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Both batch commands did not start.");
            if (lineNumber == 1)
            {
                cancellation.Cancel();
                cancellationTriggered.Set();
            }
            else if (!cancellationTriggered.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The first batch command did not cancel the batch.");
            }
        };

        try
        {
            var input = """
            {"command":"status","args":["--json"]}
            {"command":"languages","args":["--format","count"]}

            """;
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(3, lines.Count);
                for (var index = 0; index < 2; index++)
                {
                    var record = lines[index].RootElement;
                    Assert.Equal(index + 1, record.GetProperty("line").GetInt32());
                    Assert.Equal("error", record.GetProperty("status").GetString());
                    var error = record.GetProperty("error");
                    Assert.Equal(CommandErrorCodes.Interrupted, error.GetProperty("error_code").GetString());
                    Assert.Equal("batch_cancelled", error.GetProperty("category").GetString());
                }
                Assert.Equal(
                    CommandExitCodes.CancelledBySignal,
                    lines[^1].RootElement.GetProperty("exit_code").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }

        var (_, restoredStdout, _) = CaptureConsole(() =>
        {
            Console.Write("restored");
            return 0;
        });
        Assert.Equal("restored", restoredStdout);
    }
}
