using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearchFind_EarlyUsageErrorsHonorOutputAndLiteralBoundaries_Issue5323()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_early_usage_5323");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        string[][] formats =
        [
            ["--json"], ["--json=array"], ["--json=ndjson"],
            ["--format", "json"], ["--format=JSON"],
            ["--format", "compact"], ["--format=compact"],
        ];
        foreach (var command in new[] { "search", "find" })
        {
            int Run(string[] args) => command == "search"
                ? QueryCommandRunner.RunSearch(args, _jsonOptions)
                : QueryCommandRunner.RunFind(args, _jsonOptions);
            string[][] invalid =
            [
                [], [""], ["   "], [new string('x', 10000)], ["--query="], ["--query", "   "],
                ["Return", "--limit"], ["Return", "--limit=-1"],
                ["Return", "--path"], ["Return", "--path="],
                ["Return", "--format=invalid"], ["Return", "--unsupported"],
                ["Return", "--unsupported=bad\u001b[31m\n\t" + new string('x', 10000)],
                ["Return", "--limit=bad\u001b[31m\n\t" + new string('x', 10000)],
                ["Return", "extra"],
                .. command == "find" ? new string[][]
                {
                    ["Return"], ["Return", "--all", "--path", "src/**"],
                    ["Return", "--path", "src/**", "--origin", "code"],
                    ["Return", "--all", "--line-scan-limit=0"],
                    ["Return", "--path", "src/**", "--line-scan-limit=1"],
                } : new string[][] { ["Return", "--regex"], ["Return", "--all"] },
            ];
            foreach (var args in invalid)
            {
                foreach (var format in formats)
                {
                    foreach (var first in new[] { false, true })
                    {
                        var input = new[] { "--db", dbPath }.Concat(first ? format.Concat(args) : args.Concat(format)).ToArray();
                        var (exit, stdout, stderr) = CaptureConsole(() => Run(input));
                        Assert.Equal(CommandExitCodes.UsageError, exit);
                        if (args.Contains("--format=invalid") && format[0].StartsWith("--format", StringComparison.Ordinal))
                            Assert.StartsWith("Warning: --format specified more than once", stderr);
                        else
                            Assert.True(stderr.Length == 0, $"{command}: {string.Join(' ', input.Select(x => x.Length > 80 ? x[..80] : x))}: {stderr}");
                        using var json = JsonDocument.Parse(stdout);
                        var error = json.RootElement;
                        Assert.Equal("1", error.GetProperty("api_version").GetString());
                        Assert.Equal(command, error.GetProperty("command").GetString());
                        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
                        Assert.Equal("usage", error.GetProperty("category").GetString());
                        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                        var message = error.GetProperty("message").GetString()!;
                        Assert.True(message.Length < 2048);
                        Assert.DoesNotContain('\u001b', message);
                        Assert.DoesNotContain('\t', message);
                        Assert.DoesNotContain("Usage:", message);
                    }
                }
                var (humanExit, humanOut, humanError) = CaptureConsole(() => Run(["--db", dbPath, .. args]));
                Assert.Equal(CommandExitCodes.UsageError, humanExit);
                Assert.Empty(humanOut);
                Assert.Contains("Error", humanError);
            }

            if (command == "find")
            {
                foreach (var output in new string[][]
                {
                    ["--json=invalid"],
                    ["--json", "--format=bad\u001b[31m\n\t" + new string('x', 10000)],
                })
                {
                    var (exit, stdout, stderr) = CaptureConsole(() => Run(
                        ["--db", dbPath, "Return", "--path", "src/**", "--allow-unknown-lang", .. output]));
                    Assert.Equal(CommandExitCodes.UsageError, exit);
                    Assert.Empty(stderr);
                    using var error = JsonDocument.Parse(stdout);
                    Assert.Equal("find", error.RootElement.GetProperty("command").GetString());
                    Assert.Equal(CommandErrorCodes.UsageError, error.RootElement.GetProperty("error_code").GetString());
                    var message = error.RootElement.GetProperty("message").GetString()!;
                    Assert.True(message.Length < 2048);
                    Assert.DoesNotContain('\n', message);
                    Assert.DoesNotContain('\u001b', message);
                }
                var (budgetExit, budgetOut, budgetError) = CaptureConsole(() => Run(
                    ["--db", dbPath, "Return", "--path", "src/**", "--allow-unknown-lang", "--json", "--max-json-bytes=0"]));
                Assert.Equal(CommandExitCodes.UsageError, budgetExit);
                Assert.Empty(budgetError);
                using var budget = JsonDocument.Parse(budgetOut);
                Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, budget.RootElement.GetProperty("error_code").GetString());
            }

            // A literal JSON token is data. Missing options before -- cannot eat the marker.
            foreach (var literal in new[] { "--json", "--json=array", "--format=compact" })
            {
                foreach (var explicitJson in new[] { false, true })
                {
                    string[][] boundaries =
                    [
                        ["--limit", "--", literal],
                        ["--query", literal, "--unsupported"],
                        ["--query=" + literal, "--unsupported"],
                        ["Return", "--path=" + literal, "--unsupported"],
                    ];
                    foreach (var boundary in boundaries)
                    {
                        var (exit, stdout, stderr) = CaptureConsole(() => Run(
                            ["--db", dbPath, .. explicitJson ? new[] { "--json" } : Array.Empty<string>(), .. boundary]));
                        Assert.Equal(CommandExitCodes.UsageError, exit);
                        if (explicitJson)
                        {
                            Assert.Empty(stderr);
                            using var json = JsonDocument.Parse(stdout);
                            Assert.Equal(command, json.RootElement.GetProperty("command").GetString());
                        }
                        else
                        {
                            Assert.Empty(stdout);
                            Assert.Contains("Error", stderr);
                        }
                    }
                }
            }
            foreach (var ending in new string[][] { ["--"], ["--", ""], ["--", "one", "two"] })
            {
                var (exit, stdout, stderr) = CaptureConsole(() => Run(["--db", dbPath, "--json", .. ending]));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Empty(stderr);
                using var json = JsonDocument.Parse(stdout);
                Assert.Equal(command, json.RootElement.GetProperty("command").GetString());
            }
            // Repeated --format keeps its documented rightmost selection. An explicit
            // --json remains explicit even when the final format is text.
            foreach (var explicitJson in new[] { false, true })
            {
                var (exit, stdout, stderr) = CaptureConsole(() => Run(
                    ["--db", dbPath, "", "--format=json", "--format=text",
                        .. explicitJson ? new[] { "--json" } : Array.Empty<string>()]));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Contains("Warning: --format specified more than once", stderr);
                if (explicitJson)
                {
                    Assert.DoesNotContain("Error:", stderr);
                    using var error = JsonDocument.Parse(stdout);
                    Assert.Equal(command, error.RootElement.GetProperty("command").GetString());
                }
                else
                {
                    Assert.Empty(stdout);
                    Assert.Contains("Error:", stderr);
                }
            }
        }
    }

    [Fact]
    public void RunBatch_RetainsEarlySearchFindErrorsAndContinues_Issue5323()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_early_5323");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        string[][] children =
        [
            ["search", "", "--json"],
            ["search", "Return", "--path", "--format=json"],
            ["find", "", "--all", "--json"],
            ["find", "Return", "--path", "src/**", "--origin", "code", "--format=compact"],
            ["find", "Return", "--json"],
            ["languages", "--format", "count"],
        ];
        var input = string.Join('\n', children.Select(child => JsonSerializer.Serialize(child))) + "\n";
        foreach (var parallelism in new[] { "1", "4" })
        {
            var (exit, stdout, stderr) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", parallelism], _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Empty(stderr);
            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(children.Length + 1, lines.Count);
                for (var i = 0; i < children.Length - 1; i++)
                {
                    var record = lines[i].RootElement;
                    Assert.Equal(children[i][0], record.GetProperty("command").GetString());
                    Assert.Equal(CommandExitCodes.UsageError, record.GetProperty("exit_code").GetInt32());
                    Assert.False(record.TryGetProperty("raw_streams", out _));
                    var error = record.GetProperty("error");
                    Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
                    Assert.Equal("usage", error.GetProperty("category").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                }
                Assert.Equal("ok", lines[^2].RootElement.GetProperty("status").GetString());
                Assert.Equal(children.Length, lines[^1].RootElement.GetProperty("commands_processed").GetInt32());
                Assert.Equal(children.Length - 1, lines[^1].RootElement.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var line in lines)
                    line.Dispose();
            }
        }
    }
}
