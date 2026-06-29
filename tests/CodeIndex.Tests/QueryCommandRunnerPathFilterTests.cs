using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData("install.sh")]
    [InlineData("./install.sh")]
    [InlineData("/install.sh")]
    public void RunFiles_PathFilterAnchorsTopLevelFile_Issue4163(string pathFilter)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_path_anchor_issue4163");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "install.sh", "shell", "echo install\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "install_modules/40-uninstall.sh", "shell", "echo uninstall\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "install_modules/60-reinstall.sh", "shell", "echo reinstall\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--path", pathFilter, "--limit", "20"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("install.sh", document.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_PathFilterAnchorsTopLevelDirectory_Issue4163()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_path_anchor_issue4163");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tools/ToolHost.cs",
                "csharp",
                "public class ToolHost { public void Run() {} }\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/CodeIndex.Tests/McpServerToolsCallTests.cs",
                "csharp",
                "public class McpServerToolsCallTests { public void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--path", "tools", "--limit", "20"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();
            var paths = rows.Select(row => row.GetProperty("path").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("tools/ToolHost.cs", paths);
            Assert.DoesNotContain("tests/CodeIndex.Tests/McpServerToolsCallTests.cs", paths);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
