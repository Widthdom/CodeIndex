using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerSearchGuardIssue4349Tests
{
    [Fact]
    public void RunSearch_CountJsonGuardFiltersUseSearchResultUnitsAndExposeContext_Issue4349()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_count_issue4349");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/guard-count.cs",
                "csharp",
                """
                public class GuardCount
                {
                    public void Run()
                    {
                        GuardMarker(); TargetCall();
                        GuardMarker(); TargetCall();
                    }
                }
                """);

            using var baseDocument = RunSearchCountJson(
                "TargetCall",
                "--db", dbPath,
                "--exact-substring",
                "--format", "count",
                "--json");
            var baseCount = baseDocument.RootElement.GetProperty("count").GetInt32();
            Assert.Equal(1, baseCount);

            using var requireDocument = RunSearchCountJson(
                "TargetCall",
                "--db", dbPath,
                "--exact-substring",
                "--format", "count",
                "--json",
                "--require-before", "GuardMarker",
                "--guard-window", "3",
                "--guard-scope", "same-line");
            var requireJson = requireDocument.RootElement;
            var requireCount = requireJson.GetProperty("count").GetInt32();
            Assert.InRange(requireCount, 0, baseCount);
            Assert.Equal(1, requireCount);

            var queryContext = requireJson.GetProperty("query_context");
            Assert.Equal(3, queryContext.GetProperty("guard_window").GetInt32());
            Assert.Equal("same-line", queryContext.GetProperty("guard_scope").GetString());
            var requireFilter = Assert.Single(queryContext.GetProperty("guard_filters").EnumerateArray());
            Assert.Equal("require-before", requireFilter.GetProperty("name").GetString());
            Assert.Equal("require", requireFilter.GetProperty("role").GetString());
            Assert.Equal("before", requireFilter.GetProperty("direction").GetString());
            Assert.Equal("GuardMarker", requireFilter.GetProperty("query").GetString());

            using var rejectDocument = RunSearchCountJson(
                "TargetCall",
                "--db", dbPath,
                "--exact-substring",
                "--format", "count",
                "--json",
                "--reject-before", "MissingGuard",
                "--guard-window", "3",
                "--guard-scope", "same-line");
            var rejectCount = rejectDocument.RootElement.GetProperty("count").GetInt32();
            Assert.InRange(rejectCount, 0, baseCount);
            Assert.Equal(1, rejectCount);

            using var originFilteredDocument = RunSearchCountJson(
                "TargetCall",
                "--db", dbPath,
                "--exact-substring",
                "--format", "count",
                "--json",
                "--require-before", "GuardMarker",
                "--guard-window", "3",
                "--guard-scope", "same-line",
                "--match-origin", "code");
            var originFilteredJson = originFilteredDocument.RootElement;
            Assert.Equal(1, originFilteredJson.GetProperty("count").GetInt32());
            Assert.Equal("same-line", originFilteredJson.GetProperty("query_context").GetProperty("guard_scope").GetString());

            using var tokenBoundaryDocument = RunSearchCountJson(
                "TargetCall",
                "--db", dbPath,
                "--token-boundary",
                "--format", "count",
                "--json",
                "--require-before", "GuardMarker",
                "--guard-window", "3",
                "--guard-scope", "same-line",
                "--match-origin", "code");
            var tokenBoundaryJson = tokenBoundaryDocument.RootElement;
            Assert.Equal(2, tokenBoundaryJson.GetProperty("count").GetInt32());
            Assert.Equal("same-line", tokenBoundaryJson.GetProperty("query_context").GetProperty("guard_scope").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private JsonDocument RunSearchCountJson(params string[] args)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, JsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return ParseJsonOutput(stdout);
    }
}
