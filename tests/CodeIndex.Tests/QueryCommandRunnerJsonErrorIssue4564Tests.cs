using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerJsonErrorIssue4564Tests
{
    [Fact]
    public void JsonValidationAndLookupFailures_EmitVersionedErrorEnvelope_Issue4564()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_json_errors_issue4564");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "public class Sample { }\n");

            var cases = new (string Name, int ExpectedExitCode, string ExpectedErrorCode, Func<int> Run)[]
            {
                (
                    "search validation",
                    CommandExitCodes.UsageError,
                    CommandErrorCodes.UsageError,
                    () => QueryCommandRunner.RunSearch(["--json"], JsonOptions)),
                (
                    "find validation",
                    CommandExitCodes.UsageError,
                    CommandErrorCodes.UsageError,
                    () => QueryCommandRunner.RunFind(["--json"], JsonOptions)),
                (
                    "status mode validation",
                    CommandExitCodes.UsageError,
                    CommandErrorCodes.UsageError,
                    () => QueryCommandRunner.RunStatus(["--config", "--check", "--json"], JsonOptions)),
                (
                    "goto not found",
                    CommandExitCodes.NotFound,
                    CommandErrorCodes.QueryNotFound,
                    () => QueryCommandRunner.RunGoto(["__NOT_FOUND_9fb0__", "--db", dbPath, "--json"], JsonOptions)),
                (
                    "excerpt file not found",
                    CommandExitCodes.NotFound,
                    CommandErrorCodes.FileNotFound,
                    () => QueryCommandRunner.RunExcerpt(["NOPE.md", "--start", "1", "--db", dbPath, "--json"], JsonOptions)),
                (
                    "excerpt line out of range",
                    CommandExitCodes.InvalidArgument,
                    CommandErrorCodes.LineOutOfRange,
                    () => QueryCommandRunner.RunExcerpt(["src/Sample.cs", "--start", "99", "--db", dbPath, "--json"], JsonOptions)),
                (
                    "excerpt non-positive line",
                    CommandExitCodes.InvalidArgument,
                    CommandErrorCodes.LineOutOfRange,
                    () => QueryCommandRunner.RunExcerpt(["src/Sample.cs", "--start", "0", "--db", dbPath, "--json"], JsonOptions)),
            };

            foreach (var testCase in cases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(testCase.Run);

                Assert.Equal(testCase.ExpectedExitCode, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                Assert.Equal("1", root.GetProperty("api_version").GetString());
                Assert.Equal("error", root.GetProperty("status").GetString());
                Assert.Equal(testCase.ExpectedErrorCode, root.GetProperty("error_code").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()), testCase.Name);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
