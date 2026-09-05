using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_PreservesChildBudgetErrorsAndRetryInBothModes_Issue5259()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_budget_5259");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Delete.cs", "csharp",
            "class Example { void Delete() { File.Delete(\"one\"); File.Delete(\"two\"); } }");
        string[][] children =
        [
            ["status", "--explain", "index_complete", "--json", "--max-json-bytes", "1024"],
            ["search", "File.Delete", "--json=array", "--max-json-bytes", "512", "--limit", "2"],
        ];
        var input = string.Join('\n', children.Select(child => JsonSerializer.Serialize(child)))
                    + "\n[\"languages\",\"--format\",\"count\"]\n"
                    + "[\"search\",\"--limit\",\"invalid\"]\n";
        string[] retainedFields =
        [
            "error_code", "category", "message", "hint", "requested_bytes", "effective_bytes",
            "minimum_required_bytes", "minimum_required_bytes_known",
            "minimum_required_bytes_unavailable_reason", "minimum_required_bytes_uncertain",
            "minimum_required_bytes_uncertainty_reason", "retry",
        ];

        foreach (var child in children)
        {
            string[] args = [.. child.Skip(1), "--db", dbPath];
            int RunChild(string[] effectiveArgs) => child[0] == "status"
                    ? QueryCommandRunner.RunStatus(effectiveArgs, _jsonOptions)
                    : QueryCommandRunner.RunSearch(effectiveArgs, _jsonOptions);
            var (exitCode, stdout, _) = CaptureConsole(() => JsonEnvelopeWrapper.ShouldWrap(child[0], args)
                ? JsonEnvelopeWrapper.RunWrapped(child[0], args, "", _jsonOptions, RunChild)
                : RunChild(args));
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            using var direct = JsonDocument.Parse(stdout);
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, direct.RootElement.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", direct.RootElement.GetProperty("category").GetString());
            Assert.True(direct.RootElement.GetProperty("minimum_required_bytes").GetInt64() > 0);
        }

        foreach (var parallelism in new[] { "1", "4" })
        {
            foreach (var raw in new[] { false, true })
            {
                var (exitCode, stdout, stderr) = CaptureConsoleWithInput(input,
                    () => QueryCommandRunner.RunBatch(
                        ["--db", dbPath, "--json-summary", "--parallel", parallelism,
                            .. raw ? new[] { "--include-raw-streams" } : Array.Empty<string>()], _jsonOptions));
                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Empty(stderr);
                var lines = ParseJsonLines(stdout);
                try
                {
                    Assert.Equal(5, lines.Count);
                    for (var index = 0; index < children.Length; index++)
                    {
                        var record = lines[index].RootElement;
                        Assert.Equal(index + 1, record.GetProperty("line").GetInt32());
                        Assert.Equal(children[index][0], record.GetProperty("command").GetString());
                        Assert.Equal(CommandExitCodes.UsageError, record.GetProperty("exit_code").GetInt32());
                        Assert.False(record.TryGetProperty("stdout", out _));
                        Assert.False(record.TryGetProperty("stderr", out _));
                        Assert.Equal(raw, record.TryGetProperty("raw_streams", out var streams));
                        var error = record.GetProperty("error");
                        Assert.Equal("command", error.GetProperty("scope").GetString());
                        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
                        Assert.Equal("response_budget", error.GetProperty("category").GetString());
                        Assert.Equal(index == 0 ? 1024 : 512, error.GetProperty("requested_bytes").GetInt64());
                        var minimum = error.GetProperty("minimum_required_bytes").GetInt64();
                        Assert.True(minimum > error.GetProperty("effective_bytes").GetInt64());
                        Assert.True(error.GetProperty("minimum_required_bytes_known").GetBoolean());
                        Assert.Equal("increase_max_json_bytes", error.GetProperty("retry").GetProperty("action").GetString());
                        Assert.True(error.GetProperty("retry").GetProperty("recommended_bytes").GetInt64() >= minimum);
                        if (raw)
                        {
                            using var childOutput = JsonDocument.Parse(streams.GetProperty("stdout").GetString()!);
                            foreach (var field in retainedFields)
                                Assert.Equal(childOutput.RootElement.GetProperty(field).GetRawText(), error.GetProperty(field).GetRawText());
                        }
                    }
                    Assert.Equal("ok", lines[2].RootElement.GetProperty("status").GetString());
                    Assert.Equal("batch_child_usage", lines[3].RootElement.GetProperty("error").GetProperty("category").GetString());
                    var summary = lines[^1].RootElement;
                    Assert.Equal(4, summary.GetProperty("commands_processed").GetInt32());
                    Assert.Equal(3, summary.GetProperty("command_failures").GetInt32());
                    Assert.Equal(stdout.Length, summary.GetProperty("output_chars").GetInt32());
                }
                finally
                {
                    foreach (var line in lines)
                        line.Dispose();
                }
            }

            var (limitedExit, limitedOutput, limitedError) = CaptureConsoleWithInput(input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", parallelism,
                        "--max-output-chars", QueryCommandRunner.BatchMinTotalOutputChars.ToString()], _jsonOptions));
            Assert.Equal(CommandExitCodes.InvalidArgument, limitedExit);
            Assert.Empty(limitedError);
            Assert.True(limitedOutput.Length <= QueryCommandRunner.BatchMinTotalOutputChars);
            var limitedLines = ParseJsonLines(limitedOutput);
            try
            {
                Assert.Equal("batch_output_limit", limitedLines[0].RootElement.GetProperty("error").GetProperty("category").GetString());
                Assert.True(limitedLines[^1].RootElement.GetProperty("output_limit_reached").GetBoolean());
                Assert.Equal(limitedOutput.Length, limitedLines[^1].RootElement.GetProperty("output_chars").GetInt32());
            }
            finally
            {
                foreach (var line in limitedLines)
                    line.Dispose();
            }
        }
    }
}
