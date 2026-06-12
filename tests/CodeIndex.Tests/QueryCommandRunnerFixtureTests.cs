using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_ExcludeFixturesKeepsProductionFilesWithTestSubstringNames_Issue3450()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_fixture_substring_names");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/latest.cs", "csharp", "var text = \"ProdFixtureNeedle\";\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/contest.py", "python", "value = \"ProdFixtureNeedle\"\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["ProdFixtureNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--exclude-fixtures"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var rows = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(["src/contest.py", "src/latest.cs"], rows.Select(row => row.GetProperty("path").GetString()).OrderBy(path => path, StringComparer.Ordinal).ToArray());
            foreach (var row in rows)
            {
                Assert.False(row.GetProperty("test_file").GetBoolean());
                Assert.False(row.GetProperty("test_fixture").GetBoolean());
                Assert.DoesNotContain(row.GetProperty("match_facets").EnumerateArray(), facet => facet.GetProperty("test_fixture").GetBoolean());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
