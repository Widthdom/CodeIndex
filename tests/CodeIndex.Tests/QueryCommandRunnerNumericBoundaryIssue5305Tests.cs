using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_NumericBoundariesPreserveOutputAndLiteralContracts_Issue5305()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_numeric_5305");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        string[] numericOptions =
        [
            "--limit", "--top", "--max-results", "--graph-budget", "--total-limit",
            "--line", "--start", "--start-line", "--end", "--end-line", "--context",
            "--before", "--after", "--focus-line", "--focus-column", "--focus-length",
            "--snippet-lines", "--max-line-width", "--body-start", "--body-lines",
            "--body-line-count", "--start-column", "--end-column", "--max-hops", "--depth",
            "--sample", "--per-file-limit", "--max-json-bytes", "--slow-query-ms",
            "--min-entrypoint-confidence", "--duplicate-threshold",
        ];
        string[][] formats =
        [
            ["--json"], ["--json=array"], ["--json=ndjson"],
            ["--format", "json"], ["--format=json"],
            ["--format", "compact"], ["--format=compact"],
        ];
        foreach (var option in numericOptions)
        {
            var diagnosticOption = option == "--top" ? "--limit" : option;
            foreach (var format in formats)
            {
                foreach (var formatFirst in new[] { true, false })
                {
                    string[] args = ["Return", "--db", dbPath,
                        .. formatFirst ? format.Concat([option]) : new[] { option }.Concat(format)];
                    var parsed = QueryCommandRunner.ParseArgs(args, jsonDefault: false);
                    Assert.True(parsed.Json);
                    Assert.True(parsed.MissingNumericOptionValue);
                    Assert.Contains(diagnosticOption + " requires a value", parsed.ParseError);
                    if (!CliFlagSchema.GetAcceptedFlagNamesForCommand("search").Contains(option))
                        continue;
                    var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));
                    Assert.Equal(CommandExitCodes.UsageError, exit);
                    Assert.True(stderr.Length == 0, $"{string.Join(' ', args)}: {stderr}");
                    using var document = JsonDocument.Parse(stdout);
                    var error = document.RootElement;
                    Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
                    Assert.Equal("usage", error.GetProperty("category").GetString());
                    Assert.Equal("search", error.GetProperty("command").GetString());
                    Assert.Contains(diagnosticOption + " requires a value", error.GetProperty("message").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                }
            }

            foreach (var literal in new[] { "--json=array", "--format=compact" })
            {
                foreach (var explicitJson in new[] { false, true })
                {
                    string[] args = ["Return", "--db", dbPath, .. explicitJson ? new[] { "--json" } : Array.Empty<string>(),
                        option, "--", literal];
                    var parsed = QueryCommandRunner.ParseArgs(args, jsonDefault: false);
                    Assert.Equal(explicitJson, parsed.Json);
                    Assert.Contains(diagnosticOption + " requires a value", parsed.ParseError);
                    if (!CliFlagSchema.GetAcceptedFlagNamesForCommand("search").Contains(option))
                        continue;
                    var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));
                    Assert.Equal(CommandExitCodes.UsageError, exit);
                    if (explicitJson)
                    {
                        Assert.Empty(stderr);
                        using var error = JsonDocument.Parse(stdout);
                        Assert.Contains(diagnosticOption + " requires a value", error.RootElement.GetProperty("message").GetString());
                    }
                    else
                    {
                        Assert.Empty(stdout);
                        Assert.Contains(diagnosticOption + " requires a value", stderr);
                        Assert.Contains("Usage: cdidx search", stderr);
                    }
                }
            }
        }

        // Actual invalid values still undergo numeric validation, even when they look like options.
        foreach (var value in new[] { "-1", "0", "10001", "2147483648", "--unknown=value", "--json=array" })
        {
            foreach (var inline in new[] { false, true })
            {
                if (!inline && value == "--json=array")
                    continue; // This is an option boundary, covered above.
                var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                    ["Return", "--db", dbPath, "--json", .. inline ? new[] { "--limit=" + value } : new[] { "--limit", value }], _jsonOptions));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Empty(stdout);
                Assert.Contains(value == "10001" ? "--limit must be less than or equal" : "--limit requires an integer between 1 and 10000", stderr);
                Assert.DoesNotContain("requires a value", stderr);
            }
        }

        foreach (var limit in new[] { "1", "10000" })
        {
            var (exit, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--db", dbPath, "--limit", limit, "--total-limit", "0", "--count", "--", "--json=array"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, exit);
            Assert.DoesNotContain("requires", stderr);
        }

        (string Command, string Option, Func<string[], int> Run)[] commands =
        [
            ("audit", "--limit", args => QueryCommandRunner.RunAudit(args, _jsonOptions)),
            ("files", "--limit", args => QueryCommandRunner.RunFiles(args, _jsonOptions)),
            ("symbols", "--limit", args => QueryCommandRunner.RunSymbols(args, _jsonOptions)),
            ("excerpt", "--start", args => QueryCommandRunner.RunExcerpt(args, _jsonOptions)),
            ("inspect", "--body-lines", args => QueryCommandRunner.RunInspect(args, _jsonOptions)),
            ("impact", "--max-hops", args => QueryCommandRunner.RunImpact(args, _jsonOptions)),
            ("deps", "--graph-budget", args => QueryCommandRunner.RunDeps(args, _jsonOptions)),
        ];
        foreach (var command in commands)
        {
            var commandFormats = command.Command is "audit" or "files" or "symbols" ? formats : [["--json"]];
            foreach (var format in commandFormats)
            {
                foreach (var formatFirst in new[] { false, true })
                {
                    string[] args = ["Return", "--db", dbPath,
                        .. formatFirst ? format.Concat([command.Option]) : new[] { command.Option }.Concat(format)];
                    var (exit, stdout, stderr) = CaptureConsole(() => command.Run(args));
                    Assert.Equal(CommandExitCodes.UsageError, exit);
                    Assert.True(stderr.Length == 0, $"{command.Command}: {string.Join(' ', args)}: {stderr}");
                    using var error = JsonDocument.Parse(stdout);
                    Assert.Equal(command.Command, error.RootElement.GetProperty("command").GetString());
                    Assert.Equal(CommandErrorCodes.UsageError, error.RootElement.GetProperty("error_code").GetString());
                    Assert.Contains(command.Option + " requires a value", error.RootElement.GetProperty("message").GetString());
                }
            }
        }
    }
}
