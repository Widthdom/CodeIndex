using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class QueryCommandRunnerBatchHintTests
{
    [Fact]
    public void RunBatch_CursorHintMatchesStandaloneInBothModes_Issue5423()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_hint_5423");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "rows.txt", "text", "Needle");
        string[] child = ["find", "Needle", "--json", "--fields", "path", "--path", "rows.txt", "--cursor", "bad"];
        var (directExit, directOutput, directError) = CaptureConsole(
            () => ProgramRunner.Run([.. child, "--db", db], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.UsageError, directExit);
        Assert.Empty(directError);
        var direct = JsonNode.Parse(directOutput)!;
        var expectedHint = direct["hint"]!.GetValue<string>();
        Assert.Contains("--limit/--max-json-bytes", expectedHint, StringComparison.Ordinal);

        foreach (var parallelism in new[] { "1", "2" })
        {
            var input = JsonSerializer.Serialize(child) + "\n" + JsonSerializer.Serialize(child) + "\n";
            var (exit, stdout, stderr) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
                ["--db", db, "--json-summary", "--parallel", parallelism], JsonOptions));
            Assert.Equal(directExit, exit);
            Assert.Empty(stderr);
            var records = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal(3, records.Length);
            for (var index = 0; index < 2; index++)
            {
                var record = records[index];
                Assert.Equal("batch_result", record["record"]!.GetValue<string>());
                Assert.Equal(index + 1, record["line"]!.GetValue<int>());
                Assert.Equal(directExit, record["exit_code"]!.GetValue<int>());
                Assert.Equal("cursor_malformed", record["error"]!["category"]!.GetValue<string>());
                Assert.Equal(direct["error_code"]!.GetValue<string>(), record["error"]!["error_code"]!.GetValue<string>());
                Assert.Equal(expectedHint, record["error"]!["hint"]!.GetValue<string>());
            }
            Assert.Equal(2, records[^1]["command_failures"]!.GetValue<int>());
        }
    }
}
