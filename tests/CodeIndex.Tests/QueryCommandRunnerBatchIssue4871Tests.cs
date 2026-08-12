using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_SerialAndParallelMixedInputPreserveRecordAndAccountingParity()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_mixed_parity");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = string.Join(
            '\n',
            "",
            """{"command":"files","args":["--format","count","--json"]}""",
            """["search","missing-batch-mixed","--json","--strict-not-found"]""",
            "42",
            """{"command":"status","args":"--json"}""",
            """["unknown"]""",
            """["languages","--format","count"]""",
            "");

        var (serialExitCode, serialStdout, serialStderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--include-raw-streams"],
                _jsonOptions));
        var (parallelExitCode, parallelStdout, parallelStderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                [
                    "--db", dbPath,
                    "--json-summary",
                    "--include-raw-streams",
                    "--parallel", "3",
                ],
                _jsonOptions));

        Assert.Equal(CommandExitCodes.NotFound, serialExitCode);
        Assert.Equal(serialExitCode, parallelExitCode);
        Assert.Equal(string.Empty, serialStderr);
        Assert.Equal(serialStderr, parallelStderr);
        var serialLines = ParseJsonLines(serialStdout);
        var parallelLines = ParseJsonLines(parallelStdout);
        try
        {
            Assert.Equal(7, serialLines.Count);
            Assert.Equal(serialLines.Count, parallelLines.Count);
            for (var index = 0; index < 6; index++)
            {
                Assert.Equal(index + 2, serialLines[index].RootElement.GetProperty("line").GetInt32());
                Assert.Equal(
                    serialLines[index].RootElement.GetRawText(),
                    parallelLines[index].RootElement.GetRawText());
            }

            var serialSummary = serialLines[^1].RootElement;
            var parallelSummary = parallelLines[^1].RootElement;
            Assert.Equal("batch_summary", serialSummary.GetProperty("record").GetString());
            Assert.Equal("batch_summary", parallelSummary.GetProperty("record").GetString());
            Assert.Equal(7, serialSummary.GetProperty("input_lines_read").GetInt32());
            Assert.Equal(7, parallelSummary.GetProperty("input_lines_read").GetInt32());
            Assert.Equal(4, serialSummary.GetProperty("commands_processed").GetInt32());
            Assert.Equal(4, parallelSummary.GetProperty("commands_processed").GetInt32());
            Assert.Equal(2, serialSummary.GetProperty("line_errors").GetInt32());
            Assert.Equal(2, parallelSummary.GetProperty("line_errors").GetInt32());
            Assert.Equal(2, serialSummary.GetProperty("command_failures").GetInt32());
            Assert.Equal(2, parallelSummary.GetProperty("command_failures").GetInt32());
            Assert.Equal(CommandExitCodes.NotFound, serialSummary.GetProperty("exit_code").GetInt32());
            Assert.Equal(CommandExitCodes.NotFound, parallelSummary.GetProperty("exit_code").GetInt32());
            Assert.False(serialSummary.GetProperty("output_limit_reached").GetBoolean());
            Assert.False(parallelSummary.GetProperty("output_limit_reached").GetBoolean());
            Assert.False(serialSummary.GetProperty("input_limit_reached").GetBoolean());
            Assert.False(parallelSummary.GetProperty("input_limit_reached").GetBoolean());
            Assert.Equal(1, serialSummary.GetProperty("parallelism").GetInt32());
            Assert.Equal(3, parallelSummary.GetProperty("parallelism").GetInt32());
            Assert.Equal(
                serialSummary.GetProperty("output_chars").GetInt32(),
                parallelSummary.GetProperty("output_chars").GetInt32());
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
                     ["--db", dbPath, "--json-summary", "--parallel", "3"],
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

    [Fact]
    public void RunBatch_SetupCancellationUsesTypedSummaryInSerialAndParallel_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_setup_cancel_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var batchArgs in new[]
                 {
                     new[] { "--db", dbPath, "--json-summary" },
                     ["--db", dbPath, "--json-summary", "--parallel", "2"],
                 })
        {
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                "[\"status\",\"--json\"]\n",
                () => QueryCommandRunner.RunBatch(
                    batchArgs,
                    _jsonOptions,
                    cancellationToken: cancellation.Token));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(string.Empty, stderr);
                var summary = Assert.Single(lines).RootElement;
                Assert.Equal("1", summary.GetProperty("api_version").GetString());
                Assert.Equal("batch_summary", summary.GetProperty("record").GetString());
                Assert.Equal(0, summary.GetProperty("input_lines_read").GetInt32());
                Assert.Equal(
                    "batch_cancelled",
                    summary.GetProperty("error").GetProperty("category").GetString());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
    }

    [Fact]
    public void RunBatch_CancellationAfterPriorFailureUsesLineEnvelopeAndCancelledSummary_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_cancel_after_failure_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);

        foreach (var batchArgs in new[]
                 {
                     new[] { "--db", dbPath, "--json-summary" },
                     ["--db", dbPath, "--json-summary", "--parallel", "2"],
                 })
        {
            using var cancellation = new CancellationTokenSource();
            QueryCommandRunner.BatchInputLineReadForTesting = lineNumber =>
            {
                if (lineNumber == 2)
                    cancellation.Cancel();
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                    "42\n[1]\n",
                    () => QueryCommandRunner.RunBatch(
                        batchArgs,
                        _jsonOptions,
                        cancellationToken: cancellation.Token));
                var lines = ParseJsonLines(stdout);
                try
                {
                    Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    Assert.Equal(3, lines.Count);
                    Assert.Equal(
                        "invalid_batch_input_shape",
                        lines[0].RootElement.GetProperty("error").GetProperty("category").GetString());
                    Assert.Equal(
                        "batch_cancelled",
                        lines[1].RootElement.GetProperty("error").GetProperty("category").GetString());
                    Assert.Equal(
                        CommandExitCodes.CancelledBySignal,
                        lines[2].RootElement.GetProperty("exit_code").GetInt32());
                    Assert.Equal(
                        "batch_cancelled",
                        lines[2].RootElement.GetProperty("error").GetProperty("category").GetString());
                }
                finally
                {
                    foreach (var document in lines)
                        document.Dispose();
                }
            }
            finally
            {
                QueryCommandRunner.BatchInputLineReadForTesting = null;
            }
        }
    }

    [Fact]
    public void RunBatch_ParallelCancellationDrainsItemPreparedDuringCancellation_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_cancel_prepared_item_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        QueryCommandRunner.BatchParallelItemPreparedForTesting = _ => cancellation.Cancel();

        try
        {
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                "[\"status\",\"--json\"]\n",
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token));
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(2, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("line").GetInt32());
                Assert.Equal(
                    "batch_cancelled",
                    lines[0].RootElement.GetProperty("error").GetProperty("category").GetString());
                Assert.Equal(
                    CommandExitCodes.CancelledBySignal,
                    lines[1].RootElement.GetProperty("exit_code").GetInt32());
                Assert.Equal(1, lines[1].RootElement.GetProperty("commands_processed").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelItemPreparedForTesting = null;
        }
    }

    [Fact]
    public void RunBatch_ParallelCancellationInterruptsBlockedInputAndPreservesNextLine_Issue4871()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_blocked_input_cancel_4871");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var reader = new BlockingBatchReader();
        using var cancellation = new CancellationTokenSource();
        using var batchCompleted = new ManualResetEventSlim();
        using var cancellationTaskCompleted = new ManualResetEventSlim();
        var forcedReaderRelease = 0;
        Exception? cancellationFailure = null;
        using var capture = ConsoleCapture.StartWithInput(
            reader,
            captureOut: true,
            captureError: true);
        _ = Task.Run(() =>
        {
            try
            {
                if (!reader.WaitUntilRead(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The parallel batch producer did not start reading input.");
                cancellation.Cancel();
                if (!batchCompleted.Wait(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Exchange(ref forcedReaderRelease, 1);
                    reader.Release();
                }
            }
            catch (Exception ex)
            {
                cancellationFailure = ex;
            }
            finally
            {
                cancellationTaskCompleted.Set();
            }
        });

        int exitCode;
        int nextExitCode;
        try
        {
            exitCode = QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", "2"],
                _jsonOptions,
                cancellationToken: cancellation.Token);
            batchCompleted.Set();
            Assert.True(cancellationTaskCompleted.Wait(TimeSpan.FromSeconds(5)));
            reader.Release();
            nextExitCode = QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary"],
                _jsonOptions);
        }
        finally
        {
            batchCompleted.Set();
            reader.Release();
            Assert.True(cancellationTaskCompleted.Wait(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(cancellationFailure);
        Assert.Equal(0, Volatile.Read(ref forcedReaderRelease));
        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(CommandExitCodes.UsageError, nextExitCode);
        Assert.Equal(string.Empty, capture.Error!.ToString());
        var lines = ParseJsonLines(capture.Out!.ToString() ?? string.Empty);
        try
        {
            Assert.Equal(3, lines.Count);
            var cancelledSummary = lines[0].RootElement;
            Assert.Equal("batch_summary", cancelledSummary.GetProperty("record").GetString());
            Assert.Equal(
                CommandExitCodes.CancelledBySignal,
                cancelledSummary.GetProperty("exit_code").GetInt32());
            Assert.Equal(
                "batch_cancelled",
                cancelledSummary.GetProperty("error").GetProperty("category").GetString());
            Assert.Equal("batch_error", lines[1].RootElement.GetProperty("record").GetString());
            Assert.Equal(
                "invalid_batch_input_shape",
                lines[1].RootElement.GetProperty("error").GetProperty("category").GetString());
            Assert.Equal("batch_summary", lines[2].RootElement.GetProperty("record").GetString());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public async Task RunBatch_ParallelOutputLimitDrainsActiveWorkersAndRestoresConsole()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_output_limit_drain");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var bothStarted = new CountdownEvent(2);
        using var fourthLineRead = new ManualResetEventSlim();
        using var releaseFourthLine = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var secondCompleted = new ManualResetEventSlim();
        using var firstRecordWritten = new ManualResetEventSlim();
        using var stdout = new NotifyingStringWriter(firstRecordWritten);
        using var stderr = new StringWriter();
        Task<int>? firstRun = null;

        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            bothStarted.Signal();
            if (!bothStarted.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Both output-limit batch workers did not start.");
            if (lineNumber == 1 && !fourthLineRead.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The terminal input-limit line was not read.");
            if (lineNumber == 2 && !releaseSecond.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The second output-limit batch worker was not released.");
        };
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber == 2)
                secondCompleted.Set();
        };
        QueryCommandRunner.BatchInputLineReadForTesting = lineNumber =>
        {
            if (lineNumber != 4)
                return;
            fourthLineRead.Set();
            if (!releaseFourthLine.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The terminal input-limit line was not released.");
        };

        try
        {
            firstRun = Task.Factory.StartNew(
                () =>
                {
                    using var capture = ConsoleCapture.Start(
                        stdout,
                        stderr,
                        new StringReader(
                            """
                            ["languages","--format","count"]
                            ["languages","--format","count"]
                            ["languages","--format","count"]
                            ["languages","--format","count"]
                            """ + "\n"));
                    var expectedOut = Console.Out;
                    var expectedError = Console.Error;
                    var exitCode = QueryCommandRunner.RunBatch(
                        [
                            "--db", dbPath,
                            "--json-summary",
                            "--parallel", "2",
                            "--max-input-lines", "3",
                            "--max-output-chars", QueryCommandRunner.BatchMinTotalOutputChars.ToString(),
                        ],
                        _jsonOptions);
                    Assert.Same(expectedOut, Console.Out);
                    Assert.Same(expectedError, Console.Error);
                    return exitCode;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(bothStarted.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(fourthLineRead.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(firstRecordWritten.Wait(TimeSpan.FromSeconds(15)));
            var firstOutputLines = ParseJsonLines(stdout.ToString());
            try
            {
                var outputLimit = Assert.Single(firstOutputLines).RootElement;
                Assert.Equal("batch_result", outputLimit.GetProperty("record").GetString());
                Assert.Equal(
                    "batch_output_limit",
                    outputLimit.GetProperty("error").GetProperty("category").GetString());
            }
            finally
            {
                foreach (var document in firstOutputLines)
                    document.Dispose();
            }
            Assert.False(firstRun.IsCompleted);

            releaseFourthLine.Set();
            Assert.False(firstRun.IsCompleted);
            releaseSecond.Set();
            var firstExitCode = await firstRun.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(secondCompleted.IsSet);
            Assert.Equal(CommandExitCodes.InvalidArgument, firstExitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            var lines = ParseJsonLines(stdout.ToString());
            try
            {
                Assert.Equal(2, lines.Count);
                var firstSummary = lines[1].RootElement;
                Assert.Equal("batch_summary", firstSummary.GetProperty("record").GetString());
                Assert.Equal(4, firstSummary.GetProperty("input_lines_read").GetInt32());
                Assert.Equal(3, firstSummary.GetProperty("commands_processed").GetInt32());
                Assert.Equal(0, firstSummary.GetProperty("line_errors").GetInt32());
                Assert.Equal(1, firstSummary.GetProperty("command_failures").GetInt32());
                Assert.True(firstSummary.GetProperty("output_limit_reached").GetBoolean());
                Assert.False(firstSummary.GetProperty("input_limit_reached").GetBoolean());
                Assert.Equal(
                    CommandExitCodes.InvalidArgument,
                    firstSummary.GetProperty("exit_code").GetInt32());
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
            QueryCommandRunner.BatchParallelCommandCompletedForTesting = null;
            QueryCommandRunner.BatchInputLineReadForTesting = null;
            releaseFourthLine.Set();
            releaseSecond.Set();
            if (firstRun is not null)
                await firstRun.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class BlockingBatchReader : TextReader
    {
        private const string DeferredLine = "42\n";
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _released = new();
        private int _position;

        public override int Read()
        {
            if (_position == 0)
            {
                _entered.Set();
                _released.Wait();
            }
            if (_position >= DeferredLine.Length)
                return -1;
            return DeferredLine[_position++];
        }

        public bool WaitUntilRead(TimeSpan timeout)
            => _entered.Wait(timeout);

        public void Release()
            => _released.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Set();
                _entered.Dispose();
                _released.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
