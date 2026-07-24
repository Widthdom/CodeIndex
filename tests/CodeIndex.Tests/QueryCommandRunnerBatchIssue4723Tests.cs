using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_AcceptsStructuredCommandsAndValidatesTheirShape_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_structured_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = """
        {"command":"status","args":["--json"]}
        {"command":"languages","args":["--format","count"]}
        {"command":"status","args":"--json"}
        {"command":"status","extra":true}

        """;

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(["--db", dbPath, "--json-summary"], _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(5, lines.Count);

            Assert.Equal("status", lines[0].RootElement.GetProperty("command").GetString());
            Assert.True(
                lines[0].RootElement.TryGetProperty("result", out var statusResult),
                lines[0].RootElement.GetRawText());
            Assert.True(statusResult.TryGetProperty("files", out _));
            Assert.Equal("languages", lines[1].RootElement.GetProperty("command").GetString());
            Assert.Equal("count", lines[1].RootElement.GetProperty("result").GetProperty("format").GetString());

            foreach (var errorDocument in lines.Skip(2).Take(2))
            {
                var errorRecord = errorDocument.RootElement;
                Assert.Equal("batch_error", errorRecord.GetProperty("record").GetString());
                Assert.Equal(
                    "invalid_batch_command_object",
                    errorRecord.GetProperty("error").GetProperty("category").GetString());
            }

            var summary = lines[^1].RootElement;
            Assert.Equal(2, summary.GetProperty("commands_processed").GetInt32());
            Assert.Equal(2, summary.GetProperty("line_errors").GetInt32());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_ConfigurableBudgetsUseEffectiveValuesAndRejectUnsafeValues_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_budgets_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = """
        []
        []
        []

        """;

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                [
                    "--db", dbPath,
                    "--json-summary",
                    "--max-input-lines", "2",
                    "--max-output-chars=8192",
                ],
                _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            var summary = lines[^1].RootElement;
            Assert.Equal(2, summary.GetProperty("input_line_limit").GetInt32());
            Assert.Equal(8192, summary.GetProperty("output_char_limit").GetInt32());
            Assert.True(summary.GetProperty("input_limit_reached").GetBoolean());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }

        var (invalidExitCode, _, invalidStderr) = CaptureConsole(() => QueryCommandRunner.RunBatch(
            ["--db", dbPath, "--json-summary", "--parallel", (QueryCommandRunner.BatchMaxParallelism + 1).ToString()],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, invalidExitCode);
        Assert.Contains($"from 1 to {QueryCommandRunner.BatchMaxParallelism}", invalidStderr);

        var (parallelWithoutSummaryExitCode, _, parallelWithoutSummaryStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(["--db", dbPath, "--parallel", "2"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, parallelWithoutSummaryExitCode);
        Assert.Contains("--parallel requires --json-summary", parallelWithoutSummaryStderr);

        var (defaultParallelWithoutSummaryExitCode, _, defaultParallelWithoutSummaryStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(["--db", dbPath, "--parallel", "1"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, defaultParallelWithoutSummaryExitCode);
        Assert.Contains("--parallel requires --json-summary", defaultParallelWithoutSummaryStderr);

        var (outputWithoutSummaryExitCode, _, outputWithoutSummaryStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(["--db", dbPath, "--max-output-chars", "8192"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, outputWithoutSummaryExitCode);
        Assert.Contains("--max-output-chars requires --json-summary", outputWithoutSummaryStderr);

        var (defaultOutputWithoutSummaryExitCode, _, defaultOutputWithoutSummaryStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(
                [
                    "--db", dbPath,
                    "--max-output-chars", QueryCommandRunner.BatchDefaultTotalOutputChars.ToString(),
                ],
                _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, defaultOutputWithoutSummaryExitCode);
        Assert.Contains("--max-output-chars requires --json-summary", defaultOutputWithoutSummaryStderr);

        var (inputAboveMaximumExitCode, _, inputAboveMaximumStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(
                [
                    "--db", dbPath,
                    "--json-summary",
                    "--max-input-lines", (QueryCommandRunner.BatchMaxInputLines + 1).ToString(),
                ],
                _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, inputAboveMaximumExitCode);
        Assert.Contains($"from 1 to {QueryCommandRunner.BatchMaxInputLines}", inputAboveMaximumStderr);

        var (outputAboveMaximumExitCode, _, outputAboveMaximumStderr) = CaptureConsole(
            () => QueryCommandRunner.RunBatch(
                [
                    "--db", dbPath,
                    "--json-summary",
                    "--max-output-chars", (QueryCommandRunner.BatchMaxTotalOutputChars + 1).ToString(),
                ],
                _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, outputAboveMaximumExitCode);
        Assert.Contains(
            $"from {QueryCommandRunner.BatchMinTotalOutputChars} to {QueryCommandRunner.BatchMaxTotalOutputChars}",
            outputAboveMaximumStderr);
    }

    [Fact]
    public void RunBatch_ParallelReadsOverlapButEmitInInputOrderAndIsolateFailures_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_parallel_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var secondCompleted = new ManualResetEventSlim();
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber == 1 && !secondCompleted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The second parallel batch command did not complete.");
        };
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber == 2)
                secondCompleted.Set();
        };

        try
        {
            var input = """
            {"command":"status","args":["--json-envelope"]}
            {"command":"unknown"}
            {"command":"languages","args":["--format","count"]}

            """;
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
                Assert.Equal(4, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("line").GetInt32());
                Assert.Equal("status", lines[0].RootElement.GetProperty("command").GetString());
                Assert.Equal("ok", lines[0].RootElement.GetProperty("status").GetString());
                var envelopeStdout = lines[0].RootElement.GetProperty("stdout").GetString();
                Assert.Contains("\"metadata\":", envelopeStdout);
                Assert.Contains("\"command\":\"status\"", envelopeStdout);
                Assert.Equal(2, lines[1].RootElement.GetProperty("line").GetInt32());
                Assert.Equal("unknown", lines[1].RootElement.GetProperty("command").GetString());
                Assert.Equal("error", lines[1].RootElement.GetProperty("status").GetString());
                Assert.Equal(3, lines[2].RootElement.GetProperty("line").GetInt32());
                Assert.Equal("languages", lines[2].RootElement.GetProperty("command").GetString());
                Assert.Equal("ok", lines[2].RootElement.GetProperty("status").GetString());
                Assert.Equal(3, lines[^1].RootElement.GetProperty("parallelism").GetInt32());
                Assert.Equal(1, lines[^1].RootElement.GetProperty("command_failures").GetInt32());
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
        }
    }

    [Fact]
    public async Task RunBatch_ParallelStreamsFirstResultBeforeMoreInputOrEof_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_parallel_streaming_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var input = new InteractiveBatchTextReader();
        using var firstRecordEmitted = new ManualResetEventSlim();
        using var stdout = new NotifyingStringWriter(firstRecordEmitted);
        using var stderr = new StringWriter();
        Task<int>? runTask = null;
        var emittedBeforeEof = false;
        var exitCode = CommandExitCodes.UnhandledException;

        try
        {
            runTask = Task.Run(() =>
            {
                using var capture = ConsoleCapture.Start(stdout, stderr, input);
                return QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions);
            });
            input.WriteLine("""{"command":"languages","args":["--format","count"]}""");

            emittedBeforeEof = firstRecordEmitted.Wait(TimeSpan.FromSeconds(15));
            Assert.False(runTask.IsCompleted);

            input.Complete();
            exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            input.Complete();
            if (runTask is not null && !runTask.IsCompleted)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch
                {
                    // Preserve the primary assertion while still making cleanup bounded.
                }
            }
        }

        Assert.True(emittedBeforeEof);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        var lines = ParseJsonLines(stdout.ToString());
        try
        {
            Assert.Equal(2, lines.Count);
            Assert.Equal("batch_result", lines[0].RootElement.GetProperty("record").GetString());
            Assert.Equal("batch_summary", lines[1].RootElement.GetProperty("record").GetString());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_ParallelFailureExitCodeFollowsInputOrder_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_parallel_exit_order_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = """
        {"command":"search","args":["missing-symbol-4723","--json","--strict-not-found"]}
        []

        """;

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", "2"],
                _jsonOptions));
        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(CommandExitCodes.NotFound, lines[0].RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal(CommandExitCodes.UsageError, lines[1].RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal(CommandExitCodes.NotFound, lines[^1].RootElement.GetProperty("exit_code").GetInt32());
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Fact]
    public void RunBatch_ParallelReadsPropagateCancellationAndRestoreConsole_Issue4723()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_cancel_4723");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var cancellation = new CancellationTokenSource();
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber == 1)
                cancellation.Cancel();
        };

        try
        {
            var exception = Record.Exception(() => CaptureConsoleWithInput(
                """
                {"command":"recipes","args":["--json"]}
                {"command":"languages","args":["--format","count"]}

                """,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token)));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        finally
        {
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }

        var (_, stdout, _) = CaptureConsole(() =>
        {
            Console.Write("restored");
            return 0;
        });
        Assert.Equal("restored", stdout);
    }

    private sealed class InteractiveBatchTextReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<char> _characters = new();

        public void WriteLine(string line)
        {
            foreach (var character in line)
                _characters.Add(character);
            _characters.Add('\n');
        }

        public void Complete()
        {
            if (!_characters.IsAddingCompleted)
                _characters.CompleteAdding();
        }

        public override int Read()
        {
            try
            {
                return _characters.Take();
            }
            catch (InvalidOperationException) when (_characters.IsCompleted)
            {
                return -1;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
                _characters.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class NotifyingStringWriter(ManualResetEventSlim lineWritten) : StringWriter
    {
        private readonly object _sync = new();

        public override void Write(char value)
        {
            lock (_sync)
            {
                base.Write(value);
                if (value == '\n')
                    lineWritten.Set();
            }
        }

        public override void Write(string? value)
        {
            lock (_sync)
            {
                base.Write(value);
                if (value?.Contains('\n') == true)
                    lineWritten.Set();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                base.WriteLine(value);
                lineWritten.Set();
            }
        }

        public override string ToString()
        {
            lock (_sync)
                return base.ToString();
        }
    }
}
