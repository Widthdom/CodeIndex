using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_GuardParseErrorsPreserveMachineAndHumanContracts_Issue5297()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_guard_errors_5297");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        string[][] invalidOptions =
        [
            ["--guard-scope", "unknown-scope"],
            ["--guard-scope=unknown"],
            ["--guard-scope"],
            ["--guard-window", "nope"],
            ["--guard-window=-1"],
            ["--guard-window=2147483648"],
            ["--guard-window=2147483647"],
            ["--guard-window"],
            ["--guard-scope", "bad\u001b[31m\n\t" + new string('x', 10000)],
            ["--guard-window", "bad\u001b[31m\n\t" + new string('x', 10000)],
            ["--require-before"],
            ["--require-after="],
            ["--reject-before="],
            ["--reject-after"],
            ["--require-before", new string('x', 10000)],
        ];
        string[][] machineFormats =
        [
            ["--json"], ["--json=ndjson"], ["--json=array"],
            ["--format", "json"], ["--format=compact"],
        ];
        foreach (var command in new[] { "search", "audit" })
        {
            int Run(string[] args) => command == "search"
                ? QueryCommandRunner.RunSearch(args, _jsonOptions)
                : QueryCommandRunner.RunAudit(args, _jsonOptions);
            foreach (var invalid in invalidOptions)
            {
                foreach (var format in machineFormats)
                {
                    foreach (var formatFirst in new[] { true, false })
                    {
                        string[] args = ["Return", "--db", dbPath,
                            .. formatFirst ? format.Concat(invalid) : invalid.Concat(format)];
                        var (exit, stdout, stderr) = CaptureConsole(() => Run(args));
                        Assert.Equal(CommandExitCodes.UsageError, exit);
                        Assert.True(stderr.Length == 0, $"{command}: {string.Join(' ', args.Select(arg => arg.Length > 100 ? arg[..100] : arg))}: {stderr}");
                        using var document = JsonDocument.Parse(stdout);
                        var error = document.RootElement;
                        Assert.Equal("1", error.GetProperty("api_version").GetString());
                        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
                        Assert.Equal("usage", error.GetProperty("category").GetString());
                        Assert.Equal(command, error.GetProperty("command").GetString());
                        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                        var message = error.GetProperty("message").GetString()!;
                        Assert.Contains(invalid[0].Split('=')[0], message);
                        Assert.True(message.Length < 2048);
                        Assert.DoesNotContain('\u001b', message);
                        if (invalid.Any(value => value.Contains('\u001b')))
                            Assert.DoesNotContain('\n', message);
                        Assert.DoesNotContain('\t', message);
                    }
                }
                var (humanExit, humanOut, humanError) = CaptureConsole(() => Run(["Return", "--db", dbPath, .. invalid]));
                Assert.Equal(CommandExitCodes.UsageError, humanExit);
                Assert.Empty(humanOut);
                Assert.Contains("Error", humanError);
                Assert.Contains("cdidx " + command, humanError);
            }
        }

        // Missing guard values must not consume the marker protecting a JSON-looking literal.
        foreach (var command in new[] { "search", "audit" })
        {
            foreach (var option in new[] { "--guard-window", "--guard-scope" })
            {
                foreach (var literal in new[] { "--json", "--json=array", "--format=compact" })
                {
                    foreach (var explicitJson in new[] { false, true })
                    {
                        string[] args = ["Return", "--db", dbPath, .. explicitJson ? new[] { "--json" } : Array.Empty<string>(),
                            option, "--", literal];
                        var (literalExit, literalOut, literalError) = CaptureConsole(() => command == "search"
                            ? QueryCommandRunner.RunSearch(args, _jsonOptions)
                            : QueryCommandRunner.RunAudit(args, _jsonOptions));
                        Assert.Equal(CommandExitCodes.UsageError, literalExit);
                        if (explicitJson)
                        {
                            Assert.Empty(literalError);
                            using var error = JsonDocument.Parse(literalOut);
                            Assert.Equal(command, error.RootElement.GetProperty("command").GetString());
                            Assert.Equal(CommandErrorCodes.UsageError, error.RootElement.GetProperty("error_code").GetString());
                            Assert.Contains(option + " requires a value", error.RootElement.GetProperty("message").GetString());
                        }
                        else
                        {
                            Assert.Empty(literalOut);
                            Assert.Contains(option + " requires a value", literalError);
                        }
                    }
                }
            }
        }
        var (jsonExit, jsonOut, jsonError) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--db", dbPath, "--json", "--guard-window", "nope", "--", "--guard-scope"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, jsonExit);
        Assert.Empty(jsonError);
        using var literalDocument = JsonDocument.Parse(jsonOut);
        Assert.Contains("--guard-window", literalDocument.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("unsupported --guard-scope", literalDocument.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void RunBatch_RetainsGuardUsageErrorsAndContinues_Issue5297()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_guard_5297");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        string[][] children =
        [
            ["search", "Return", "--guard-scope", "unknown-scope", "--json"],
            ["search", "Return", "--guard-window", "nope", "--json"],
            ["audit", "safe-file-delete", "--guard-scope", "unknown-scope", "--format=json"],
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
                Assert.Equal(5, lines.Count);
                for (var i = 0; i < 3; i++)
                {
                    var record = lines[i].RootElement;
                    Assert.Equal(children[i][0], record.GetProperty("command").GetString());
                    Assert.Equal(CommandExitCodes.UsageError, record.GetProperty("exit_code").GetInt32());
                    Assert.False(record.TryGetProperty("raw_streams", out _));
                    var error = record.GetProperty("error");
                    Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
                    Assert.Equal("usage", error.GetProperty("category").GetString());
                    Assert.Contains(i == 1 ? "--guard-window" : "--guard-scope", error.GetProperty("message").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
                }
                Assert.Equal("ok", lines[3].RootElement.GetProperty("status").GetString());
                Assert.Equal(4, lines[^1].RootElement.GetProperty("commands_processed").GetInt32());
                Assert.Equal(3, lines[^1].RootElement.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var line in lines)
                    line.Dispose();
            }
        }
    }
}
