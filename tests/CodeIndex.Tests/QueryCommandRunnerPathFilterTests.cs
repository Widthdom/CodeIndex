using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunFiles_PositionalGlobUsesPathFilterSemantics_Issue4565()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_positional_glob_issue4565");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "App.cs", "csharp", "public class App {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/A.cs", "csharp", "public class A {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/deep/NestedThing.cs", "csharp", "public class NestedThing {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/deep/NestedThing.txt", "text", "not C#\n");

            var cases = new[]
            {
                (Pattern: "*.cs", ExpectedPaths: new[] { "App.cs", "src/A.cs", "src/deep/NestedThing.cs" }),
                (Pattern: "src/?.cs", ExpectedPaths: new[] { "src/A.cs" }),
                (Pattern: "**/Nested*.cs", ExpectedPaths: new[] { "src/deep/NestedThing.cs" }),
            };

            foreach (var testCase in cases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                    ["--db", dbPath, "--json", testCase.Pattern, "--limit", "20"],
                    _jsonOptions));

                var paths = ParseJsonLines(stdout)
                    .Select(document => document.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null)
                    .Where(path => path != null)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(testCase.ExpectedPaths.OrderBy(path => path, StringComparer.Ordinal), paths);
            }

            var explicitQueryCases = new[]
            {
                (Args: new[] { "--query", "*.cs" }, ExpectedQuery: "*.cs"),
                (Args: new[] { "--", "*.cs" }, ExpectedQuery: "*.cs"),
                (Args: new[] { @"literal\*.cs" }, ExpectedQuery: @"literal\*.cs"),
            };
            foreach (var testCase in explicitQueryCases)
            {
                var options = QueryCommandRunner.ParseArgs(
                    testCase.Args,
                    jsonDefault: false,
                    allowNamedQuery: true,
                    positionalGlobAsPath: true);

                Assert.Equal(testCase.ExpectedQuery, options.Query);
                Assert.Empty(options.PathPatterns);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

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
