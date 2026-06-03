using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunValidate_ReplacementCharJson_IncludesOriginAndSeverity()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_replacement_origin");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "literal.cs"),
                "class Literal { const char Value = '\uFFFD'; }\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--kind", "replacement_char"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var issue = json.GetProperty("issues")[0];

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("replacement_char", issue.GetProperty("kind").GetString());
            Assert.Equal(FileIssue.OriginSourceLiteral, issue.GetProperty("origin").GetString());
            Assert.Equal(FileIssue.SeverityInfo, issue.GetProperty("severity").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
