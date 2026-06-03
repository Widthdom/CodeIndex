using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_ExactSubstringJsonOutputsLiteralHighlightMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_literal_highlight");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql.cs",
                "csharp",
                "var CommandText = $\"SELECT 1\";\nvar CommandText = other;");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--json", "--exact-substring", "--snippet-lines", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var highlight = document.RootElement.GetProperty("highlights")[0];
            var literalOccurrence = highlight.GetProperty("literal_term_occurrences")[0];

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("CommandText = $", highlight.GetProperty("literal_terms")[0].GetString());
            Assert.Equal("CommandText = $", literalOccurrence.GetProperty("term").GetString());
            Assert.Equal(1, literalOccurrence.GetProperty("line").GetInt32());
            Assert.Equal(5, literalOccurrence.GetProperty("column").GetInt32());
            Assert.Equal("CommandText = $".Length, literalOccurrence.GetProperty("length").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_PunctuationHeavyTextSuggestsExactSubstring()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_substring_hint_text");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql.cs",
                "csharp",
                "var CommandText = $\"SELECT 1\";\nvar CommandText = other;");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/sql.cs", stdout);
            Assert.Contains("Hint: This looks like a literal code phrase; try --exact-substring for punctuation-sensitive matching.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_PunctuationHeavyJsonAddsExactSubstringHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_substring_hint_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql.cs",
                "csharp",
                "var CommandText = $\"SELECT 1\";\nvar CommandText = other;");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var hint = document.RootElement.GetProperty("exact_substring_hint");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("punctuation_heavy_query", hint.GetProperty("reason").GetString());
            Assert.Equal("--exact-substring", hint.GetProperty("flag").GetString());
            Assert.Equal("exactSubstring", hint.GetProperty("mcp_argument").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_PunctuationHeavyJsonArraySuppressesRankOnlyRows_Issue2821()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_rank_only_2821");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "void Run() { throw new InvalidOperationException(); }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["throw;", "--db", dbPath, "--json=array"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(document.RootElement.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
