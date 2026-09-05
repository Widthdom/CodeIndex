using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerAuditProgressIssue5260Tests
{
    [Theory]
    [InlineData("--json")]
    [InlineData("--json=ndjson")]
    public void AuditProgress_SlowChildEmitsBeforeCompletionWithoutLeakingIdentifiers(string format)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_progress_5260");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/One.cs", "csharp", "class Needle5260 {}\n");
        using var heartbeat = new ManualResetEventSlim();
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
        {
            var original = Console.Error;
            Console.SetError(new HeartbeatWriter(original, heartbeat));
            try
            {
                return QueryCommandRunner.RunAuditAllForTesting(
                    ["--all", "--progress", "--db", db, format], JsonOptions,
                    [Recipe("/private/secret\n\u001b[31m", "token=secret", "Needle5260")],
                    beforeQueryForTesting: _ => Assert.True(heartbeat.Wait(TimeSpan.FromSeconds(10))));
            }
            finally
            {
                Console.SetError(original);
            }
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("active_recipe=1 active_query=1", stderr);
        Assert.Contains("audit: completed", stderr);
        Assert.Contains("recipes_completed=1/1 queries_completed=1/1", stderr);
        Assert.DoesNotContain("secret", stderr);
        Assert.DoesNotContain(project.Root, stderr);
        Assert.DoesNotContain("\u001b", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.All(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => JsonDocument.Parse(line).Dispose());
        Assert.All(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.Length <= 256));
    }

    [Fact]
    public void AuditProgress_ClosesCancellationFailureAndBudgetStates()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_progress_states_5260");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        var recipes = new[] { Recipe("one", "query", "Needle5260") };
        using var cancellation = new CancellationTokenSource();
        foreach (var mode in new[] { "cancel", "failure", "budget", "pre-cancel", "output-failure" })
        {
            if (mode == "pre-cancel")
                cancellation.Cancel();
            QueryCommandRunner.AuditAllTimeBudgetForTesting = mode == "budget" ? TimeSpan.FromMilliseconds(100) : null;
            try
            {
                var args = new List<string> { "--all", "--progress", "--db", db, "--json", "--allow-partial" };
                if (mode == "output-failure")
                    args.AddRange(["--max-json-bytes", "1"]);
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
                    args.ToArray(), JsonOptions, recipes,
                    mode is "cancel" or "pre-cancel" ? cancellation.Token : default,
                    beforeQueryForTesting: reader =>
                    {
                        if (mode == "cancel")
                            cancellation.Cancel();
                        if (mode == "budget")
                            Assert.True(reader.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
                        if (mode == "failure")
                            throw new InvalidOperationException("test failure");
                        reader.ThrowIfCancellationRequested();
                    }));
                if (mode != "output-failure")
                    JsonDocument.Parse(stdout).Dispose();
                Assert.Contains("audit: " + (mode is "cancel" or "pre-cancel" ? "cancelled" : mode == "output-failure" ? "failed" : "partial"), stderr);
                Assert.Equal(mode is "cancel" or "pre-cancel" ? CommandExitCodes.CancelledBySignal
                    : mode == "output-failure" ? CommandExitCodes.UsageError : 0, exitCode);
                if (mode == "failure")
                    Assert.Contains("queries_completed=0/1 queries_failed=1", stderr);
            }
            finally
            {
                QueryCommandRunner.AuditAllTimeBudgetForTesting = null;
            }
        }
    }

    [Fact]
    public void AuditProgress_GlobalSuppressionAndScopeArePreserved()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_progress_flags_5260");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        foreach (var flags in new string[][] { [], ["--quiet", "--progress"], ["--progress", "--quiet"],
                     ["--no-progress", "--progress"], ["--progress", "--no-progress"] })
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["audit", "--all", "--db", db, "--summary-only", "--json", .. flags], appVersion: "1.46.1"));
            Assert.Equal(0, exitCode);
            JsonDocument.Parse(stdout).Dispose();
            Assert.Empty(stderr);
        }
        foreach (var command in new string[][] { ["audit", "risky-code"], ["search", "needle"] })
        {
            var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
                [.. command, "--progress", "--db", db, "--json"], appVersion: "1.46.1"));
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
        }
        ConsoleUi.SetProgressAnimationEnabled(null);
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("audit"), flag => flag.Name == "--progress");
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("search"), flag => flag.Name == "--progress");
    }

    [Fact]
    public void AuditProgress_RateLimitAndTerminalRedrawAreBoundedAndStopAfterFinish()
    {
        foreach (var interactive in new[] { false, true })
        {
            using var output = new StringWriter();
            var elapsed = TimeSpan.Zero;
            using var progress = new ConsoleUi.AuditProgress(2, 3, output, interactive,
                width: 60, elapsed: () => elapsed, startTimer: false);
            progress.SetActive(1, 2);
            for (var i = 0; i < 100; i++)
                progress.Heartbeat();
            Assert.DoesNotContain("active_query=2", output.ToString());
            elapsed = TimeSpan.FromSeconds(1);
            progress.Heartbeat();
            progress.SetCompleted(1, 2, 0);
            progress.PauseForOutput();
            var paused = output.ToString();
            elapsed = TimeSpan.FromSeconds(2);
            progress.Heartbeat();
            Assert.Equal(paused, output.ToString());
            if (interactive)
                Assert.EndsWith("\r", paused, StringComparison.Ordinal);
            output.WriteLine("RESULT");
            progress.Finish("partial");
            var completed = output.ToString();
            elapsed = TimeSpan.FromSeconds(2);
            progress.Heartbeat();
            progress.Finish("completed");
            Assert.Equal(completed, output.ToString());
            Assert.DoesNotContain("%", completed);
            Assert.EndsWith(Environment.NewLine, completed);
            Assert.Equal(3, completed.Split("audit:").Length - 1);
            Assert.Contains("RESULT" + Environment.NewLine, completed, StringComparison.Ordinal);
            if (interactive)
                Assert.All(completed.Split('\r', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.TrimEnd('\n').Length <= 59));
            else
                Assert.Contains("active_recipe=1 active_query=2", completed);
        }
    }

    private static SearchAuditRecipe Recipe(string name, string queryName, string query)
        => new(name, "test", [new SearchAuditRecipeQuery(queryName, query, "test", [], "review", ExactSubstring: true)]);

    private sealed class HeartbeatWriter(TextWriter inner, ManualResetEventSlim heartbeat) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (value?.Contains("active_recipe=1 active_query=1", StringComparison.Ordinal) == true)
                heartbeat.Set();
        }
        public override void Flush() => inner.Flush();
    }
}
