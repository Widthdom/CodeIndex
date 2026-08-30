using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunImpact_CountModesReturnLimitIndependentTotalsAcrossDepthsAndFilters_Issue5226()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_impact_count_issue5226");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/lib.py",
                "python",
                "def issue5226_target():\n    return 0\n");
            for (int i = 0; i < 55; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/caller_{i:D2}.py",
                    "python",
                    $"def issue5226_caller_{i:D2}():\n    return issue5226_target()\n");
            }
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/top.py",
                "python",
                "def issue5226_top():\n    return issue5226_caller_00()\n");
            MarkGraphAndFoldReady(dbPath);

            var (humanExitCode, humanStdout, _) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["issue5226_target", "--db", dbPath, "--count", "--limit", "1", "--max-hops", "1"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Equal("55", humanStdout.Trim());

            foreach (var machineFlag in new[] { "--json", "--compact" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                    ["issue5226_target", "--db", dbPath, machineFlag, "--count", "--limit", "1", "--max-hops", "1"],
                    _jsonOptions));
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(55, json.GetProperty("count").GetInt32());
                Assert.Equal(55, json.GetProperty("file_count").GetInt32());
                Assert.False(json.GetProperty("truncated").GetBoolean());
                Assert.True(json.GetProperty("authoritative_count").GetBoolean());
            }

            var one = RunCountJson(
                dbPath,
                ["--path", "src/lib.py", "--path", "src/caller_00.py", "--limit", "1", "--max-hops", "1"]);
            Assert.Equal(1, one.GetProperty("count").GetInt32());

            var zero = RunCountJson(
                dbPath,
                ["--path", "src/lib.py", "--limit", "1", "--max-hops", "1"]);
            Assert.Equal(0, zero.GetProperty("count").GetInt32());

            var filtered = RunCountJson(
                dbPath,
                ["--exclude-path", "src/caller_00.py", "--limit", "1", "--max-hops", "1"]);
            Assert.Equal(54, filtered.GetProperty("count").GetInt32());

            var multiHop = RunCountJson(dbPath, ["--limit", "1", "--max-hops", "2"]);
            Assert.Equal(56, multiHop.GetProperty("count").GetInt32());
            Assert.Equal(2, multiHop.GetProperty("actual_depth").GetInt32());

            var (rowsExitCode, rowsStdout, rowsStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["issue5226_target", "--db", dbPath, "--json", "--limit", "1", "--max-hops", "1"],
                _jsonOptions));
            using var rowsDocument = ParseJsonOutput(rowsStdout);
            var rows = rowsDocument.RootElement;
            Assert.Equal(CommandExitCodes.Success, rowsExitCode);
            Assert.Equal(string.Empty, rowsStderr);
            Assert.Equal(1, rows.GetProperty("count").GetInt32());
            Assert.Single(rows.GetProperty("callers").EnumerateArray());
            Assert.True(rows.GetProperty("truncated").GetBoolean());
            Assert.Equal("user_limit", rows.GetProperty("truncated_reason").GetString());

            System.Text.Json.JsonElement RunCountJson(string path, string[] extraArgs)
            {
                var args = new List<string>
                {
                    "issue5226_target", "--db", path, "--json", "--count",
                };
                args.AddRange(extraArgs);
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact([.. args], _jsonOptions));
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = ParseJsonOutput(stdout);
                return document.RootElement.Clone();
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
