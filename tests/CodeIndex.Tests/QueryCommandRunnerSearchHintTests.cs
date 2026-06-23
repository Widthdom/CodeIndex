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
            var root = document.RootElement;
            var highlight = document.RootElement.GetProperty("highlights")[0];
            var literalOccurrence = highlight.GetProperty("literal_term_occurrences")[0];

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, root.GetProperty("snippet_lines").GetInt32());
            Assert.Equal(512, root.GetProperty("max_line_width").GetInt32());
            Assert.True(root.GetProperty("exact").GetBoolean());
            Assert.False(root.GetProperty("raw_fts").GetBoolean());
            Assert.True(root.GetProperty("literal_highlights_available").GetBoolean());
            Assert.False(root.TryGetProperty("literal_highlight_warning", out _));
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
    public void RunSearch_ExactSubstringWithRawFtsReportsEffectiveLiteralHighlightMode_Issue3558()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_raw_fts_metadata_3558");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql.cs",
                "csharp",
                "var CommandText = $\"SELECT 1\";\nvar CommandText = other;");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--json", "--fts", "--exact-substring"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var highlight = root.GetProperty("highlights")[0];

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(root.GetProperty("exact").GetBoolean());
            Assert.False(root.GetProperty("raw_fts").GetBoolean());
            Assert.True(root.GetProperty("literal_highlights_available").GetBoolean());
            Assert.False(root.TryGetProperty("literal_highlight_warning", out _));
            Assert.Equal("CommandText = $", highlight.GetProperty("literal_terms")[0].GetString());
            Assert.Equal("CommandText = $", highlight.GetProperty("literal_term_occurrences")[0].GetProperty("term").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsJsonReportsLiteralHighlightGapMetadata_Issue3558()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_raw_fts_metadata_3558");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql.cs",
                "csharp",
                "var CommandText = $\"SELECT 1\";\nvar CommandText = other;");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText", "--db", dbPath, "--json", "--fts", "--snippet-lines", "3", "--max-line-width", "80"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, root.GetProperty("snippet_lines").GetInt32());
            Assert.Equal(80, root.GetProperty("max_line_width").GetInt32());
            Assert.False(root.GetProperty("exact").GetBoolean());
            Assert.True(root.GetProperty("raw_fts").GetBoolean());
            Assert.False(root.GetProperty("literal_highlights_available").GetBoolean());
            Assert.Equal("literal_highlights_unavailable_raw_fts", root.GetProperty("literal_highlight_warning").GetString());
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
            Assert.Contains(
                "Hint: This looks like a literal code phrase; rerun with `--exact-substring`, for example: `cdidx search --exact-substring --query \"...\"`, for punctuation-sensitive matching.",
                stderr);
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
            Assert.Equal(
                "This looks like a literal code phrase; rerun with `--exact-substring`, for example: `cdidx search --exact-substring --query \"...\"`, for punctuation-sensitive matching.",
                hint.GetProperty("suggested_action").GetString());
            Assert.Equal("--exact-substring", hint.GetProperty("flag").GetString());
            Assert.Equal("exactSubstring", hint.GetProperty("mcp_argument").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_PunctuationHeavyJsonArrayAddsHintOnlyToFirstResult_Issue3903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_substring_hint_once_3903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/one.cs",
                "csharp",
                "var items = values.ToArray();\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/two.cs",
                "csharp",
                "return values.ToArray();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["ToArray()", "--db", dbPath, "--json=array", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var rows = document.RootElement.EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Length);
            Assert.True(rows[0].TryGetProperty("exact_substring_hint", out _));
            Assert.DoesNotContain(rows.Skip(1), row => row.TryGetProperty("exact_substring_hint", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SearchQueryAdvisor_SuggestsExactSubstringForTwoTokenCodePhrase_Issue3975()
    {
        Assert.True(SearchQueryAdvisor.ShouldSuggestExactSubstring("async void", rawQuery: false, exact: false, prefix: false));
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
