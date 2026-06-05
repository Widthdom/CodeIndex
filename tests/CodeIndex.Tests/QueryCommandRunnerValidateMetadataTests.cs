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

    [Fact]
    public void RunValidate_SeverityFilterNarrowsIssues_Issue3008()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_severity_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "literal.cs"),
                "class Literal { const char Value = '\uFFFD'; }\n");

            var bytes = new List<byte>();
            void AddUtf8(string text) => bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(text));
            AddUtf8("line1 clean\n");
            AddUtf8("line2 has ");
            bytes.Add(0xFF);
            AddUtf8(" here\n");
            for (var i = 0; i < 200; i++)
                AddUtf8("filler ascii ascii ascii\n");
            File.WriteAllBytes(Path.Combine(projectRoot, "src", "decode.cs"), bytes.ToArray());

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (warningExitCode, warningStdout, warningStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--kind", "replacement_char", "--severity", "warning"],
                _jsonOptions));
            var (infoExitCode, infoStdout, infoStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--kind", "replacement_char", "--severity", "info"],
                _jsonOptions));

            using var warningDocument = ParseJsonOutput(warningStdout);
            using var infoDocument = ParseJsonOutput(infoStdout);
            var warningIssues = warningDocument.RootElement.GetProperty("issues");
            var infoIssues = infoDocument.RootElement.GetProperty("issues");

            Assert.Equal(CommandExitCodes.Success, warningExitCode);
            Assert.Equal(CommandExitCodes.Success, infoExitCode);
            Assert.Equal(string.Empty, warningStderr);
            Assert.Equal(string.Empty, infoStderr);
            Assert.True(warningIssues.GetArrayLength() > 0);
            Assert.Equal(warningIssues.GetArrayLength(), warningDocument.RootElement.GetProperty("count").GetInt32());
            Assert.All(warningIssues.EnumerateArray(), issue =>
            {
                Assert.Equal("replacement_char", issue.GetProperty("kind").GetString());
                Assert.Equal(FileIssue.SeverityWarning, issue.GetProperty("severity").GetString());
                Assert.Equal(FileIssue.OriginDecodeReplacement, issue.GetProperty("origin").GetString());
            });
            Assert.Equal(1, infoDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, infoIssues.GetArrayLength());
            Assert.Equal(FileIssue.SeverityInfo, infoIssues[0].GetProperty("severity").GetString());
            Assert.Equal(FileIssue.OriginSourceLiteral, infoIssues[0].GetProperty("origin").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
