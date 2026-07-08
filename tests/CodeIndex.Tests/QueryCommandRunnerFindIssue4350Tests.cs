using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunFind_AllScopeLineScanLimitControlsCountJson_Issue4350()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_line_scan_limit_4350");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/large.txt", "text", "alpha\nalpha\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--count", "--line-scan-limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("lines_scanned").GetInt32());
            Assert.True(json.GetProperty("scan_truncated").GetBoolean());
            Assert.True(json.GetProperty("scan_cap_reached").GetBoolean());
            Assert.Equal("line_scan_limit", json.GetProperty("scan_truncation_reason").GetString());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
            Assert.Equal(1, json.GetProperty("line_scan_limit").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10000001")]
    public void RunFind_LineScanLimitRejectsInvalidBounds_Issue4350(string value)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["needle", "--all", "--line-scan-limit", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--line-scan-limit", stderr);
    }

    [Fact]
    public void RunFind_LineScanLimitRequiresAll_Issue4350()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["needle", "--path", "src/app.txt", "--line-scan-limit", "1000"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--line-scan-limit is only supported with find --all", stderr);
    }

    [Theory]
    [InlineData("--before", "1")]
    [InlineData("--after", "1")]
    [InlineData("--snippet-lines", "3")]
    public void RunFind_CompactRejectsContextFlags_Issue4350(string flag, string value)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["needle", "--path", "src/app.txt", "--format", "compact", flag, value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("find --format compact does not include snippets", stderr);
    }
}
