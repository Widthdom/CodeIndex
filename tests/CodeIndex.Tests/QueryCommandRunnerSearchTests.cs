using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void FetchLimitForSearchEnvelope_ClampsHugeInternalLimit_Issue3964()
    {
        Assert.Equal(1, QueryCommandRunner.FetchLimitForSearchEnvelopeForTests(0));
        Assert.Equal(200, QueryCommandRunner.FetchLimitForSearchEnvelopeForTests(1));
        Assert.Equal(QueryCommandRunner.MaxQueryResultLimit, QueryCommandRunner.FetchLimitForSearchEnvelopeForTests(int.MaxValue));
    }

    [Fact]
    public void GetUnusedFetchLimit_ClampsDeepCursorFetch_Issue3964()
    {
        Assert.Equal(21, QueryCommandRunner.GetUnusedFetchLimitForTests(20, 0));
        Assert.Equal(
            QueryCommandRunner.MaxUnusedPaginationFetchLimit,
            QueryCommandRunner.GetUnusedFetchLimitForTests(QueryCommandRunner.MaxQueryResultLimit, int.MaxValue));
        Assert.True(QueryCommandRunner.IsUnusedCursorOffsetWithinFetchCapForTests(
            QueryCommandRunner.MaxQueryResultLimit,
            QueryCommandRunner.MaxUnusedPaginationOffset));
        Assert.False(QueryCommandRunner.IsUnusedCursorOffsetWithinFetchCapForTests(
            QueryCommandRunner.MaxQueryResultLimit,
            QueryCommandRunner.MaxUnusedPaginationOffset + 1));
    }

    [Fact]
    public void RunSearch_GroupByFileCountStreamsFileCounts_Issue3741()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_file_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/b.cs", "csharp", "Needle();\nNeedle();\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a.cs", "csharp", "Needle();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Needle", "--db", dbPath, "--exact-substring", "--group-by", "file", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            Assert.Equal(2, root.GetProperty("files").GetInt32());
            var groups = root.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal("src/a.cs", groups[0].GetProperty("file").GetString());
            Assert.Equal(1, groups[0].GetProperty("count").GetInt32());
            Assert.Equal("src/b.cs", groups[1].GetProperty("file").GetString());
            Assert.Equal(1, groups[1].GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_EditorAndDelimitedFormatsUsePrimaryMatchSpan_Issue3931()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_primary_match_span_3931");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App\n{\n    void Run()\n    {\n        var value = Needle;\n    }\n}\n");

            var (lspExitCode, lspStdout, lspStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Needle", "--db", dbPath, "--format", "lsp", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, lspExitCode);
            Assert.Equal(string.Empty, lspStderr);
            using (var document = ParseJsonOutput(lspStdout))
            {
                var location = Assert.Single(document.RootElement.EnumerateArray());
                var range = location.GetProperty("range");
                Assert.Equal(4, range.GetProperty("start").GetProperty("line").GetInt32());
                Assert.Equal(20, range.GetProperty("start").GetProperty("character").GetInt32());
                Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
                Assert.Equal(26, range.GetProperty("end").GetProperty("character").GetInt32());
            }

            var (qfExitCode, qfStdout, qfStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Needle", "--db", dbPath, "--format", "qf", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, qfExitCode);
            Assert.Equal(string.Empty, qfStderr);
            Assert.Equal("src/app.cs:5:21:search match: Needle", qfStdout.Trim());

            var (csvExitCode, csvStdout, csvStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Needle", "--db", dbPath, "--format", "csv", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, csvExitCode);
            Assert.Equal(string.Empty, csvStderr);
            var csvLines = csvStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToArray();
            Assert.Equal(2, csvLines.Length);
            var fields = csvLines[1].Split(',');
            Assert.Equal("src/app.cs", fields[0]);
            Assert.Equal("5", fields[1]);
            Assert.Equal("21", fields[2]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonIncludesMatchOrigins_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_match_origins");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/match.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public class Demo
                {
                    public void Run()
                    {
                        OriginNeedle();
                        // OriginNeedle in comment
                        var text = "OriginNeedle in string";
                        var regex = new Regex("OriginNeedle\d+");
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["OriginNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--snippet-lines", "12"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            var origins = row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("code", origins);
            Assert.Contains("comment", origins);
            Assert.Contains("string_literal", origins);
            Assert.Contains("regex_literal", origins);

            var facets = row.GetProperty("match_facets").EnumerateArray().ToArray();
            Assert.Contains(facets, facet => facet.GetProperty("origin").GetString() == "comment");
            Assert.Contains(facets, facet => facet.GetProperty("origin").GetString() == "regex_literal");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GitHubActionsRunBlocksClassifyAsCode_Issue3917()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_yaml_run_origin_3917");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                ".github/workflows/release.yml",
                "yaml",
                """
                name: release
                jobs:
                  publish:
                    steps:
                      - name: verify release asset
                        run: |
                          code="$(curl -fsSL --connect-timeout 10 "$url" || true)"
                      - name: folded script
                        run: >
                          wget --quiet https://example.invalid/package
                      - name: prose
                        notes: |
                          "DocNeedle --help" is documentation, not a workflow script.
                """);

            var (curlExitCode, curlStdout, curlStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["curl", "--db", dbPath, "--path", ".github/**", "--json=array"],
                _jsonOptions));
            var (wgetExitCode, wgetStdout, wgetStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["wget", "--db", dbPath, "--path", ".github/**", "--json=array"],
                _jsonOptions));
            var (docsExitCode, docsStdout, docsStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["DocNeedle", "--db", dbPath, "--path", ".github/**", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, curlExitCode);
            Assert.Equal(string.Empty, curlStderr);
            Assert.Equal(CommandExitCodes.Success, wgetExitCode);
            Assert.Equal(string.Empty, wgetStderr);
            Assert.Equal(CommandExitCodes.Success, docsExitCode);
            Assert.Equal(string.Empty, docsStderr);

            Assert.Contains("code", SingleMatchOrigins(curlStdout));
            Assert.Contains("code", SingleMatchOrigins(wgetStdout));
            Assert.Contains("help_text", SingleMatchOrigins(docsStdout));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static string[] SingleMatchOrigins(string stdout)
        {
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            return row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
    }

    [Fact]
    public void RunSearch_ExcludeCommentsSuppressesCommentOnlyMatches_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_comments");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.cs", "csharp", "// FilterNeedle appears only in a comment\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.cs", "csharp", "public class Demo { void Run() { FilterNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["FilterNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--exclude-comments"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.DoesNotContain("comment", row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeCommentsCountUsesOriginFilter_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_comments_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.cs", "csharp", "// CountNeedle appears only in a comment\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.cs", "csharp", "public class Demo { void Run() { CountNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CountNeedle", "--db", dbPath, "--exact-substring", "--exclude-comments", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeCommentsKeepsCodeMatchOutsideSnippet_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_comments_outside_snippet");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/mixed.cs",
                "csharp",
                """
                // FarNeedle appears in a comment
                // filler 1
                // filler 2
                // filler 3
                // filler 4
                // filler 5
                // filler 6
                // filler 7
                // filler 8
                public class Demo { void Run() { FarNeedle(); } }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["FarNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--snippet-lines", "1", "--exclude-comments"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/mixed.cs", row.GetProperty("path").GetString());
            Assert.Equal(10, row.GetProperty("snippet_start_line").GetInt32());
            Assert.Equal(10, row.GetProperty("snippet_end_line").GetInt32());
            Assert.Contains("FarNeedle();", row.GetProperty("snippet").GetString());
            Assert.DoesNotContain("appears in a comment", row.GetProperty("snippet").GetString());
            Assert.Equal([10], row.GetProperty("match_lines").EnumerateArray().Select(value => value.GetInt32()).ToArray());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeCommentsSuppressesNonCSharpInlineComments_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_inline_comments");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.js", "javascript", "run(); // InlineCommentNeedle\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment_quote.js", "javascript", "run(); // don't InlineCommentNeedle\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.py", "python", "run()  # InlineCommentNeedle\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment_quote.py", "python", "run()  # \"InlineCommentNeedle\"\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/block.js", "javascript", "run(); /* InlineCommentNeedle */\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/block_quote.js", "javascript", "run(); /* don't InlineCommentNeedle */\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.js", "javascript", "InlineCommentNeedle();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["InlineCommentNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--exclude-comments"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.js", row.GetProperty("path").GetString());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("javascript", "run(); // InlineMarkerNeedle\n", "//")]
    [InlineData("python", "run()  # InlineMarkerNeedle\n", "#")]
    [InlineData("javascript", "run(); /* InlineMarkerNeedle */\n", "/*")]
    public void RunSearch_ExcludeCommentsSuppressesInlineCommentMarkers_Issue3423(string lang, string content, string query)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_inline_comment_markers");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, $"src/comment.{lang}", lang, content);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath, "--exact-substring", "--json=array", "--exclude-comments"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Empty(document.RootElement.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeStringsSuppressesStringAndRegexMatches_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_strings");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/string.cs", "csharp", "var text = \"StringOnlyNeedle\";\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/regex.cs", "csharp", "var pattern = new Regex(\"StringOnlyNeedle\");\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.cs", "csharp", "public class Demo { void Run() { StringOnlyNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["StringOnlyNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--exclude-strings"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeStringsOverfetchesPastFilteredLimit_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_strings_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/aaa_string.cs", "csharp", "var text = \"LimitNeedle\";\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/zzz_code.cs", "csharp", "public class Real { void Run() { LimitNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["LimitNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--limit", "1", "--exclude-strings"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/zzz_code.cs", row.GetProperty("path").GetString());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeStringsSuppressesRawFtsStringMatches_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_strings_raw_fts");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/string.cs", "csharp", "var text = \"RawFtsNeedle\";\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.cs", "csharp", "public class Demo { void Run() { RawFtsNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["content:RawFtsNeedle", "--db", dbPath, "--fts", "--json=array", "--exclude-strings"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.NotEmpty(row.GetProperty("match_facets").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeStringsSuppressesRawFtsNumericStringMatches_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_strings_raw_fts_numeric");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/string.cs", "csharp", "var text = \"12345\";\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/code.cs", "csharp", "public class Demo { void Run() { var value = 12345; } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["content:12345", "--db", dbPath, "--fts", "--json=array", "--exclude-strings"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.Equal(["code"], row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonIdentifiesTestFixtureMatches_Issue3450()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_test_fixtures");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/DemoTests.cs",
                "csharp",
                """
                using Xunit;

                public class DemoTests
                {
                    [Fact]
                    public void MatchesFixture()
                    {
                        var fixtureSource = "FixtureNeedle();";
                        FixtureNeedle();
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["FixtureNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--snippet-lines", "12"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.True(row.GetProperty("test_file").GetBoolean());
            Assert.True(row.GetProperty("test_symbol").GetBoolean());
            Assert.True(row.GetProperty("test_fixture").GetBoolean());

            var facets = row.GetProperty("match_facets").EnumerateArray().ToArray();
            var fixtureFacet = Assert.Single(facets, facet => facet.GetProperty("origin").GetString() == "string_literal");
            Assert.True(fixtureFacet.GetProperty("test_file").GetBoolean());
            Assert.True(fixtureFacet.GetProperty("test_symbol").GetBoolean());
            Assert.True(fixtureFacet.GetProperty("test_fixture").GetBoolean());

            var codeFacet = Assert.Single(facets, facet => facet.GetProperty("origin").GetString() == "code");
            Assert.True(codeFacet.GetProperty("test_file").GetBoolean());
            Assert.True(codeFacet.GetProperty("test_symbol").GetBoolean());
            Assert.False(codeFacet.GetProperty("test_fixture").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeFixturesSuppressesFixtureOnlyMatches_Issue3450()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_exclude_fixtures");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/FixtureTests.cs",
                "csharp",
                """
                using Xunit;

                public class FixtureTests
                {
                    [Fact]
                    public void HasFixtureSource()
                    {
                        var fixtureSource = "FixtureOnlyNeedle();";
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/real.cs",
                "csharp",
                "public class Real { public void Run() { FixtureOnlyNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["FixtureOnlyNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--exclude-fixtures"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/real.cs", row.GetProperty("path").GetString());
            Assert.False(row.GetProperty("test_fixture").GetBoolean());
            Assert.DoesNotContain(row.GetProperty("match_facets").EnumerateArray(), facet => facet.GetProperty("test_fixture").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FormatCompactEmitsBoundedSnippet_Issue3481()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_format_compact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { void Run() { Authenticate(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--format", "compact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("Authenticate", row.GetProperty("query").GetString());
            Assert.Equal("src/app.cs", row.GetProperty("path").GetString());
            Assert.True(row.GetProperty("chunk_start_line").GetInt32() > 0);
            Assert.Contains("Authenticate", row.GetProperty("snippet").GetString(), StringComparison.Ordinal);
            Assert.NotEmpty(row.GetProperty("match_lines").EnumerateArray());
            Assert.NotEmpty(row.GetProperty("highlights").EnumerateArray());
            Assert.False(row.TryGetProperty("name", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MaxResultsAliasLimitsSearchResults_Issue3521()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_max_results_3521");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/one.cs", "csharp", "class One { string Value = \"needle\"; }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/two.cs", "csharp", "class Two { string Value = \"needle\"; }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["needle", "--db", dbPath, "--json=array", "--max-results", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(document.RootElement.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MissingDashLiteralQuerySuggestsEscapes_Issue3521()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--exact", "--profile", "--limit", "5"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: search requires a query argument", stderr);
        Assert.Contains("`--query \"--profile\"`", stderr);
        Assert.Contains("`cdidx search -- \"--profile\"`", stderr);
    }

    [Fact]
    public void RunSearch_PathGlobExpansionHintSuggestsQuotedPath_Issue3445()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["needle", "--path", "src/CodeIndex/Cli", "src/CodeIndex/Database"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: unexpected extra positional 1 argument for search: `src/CodeIndex/Database`.", stderr);
        Assert.Contains("quote --path globs so the shell passes one literal pattern", stderr);
        Assert.Contains("`--path 'src/CodeIndex/**'`", stderr);
    }

    [Fact]
    public void RunSearch_MultiwordLiteralRanksExactPhraseContentFirst_Issue3389()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_multiword_phrase_3389");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/not supported noise.cs",
                "csharp",
                "class Noise { string Message = \"not every platform is supported\"; }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/z_phrase.cs",
                "csharp",
                "class Phrase { string Message = \"feature is not supported here\"; }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["not supported", "--db", dbPath, "--json=array", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var rows = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal("src/z_phrase.cs", rows[0].GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_NamedQueriesReturnGroupedCompactResults_Issue3481()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_named_queries");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "release/pack.md",
                "markdown",
                "Run dotnet pack before publishing.");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "release/pack-extra.md",
                "markdown",
                "Run dotnet pack after signing.");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "release/push.md",
                "markdown",
                "Run nuget push after package validation.");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--named-query=pack=dotnet pack", "--named-query=push=nuget push", "--db", dbPath, "--format", "compact", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal(2, root.GetProperty("query_count").GetInt32());
            Assert.Equal(2, root.GetProperty("result_count").GetInt32());
            var queries = root.GetProperty("queries").EnumerateArray().ToList();
            var pack = Assert.Single(queries, query => query.GetProperty("name").GetString() == "pack");
            Assert.Equal("dotnet pack", pack.GetProperty("query").GetString());
            var packResult = Assert.Single(pack.GetProperty("results").EnumerateArray());
            var packPath = packResult.GetProperty("path").GetString();
            Assert.NotNull(packPath);
            Assert.True(pack.GetProperty("truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, pack.GetProperty("next_cursor").ValueKind);
            Assert.Equal(packPath, pack.GetProperty("top_files")[0].GetProperty("path").GetString());
            Assert.StartsWith("release/pack", packPath, StringComparison.Ordinal);
            Assert.Contains("dotnet pack", packResult.GetProperty("snippet").GetString(), StringComparison.Ordinal);
            Assert.NotEmpty(packResult.GetProperty("match_lines").EnumerateArray());

            var (capExitCode, capStdout, capStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--named-query=pack=dotnet pack", "--db", dbPath, "--format", "compact", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, capExitCode);
            Assert.Equal(string.Empty, capStdout);
            Assert.Contains("named-query search JSON output", capStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_NamedQueriesRejectExactPrefixConflict_Issue3481()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_named_queries_exact_prefix");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { void Run() { Authenticate(); } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--named-query", "auth=Authenticate", "--db", dbPath, "--exact-substring", "--prefix"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--prefix cannot be combined with --exact", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByFileCountJsonReturnsRankedGroups_Issue3388()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/alpha.cs",
                "csharp",
                "public class Alpha { public void Run() { AuditMarker(); } }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/beta.cs",
                "csharp",
                "public class Beta { public void Run() { AuditMarker(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["AuditMarker();", "--db", dbPath, "--exact-substring", "--group-by", "file", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal("AuditMarker();", root.GetProperty("query").GetString());
            Assert.Equal("file", root.GetProperty("group_by").GetString());
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            Assert.Equal(2, root.GetProperty("files").GetInt32());
            var groups = root.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(["src/alpha.cs", "src/beta.cs"], groups.Select(group => group.GetProperty("file").GetString()).ToArray());
            Assert.All(groups, group => Assert.Equal(1, group.GetProperty("count").GetInt32()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByFileCountLimitCapsReturnedGroups_Issue4119()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_file_limit_4119");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/alpha.cs", "csharp", "public class Alpha { void Run() { LimitMarker(); } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/beta.cs", "csharp", "public class Beta { void Run() { LimitMarker(); } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/gamma.cs", "csharp", "public class Gamma { void Run() { LimitMarker(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["LimitMarker", "--db", dbPath, "--exact-substring", "--group-by", "file", "--count", "--json", "--limit", "2"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var groups = root.GetProperty("groups").EnumerateArray().ToArray();

            Assert.Equal(3, root.GetProperty("count").GetInt32());
            Assert.Equal(3, root.GetProperty("files").GetInt32());
            Assert.Equal(2, root.GetProperty("returned_groups").GetInt32());
            Assert.Equal(3, root.GetProperty("total_groups").GetInt32());
            Assert.True(root.GetProperty("groups_truncated").GetBoolean());
            Assert.Equal(2, root.GetProperty("group_limit").GetInt32());
            Assert.Equal(2, groups.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupBySymbolCountJsonIncludesEnclosingSymbols_Issue3388()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_symbol");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/alpha.cs",
                "csharp",
                """
                public class Alpha
                {
                    public void Run()
                    {
                        AuditMarker();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/beta.cs",
                "csharp",
                """
                public class Beta
                {
                    public void Execute()
                    {
                        AuditMarker();
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["AuditMarker();", "--db", dbPath, "--exact-substring", "--group-by", "symbol", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal("symbol", root.GetProperty("group_by").GetString());
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            var groups = root.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group =>
                group.GetProperty("file").GetString() == "src/alpha.cs" &&
                group.GetProperty("symbol_name").GetString() == "Run" &&
                group.GetProperty("symbol_kind").GetString() == "function" &&
                group.GetProperty("symbol_start_line").GetInt32() > 0);
            Assert.Contains(groups, group =>
                group.GetProperty("file").GetString() == "src/beta.cs" &&
                group.GetProperty("symbol_name").GetString() == "Execute" &&
                group.GetProperty("symbol_kind").GetString() == "function" &&
                group.GetProperty("symbol_start_line").GetInt32() > 0);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByRequiresCount_Issue3388()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_requires_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { public void Run() { AuditMarker(); } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["AuditMarker();", "--db", dbPath, "--exact-substring", "--group-by", "file"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("search --group-by requires --count", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByIsRejectedForSearchSubmodes_Issue3388()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_submodes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { public void Run() { AuditMarker(); } }");

            var (listExitCode, _, listStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--list-recipes", "--group-by", "file"],
                _jsonOptions));
            var (recipeExitCode, _, recipeStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--group-by", "file"],
                _jsonOptions));
            var (namedExitCode, _, namedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--named-query", "audit=AuditMarker", "--db", dbPath, "--group-by", "file"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, listExitCode);
            Assert.Equal(CommandExitCodes.UsageError, recipeExitCode);
            Assert.Equal(CommandExitCodes.UsageError, namedExitCode);
            Assert.Contains("--group-by is not supported with --list-recipes", listStderr, StringComparison.Ordinal);
            Assert.Contains("search --recipe --group-by requires --count", recipeStderr, StringComparison.Ordinal);
            Assert.Contains("--group-by is not supported with --named-query", namedStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FormatLspEmitsLocationArray()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_format_lsp");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { void Run() { Authenticate(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--format", "lsp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.EndsWith("/src/app.cs", row.GetProperty("uri").GetString(), StringComparison.Ordinal);
            Assert.True(row.GetProperty("range").TryGetProperty("start", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FormatSarifEmitsResultsArray()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_format_sarif");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { void Run() { Authenticate(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--format", "sarif"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal("2.1.0", root.GetProperty("version").GetString());
            var run = root.GetProperty("runs")[0];
            var rule = Assert.Single(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
            var result = Assert.Single(run.GetProperty("results").EnumerateArray());

            Assert.Equal("search", rule.GetProperty("id").GetString());
            Assert.Equal("cdidx search", rule.GetProperty("name").GetString());
            Assert.Equal("https://github.com/Widthdom/CodeIndex", rule.GetProperty("helpUri").GetString());
            Assert.Contains("surrounding code", rule.GetProperty("help").GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Contains(rule.GetProperty("properties").GetProperty("tags").EnumerateArray(), tag => tag.GetString() == "cdidx");
            Assert.Equal("search", result.GetProperty("ruleId").GetString());
            Assert.Equal("warning", result.GetProperty("level").GetString());
            Assert.Equal("src/app.cs", result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FormatCsvEmitsDelimitedRows_Issue1941()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_format_csv");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { void Run() { Authenticate(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--format", "csv"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var lines = stdout.Trim().Split(Environment.NewLine);
            Assert.Equal("file,line,column,label,query,recipe,query_name,lang,visibility,enclosing_symbol_name,enclosing_symbol_kind,match_lines", lines[0]);
            var cells = lines[1].Split(',');
            Assert.Equal(12, cells.Length);
            Assert.Equal("src/app.cs", cells[0]);
            Assert.Equal("search match: Authenticate", cells[3]);
            Assert.Equal("Authenticate", cells[4]);
            Assert.Equal(string.Empty, cells[5]);
            Assert.Equal(string.Empty, cells[6]);
            Assert.Equal("csharp", cells[7]);
            Assert.Equal("Run", cells[9]);
            Assert.Equal("function", cells[10]);
            Assert.Equal("1", cells[11]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_EmitsVisibilityInJsonAndHumanOutput_Issue1868()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_visibility_output");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/private-auth.cs",
                "csharp",
                """
                public class AuthFixture
                {
                    private void Authenticate() { }
                }
                """);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--lang", "csharp", "--exact", "--json"],
                _jsonOptions));
            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(jsonStdout);

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal("private", document.RootElement.GetProperty("visibility").GetString());
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("src/private-auth.cs:1-4 [private]", humanStdout);
            Assert.Contains("1 results in 1 files", humanStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonArrayEmitsSingleArray_Issue1850()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_json_array");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                """
                public class AuthFixture
                {
                    public void Authenticate() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--lang", "csharp", "--exact", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.Single(document.RootElement.EnumerateArray());
            Assert.DoesNotContain("\"done\"", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonArrayNoResultsEmitsEmptyArray_Issue1850()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_json_array_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                "public class AuthFixture { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Missing", "--db", dbPath, "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.Empty(document.RootElement.EnumerateArray());
            Assert.DoesNotContain("\"done\"", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_StrictNotFoundReturnsNotFoundForZeroResults_Issue1425()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_strict_not_found");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                "public class AuthFixture { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Missing", "--db", dbPath, "--strict-not-found"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("No results found", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonFormatRejectsUnknownValue_Issue1850()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["Authenticate", "--json=pretty"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--json format must be one of ndjson or array", stderr);
    }

    [Fact]
    public void RunSearch_SourceOnlyAppliesProductionScopeToAdHocSearch_Issue3978()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_source_only_3978");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public class App { void Run() { SourceOnlyNeedle(); } }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/AppTests.cs", "csharp", "public class AppTests { void Run() { SourceOnlyNeedle(); } }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "SourceOnlyNeedle\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["SourceOnlyNeedle", "--db", dbPath, "--source-only", "--count", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var queryContext = root.GetProperty("query_context");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal("source", queryContext.GetProperty("audit_scope").GetString());
            Assert.Contains(queryContext.GetProperty("path").EnumerateArray(), path => path.GetString() == "src/**");
            Assert.Contains(queryContext.GetProperty("exclude_path").EnumerateArray(), path => path.GetString() == "README.md");
            Assert.Contains(queryContext.GetProperty("exclude_path").EnumerateArray(), path => path.GetString() == ".github/**");
            Assert.True(queryContext.GetProperty("exclude_tests").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ListRecipesJsonIncludesBuiltInAuditMetadata_Issue3144()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var recipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "risky-code");
        var jsonRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "json-parse-apis");
        var dotnetRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "dotnet-risk-patterns");
        var authTokenRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "auth-token-audit");
        var dogfoodRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "dogfood-risk-patterns");
        var sqlitePolicyRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "sqlite-query-policy-surfaces");
        var xmlRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "xml-parser-security");
        var traversalRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "filesystem-traversal");
        var boundedReadRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "bounded-read-evidence");
        var broadTokenRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "broad-token-audit");
        var phraseRiskRecipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "phrase-risk-patterns");
        var query = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "raw-diagnostic-echo");
        var fileReadAllTextQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "file-read-all-text");
        var tokenQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "token-term");
        var authBearerQuery = authTokenRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "bearer-token");
        var dogfoodRegexQuery = dogfoodRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-api");
        var dogfoodSqlQuery = dogfoodRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "raw-sql-command-text");
        var sqlitePolicyCommandTextQuery = sqlitePolicyRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "sqlite-policy-command-text");
        var sqlitePolicyPragmaQuery = sqlitePolicyRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "sqlite-policy-pragma");
        var emptyCatchQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "empty-catch-review");
        var regexQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "regex-construction");
        var staticRegexIsMatchQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-is-match");
        var staticRegexMatchQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-match");
        var staticRegexMatchesQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-matches");
        var staticRegexReplaceQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-replace");
        var staticRegexSplitQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "static-regex-split");
        var boundedRegexAliasQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "bounded-regex-alias");
        var broadCatchQuery = recipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "broad-exception-catch");
        var enumerateWithoutOptionsQuery = traversalRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "enumerate-without-options");
        var phraseResultQuery = phraseRiskRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "task-result-property-review");
        var phraseSkipQuery = phraseRiskRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "active-test-skip-assignment");
        var phraseTodoQuery = phraseRiskRecipe
            .GetProperty("queries")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "todo-production-comment");

        Assert.True(root.GetProperty("count").GetInt32() >= 7);
        Assert.Contains(recipe.GetProperty("recommended_labels").EnumerateArray(), label => label.GetString() == "audit");
        Assert.Equal("source", recipe.GetProperty("default_scope").GetString());
        Assert.Contains(recipe.GetProperty("default_path_patterns").EnumerateArray(), path => path.GetString() == "src/**");
        Assert.Contains(recipe.GetProperty("default_exclude_paths").EnumerateArray(), path => path.GetString() == "src/CodeIndex/Cli/SearchAuditRecipes.cs");
        Assert.Contains(recipe.GetProperty("supported_formats").EnumerateArray(), format => format.GetString() == "issue-drafts");
        Assert.True(recipe.GetProperty("filter_support").GetProperty("exclude_tests").GetBoolean());
        Assert.True(recipe.GetProperty("filter_support").GetProperty("guard_filters").GetBoolean());
        Assert.Equal("per_query", recipe.GetProperty("limit_semantics").GetProperty("scope").GetString());
        Assert.Equal(20, recipe.GetProperty("limit_semantics").GetProperty("default").GetInt32());
        Assert.Equal("ex.Message", query.GetProperty("query").GetString());
        Assert.True(query.GetProperty("exact_substring").GetBoolean());
        Assert.Contains("redaction", query.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("False positives", query.GetProperty("false_positive_guidance").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(query.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("DiagnosticRedactor", StringComparison.Ordinal));
        Assert.Contains(fileReadAllTextQuery.GetProperty("exclude_origins").EnumerateArray(), origin => origin.GetString() == "help_text");
        Assert.Equal("catch", emptyCatchQuery.GetProperty("query").GetString());
        Assert.False(emptyCatchQuery.GetProperty("exact_substring").GetBoolean());
        Assert.Contains(emptyCatchQuery.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "code");
        Assert.Contains(emptyCatchQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("broad or empty catch", StringComparison.Ordinal));
        Assert.Contains(emptyCatchQuery.GetProperty("guard_filters").EnumerateArray(), filter =>
            filter.GetProperty("option").GetString() == "--require-before" &&
            filter.GetProperty("query").GetString() == "}");
        Assert.Contains(emptyCatchQuery.GetProperty("guard_filters").EnumerateArray(), filter =>
            filter.GetProperty("option").GetString() == "--require-after" &&
            filter.GetProperty("query").GetString() == "{");
        Assert.Equal(0, emptyCatchQuery.GetProperty("exclude_origins").GetArrayLength());
        Assert.Equal(0, emptyCatchQuery.GetProperty("result_kinds").GetArrayLength());
        Assert.Equal("new Regex(", regexQuery.GetProperty("query").GetString());
        Assert.Contains(regexQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("bounded-regex-alias", StringComparison.Ordinal));
        AssertRegexBoundedGuardFilters(regexQuery);
        Assert.Equal(" Regex.IsMatch(", staticRegexIsMatchQuery.GetProperty("query").GetString());
        Assert.Contains(staticRegexIsMatchQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("shared timeout policy", StringComparison.Ordinal));
        AssertRegexBoundedGuardFilters(staticRegexIsMatchQuery);
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-is-match-negated");
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-is-match-parenthesized");
        Assert.Equal(" Regex.Match(", staticRegexMatchQuery.GetProperty("query").GetString());
        Assert.Contains(staticRegexMatchQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("shared timeout policy", StringComparison.Ordinal));
        Assert.Equal(" Regex.Matches(", staticRegexMatchesQuery.GetProperty("query").GetString());
        Assert.Contains(staticRegexMatchesQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("shared timeout policy", StringComparison.Ordinal));
        Assert.Equal(" Regex.Replace(", staticRegexReplaceQuery.GetProperty("query").GetString());
        Assert.Contains(staticRegexReplaceQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("shared timeout policy", StringComparison.Ordinal));
        Assert.Equal(" Regex.Split(", staticRegexSplitQuery.GetProperty("query").GetString());
        Assert.Contains(staticRegexSplitQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("shared timeout policy", StringComparison.Ordinal));
        Assert.Equal("info", boundedRegexAliasQuery.GetProperty("severity").GetString());
        Assert.Contains(boundedRegexAliasQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("aliases CodeIndex.Indexer.BoundedRegex", StringComparison.Ordinal));
        var broadCatchTaxonomy = broadCatchQuery.GetProperty("broad_catch_taxonomy");
        Assert.Contains(broadCatchTaxonomy.GetProperty("boundary_categories").EnumerateArray(), item => item.GetProperty("name").GetString() == "top_level_normalization");
        Assert.Contains(broadCatchTaxonomy.GetProperty("boundary_categories").EnumerateArray(), item => item.GetProperty("name").GetString() == "unexpected_bug");
        Assert.Contains(broadCatchTaxonomy.GetProperty("diagnostic_behaviors").EnumerateArray(), item => item.GetProperty("name").GetString() == "stable_sanitized_diagnostic");
        Assert.Contains(broadCatchTaxonomy.GetProperty("diagnostic_behaviors").EnumerateArray(), item => item.GetProperty("name").GetString() == "narrow_or_rethrow_required");
        Assert.Contains("Classify each broad catch by boundary first", broadCatchTaxonomy.GetProperty("triage_guidance").GetString(), StringComparison.Ordinal);
        Assert.Equal("auth token", tokenQuery.GetProperty("query").GetString());
        Assert.Contains("broad-token-audit", tokenQuery.GetProperty("false_positive_guidance").GetString(), StringComparison.Ordinal);
        Assert.Contains(tokenQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("auth-token material", StringComparison.Ordinal));
        Assert.Contains(authTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "github-token");
        Assert.Contains(authTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "api-token");
        Assert.Contains(authTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "token-secret");
        Assert.Contains(authBearerQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("bearer tokens", StringComparison.Ordinal));
        Assert.Contains(dogfoodRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "exception-message-classifier");
        Assert.Contains(dogfoodRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "plugin-activator");
        Assert.Equal(" Regex.", dogfoodRegexQuery.GetProperty("query").GetString());
        Assert.Contains(dogfoodRegexQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("raw System.Text.RegularExpressions.Regex static APIs", StringComparison.Ordinal));
        Assert.Contains(dogfoodRegexQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("BoundedRegex aliases and instance names ending in Regex are filtered out", StringComparison.Ordinal));
        AssertRegexBoundedGuardFilters(dogfoodRegexQuery);
        Assert.Contains(dogfoodRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-api-negated");
        Assert.Contains(dogfoodRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-api-parenthesized");
        Assert.Contains(dogfoodSqlQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("identifier", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sqlitePolicyCommandTextQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("SqliteCommandPolicy", StringComparison.Ordinal));
        Assert.Contains(sqlitePolicyPragmaQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("cannot bind every pragma value", StringComparison.Ordinal));
        Assert.Contains(sqlitePolicyRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sqlite-policy-immutable-uri");
        Assert.Contains(sqlitePolicyRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sqlite-policy-maintenance-progress");
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "file-read-all-text");
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "file-read-all-bytes");
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "thread-sleep");
        Assert.Contains(recipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "http-client-construction");
        Assert.Contains(jsonRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "json-node-parse");
        Assert.Contains(jsonRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "json-serializer-deserialize");
        Assert.Contains(jsonRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "json-async-deserialize");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sqlite-addwithvalue");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sqlite-quoted-identifier");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sqlite-typed-parameter");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "fully-qualified-regex-construction");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-is-match");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-match");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-matches");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-replace");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "static-regex-split");
        Assert.Contains(dotnetRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "sync-over-async");
        Assert.Contains(xmlRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "dtd-processing");
        Assert.Contains(traversalRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "enumerate-files");
        Assert.Equal("Directory.Enumerate", enumerateWithoutOptionsQuery.GetProperty("query").GetString());
        Assert.Contains(enumerateWithoutOptionsQuery.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("without nearby EnumerationOptions", StringComparison.Ordinal));
        Assert.Contains(enumerateWithoutOptionsQuery.GetProperty("guard_filters").EnumerateArray(), filter =>
            filter.GetProperty("option").GetString() == "--reject-before" &&
            filter.GetProperty("query").GetString() == "EnumerationOptions");
        Assert.Contains(enumerateWithoutOptionsQuery.GetProperty("guard_filters").EnumerateArray(), filter =>
            filter.GetProperty("option").GetString() == "--reject-after" &&
            filter.GetProperty("query").GetString() == "EnumerationOptions");
        Assert.Contains(boundedReadRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "bounded-memory-accumulator");
        Assert.Equal("all", broadTokenRecipe.GetProperty("default_scope").GetString());
        Assert.Equal(0, broadTokenRecipe.GetProperty("default_path_patterns").GetArrayLength());
        Assert.Equal(0, broadTokenRecipe.GetProperty("default_exclude_paths").GetArrayLength());
        Assert.Contains(broadTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "token-term-broad");
        Assert.Contains(broadTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "auth-token");
        Assert.Contains(broadTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "parser-token");
        Assert.Contains(broadTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "cancellation-token");
        Assert.Contains(broadTokenRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "lsp-token");
        Assert.Equal("all", phraseRiskRecipe.GetProperty("default_scope").GetString());
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "async-void-code");
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "throw-new-exception-code");
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "unsafe-keyword-code");
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "readalltext-call-site");
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "version-project-config");
        Assert.Contains(phraseRiskRecipe.GetProperty("queries").EnumerateArray(), item => item.GetProperty("name").GetString() == "obsolete-production-code");
        Assert.Equal(".Result", phraseResultQuery.GetProperty("query").GetString());
        Assert.Contains(phraseResultQuery.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "code");
        Assert.Contains(phraseResultQuery.GetProperty("result_kinds").EnumerateArray(), kind => kind.GetString() == "identifier");
        Assert.Contains("DTO", phraseResultQuery.GetProperty("false_positive_guidance").GetString(), StringComparison.Ordinal);
        Assert.Equal("Skip =", phraseSkipQuery.GetProperty("query").GetString());
        Assert.Contains(phraseSkipQuery.GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "tests/**");
        Assert.Equal("TODO", phraseTodoQuery.GetProperty("query").GetString());
        Assert.Contains(phraseTodoQuery.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "comment");

        static void AssertRegexBoundedGuardFilters(JsonElement query)
        {
            var filters = query.GetProperty("guard_filters").EnumerateArray().ToArray();
            Assert.Contains(filters, filter =>
                filter.GetProperty("role").GetString() == "reject" &&
                filter.GetProperty("direction").GetString() == "before" &&
                filter.GetProperty("query").GetString() == "RegexOptions.NonBacktracking" &&
                filter.GetProperty("scope").GetString() == "window");
            Assert.Contains(filters, filter =>
                filter.GetProperty("role").GetString() == "reject" &&
                filter.GetProperty("direction").GetString() == "after" &&
                filter.GetProperty("query").GetString() == "TimeSpan." &&
                filter.GetProperty("scope").GetString() == "same_line");
            Assert.Contains(filters, filter =>
                filter.GetProperty("role").GetString() == "reject" &&
                filter.GetProperty("direction").GetString() == "after" &&
                filter.GetProperty("query").GetString() == "MatchTimeout(" &&
                filter.GetProperty("scope").GetString() == "same_line");
        }
    }

    [Fact]
    public void RunSearch_ListRecipesNamesJsonEmitsDeterministicSmallPayload_Issue4064()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--names", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var names = root.GetProperty("names")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray();

        Assert.Equal(root.GetProperty("count").GetInt32(), names.Length);
        Assert.Contains("risky-code", names);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), names);
        Assert.False(root.TryGetProperty("recipes", out _));

        var (capExitCode, capStdout, capStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--names", "--json", "--max-json-bytes", "1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, capExitCode);
        Assert.Equal(string.Empty, capStdout);
        Assert.Contains("recipe-name list JSON output", capStderr, StringComparison.Ordinal);

        var (textCapExitCode, textCapStdout, textCapStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--names", "--max-json-bytes", "1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, textCapExitCode);
        Assert.Equal(string.Empty, textCapStdout);
        Assert.Contains("--max-json-bytes is only supported with JSON recipe-list output", textCapStderr, StringComparison.Ordinal);

        var (compactNamesCapExitCode, compactNamesCapStdout, compactNamesCapStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--names", "--format", "compact", "--max-json-bytes", "1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, compactNamesCapExitCode);
        Assert.Equal(string.Empty, compactNamesCapStdout);
        Assert.Contains("recipe-name list JSON output", compactNamesCapStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_ListRecipesSummaryOnlyJsonOmitsChildQueries_Issue4064()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--summary-only", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var recipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "risky-code");

        Assert.True(root.GetProperty("count").GetInt32() >= 1);
        Assert.True(recipe.GetProperty("query_count").GetInt32() >= 1);
        Assert.False(recipe.TryGetProperty("queries", out _));
    }

    [Fact]
    public void RunSearch_RiskyCodeRecipeExcludesHelpTextByDefault_Issue3918()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_recipe_help_text_origin_3918");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                """
                using System.IO;

                public static class App
                {
                    public static string Load(string path) => File.ReadAllText(path);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/CodeIndex/Cli/ConsoleUi.cs",
                "csharp",
                """
                namespace CodeIndex.Cli;

                public static class ConsoleUi
                {
                    public const string Example = "cdidx search --query File.ReadAllText --exact-substring";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/file-read-all-text", "--db", dbPath, "--format", "compact", "--limit", "10"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var queryResult = Assert.Single(root.GetProperty("queries").EnumerateArray());
            var result = Assert.Single(queryResult.GetProperty("results").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains(queryResult.GetProperty("exclude_origins").EnumerateArray(), origin => origin.GetString() == "help_text");
            Assert.Equal("src/App.cs", result.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ListRecipesQueryFiltersChildQueries_Issue3975()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--query", "sqlite", "--json"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var recipes = root.GetProperty("recipes").EnumerateArray().ToList();
        var dotnetRecipe = Assert.Single(recipes, recipe => recipe.GetProperty("name").GetString() == "dotnet-risk-patterns");
        var dogfoodRecipe = Assert.Single(recipes, recipe => recipe.GetProperty("name").GetString() == "dogfood-risk-patterns");
        var sqlitePolicyRecipe = Assert.Single(recipes, recipe => recipe.GetProperty("name").GetString() == "sqlite-query-policy-surfaces");
        var dotnetQueryNames = dotnetRecipe.GetProperty("queries")
            .EnumerateArray()
            .Select(query => query.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        var dogfoodQueryNames = dogfoodRecipe.GetProperty("queries")
            .EnumerateArray()
            .Select(query => query.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        var sqlitePolicyQueryNames = sqlitePolicyRecipe.GetProperty("queries")
            .EnumerateArray()
            .Select(query => query.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(root.GetProperty("count").GetInt32(), recipes.Count);
        Assert.Contains("sqlite-addwithvalue", dotnetQueryNames);
        Assert.All(dotnetQueryNames, name => Assert.Contains("sqlite", name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["pragma-command"], dogfoodQueryNames);
        Assert.Contains("sqlite-policy-command-text", sqlitePolicyQueryNames);
        Assert.Contains("sqlite-policy-pragma", sqlitePolicyQueryNames);
        Assert.All(sqlitePolicyQueryNames, name => Assert.Contains("sqlite", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunSearch_ListRecipesQueryCombinesRecipeAndChildFields_Issue3975()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--query", "dotnet sqlite", "--json"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var recipe = Assert.Single(root.GetProperty("recipes").EnumerateArray());
        var queryNames = recipe.GetProperty("queries")
            .EnumerateArray()
            .Select(query => query.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal("dotnet-risk-patterns", recipe.GetProperty("name").GetString());
        Assert.Contains("sqlite-addwithvalue", queryNames);
        Assert.All(queryNames, name => Assert.Contains("sqlite", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunSearch_BuiltInRecipeSnapshotCoversNamesScopesAndQueries_Issue3692()
    {
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, null);

        var recipes = SearchAuditRecipes.All;
        var expectedSourceExcludes = new[]
        {
            "src/CodeIndex/Cli/SearchAuditRecipes.cs",
            "tests/**",
            "docs/**",
            "CHANGELOG.md",
            "changelog.d/**",
            "README.md",
            "USER_GUIDE.md",
            "DEVELOPER_GUIDE.md",
            "TESTING_GUIDE.md",
            "AGENT_GUIDE.md",
            ".agent_harness/**",
            ".claude/**",
            ".codex/**",
            ".github/**"
        };

        Assert.Equal(
            ["risky-code", "auth-token-audit", "dogfood-risk-patterns", "sqlite-query-policy-surfaces", "json-parse-apis", "dotnet-risk-patterns", "xml-parser-security", "filesystem-traversal", "bounded-read-evidence", "phrase-risk-patterns", "broad-token-audit"],
            recipes.Select(recipe => recipe.Name).ToArray());

        AssertRecipe(
            "risky-code",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            [
                "unbounded-json-parse",
                "full-materialization",
                "file-read-all-text",
                "file-read-all-bytes",
                "max-value-probe",
                "raw-diagnostic-echo",
                "cancellation-gap",
                "empty-catch-review",
                "broad-exception-catch",
                "process-start-info",
                "process-start-direct",
                "recursive-delete",
                "infinite-timeout",
                "thread-sleep",
                "path-case-heuristic",
                "regex-construction",
                "bounded-regex-alias",
                "fully-qualified-regex-construction",
                "static-regex-is-match",
                "static-regex-is-match-negated",
                "static-regex-is-match-parenthesized",
                "static-regex-match",
                "static-regex-match-negated",
                "static-regex-match-parenthesized",
                "static-regex-matches",
                "static-regex-matches-negated",
                "static-regex-matches-parenthesized",
                "static-regex-replace",
                "static-regex-replace-negated",
                "static-regex-replace-parenthesized",
                "static-regex-split",
                "static-regex-split-negated",
                "static-regex-split-parenthesized",
                "regex-timeout-handling",
                "environment-secret-source",
                "authorization-handling",
                "http-client-construction",
                "bearer-token-handling",
                "credential-term",
                "secret-term",
                "token-term"
            ]);
        AssertRecipe(
            "auth-token-audit",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["bearer-token", "authorization-header", "github-token", "api-token", "access-token", "token-secret"]);
        AssertRecipe(
            "dogfood-risk-patterns",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            [
                "exception-message-classifier",
                "static-regex-api",
                "static-regex-api-negated",
                "static-regex-api-parenthesized",
                "relaxed-json-encoder",
                "temp-file-name",
                "overwrite-file-move",
                "suppressed-cleanup-diagnostics",
                "wall-clock-deadline",
                "local-wall-clock-deadline",
                "max-value-sentinel",
                "recipe-output-contract",
                "raw-sql-command-text",
                "pragma-command",
                "environment-variable-parser",
                "plugin-activator",
                "assembly-load-context"
            ]);
        AssertRecipe(
            "sqlite-query-policy-surfaces",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            [
                "sqlite-policy-command-text",
                "sqlite-policy-create-command",
                "sqlite-policy-execute-reader",
                "sqlite-policy-execute-non-query",
                "sqlite-policy-execute-scalar",
                "sqlite-policy-add-with-value",
                "sqlite-policy-pragma",
                "sqlite-policy-create-table",
                "sqlite-policy-alter-table",
                "sqlite-policy-create-index",
                "sqlite-policy-drop-table",
                "sqlite-policy-delete-from",
                "sqlite-policy-begin-transaction",
                "sqlite-policy-codeindex-meta",
                "sqlite-policy-user-version",
                "sqlite-policy-check-constraint",
                "sqlite-policy-immutable-uri",
                "sqlite-policy-read-only",
                "sqlite-policy-migration",
                "sqlite-policy-maintenance-progress"
            ]);
        AssertRecipe(
            "json-parse-apis",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["json-document-parse", "json-node-parse", "json-serializer-deserialize", "json-async-deserialize"]);
        AssertRecipe(
            "dotnet-risk-patterns",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["sqlite-addwithvalue", "sqlite-quoted-identifier", "sqlite-typed-parameter", "regex-construction", "bounded-regex-alias", "fully-qualified-regex-construction", "static-regex-is-match", "static-regex-is-match-negated", "static-regex-is-match-parenthesized", "static-regex-match", "static-regex-match-negated", "static-regex-match-parenthesized", "static-regex-matches", "static-regex-matches-negated", "static-regex-matches-parenthesized", "static-regex-replace", "static-regex-replace-negated", "static-regex-replace-parenthesized", "static-regex-split", "static-regex-split-negated", "static-regex-split-parenthesized", "cancellation-token-none", "sync-wait-call", "sync-over-async"]);
        AssertRecipe(
            "xml-parser-security",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["xml-reader-settings", "dtd-processing", "xml-resolver"]);
        AssertRecipe(
            "filesystem-traversal",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["enumerate-files", "enumerate-directories", "enumerate-file-system-entries", "enumerate-without-options", "enumeration-options"]);
        AssertRecipe(
            "bounded-read-evidence",
            SearchAuditRecipes.DefaultAuditScope,
            ["src/**"],
            expectedSourceExcludes,
            ["bounded-file-open-helper", "bounded-memory-accumulator", "bounded-full-byte-read-helper"]);
        AssertRecipe(
            "phrase-risk-patterns",
            SearchAuditRecipes.AllAuditScope,
            [],
            [],
            [
                "async-void-code",
                "throw-new-exception-code",
                "task-result-property-review",
                "unsafe-keyword-code",
                "active-test-skip-assignment",
                "readalltext-call-site",
                "version-project-config",
                "todo-production-comment",
                "obsolete-production-code"
            ]);
        AssertRecipe(
            "broad-token-audit",
            SearchAuditRecipes.AllAuditScope,
            [],
            [],
            ["token-term-broad", "auth-token", "parser-token", "cancellation-token", "lsp-token"]);

        void AssertRecipe(
            string name,
            string expectedScope,
            string[] expectedPathPatterns,
            string[] expectedExcludePaths,
            string[] expectedQueries)
        {
            var recipe = recipes.Single(item => item.Name == name);
            Assert.Equal(expectedScope, recipe.DefaultScope);
            Assert.Equal(expectedPathPatterns, recipe.DefaultPathPatterns);
            Assert.Equal(expectedExcludePaths, recipe.DefaultExcludePaths);
            Assert.Equal(expectedQueries, recipe.Queries.Select(query => query.Name).ToArray());
            Assert.All(recipe.Queries, query =>
            {
                Assert.NotEmpty(query.RecommendedLabels);
                Assert.All(query.RecommendedLabels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
                Assert.False(string.IsNullOrWhiteSpace(query.FalsePositiveGuidance));
            });
        }
    }

    [Fact]
    public void RunSearch_DotnetRiskSyncQueriesSeparateBlockingShapes_Issue4125()
    {
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, null);

        var recipe = Assert.Single(SearchAuditRecipes.All, item => item.Name == "dotnet-risk-patterns");
        var syncWait = Assert.Single(recipe.Queries, query => query.Name == "sync-wait-call");
        var syncOverAsync = Assert.Single(recipe.Queries, query => query.Name == "sync-over-async");

        Assert.Equal(".Wait(", syncWait.Query);
        Assert.Contains("Monitor.Wait", syncWait.FalsePositiveGuidance, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", syncWait.RiskEvidence[1], StringComparison.Ordinal);

        Assert.Equal("GetAwaiter().GetResult", syncOverAsync.Query);
        Assert.Contains("Task/ValueTask", syncOverAsync.RiskEvidence[0], StringComparison.Ordinal);
        Assert.Contains("Result-named properties", syncOverAsync.FalsePositiveGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", syncOverAsync.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_UnknownRecipeErrorListsBuiltInNames_Issue3692()
    {
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, null);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "missing-audit-recipe", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown search recipe 'missing-audit-recipe'", stderr);
        Assert.Contains("Available recipes:", stderr);
        foreach (var recipeName in new[] { "risky-code", "json-parse-apis", "dotnet-risk-patterns", "xml-parser-security", "filesystem-traversal", "broad-token-audit" })
            Assert.Contains(recipeName, stderr);
    }

    [Fact]
    public void RunSearch_UnknownRecipeQuerySuggestsAcrossRecipeGroups_Issue3975()
    {
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, null);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/raw-sql", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown recipe query 'raw-sql' for recipe 'risky-code'", stderr);
        Assert.Contains("Suggestions across all recipes:", stderr);
        Assert.Contains("dogfood-risk-patterns/raw-sql-command-text", stderr);
        Assert.Contains("sqlite-query-policy-surfaces/sqlite-policy-add-with-value", stderr);
    }

    [Fact]
    public void RunSearch_GuardLimitErrorIncludesCandidateStats_Issue3940()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_stats_3940");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 201; i++)
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/guard-budget-{i:0000}.cs", "csharp", "public void Run() { GuardStatsNeedle(); }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["GuardStatsNeedle", "--db", dbPath, "--require-before", "MissingGuardMarker", "--guard-window", "1", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("guarded search inspected the maximum", stderr);
            Assert.Contains("Candidate files sampled before refusal:", stderr);
            Assert.Contains("src/guard-budget-", stderr);
            Assert.Contains("Candidate languages sampled before refusal:", stderr);
            Assert.Contains("csharp", stderr);
            Assert.Contains("--count", stderr);
            Assert.Contains("--count-by path", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_CatchRecipeFiltersCommentOnlyCatchMatches_Issue3709()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_catch_origin_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/spaced.cs",
                "csharp",
                """
                public sealed class App
                {
                    public void Run()
                    {
                        try
                        {
                            Work();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/no-space.cs",
                "csharp",
                """
                public sealed class NoSpaceCatch
                {
                    public void Run()
                    {
                        try
                        {
                            Work();
                        }
                        catch(Exception)
                        {
                        }
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/bare.cs",
                "csharp",
                """
                public sealed class BareCatch
                {
                    public void Run()
                    {
                        try
                        {
                            Work();
                        }
                        catch
                        {
                        }
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/comment.cs",
                "csharp",
                """
                public sealed class Notes
                {
                    // catch appears only in a comment and should not satisfy the recipe.
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/parser.cs",
                "csharp",
                """
                public sealed class Parser
                {
                    public int catchIndex = 0;
                    public string Syntax = "catch (Exception)";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/empty-catch-review", "--db", dbPath, "--lang", "csharp", "--limit", "10", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var query = Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray());
            Assert.Equal("empty-catch-review", query.GetProperty("name").GetString());
            Assert.Contains(query.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "code");
            Assert.Equal(3, query.GetProperty("count").GetInt32());
            var results = query.GetProperty("results").EnumerateArray().ToList();
            var resultPaths = results.Select(result => result.GetProperty("path").GetString()).ToList();
            Assert.Contains("src/spaced.cs", resultPaths);
            Assert.Contains("src/no-space.cs", resultPaths);
            Assert.Contains("src/bare.cs", resultPaths);
            Assert.All(results, result => Assert.Contains(result.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "code"));
            Assert.DoesNotContain(query.GetProperty("top_files").EnumerateArray(), file => file.GetProperty("path").GetString() == "src/comment.cs");
            Assert.DoesNotContain(query.GetProperty("top_files").EnumerateArray(), file => file.GetProperty("path").GetString() == "src/parser.cs");

            var (commentExitCode, commentStdout, commentStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/empty-catch-review", "--db", dbPath, "--lang", "csharp", "--limit", "10", "--origin", "comment", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, commentExitCode);
            Assert.Equal(string.Empty, commentStderr);
            using var commentDocument = ParseJsonOutput(commentStdout);
            Assert.Equal(0, commentDocument.RootElement.GetProperty("result_count").GetInt32());
            var commentQuery = Assert.Single(commentDocument.RootElement.GetProperty("queries").EnumerateArray());
            Assert.Equal(0, commentQuery.GetProperty("count").GetInt32());
            Assert.Empty(commentQuery.GetProperty("results").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RegexRecipeSeparatesBoundedAliasAndRawConstruction_Issue3919()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_regex_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/raw.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public sealed class RawRegex
                {
                    public object Build()
                    {
                        var regex = new Regex("token");
                        var diagnostic = new RegexTimeoutDiagnostic();
                        return regex;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/instance.cs",
                "csharp",
                """
                using System;
                using System.Text.RegularExpressions;

                public sealed class InstanceRegexUse
                {
                    private static readonly Regex TokenRegex = new("token", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

                    public bool ContainsToken(string input) => TokenRegex.IsMatch(input);
                    public bool HasToken(string input) => TokenRegex.Match(input).Success;
                    public int CountTokens(string input) => TokenRegex.Matches(input).Count;
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/bounded.cs",
                "csharp",
                """
                using Regex = CodeIndex.Indexer.BoundedRegex;

                public sealed class BoundedRegexUse
                {
                    public object Build() => new Regex("token");
                    public bool ContainsToken(string input) => Regex.IsMatch(input, "token");
                    public bool HasToken(string input) => Regex.Match(input, "token").Success;
                    public int CountTokens(string input) => Regex.Matches(input, "token").Count;
                    public string Clean(string input) => Regex.Replace(input, "token", "value");
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/static-raw.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public sealed class StaticRawRegexUse
                {
                    public bool ContainsToken(string input) => Regex.IsMatch(input, "token");
                    public bool MissingToken(string input) => !Regex.IsMatch(input, "token");
                    public bool HasToken(string input) => Regex.Match(input, "token").Success;
                    public bool HasTokenInGroup(string input) => (Regex.Match(input, "token")).Success;
                    public int CountTokens(string input) => Regex.Matches(input, "token").Count;
                    public string Clean(string input) => Regex.Replace(input, "token", "value");
                    public string[] SplitTokens(string input) => Regex.Split(input, "token");
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/full.cs",
                "csharp",
                """
                public sealed class FullyQualifiedRegexUse
                {
                    public object Build() => new System.Text.RegularExpressions.Regex("token");
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/diagnostic.cs",
                "csharp",
                """
                public sealed class RegexDiagnosticOnly
                {
                    public object Build() => new RegexTimeoutDiagnostic();
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code",
                    "--include-query", "regex-construction,bounded-regex-alias,fully-qualified-regex-construction,static-regex-is-match,static-regex-is-match-negated,static-regex-match,static-regex-match-parenthesized,static-regex-matches,static-regex-replace,static-regex-split",
                    "--db", dbPath,
                    "--json",
                    "--limit", "10",
                    "--lang", "csharp"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var queries = document.RootElement.GetProperty("queries").EnumerateArray().ToList();
            var constructionPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "regex-construction")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var boundedAlias = queries.Single(item => item.GetProperty("name").GetString() == "bounded-regex-alias");
            var fullyQualified = queries.Single(item => item.GetProperty("name").GetString() == "fully-qualified-regex-construction");
            var staticIsMatchPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-is-match")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticMatchPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-match")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticNegatedIsMatchPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-is-match-negated")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticParenthesizedMatchPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-match-parenthesized")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticMatchesPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-matches")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticReplacePaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-replace")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var staticSplitPaths = queries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-split")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();

            Assert.Contains("src/raw.cs", constructionPaths);
            Assert.DoesNotContain("src/bounded.cs", constructionPaths);
            Assert.DoesNotContain("src/diagnostic.cs", constructionPaths);
            Assert.Equal("src/bounded.cs", Assert.Single(boundedAlias.GetProperty("results").EnumerateArray()).GetProperty("path").GetString());
            Assert.Contains(boundedAlias.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("aliases CodeIndex.Indexer.BoundedRegex", StringComparison.Ordinal));
            Assert.Equal("src/full.cs", Assert.Single(fullyQualified.GetProperty("results").EnumerateArray()).GetProperty("path").GetString());
            Assert.Contains("src/static-raw.cs", staticIsMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", staticIsMatchPaths);
            Assert.DoesNotContain("src/instance.cs", staticIsMatchPaths);
            Assert.Contains("src/static-raw.cs", staticNegatedIsMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", staticNegatedIsMatchPaths);
            Assert.DoesNotContain("src/instance.cs", staticNegatedIsMatchPaths);
            Assert.Contains("src/static-raw.cs", staticMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", staticMatchPaths);
            Assert.DoesNotContain("src/instance.cs", staticMatchPaths);
            Assert.Contains("src/static-raw.cs", staticParenthesizedMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", staticParenthesizedMatchPaths);
            Assert.DoesNotContain("src/instance.cs", staticParenthesizedMatchPaths);
            Assert.Contains("src/static-raw.cs", staticMatchesPaths);
            Assert.DoesNotContain("src/bounded.cs", staticMatchesPaths);
            Assert.DoesNotContain("src/instance.cs", staticMatchesPaths);
            Assert.Contains("src/static-raw.cs", staticReplacePaths);
            Assert.DoesNotContain("src/bounded.cs", staticReplacePaths);
            Assert.DoesNotContain("src/instance.cs", staticReplacePaths);
            Assert.Contains("src/static-raw.cs", staticSplitPaths);
            Assert.DoesNotContain("src/bounded.cs", staticSplitPaths);
            Assert.DoesNotContain("src/instance.cs", staticSplitPaths);

            var (dotnetExitCode, dotnetStdout, dotnetStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "dotnet-risk-patterns",
                    "--include-query", "regex-construction,bounded-regex-alias,fully-qualified-regex-construction,static-regex-is-match,static-regex-is-match-negated,static-regex-match,static-regex-match-parenthesized,static-regex-matches,static-regex-replace,static-regex-split",
                    "--db", dbPath,
                    "--json",
                    "--limit", "10",
                    "--lang", "csharp"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, dotnetExitCode);
            Assert.Equal(string.Empty, dotnetStderr);
            using var dotnetDocument = ParseJsonOutput(dotnetStdout);
            var dotnetQueries = dotnetDocument.RootElement.GetProperty("queries").EnumerateArray().ToList();
            var dotnetConstructionPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "regex-construction")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetBoundedAlias = dotnetQueries.Single(item => item.GetProperty("name").GetString() == "bounded-regex-alias");
            var dotnetFullyQualified = dotnetQueries.Single(item => item.GetProperty("name").GetString() == "fully-qualified-regex-construction");
            var dotnetStaticIsMatchPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-is-match")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticMatchPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-match")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticNegatedIsMatchPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-is-match-negated")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticParenthesizedMatchPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-match-parenthesized")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticMatchesPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-matches")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticReplacePaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-replace")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();
            var dotnetStaticSplitPaths = dotnetQueries
                .Single(item => item.GetProperty("name").GetString() == "static-regex-split")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();

            Assert.Contains("src/raw.cs", dotnetConstructionPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetConstructionPaths);
            Assert.Equal("src/bounded.cs", Assert.Single(dotnetBoundedAlias.GetProperty("results").EnumerateArray()).GetProperty("path").GetString());
            Assert.Equal("src/full.cs", Assert.Single(dotnetFullyQualified.GetProperty("results").EnumerateArray()).GetProperty("path").GetString());
            Assert.Contains("src/static-raw.cs", dotnetStaticIsMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticIsMatchPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticIsMatchPaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticNegatedIsMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticNegatedIsMatchPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticNegatedIsMatchPaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticMatchPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticMatchPaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticParenthesizedMatchPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticParenthesizedMatchPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticParenthesizedMatchPaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticMatchesPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticMatchesPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticMatchesPaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticReplacePaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticReplacePaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticReplacePaths);
            Assert.Contains("src/static-raw.cs", dotnetStaticSplitPaths);
            Assert.DoesNotContain("src/bounded.cs", dotnetStaticSplitPaths);
            Assert.DoesNotContain("src/instance.cs", dotnetStaticSplitPaths);

            var (dogfoodExitCode, dogfoodStdout, dogfoodStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "dogfood-risk-patterns/static-regex-api",
                    "--db", dbPath,
                    "--json",
                    "--limit", "20",
                    "--lang", "csharp"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, dogfoodExitCode);
            Assert.Equal(string.Empty, dogfoodStderr);
            using var dogfoodDocument = ParseJsonOutput(dogfoodStdout);
            var dogfoodQuery = Assert.Single(dogfoodDocument.RootElement.GetProperty("queries").EnumerateArray());
            var dogfoodPaths = dogfoodQuery
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();

            Assert.Contains("src/static-raw.cs", dogfoodPaths);
            Assert.DoesNotContain("src/bounded.cs", dogfoodPaths);
            Assert.DoesNotContain("src/instance.cs", dogfoodPaths);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeDefaultsApplyToScope_Issue3807()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_defaults_3807");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var recipePath = Path.Combine(projectRoot, "external-recipes.json");
            File.WriteAllText(
                recipePath,
                """
                {
                  "recipes": [
                    {
                      "name": "local-defaults",
                      "description": "Exercise external defaults.",
                      "default_scope": "source",
                      "default_path_patterns": ["docs/**"],
                      "default_exclude_paths": ["docs/private/**"],
                      "queries": [
                        {
                          "name": "config-needle",
                          "query": "ConfigNeedle",
                          "description": "Find the configured marker.",
                          "recommended_labels": ["audit"],
                          "false_positive_guidance": "Review surrounding context."
                        }
                      ]
                    }
                  ]
                }
                """);

            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { string Value = \"ConfigNeedle\"; }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/public.md", "markdown", "ConfigNeedle in public docs.\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/private/secret.md", "markdown", "ConfigNeedle in private docs.\n");

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var (listExitCode, listStdout, listStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--list-recipes", "--json"],
                _jsonOptions));
            var (runExitCode, runStdout, runStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "local-defaults", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, listExitCode);
            Assert.Equal(string.Empty, listStderr);
            using var listDocument = ParseJsonOutput(listStdout);
            var recipe = listDocument.RootElement
                .GetProperty("recipes")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "local-defaults");
            Assert.Equal("source", recipe.GetProperty("default_scope").GetString());
            Assert.Contains(recipe.GetProperty("default_path_patterns").EnumerateArray(), path => path.GetString() == "docs/**");
            Assert.Contains(recipe.GetProperty("default_exclude_paths").EnumerateArray(), path => path.GetString() == "docs/private/**");

            Assert.Equal(CommandExitCodes.Success, runExitCode);
            Assert.Equal(string.Empty, runStderr);
            using var runDocument = ParseJsonOutput(runStdout);
            var root = runDocument.RootElement;
            var query = Assert.Single(root.GetProperty("queries").EnumerateArray());
            var result = Assert.Single(query.GetProperty("results").EnumerateArray());
            Assert.Equal("docs/public.md", result.GetProperty("path").GetString());
            Assert.Contains(root.GetProperty("scope").GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "docs/**");
            Assert.Contains(root.GetProperty("scope").GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "docs/private/**");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeDuplicateDiagnosticsHideRawSourcePath_Issue3807()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_diagnostics_3807");
        try
        {
            var recipePath = Path.Combine(projectRoot, "secret-recipe-path-3807.json");
            File.WriteAllText(
                recipePath,
                """
                [
                  {
                    "name": "risky-code",
                    "description": "Duplicate a built-in recipe.",
                    "queries": [
                      {
                        "name": "duplicate-query",
                        "query": "DuplicateNeedle",
                        "description": "Find a duplicate marker."
                      }
                    ]
                  }
                ]
                """);

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var registry = SearchAuditRecipes.Load();

            var diagnostic = Assert.Single(registry.Diagnostics);
            Assert.Equal("recipe source #1 defines duplicate recipe 'risky-code'; keeping the first definition.", diagnostic);
            Assert.DoesNotContain(recipePath, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-recipe-path", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeQueryScopeAndSeverityApply_Issue3826()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_query_scope_3826");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var recipePath = Path.Combine(projectRoot, "query-scope-recipes.json");
            File.WriteAllText(
                recipePath,
                """
                {
                  "recipes": [
                    {
                      "name": "query-scoped",
                      "description": "Exercise query-local scope metadata.",
                      "default_scope": "source",
                      "default_path_patterns": ["src/**"],
                      "queries": [
                        {
                          "name": "docs-only",
                          "query": "BoundaryNeedle",
                          "description": "Find the marker in docs only.",
                          "severity": "high",
                          "path_patterns": ["docs/**"],
                          "exclude_paths": ["docs/private/**"]
                        }
                      ]
                    }
                  ]
                }
                """);

            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { string Value = \"BoundaryNeedle\"; }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/public.md", "markdown", "BoundaryNeedle in public docs.\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/private/secret.md", "markdown", "BoundaryNeedle in private docs.\n");

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var (listExitCode, listStdout, listStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--list-recipes", "--json"],
                _jsonOptions));
            var (runExitCode, runStdout, runStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "query-scoped", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, listExitCode);
            Assert.Equal(string.Empty, listStderr);
            using var listDocument = ParseJsonOutput(listStdout);
            var listedQuery = listDocument.RootElement
                .GetProperty("recipes")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "query-scoped")
                .GetProperty("queries")
                .EnumerateArray()
                .Single();
            Assert.Equal("high", listedQuery.GetProperty("severity").GetString());
            Assert.Contains(listedQuery.GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "docs/**");
            Assert.Contains(listedQuery.GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "docs/private/**");

            Assert.Equal(CommandExitCodes.Success, runExitCode);
            Assert.Equal(string.Empty, runStderr);
            using var runDocument = ParseJsonOutput(runStdout);
            var query = Assert.Single(runDocument.RootElement.GetProperty("queries").EnumerateArray());
            var result = Assert.Single(query.GetProperty("results").EnumerateArray());
            Assert.Equal("high", query.GetProperty("severity").GetString());
            Assert.Contains(query.GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "docs/**");
            Assert.Contains(query.GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "docs/private/**");
            Assert.Equal("docs/public.md", result.GetProperty("path").GetString());

            TestProjectHelper.InsertIndexedFile(dbPath, "docs/other.md", "markdown", "BoundaryNeedle in another public doc.\n");

            var (userPathExitCode, userPathStdout, userPathStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "query-scoped", "--db", dbPath, "--json", "--limit", "10", "--path", "docs/public.md"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, userPathExitCode);
            Assert.Equal(string.Empty, userPathStderr);
            using var userPathDocument = ParseJsonOutput(userPathStdout);
            var userPathQuery = Assert.Single(userPathDocument.RootElement.GetProperty("queries").EnumerateArray());
            var userPathResult = Assert.Single(userPathQuery.GetProperty("results").EnumerateArray());
            Assert.Equal("docs/public.md", userPathResult.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeSourceReadIsBounded_Issues3826_3674()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_bounded_read_3826");
        try
        {
            var recipePath = Path.Combine(projectRoot, "oversized-recipes.json");
            File.WriteAllText(recipePath, new string(' ', SearchAuditRecipes.MaxRecipeSourceBytes + 1));

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var registry = SearchAuditRecipes.Load();

            var diagnostic = Assert.Single(registry.Diagnostics);
            Assert.Equal($"recipe source #1 is too large (max {SearchAuditRecipes.MaxRecipeSourceBytes} bytes).", diagnostic);
            Assert.DoesNotContain(recipePath, diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeCountValidationReportsBoundedDiagnostic_Issue3751()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_count_3751");
        try
        {
            var recipePath = Path.Combine(projectRoot, "many-recipes.json");
            var recipes = Enumerable.Range(0, 33)
                .Select(index => $$"""
                    {
                      "name": "local-audit-{{index}}",
                      "description": "Exercise count validation.",
                      "queries": [
                        {
                          "name": "marker-{{index}}",
                          "query": "Marker{{index}}",
                          "description": "Find the configured marker."
                        }
                      ]
                    }
                    """);
            File.WriteAllText(recipePath, "[" + string.Join(",", recipes) + "]");

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var registry = SearchAuditRecipes.Load();

            Assert.Contains(
                "recipe source #1 has more than 32 recipes; extra entries are ignored.",
                registry.Diagnostics);
            Assert.DoesNotContain(registry.Recipes, recipe => recipe.Name == "local-audit-32");
            Assert.Contains(registry.Recipes, recipe => recipe.Name == "local-audit-31");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExternalRecipeQueryAndLabelValidationReportsBoundedDiagnostics_Issue3751()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_query_label_3751");
        try
        {
            var recipePath = Path.Combine(projectRoot, "invalid-recipes.json");
            var longQuery = new string('q', QueryLimits.MaxQueryLength + 1);
            var longLabel = new string('l', 65);
            File.WriteAllText(
                recipePath,
                $$"""
                [
                  {
                    "name": "local-audit",
                    "description": "Exercise query and label validation.",
                    "queries": [
                      {
                        "name": "too-long-query",
                        "query": "{{longQuery}}",
                        "description": "This query should be rejected."
                      },
                      {
                        "name": "bounded-labels",
                        "query": "BoundedNeedle",
                        "description": "This query should keep only valid labels.",
                        "recommended_labels": ["audit", "{{longLabel}}", ""]
                      }
                    ]
                  }
                ]
                """);

            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            var registry = SearchAuditRecipes.Load();

            Assert.Contains(
                $"recipe source #1 item #1 field 'query' exceeds {QueryLimits.MaxQueryLength} characters.",
                registry.Diagnostics);
            Assert.Contains(
                "recipe source #1 recipe 'local-audit' query 'bounded-labels' label #2 exceeds 64 characters.",
                registry.Diagnostics);
            Assert.Contains(
                "recipe source #1 recipe 'local-audit' query 'bounded-labels' label #3 must be a non-empty string.",
                registry.Diagnostics);
            var recipe = Assert.Single(registry.Recipes.Where(recipe => recipe.Name == "local-audit"));
            var query = Assert.Single(recipe.Queries);
            Assert.Equal("bounded-labels", query.Name);
            Assert.Equal(["audit"], query.RecommendedLabels);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("csv")]
    public void RunSearch_ListRecipesRejectsUnsupportedFormattedOutputs_Issue3144(string format)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--format", format],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --list-recipes", stderr);
    }

    [Fact]
    public void RunSearch_ListRecipesCompactFormatEmitsSummary_Issue3957()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--format", "compact"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var recipe = root
            .GetProperty("recipes")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "risky-code");

        Assert.True(root.GetProperty("count").GetInt32() >= 1);
        Assert.True(recipe.GetProperty("query_count").GetInt32() >= 1);
        Assert.Contains(recipe.GetProperty("recommended_labels").EnumerateArray(), label => label.GetString() == "audit");
        Assert.False(recipe.TryGetProperty("queries", out _));
    }

    [Fact]
    public void RunSearch_ListRecipesRejectsJsonArray_Issue3144()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--list-recipes", "--json=array"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json=array is not supported with --list-recipes", stderr);
    }

    [Theory]
    [InlineData("NaN:1:0")]
    [InlineData("Infinity:1:0")]
    [InlineData("1:-1:0")]
    [InlineData("1:1:-1")]
    public void RunSearch_RecipeInvalidCursorDomain_ReturnsUsageError_Issue3837(string cursor)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/raw-diagnostic-echo", "--cursor", cursor],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--cursor", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_RecipeJsonRunsBuiltInQueries_Issue3144()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.Text.Json;

                public sealed class App
                {
                    public void Run(Exception ex, CancellationToken token)
                    {
                        JsonDocument.Parse("{}");
                        reader.ReadToEnd();
                        Console.WriteLine(ex.Message);
                        _ = CancellationToken.None;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--lang", "csharp", "--limit", "2", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var unboundedJsonParse = root
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "unbounded-json-parse");

            Assert.Equal("risky-code", root.GetProperty("recipe").GetProperty("name").GetString());
            Assert.Equal(41, root.GetProperty("query_count").GetInt32());
            Assert.Equal("source", root.GetProperty("scope").GetProperty("name").GetString());
            Assert.Contains(root.GetProperty("scope").GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "src/**");
            Assert.Contains(root.GetProperty("scope").GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "src/CodeIndex/Cli/SearchAuditRecipes.cs");
            Assert.True(root.GetProperty("scope").GetProperty("exclude_tests").GetBoolean());
            Assert.True(root.GetProperty("result_count").GetInt32() >= 4);
            Assert.Equal(1, unboundedJsonParse.GetProperty("count").GetInt32());
            Assert.Equal(1, unboundedJsonParse.GetProperty("emitted_count").GetInt32());
            Assert.Equal(1, unboundedJsonParse.GetProperty("minimum_matched_count").GetInt32());
            Assert.Equal(0, unboundedJsonParse.GetProperty("omitted_count").GetInt32());
            Assert.Equal("JsonDocument.Parse", unboundedJsonParse.GetProperty("query").GetString());
            Assert.Equal("src/app.cs", unboundedJsonParse.GetProperty("results")[0].GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeCountFormatReportsMatchedAndOmittedCounts_Issue3941()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_count_format");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        logger.LogWarning(ex.Message);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/b.cs",
                "csharp",
                """
                public sealed class B
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/comments.cs",
                "csharp",
                """
                public sealed class Comments
                {
                    // ex.Message should not be counted when the caller asks for code origins.
                    private const string Diagnostic = "ex.Message";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "count", "--origin", "code"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var query = Assert.Single(root.GetProperty("queries").EnumerateArray());

            Assert.Equal("risky-code", root.GetProperty("recipe").GetProperty("name").GetString());
            Assert.Equal(1, root.GetProperty("query_count").GetInt32());
            Assert.Equal(2, root.GetProperty("result_count").GetInt32());
            Assert.Equal(2, root.GetProperty("file_count").GetInt32());
            Assert.Equal("raw-diagnostic-echo", query.GetProperty("name").GetString());
            Assert.Equal(2, query.GetProperty("count").GetInt32());
            Assert.Equal(2, query.GetProperty("matched_count").GetInt32());
            Assert.Equal(0, query.GetProperty("emitted_count").GetInt32());
            Assert.Equal(2, query.GetProperty("omitted_count").GetInt32());
            Assert.Equal(2, query.GetProperty("file_count").GetInt32());
            Assert.False(query.GetProperty("truncated").GetBoolean());
            Assert.Equal(2, query.GetProperty("top_files").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeCountSummaryOnlyJsonOmitsRecipeMetadataAndReportsFreshness_Issues4064_4118()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_count_summary_4064");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/b.cs",
                "csharp",
                """
                public sealed class B
                {
                    public void Run(Exception ex)
                    {
                        logger.LogWarning(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code",
                    "--include-query", "raw-diagnostic-echo",
                    "--include-query", "unbounded-json-parse",
                    "--db", dbPath,
                    "--format", "count",
                    "--summary-only",
                    "--origin", "code"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var queries = root.GetProperty("queries").EnumerateArray().ToArray();
            var query = Assert.Single(queries, item => item.GetProperty("name").GetString() == "raw-diagnostic-echo");
            var staleQuery = Assert.Single(queries, item => item.GetProperty("name").GetString() == "unbounded-json-parse");
            var freshness = root.GetProperty("query_freshness");

            Assert.Equal("risky-code", root.GetProperty("recipe").GetString());
            Assert.Equal("source", root.GetProperty("scope").GetString());
            Assert.Equal(2, root.GetProperty("query_count").GetInt32());
            Assert.Equal(2, root.GetProperty("result_count").GetInt32());
            Assert.Equal(2, root.GetProperty("file_count").GetInt32());
            Assert.Equal("raw-diagnostic-echo", query.GetProperty("name").GetString());
            Assert.Equal(2, query.GetProperty("count").GetInt32());
            Assert.Equal(2, query.GetProperty("file_count").GetInt32());
            Assert.Equal(0, staleQuery.GetProperty("count").GetInt32());
            Assert.Equal(0, staleQuery.GetProperty("file_count").GetInt32());
            Assert.Equal(1, freshness.GetProperty("positive_evidence_query_count").GetInt32());
            Assert.Equal(1, freshness.GetProperty("zero_result_query_count").GetInt32());
            Assert.Contains(freshness.GetProperty("stale_query_names").EnumerateArray(), name => name.GetString() == "unbounded-json-parse");
            Assert.False(query.TryGetProperty("query", out _));
            Assert.False(query.TryGetProperty("top_files", out _));

            var (capExitCode, capStdout, capStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "count", "--summary-only", "--origin", "code", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, capExitCode);
            Assert.Equal(string.Empty, capStdout);
            Assert.Contains("recipe count summary JSON output", capStderr, StringComparison.Ordinal);

            var (aggregationCapExitCode, aggregationCapStdout, aggregationCapStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "count", "--summary-only", "--group-by", "file", "--origin", "code", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, aggregationCapExitCode);
            Assert.Equal(string.Empty, aggregationCapStdout);
            Assert.Contains("recipe aggregation JSON output", aggregationCapStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeResultsOnlyProjectionIncludesQueryName_Issue3957()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_projection");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public sealed class App
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--results-only", "--search-fields", "path,line,query_name,recipe", "--limit", "10"],
                _jsonOptions));
            var (ndjsonExitCode, ndjsonStdout, ndjsonStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--json=ndjson", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(CommandExitCodes.Success, ndjsonExitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(string.Empty, ndjsonStderr);
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;
            var ndjsonRow = Assert.Single(ParseJsonLines(ndjsonStdout)).RootElement;

            Assert.Equal("src/app.cs", row.GetProperty("path").GetString());
            Assert.Equal("raw-diagnostic-echo", row.GetProperty("query_name").GetString());
            Assert.Equal("risky-code", row.GetProperty("recipe").GetString());
            Assert.True(row.GetProperty("line").GetInt32() > 0);
            Assert.Equal("raw-diagnostic-echo", ndjsonRow.GetProperty("query_name").GetString());
            Assert.Equal("risky-code", ndjsonRow.GetProperty("recipe").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeAggregationGroupsByChildQuery_Issue3957()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_aggregation");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                "public sealed class A { public void Run(Exception ex) { Console.WriteLine(ex.Message); } }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/b.cs",
                "csharp",
                "public sealed class B { public void Run(Exception ex) { Console.WriteLine(ex.Message); } }");

            var (countByExitCode, countByStdout, countByStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--count-by", "file", "--json"],
                _jsonOptions));
            var (groupByExitCode, groupByStdout, groupByStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--group-by", "file", "--count", "--json"],
                _jsonOptions));
            var (limitedExitCode, limitedStdout, limitedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--count-by", "file", "--json", "--limit", "1"],
                _jsonOptions));
            var (textExitCode, textStdout, textStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--count-by", "file", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, countByExitCode);
            Assert.Equal(CommandExitCodes.Success, groupByExitCode);
            Assert.Equal(CommandExitCodes.Success, limitedExitCode);
            Assert.Equal(CommandExitCodes.Success, textExitCode);
            Assert.Equal(string.Empty, countByStderr);
            Assert.Equal(string.Empty, groupByStderr);
            Assert.Equal(string.Empty, limitedStderr);
            using var countByDocument = ParseJsonOutput(countByStdout);
            using var groupByDocument = ParseJsonOutput(groupByStdout);
            using var limitedDocument = ParseJsonOutput(limitedStdout);
            var countByRoot = countByDocument.RootElement;
            var groupByRoot = groupByDocument.RootElement;
            var query = Assert.Single(countByRoot.GetProperty("queries").EnumerateArray());
            var limitedQuery = Assert.Single(limitedDocument.RootElement.GetProperty("queries").EnumerateArray());

            Assert.Equal("count_by", countByRoot.GetProperty("mode").GetString());
            Assert.Equal("group_by", groupByRoot.GetProperty("mode").GetString());
            Assert.Equal("file", countByRoot.GetProperty("group_by").GetString());
            Assert.Equal("raw-diagnostic-echo", query.GetProperty("name").GetString());
            Assert.Equal(2, query.GetProperty("count").GetInt32());
            Assert.Equal(2, query.GetProperty("groups").GetArrayLength());
            Assert.Contains(query.GetProperty("groups").EnumerateArray(), group => group.GetProperty("file").GetString() == "src/a.cs");
            Assert.Equal(1, limitedQuery.GetProperty("returned_groups").GetInt32());
            Assert.Equal(2, limitedQuery.GetProperty("total_groups").GetInt32());
            Assert.True(limitedQuery.GetProperty("groups_truncated").GetBoolean());
            Assert.Equal(1, limitedQuery.GetProperty("groups").GetArrayLength());
            Assert.Contains("[raw-diagnostic-echo]", textStdout, StringComparison.Ordinal);
            Assert.Contains("showing 1 of 2 groups", textStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeTotalLimitCapsAcrossChildQueries_Issue3904()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_total_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System;
                using System.Threading;

                public sealed class App
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        Thread.Sleep(10);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--json", "--limit", "10", "--total-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var emittedRows = root
                .GetProperty("queries")
                .EnumerateArray()
                .Sum(query => query.GetProperty("results").GetArrayLength());
            var threadSleep = root
                .GetProperty("queries")
                .EnumerateArray()
                .Single(query => query.GetProperty("name").GetString() == "thread-sleep");

            Assert.Equal(1, root.GetProperty("result_count").GetInt32());
            Assert.Equal(1, emittedRows);
            Assert.Equal(1, root.GetProperty("summary").GetProperty("total_limit").GetInt32());
            Assert.Equal(0, threadSleep.GetProperty("result_limit").GetInt32());
            Assert.True(threadSleep.GetProperty("minimum_omitted_result_count").GetInt32() >= 1);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MaxJsonBytesBoundsCompactOutput_Issue4119()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_compact_byte_cap_4119");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/token.cs",
                "csharp",
                "public sealed class TokenStore { private string token = \"secret\"; }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["token", "--db", dbPath, "--format", "compact", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("compact search results JSON output", stderr, StringComparison.Ordinal);

            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingToken", "--db", dbPath, "--format", "compact", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, zeroExitCode);
            Assert.Equal(string.Empty, zeroStdout);
            Assert.Contains("compact search results JSON output", zeroStderr, StringComparison.Ordinal);

            var (groupedZeroExitCode, groupedZeroStdout, groupedZeroStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingToken", "--db", dbPath, "--format", "grouped", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, groupedZeroExitCode);
            Assert.Equal(string.Empty, groupedZeroStdout);
            Assert.Contains("grouped search results JSON output", groupedZeroStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_NdjsonByteCapMarksDoneFalseWhenInterrupted_Issue3904()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_ndjson_byte_cap");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/token.cs",
                "csharp",
                "public sealed class TokenStore { private string token = \"secret\"; }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["token", "--db", dbPath, "--json=ndjson", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var done = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.False(done.GetProperty("done").GetBoolean());
            Assert.True(done.GetProperty("interrupted").GetBoolean());
            Assert.Equal(0, done.GetProperty("count").GetInt32());

            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingToken", "--db", dbPath, "--json=ndjson", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, zeroExitCode);
            Assert.Equal(string.Empty, zeroStderr);
            var zeroDone = Assert.Single(ParseJsonLines(zeroStdout)).RootElement;

            Assert.False(zeroDone.GetProperty("done").GetBoolean());
            Assert.True(zeroDone.GetProperty("interrupted").GetBoolean());
            Assert.Equal(0, zeroDone.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonParseRecipeGroupsApiFamilies_Issues3710_3714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_json_parse_recipe");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/json.cs",
                "csharp",
                """
                using System.Text.Json;
                using System.Text.Json.Nodes;

                public sealed class JsonAudit
                {
                    public void Run(string text)
                    {
                        JsonNode.Parse(text);
                        JsonSerializer.Deserialize<object>(text);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "json-parse-apis", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var queries = root.GetProperty("queries").EnumerateArray().ToList();
            var jsonNode = queries.Single(item => item.GetProperty("name").GetString() == "json-node-parse");
            var serializer = queries.Single(item => item.GetProperty("name").GetString() == "json-serializer-deserialize");

            Assert.Equal("json-parse-apis", root.GetProperty("recipe").GetProperty("name").GetString());
            Assert.Equal(4, root.GetProperty("query_count").GetInt32());
            Assert.Equal(1, jsonNode.GetProperty("count").GetInt32());
            Assert.Equal("JsonNode.Parse", jsonNode.GetProperty("query").GetString());
            Assert.Equal("src/json.cs", jsonNode.GetProperty("top_files")[0].GetProperty("path").GetString());
            Assert.False(jsonNode.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, serializer.GetProperty("count").GetInt32());
            Assert.Equal("JsonSerializer.Deserialize", serializer.GetProperty("query").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FocusedAuditRecipesFindDotnetXmlAndFilesystemApis_Issues3731_3694_3693()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_focused_audit_recipes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/audit.cs",
                "csharp",
                """
                using System.Data.Common;
                using System.IO;
                using System.Xml;

                public sealed class AuditPatterns
                {
                    public void Run(DbCommand command)
                    {
                        command.Parameters.AddWithValue("@id", 1);
                        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                        foreach (var path in Directory.EnumerateFiles("src")) { }
                    }
                }
                """);

            var dotnet = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "dotnet-risk-patterns", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));
            var xml = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "xml-parser-security", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));
            var filesystem = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "filesystem-traversal", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, dotnet.Result);
            Assert.Equal(CommandExitCodes.Success, xml.Result);
            Assert.Equal(CommandExitCodes.Success, filesystem.Result);
            Assert.Equal(string.Empty, dotnet.Stderr);
            Assert.Equal(string.Empty, xml.Stderr);
            Assert.Equal(string.Empty, filesystem.Stderr);

            using var dotnetDocument = ParseJsonOutput(dotnet.Stdout);
            using var xmlDocument = ParseJsonOutput(xml.Stdout);
            using var filesystemDocument = ParseJsonOutput(filesystem.Stdout);

            AssertRecipeQueryHit(dotnetDocument.RootElement, "sqlite-addwithvalue", "AddWithValue");
            AssertRecipeQueryHit(xmlDocument.RootElement, "dtd-processing", "DtdProcessing");
            AssertRecipeQueryHit(filesystemDocument.RootElement, "enumerate-files", "Directory.EnumerateFiles");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static void AssertRecipeQueryHit(JsonElement root, string queryName, string queryText)
        {
            var query = root
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == queryName);

            Assert.Equal(queryText, query.GetProperty("query").GetString());
            Assert.Equal(1, query.GetProperty("count").GetInt32());
            Assert.Equal("src/audit.cs", query.GetProperty("results")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public void RunSearch_BoundedReadEvidenceRecipeFindsBoundedHelpers_Issue3994()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_bounded_read_evidence");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/CodeIndex/BoundedFile.cs",
                "csharp",
                """
                internal static class BoundedFile
                {
                    internal static Stream OpenReadForHash(string path)
                        => BoundedFile.OpenRead(path, FileShare.Read);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/CodeIndex/BoundedLineReader.cs",
                "csharp",
                """
                internal static class BoundedLineReader
                {
                    internal static void TryReadUtf8File(Stream stream, int maxBytes)
                    {
                        using var accumulator = new MemoryStream(Math.Min(maxBytes, 8192));
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/CodeIndex/Indexer/Scanning/FileContentLoader.cs",
                "csharp",
                """
                internal sealed class FileContentLoader
                {
                    private byte[] ReadRawBytesWithSizeLimit(string path) => [];
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "bounded-read-evidence", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            AssertRecipeQueryHit(root, "bounded-file-open-helper", "BoundedFile.OpenRead", "src/CodeIndex/BoundedFile.cs");
            AssertRecipeQueryHit(root, "bounded-memory-accumulator", "MemoryStream", "src/CodeIndex/BoundedLineReader.cs");
            AssertRecipeQueryHit(root, "bounded-full-byte-read-helper", "ReadRawBytesWithSizeLimit", "src/CodeIndex/Indexer/Scanning/FileContentLoader.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static void AssertRecipeQueryHit(JsonElement root, string queryName, string queryText, string path)
        {
            var query = root
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == queryName);

            Assert.Equal(queryText, query.GetProperty("query").GetString());
            Assert.Equal(1, query.GetProperty("count").GetInt32());
            Assert.Equal(path, query.GetProperty("results")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public void RunSearch_BroadTokenRecipeDefaultsToAllScope_Issue3670()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_broad_token_all_scope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/parser.cs", "csharp", "public sealed class Parser { string token = \"src\"; }");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/token.md", "markdown", "Document token review notes.");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/TokenTests.cs", "csharp", "public sealed class TokenTests { string token = \"test\"; }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "broad-token-audit", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var paths = root
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "token-term-broad")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();

            Assert.Equal("all", root.GetProperty("scope").GetProperty("name").GetString());
            Assert.Contains("src/parser.cs", paths);
            Assert.Contains("docs/token.md", paths);
            Assert.Contains("tests/TokenTests.cs", paths);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeSourceScopeSuppressesDefinitionsDocsChangelogAndTests_Issues3440_3448()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_scope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            foreach (var path in new[]
            {
                "src/app.cs",
                "src/CodeIndex/Cli/SearchAuditRecipes.cs",
                "docs/audit.md",
                "CHANGELOG.md",
                "tests/AppTests.cs",
            })
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    path,
                    path.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "markdown",
                    "ProcessStartInfo");
            }

            var sourceScope = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));
            var diagnosticScope = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--json", "--limit", "10", "--show-excluded"],
                _jsonOptions));
            var allScope = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--json", "--limit", "10", "--audit-scope", "all"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, sourceScope.Result);
            Assert.Equal(string.Empty, sourceScope.Stderr);
            using var sourceDocument = ParseJsonOutput(sourceScope.Stdout);
            var sourceQuery = sourceDocument.RootElement
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "process-start-info");
            var sourceResult = Assert.Single(sourceQuery.GetProperty("results").EnumerateArray());

            Assert.Equal("source", sourceDocument.RootElement.GetProperty("scope").GetProperty("name").GetString());
            Assert.Equal("src/app.cs", sourceResult.GetProperty("path").GetString());
            Assert.False(sourceDocument.RootElement.GetProperty("scope").TryGetProperty("excluded_diagnostics", out _));

            Assert.Equal(CommandExitCodes.Success, diagnosticScope.Result);
            Assert.Equal(string.Empty, diagnosticScope.Stderr);
            using var diagnosticDocument = ParseJsonOutput(diagnosticScope.Stdout);
            var diagnostics = diagnosticDocument.RootElement
                .GetProperty("scope")
                .GetProperty("excluded_diagnostics")
                .EnumerateArray()
                .ToList();
            var defaultExcludes = diagnostics.Single(item => item.GetProperty("reason").GetString() == "recipe_default_exclude_paths");
            Assert.True(defaultExcludes.GetProperty("applied").GetBoolean());
            Assert.Contains(defaultExcludes.GetProperty("patterns").EnumerateArray(), path => path.GetString() == "docs/**");

            Assert.Equal(CommandExitCodes.Success, allScope.Result);
            Assert.Equal(string.Empty, allScope.Stderr);
            using var allDocument = ParseJsonOutput(allScope.Stdout);
            var allPaths = allDocument.RootElement
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "process-start-info")
                .GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString())
                .ToList();

            Assert.Equal("all", allDocument.RootElement.GetProperty("scope").GetProperty("name").GetString());
            Assert.Contains("src/CodeIndex/Cli/SearchAuditRecipes.cs", allPaths);
            Assert.Contains("docs/audit.md", allPaths);
            Assert.Contains("CHANGELOG.md", allPaths);
            Assert.Contains("tests/AppTests.cs", allPaths);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeJsonWithRawFtsReportsEffectiveSanitizedMode_Issue3558()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_json_raw_fts_3558");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.Text.Json;

                public sealed class App
                {
                    public void Run()
                    {
                        JsonDocument.Parse("{}");
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--lang", "csharp", "--limit", "2", "--json", "--fts"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var result = document.RootElement
                .GetProperty("queries")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "unbounded-json-parse")
                .GetProperty("results")
                .EnumerateArray()
                .Single();

            Assert.False(result.GetProperty("raw_fts").GetBoolean());
            Assert.False(result.TryGetProperty("literal_highlight_warning", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeChildSelectorRunsSingleQuery_Issue3519()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_child_selector");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.Text.Json;

                public sealed class App
                {
                    public void Run(Exception ex)
                    {
                        JsonDocument.Parse("{}");
                        Console.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var query = Assert.Single(root.GetProperty("queries").EnumerateArray());
            var recipeQuery = Assert.Single(root.GetProperty("recipe").GetProperty("queries").EnumerateArray());

            Assert.Equal(1, root.GetProperty("query_count").GetInt32());
            Assert.Equal("raw-diagnostic-echo", query.GetProperty("name").GetString());
            Assert.Equal("raw-diagnostic-echo", recipeQuery.GetProperty("name").GetString());
            Assert.Equal("ex.Message", query.GetProperty("query").GetString());
            Assert.Contains(query.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("raw exception messages", StringComparison.Ordinal));
            Assert.Equal(1, query.GetProperty("count").GetInt32());
            var result = Assert.Single(query.GetProperty("results").EnumerateArray());
            Assert.Contains(result.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("CommandErrorWriter", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RegexConstructionRecipeFiltersBoundedEvidence_Issue4136()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_regex_bounded_evidence");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/unbounded.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public sealed class UnboundedRegex
                {
                    public Regex Build(string pattern)
                    {
                        return new Regex(pattern);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/timeout-method.cs",
                "csharp",
                """
                using System;
                using System.Text.RegularExpressions;

                public sealed class TimeoutMethodRegex
                {
                    public Regex Build(string pattern)
                    {
                        return new Regex(pattern, RegexOptions.CultureInvariant, ResolveFindRegexMatchTimeout());
                    }

                    private static TimeSpan ResolveFindRegexMatchTimeout() => TimeSpan.FromMilliseconds(50);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/nonbacktracking-before.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public sealed class NonBacktrackingBeforeRegex
                {
                    public Regex Build(string pattern)
                    {
                        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
                        return new Regex(pattern, options);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/nonbacktracking-after.cs",
                "csharp",
                """
                using System.Text.RegularExpressions;

                public sealed class NonBacktrackingAfterRegex
                {
                    public Regex Build(string pattern)
                    {
                        return new Regex(
                            pattern,
                            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/timespan-after.cs",
                "csharp",
                """
                using System;
                using System.Text.RegularExpressions;

                public sealed class TimeSpanAfterRegex
                {
                    public Regex Build(string pattern)
                    {
                        return new Regex(
                            pattern,
                            RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(50));
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/regex-construction", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var query = Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray());
            var result = Assert.Single(query.GetProperty("results").EnumerateArray());

            Assert.Equal("regex-construction", query.GetProperty("name").GetString());
            Assert.Equal(1, query.GetProperty("count").GetInt32());
            Assert.Equal("src/unbounded.cs", result.GetProperty("path").GetString());
            Assert.DoesNotContain(query.GetProperty("top_files").EnumerateArray(), item => item.GetProperty("path").GetString() == "src/timeout-method.cs");
            Assert.Contains(query.GetProperty("guard_filters").EnumerateArray(), filter =>
                filter.GetProperty("query").GetString() == "RegexOptions.NonBacktracking" &&
                filter.GetProperty("scope").GetString() == "window");
            Assert.Contains(query.GetProperty("guard_filters").EnumerateArray(), filter =>
                filter.GetProperty("query").GetString() == "MatchTimeout(" &&
                filter.GetProperty("scope").GetString() == "same_line");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FilesystemTraversalRecipeFiltersNearbyEnumerationOptions_Issue3920()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_enumeration_options");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/unguarded.cs",
                "csharp",
                """
                public sealed class UnguardedTraversal
                {
                    public void Run(string root)
                    {
                        foreach (var path in Directory.EnumerateFiles(root))
                        {
                            Console.WriteLine(path);
                        }
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/options.cs",
                "csharp",
                """
                public sealed class OptionsTraversal
                {
                    public void Run(string root)
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true };
                        foreach (var path in Directory.EnumerateFiles(root, "*", options))
                        {
                            Console.WriteLine(path);
                        }
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "filesystem-traversal/enumerate-without-options", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var query = Assert.Single(root.GetProperty("queries").EnumerateArray());
            var result = Assert.Single(query.GetProperty("results").EnumerateArray());

            Assert.Equal("enumerate-without-options", query.GetProperty("name").GetString());
            Assert.Equal(1, query.GetProperty("count").GetInt32());
            Assert.Equal("src/unguarded.cs", result.GetProperty("path").GetString());
            Assert.DoesNotContain(query.GetProperty("top_files").EnumerateArray(), item => item.GetProperty("path").GetString() == "src/options.cs");
            Assert.Contains(query.GetProperty("guard_filters").EnumerateArray(), filter => filter.GetProperty("option").GetString() == "--reject-before");
            Assert.Contains(query.GetProperty("guard_filters").EnumerateArray(), filter => filter.GetProperty("option").GetString() == "--reject-after");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_AuthTokenRecipeAvoidsParserCancellationAndLspTokenNoise_Issue3923()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_auth_token");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                """
                using System.Net.Http.Headers;

                public sealed class AuthTokenFlow
                {
                    public void Run(HttpRequestMessage request, string githubToken)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
                        var tokenSecret = $"token secret:{githubToken}";
                        var apiToken = githubToken;
                        Console.WriteLine(tokenSecret.Length + apiToken.Length);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/token-noise.cs",
                "csharp",
                """
                public sealed class TokenNoise
                {
                    public void Run(SyntaxToken token, CancellationToken cancellationToken, SemanticToken semanticToken)
                    {
                        Console.WriteLine(token.RawKind + semanticToken.TokenType);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "auth-token-audit", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var queries = document.RootElement.GetProperty("queries").EnumerateArray().ToList();
            var allResults = queries
                .SelectMany(query => query.GetProperty("results").EnumerateArray())
                .ToList();

            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "bearer-token");
            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "github-token");
            Assert.Contains(allResults, result => result.GetProperty("path").GetString() == "src/auth.cs");
            Assert.DoesNotContain(allResults, result => result.GetProperty("path").GetString() == "src/token-noise.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_BareTokenZeroResultSuggestsAuthTokenAudit_Issue3923()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_token_zero_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public sealed class App { public void Run() { Console.WriteLine(\"ok\"); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["token", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("No results found", stderr);
            Assert.Contains("auth-token-audit", stderr);
            Assert.Contains("broad-token-audit", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_BareTokenNextStepsSuggestAuthTokenAudit_Issue3923()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_token_next_steps");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/noise.cs",
                "csharp",
                "public sealed class Noise { public void Run(CancellationToken token) { Console.WriteLine(token.CanBeCanceled); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["token", "--db", dbPath, "--json", "--next-steps", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var result = document.RootElement;

            Assert.Contains(result.GetProperty("next_steps").EnumerateArray(), step =>
                step.GetProperty("command").GetString() == "cdidx search --recipe auth-token-audit --exclude-tests");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_DogfoodRiskRecipeCoversRecurringPatterns_Issue3967()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_dogfood_risk_recipe");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/dogfood.cs",
                "csharp",
                """
                using System.Runtime.Loader;
                using System.Text.Encodings.Web;
                using System.Text.RegularExpressions;

                public sealed class DogfoodRisks
                {
                    public void Run(Exception ex, DbCommand command, Type pluginType)
                    {
                        if (ex.Message.Contains("locked", StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine("classified");
                        Regex.IsMatch("payload", "p.*");
                        _ = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                        var stamp = DateTime.UtcNow;
                        var local = DateTime.Now;
                        var limit = int.MaxValue;
                        command.CommandText = "PRAGMA table_info(user_input)";
                        var plugin = Activator.CreateInstance(pluginType);
                        var context = AssemblyLoadContext.GetLoadContext(pluginType.Assembly);
                        Console.WriteLine($"{stamp}{local}{limit}{plugin}{context}");
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe",
                    "dogfood-risk-patterns",
                    "--include-query",
                    "exception-message-classifier,static-regex-api,relaxed-json-encoder,wall-clock-deadline,local-wall-clock-deadline,max-value-sentinel,raw-sql-command-text,pragma-command,plugin-activator,assembly-load-context",
                    "--db",
                    dbPath,
                    "--json",
                    "--limit",
                    "10"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var queries = document.RootElement.GetProperty("queries").EnumerateArray().ToList();

            Assert.All(queries, query => Assert.Equal(1, query.GetProperty("count").GetInt32()));
            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "exception-message-classifier");
            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "static-regex-api");
            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "relaxed-json-encoder");
            Assert.Contains(queries, query => query.GetProperty("name").GetString() == "plugin-activator");
            Assert.All(queries, query => Assert.Contains(query.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.StartsWith("risk:", StringComparison.Ordinal)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIncludeExcludeQueriesFilterChildren_Issue3519()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_include_exclude");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.Text.Json;

                public sealed class App
                {
                    public void Run(Exception ex)
                    {
                        JsonDocument.Parse("{}");
                        Console.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--include-query", "raw-diagnostic-echo,unbounded-json-parse", "--exclude-query", "raw-diagnostic-echo", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var query = Assert.Single(root.GetProperty("queries").EnumerateArray());

            Assert.Equal(1, root.GetProperty("query_count").GetInt32());
            Assert.Equal("unbounded-json-parse", query.GetProperty("name").GetString());
            Assert.Equal("JsonDocument.Parse", query.GetProperty("query").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeUnknownChildSelectorReturnsUsage_Issue3519()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/missing-child", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown recipe query 'missing-child'", stderr);
        Assert.Contains("raw-diagnostic-echo", stderr);
    }

    [Fact]
    public void RunSearch_RecipeCompactJsonEmitsSummaryAndCursor_Issues3392_3667()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_compact_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/b.cs",
                "csharp",
                """
                public sealed class B
                {
                    public void Run(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                """);

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "compact", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = ParseJsonOutput(firstStdout);
            var firstRoot = firstDocument.RootElement;
            var firstQuery = Assert.Single(firstRoot.GetProperty("queries").EnumerateArray());
            var firstResult = Assert.Single(firstQuery.GetProperty("results").EnumerateArray());
            var firstSummary = firstRoot.GetProperty("summary");
            var firstPath = firstResult.GetProperty("path").GetString();
            var nextCursor = firstQuery.GetProperty("next_cursor").GetString();

            Assert.Equal(1, firstRoot.GetProperty("query_count").GetInt32());
            Assert.Equal(1, firstRoot.GetProperty("result_count").GetInt32());
            Assert.Equal(1, firstSummary.GetProperty("limit_per_query").GetInt32());
            Assert.Equal(1, firstSummary.GetProperty("emitted_result_count").GetInt32());
            Assert.Equal(1, firstSummary.GetProperty("truncated_query_count").GetInt32());
            Assert.True(firstSummary.GetProperty("cursoring_available").GetBoolean());
            Assert.Equal("raw-diagnostic-echo", firstQuery.GetProperty("name").GetString());
            Assert.Equal(1, firstQuery.GetProperty("result_limit").GetInt32());
            Assert.Equal(1, firstQuery.GetProperty("minimum_omitted_result_count").GetInt32());
            Assert.Equal(1, firstQuery.GetProperty("top_files")[0].GetProperty("count").GetInt32());
            Assert.Contains(firstResult.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("CommandErrorWriter", StringComparison.Ordinal));
            Assert.True(firstResult.TryGetProperty("match_lines", out _));
            Assert.False(firstResult.TryGetProperty("snippet", out _));
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            using var jsonDocument = ParseJsonOutput(jsonStdout);
            var jsonSummary = jsonDocument.RootElement.GetProperty("summary");
            var jsonQuery = Assert.Single(jsonDocument.RootElement.GetProperty("queries").EnumerateArray());
            Assert.Equal(1, jsonSummary.GetProperty("limit_per_query").GetInt32());
            Assert.Equal(1, jsonSummary.GetProperty("minimum_omitted_result_count").GetInt32());
            Assert.True(jsonQuery.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, jsonQuery.GetProperty("minimum_omitted_result_count").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(jsonQuery.GetProperty("next_cursor").GetString()));

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "compact", "--limit", "1", "--cursor", nextCursor!],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = ParseJsonOutput(secondStdout);
            var secondResult = Assert.Single(Assert.Single(secondDocument.RootElement.GetProperty("queries").EnumerateArray()).GetProperty("results").EnumerateArray());

            Assert.NotEqual(firstPath, secondResult.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_BroadExceptionCatchRecipeEmitsTaxonomy_Issue3992()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_broad_catch_taxonomy_3992");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public sealed class App
                {
                    public void Run()
                    {
                        try
                        {
                            Work();
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(ex.Message);
                        }
                    }

                    private static void Work() { }
                }
                """);

            var (listExitCode, listStdout, listStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--list-recipes"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, listExitCode);
            Assert.Equal(string.Empty, listStderr);
            Assert.Contains("broad catch boundaries: top_level_normalization", listStdout, StringComparison.Ordinal);
            Assert.Contains("worker_process_boundary", listStdout, StringComparison.Ordinal);
            Assert.Contains("broad catch diagnostics: stable_sanitized_diagnostic", listStdout, StringComparison.Ordinal);
            Assert.Contains("narrow_or_rethrow_required", listStdout, StringComparison.Ordinal);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/broad-exception-catch", "--db", dbPath, "--json", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            using var jsonDocument = ParseJsonOutput(jsonStdout);
            var jsonQuery = Assert.Single(jsonDocument.RootElement.GetProperty("queries").EnumerateArray());
            var taxonomy = jsonQuery.GetProperty("broad_catch_taxonomy");
            Assert.Equal("broad-exception-catch", jsonQuery.GetProperty("name").GetString());
            Assert.Contains(taxonomy.GetProperty("boundary_categories").EnumerateArray(), item => item.GetProperty("name").GetString() == "cleanup_best_effort");
            Assert.Contains(taxonomy.GetProperty("boundary_categories").EnumerateArray(), item => item.GetProperty("name").GetString() == "worker_process_boundary");
            Assert.Contains(taxonomy.GetProperty("diagnostic_behaviors").EnumerateArray(), item => item.GetProperty("name").GetString() == "documented_fallback");
            Assert.Contains("stable sanitized diagnostics", taxonomy.GetProperty("triage_guidance").GetString(), StringComparison.Ordinal);

            var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/broad-exception-catch", "--db", dbPath, "--format", "compact", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            using var compactDocument = ParseJsonOutput(compactStdout);
            var compactQuery = Assert.Single(compactDocument.RootElement.GetProperty("queries").EnumerateArray());
            Assert.Contains(
                compactQuery.GetProperty("broad_catch_taxonomy").GetProperty("diagnostic_behaviors").EnumerateArray(),
                item => item.GetProperty("name").GetString() == "narrow_or_rethrow_required");

            var (draftExitCode, draftStdout, draftStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/broad-exception-catch", "--db", dbPath, "--format", "issue-drafts", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, draftExitCode);
            Assert.Equal(string.Empty, draftStderr);
            using var draftDocument = ParseJsonOutput(draftStdout);
            var draft = Assert.Single(draftDocument.RootElement.GetProperty("drafts").EnumerateArray());
            var body = draft.GetProperty("body").GetString();
            Assert.Contains("## Broad-catch taxonomy", body, StringComparison.Ordinal);
            Assert.Contains("`top_level_normalization`", body, StringComparison.Ordinal);
            Assert.Contains("`stable_sanitized_diagnostic`", body, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeCursorSkipsDedupedEnvelopeRows_Issue3392()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_cursor_dedup");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/overlap.cs", "csharp", "Console.WriteLine(ex.Message);\n");
            ReplaceChunks(
                dbPath,
                "src/overlap.cs",
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 20, Content = "Console.WriteLine(ex.Message);\n" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 1, EndLine = 20, Content = "Console.WriteLine(ex.Message);\n" },
                new ChunkRecord { ChunkIndex = 2, StartLine = 40, EndLine = 60, Content = "Console.WriteLine(ex.Message);\n" });

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "compact", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = ParseJsonOutput(firstStdout);
            var firstQuery = Assert.Single(firstDocument.RootElement.GetProperty("queries").EnumerateArray());
            var firstResult = Assert.Single(firstQuery.GetProperty("results").EnumerateArray());
            var nextCursor = firstQuery.GetProperty("next_cursor").GetString();

            Assert.True(firstQuery.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, firstResult.GetProperty("chunk_start_line").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code/raw-diagnostic-echo", "--db", dbPath, "--format", "compact", "--limit", "1", "--cursor", nextCursor!],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = ParseJsonOutput(secondStdout);
            var secondResult = Assert.Single(Assert.Single(secondDocument.RootElement.GetProperty("queries").EnumerateArray()).GetProperty("results").EnumerateArray());

            Assert.Equal(40, secondResult.GetProperty("chunk_start_line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static void ReplaceChunks(string dbPath, string path, params ChunkRecord[] chunks)
        {
            using var db = new DbContext(dbPath);
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM files WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);
            var fileId = (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException($"Missing indexed file {path}."));
            var writer = new DbWriter(db.Connection);
            writer.DeleteFileData(fileId);
            foreach (var chunk in chunks)
                chunk.FileId = fileId;
            writer.InsertChunks(chunks);
        }
    }

    [Fact]
    public void RunSearch_RecipeCursorRequiresSingleChildQuery_Issue3392()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code", "--format", "compact", "--cursor", "0:1:1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--cursor requires exactly one selected recipe query", stderr);
    }

    [Theory]
    [InlineData("csv")]
    public void RunSearch_RecipeRejectsUnsupportedFormattedOutputs_Issue3144(string format)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code", "--format", format],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--format csv/tsv/lsp/qf/sarif is not supported with --recipe", stderr);
    }

    [Fact]
    public void RunSearch_RecipeRejectsJsonArray_Issue3144()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code", "--json=array"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json=array is not supported with --recipe", stderr);
    }

    [Fact]
    public void RunSearch_ShowExcludedRequiresRecipe_Issue3696()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["JsonDocument.Parse", "--show-excluded"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--show-excluded is only supported", stderr);
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsIncludeLabelsEvidenceAndDuplicatePreflight_Issue3145()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var openIssuesPath = Path.Combine(projectRoot, "open-issues.json");
            File.WriteAllText(
                openIssuesPath,
                """
                [
                  {
                    "number": 3145,
                    "title": "Search audit recipe risky-code: unbounded-json-parse",
                    "labels": [{"name": "audit"}, {"name": "bug"}],
                    "url": "https://example.test/issues/3145"
                  }
                ]
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.Text.Json;

                public sealed class App
                {
                    public void Run()
                    {
                        JsonDocument.Parse("{}");
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--format", "issue-drafts", "--limit", "5", "--lang", "csharp", "--path", "src/app.cs", "--exclude-tests", "--open-issues", openIssuesPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var draft = Assert.Single(root.GetProperty("drafts").EnumerateArray());
            var triage = draft.GetProperty("triage");
            var duplicatePreflight = draft.GetProperty("duplicate_preflight");
            var match = Assert.Single(duplicatePreflight.GetProperty("matches").EnumerateArray());
            var body = draft.GetProperty("body").GetString();

            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.True(root.GetProperty("duplicate_preflight").GetProperty("checked").GetBoolean());
            Assert.Equal(1, root.GetProperty("duplicate_preflight").GetProperty("open_issue_count").GetInt32());
            Assert.Equal("medium", root.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
            Assert.Equal(0.45, root.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
            Assert.Equal("Search audit recipe risky-code: unbounded-json-parse", draft.GetProperty("title").GetString());
            Assert.Contains(draft.GetProperty("labels").EnumerateArray(), label => label.GetString() == "audit");
            Assert.Contains(draft.GetProperty("labels").EnumerateArray(), label => label.GetString() == "bug");
            Assert.Equal("src/app.cs", draft.GetProperty("evidence_paths")[0].GetString());
            Assert.Equal("medium", triage.GetProperty("severity").GetString());
            Assert.Equal("low", triage.GetProperty("confidence").GetString());
            Assert.Equal(1, triage.GetProperty("evidence_count").GetInt32());
            Assert.Contains("merge evidence", triage.GetProperty("duplicate_guidance").GetString(), StringComparison.Ordinal);
            Assert.Contains("JsonDocument.Parse", body, StringComparison.Ordinal);
            Assert.Contains("## Triage metadata", body, StringComparison.Ordinal);
            Assert.Contains("severity: `medium`", body, StringComparison.Ordinal);
            Assert.Contains("confidence: `low`", body, StringComparison.Ordinal);
            Assert.Contains("False-positive guidance", body, StringComparison.Ordinal);
            Assert.Contains("## Risk evidence", body, StringComparison.Ordinal);
            Assert.Contains("DOM parsing can materialize", body, StringComparison.Ordinal);
            Assert.Contains("## Replay command", body, StringComparison.Ordinal);
            Assert.Contains("cdidx search --recipe risky-code/unbounded-json-parse --format issue-drafts --limit 5", body, StringComparison.Ordinal);
            Assert.Contains("--lang csharp --path src/app.cs --exclude-tests", body, StringComparison.Ordinal);
            Assert.Contains($"--open-issues {QuoteReplayShellArgForAssertion(openIssuesPath)}", body, StringComparison.Ordinal);
            Assert.DoesNotContain("public sealed class App", body, StringComparison.Ordinal);
            Assert.Equal("unbounded-json-parse", draft.GetProperty("source").GetProperty("query_name").GetString());
            Assert.Contains(draft.GetProperty("source").GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("byte caps", StringComparison.Ordinal));
            Assert.Equal(1, duplicatePreflight.GetProperty("match_count").GetInt32());
            Assert.Equal(3145, match.GetProperty("number").GetInt32());
            Assert.Equal("title_exact", match.GetProperty("reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsWarnForMissingRepositoryLabels_Issue3926()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts_labels");
        var handler = new IssueDraftRepositoryLabelsHandler();
        var httpClient = new HttpClient(handler);
        IssueDuplicatePreflight.s_httpClientOverride = httpClient;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/reporter.cs",
                "csharp",
                """
                public sealed class Reporter
                {
                    public void Run(System.Exception ex)
                    {
                        System.Console.Error.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code/raw-diagnostic-echo",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--limit", "5",
                    "--open-issues", "github",
                    "--repo", "Widthdom/CodeIndex"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var draft = Assert.Single(document.RootElement.GetProperty("drafts").EnumerateArray());
            var missingLabels = draft
                .GetProperty("missing_labels")
                .EnumerateArray()
                .Select(label => label.GetString())
                .ToList();

            Assert.Equal(["audit"], missingLabels);
            Assert.Equal(
                "Repository label validation against github:Widthdom/CodeIndex found missing label(s): audit.",
                draft.GetProperty("label_warning").GetString());
            Assert.Contains(handler.RequestUris, uri => uri == "https://api.github.com/repos/Widthdom/CodeIndex/issues?state=open&per_page=100&page=1");
            Assert.Contains(handler.RequestUris, uri => uri == "https://api.github.com/repos/Widthdom/CodeIndex/labels?per_page=100&page=1");
        }
        finally
        {
            IssueDuplicatePreflight.s_httpClientOverride = null;
            httpClient.Dispose();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsIncludeRepresentativeEvidenceAndOmittedCounts_Issue3950()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts_evidence");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(System.Exception ex)
                    {
                        System.Console.Error.WriteLine(ex.Message);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/b.cs",
                "csharp",
                """
                public sealed class B
                {
                    public void Run(System.Exception ex)
                    {
                        _ = ex.Message;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code/raw-diagnostic-echo",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--limit", "1",
                    "--snippet-lines", "3"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var draft = Assert.Single(document.RootElement.GetProperty("drafts").EnumerateArray());
            var evidence = Assert.Single(draft.GetProperty("evidence").EnumerateArray());
            var source = draft.GetProperty("source");
            var body = draft.GetProperty("body").GetString();

            Assert.StartsWith("src/", evidence.GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.True(evidence.GetProperty("line").GetInt32() > 0);
            Assert.Contains("ex.Message", evidence.GetProperty("snippet").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, source.GetProperty("result_limit").GetInt32());
            Assert.Equal(1, source.GetProperty("omitted_count").GetInt32());
            Assert.Equal(1, source.GetProperty("minimum_omitted_result_count").GetInt32());
            Assert.True(source.GetProperty("truncated").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("next_cursor").GetString()));
            Assert.Contains("## Representative evidence", body, StringComparison.Ordinal);
            Assert.Contains("ex.Message", body, StringComparison.Ordinal);
            Assert.Contains("## Omitted results", body, StringComparison.Ordinal);
            Assert.Contains("minimum_omitted_result_count: `1`", body, StringComparison.Ordinal);
            Assert.Contains("## Replay command", body, StringComparison.Ordinal);
            Assert.DoesNotContain("public sealed class", body, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsSnippetLinesZeroOmitsSnippetsAndHonorsByteLimit_Issue4064()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts_snippetless_4064");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(System.Exception ex)
                    {
                        System.Console.Error.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code/raw-diagnostic-echo",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--limit", "1",
                    "--snippet-lines", "0",
                    "--max-json-bytes", "20000"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var draft = Assert.Single(document.RootElement.GetProperty("drafts").EnumerateArray());
            var evidence = Assert.Single(draft.GetProperty("evidence").EnumerateArray());
            var body = draft.GetProperty("body").GetString();

            Assert.Equal("src/a.cs", evidence.GetProperty("path").GetString());
            Assert.True(evidence.GetProperty("line").GetInt32() > 0);
            Assert.Equal(string.Empty, evidence.GetProperty("snippet").GetString());
            Assert.Contains("- `src/a.cs:", body, StringComparison.Ordinal);
            Assert.DoesNotContain("```text", body, StringComparison.Ordinal);
            Assert.Contains("--snippet-lines 0", body, StringComparison.Ordinal);

            var (capExitCode, capStdout, capStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code/raw-diagnostic-echo",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--limit", "1",
                    "--snippet-lines", "0",
                    "--max-json-bytes", "1"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, capExitCode);
            Assert.Equal(string.Empty, capStdout);
            Assert.Contains("issue-draft JSON output", capStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsSummaryOnlyEmitsCompactMetadataAndFreshness_Issue4118()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts_summary_4118");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run(System.Exception ex)
                    {
                        System.Console.Error.WriteLine(ex.Message);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code",
                    "--include-query", "raw-diagnostic-echo",
                    "--include-query", "unbounded-json-parse",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--summary-only",
                    "--limit", "1",
                    "--snippet-lines", "0"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var recipeSummary = root.GetProperty("recipe_summary");
            var freshness = root.GetProperty("query_freshness");
            var draft = Assert.Single(root.GetProperty("drafts").EnumerateArray());
            var source = draft.GetProperty("source");

            if (root.TryGetProperty("recipe", out var recipeMetadata))
                Assert.Equal(JsonValueKind.Null, recipeMetadata.ValueKind);
            Assert.Equal("summary", root.GetProperty("metadata_mode").GetString());
            Assert.Equal("risky-code", recipeSummary.GetProperty("name").GetString());
            Assert.Equal(2, recipeSummary.GetProperty("query_count").GetInt32());
            Assert.False(recipeSummary.TryGetProperty("queries", out _));
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal(1, root.GetProperty("result_count").GetInt32());
            Assert.Equal(1, freshness.GetProperty("positive_evidence_query_count").GetInt32());
            Assert.Equal(1, freshness.GetProperty("zero_result_query_count").GetInt32());
            Assert.Contains(freshness.GetProperty("stale_query_names").EnumerateArray(), name => name.GetString() == "unbounded-json-parse");
            Assert.Equal("raw-diagnostic-echo", source.GetProperty("query_name").GetString());
            Assert.Contains(source.GetProperty("risk_evidence").EnumerateArray(), evidence => evidence.GetString()!.Contains("raw exception messages", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeIssueDraftsSummaryOnlyFreshnessUsesMatchedCountsWhenTotalLimited_Issue4118()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_recipe_issue_drafts_summary_total_limit_4118");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/a.cs",
                "csharp",
                """
                public sealed class A
                {
                    public void Run()
                    {
                        try
                        {
                        }
                        catch (Exception ex)
                        {
                            System.Console.Error.WriteLine(ex.Message);
                        }
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "--recipe", "risky-code",
                    "--include-query", "raw-diagnostic-echo",
                    "--include-query", "broad-exception-catch",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--summary-only",
                    "--total-limit", "1",
                    "--snippet-lines", "0"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var freshness = root.GetProperty("query_freshness");

            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal(1, root.GetProperty("result_count").GetInt32());
            Assert.Equal(2, freshness.GetProperty("positive_evidence_query_count").GetInt32());
            Assert.Equal(0, freshness.GetProperty("zero_result_query_count").GetInt32());
            Assert.Empty(freshness.GetProperty("stale_query_names").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_AdHocIssueDraftsUseTitleLabelsAndDuplicatePreflight_Issue3520()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_issue_drafts_ad_hoc");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var openIssuesPath = Path.Combine(projectRoot, "open-issues.json");
            File.WriteAllText(
                openIssuesPath,
                """
                [
                  {
                    "number": 3520,
                    "title": "Thread.Yield audit",
                    "labels": [{"name": "audit"}],
                    "url": "https://example.test/issues/3520"
                  }
                ]
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/scheduler.cs",
                "csharp",
                """
                public sealed class Scheduler
                {
                    public void Run()
                    {
                        Thread.Yield();
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "Thread.Yield",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--exact-substring",
                    "--issue-title", "Thread.Yield audit",
                    "--issue-label", "audit,bug",
                    "--issue-label", "needs-triage",
                    "--open-issues", openIssuesPath
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var draft = Assert.Single(root.GetProperty("drafts").EnumerateArray());
            var labels = draft.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToList();
            var triage = draft.GetProperty("triage");
            var duplicatePreflight = draft.GetProperty("duplicate_preflight");
            var match = Assert.Single(duplicatePreflight.GetProperty("matches").EnumerateArray());
            var body = draft.GetProperty("body").GetString();

            Assert.Equal(1, root.GetProperty("query_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("recipe").ValueKind);
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal("medium", root.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
            Assert.Equal(0.45, root.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
            Assert.Equal("Thread.Yield audit", draft.GetProperty("title").GetString());
            Assert.Contains("audit", labels);
            Assert.Contains("bug", labels);
            Assert.Contains("needs-triage", labels);
            Assert.Equal("src/scheduler.cs", draft.GetProperty("evidence_paths")[0].GetString());
            Assert.Equal("medium", triage.GetProperty("severity").GetString());
            Assert.Equal("low", triage.GetProperty("confidence").GetString());
            Assert.Equal(1, triage.GetProperty("evidence_count").GetInt32());
            Assert.Contains("Thread.Yield", body, StringComparison.Ordinal);
            Assert.Contains("## Triage metadata", body, StringComparison.Ordinal);
            Assert.DoesNotContain("public sealed class Scheduler", body, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("source").GetProperty("recipe").ValueKind);
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("source").GetProperty("query_name").ValueKind);
            Assert.Equal("Thread.Yield", draft.GetProperty("source").GetProperty("query").GetString());
            Assert.True(draft.GetProperty("source").GetProperty("exact_substring").GetBoolean());
            Assert.Equal(1, duplicatePreflight.GetProperty("match_count").GetInt32());
            Assert.Equal(3520, match.GetProperty("number").GetInt32());
            Assert.Equal("title_exact", match.GetProperty("reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_AdHocIssueDraftDuplicateThresholdFiltersMatches_Issue3827()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_issue_draft_threshold_3827");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var openIssuesPath = Path.Combine(projectRoot, "open-issues.json");
            File.WriteAllText(
                openIssuesPath,
                """
                [
                  {
                    "number": 3827,
                    "title": "Search issue draft: Thread.Yield stale match follow-up scheduler report review backlog duplicate candidate validation note",
                    "labels": [{"name": "bug"}],
                    "url": "https://example.test/issues/3827"
                  }
                ]
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/scheduler.cs",
                "csharp",
                """
                public sealed class Scheduler
                {
                    public void Run()
                    {
                        Thread.Yield();
                    }
                }
                """);

            var (highExitCode, highStdout, highStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "Thread.Yield",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--exact-substring",
                    "--issue-label", "bug",
                    "--open-issues", openIssuesPath,
                    "--duplicate-confidence", "high"
                ],
                _jsonOptions));
            var (customExitCode, customStdout, customStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [
                    "Thread.Yield",
                    "--db", dbPath,
                    "--format", "issue-drafts",
                    "--exact-substring",
                    "--issue-label", "bug",
                    "--open-issues", openIssuesPath,
                    "--duplicate-threshold", "0.4"
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, highExitCode);
            Assert.Equal(string.Empty, highStderr);
            using var highDocument = ParseJsonOutput(highStdout);
            var highRoot = highDocument.RootElement;
            var highDraft = Assert.Single(highRoot.GetProperty("drafts").EnumerateArray());
            Assert.Equal("high", highRoot.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
            Assert.Equal(0.7, highRoot.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
            Assert.Equal(0, highDraft.GetProperty("duplicate_preflight").GetProperty("match_count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, customExitCode);
            Assert.Equal(string.Empty, customStderr);
            using var customDocument = ParseJsonOutput(customStdout);
            var customRoot = customDocument.RootElement;
            var customDraft = Assert.Single(customRoot.GetProperty("drafts").EnumerateArray());
            var customMatch = Assert.Single(customDraft.GetProperty("duplicate_preflight").GetProperty("matches").EnumerateArray());
            Assert.Equal("custom", customRoot.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
            Assert.Equal(0.4, customRoot.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
            Assert.Equal(1, customDraft.GetProperty("duplicate_preflight").GetProperty("match_count").GetInt32());
            Assert.Equal(3827, customMatch.GetProperty("number").GetInt32());
            Assert.Equal("title_label_contains", customMatch.GetProperty("reason").GetString());
            Assert.Equal(0.45, customMatch.GetProperty("score").GetDouble());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ProfileEmitsSqlPhasesAndQueryPlan_Issue1643()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_profile");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                """
                public class AuthFixture
                {
                    public void Authenticate() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--json", "--profile", "--slow-query-ms", "0"],
                _jsonOptions));
            var rawLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lines = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return !IsJsonStreamDoneSentinel(document.RootElement);
                })
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using (var doneDocument = JsonDocument.Parse(rawLines[^1]))
            {
                Assert.True(IsJsonStreamDoneSentinel(doneDocument.RootElement));
            }
            Assert.Equal(2, lines.Length);

            using var resultDocument = JsonDocument.Parse(lines[0]);
            Assert.Equal("src/auth.cs", resultDocument.RootElement.GetProperty("path").GetString());

            using var profileDocument = JsonDocument.Parse(lines[1]);
            var profile = profileDocument.RootElement.GetProperty("profile");
            var phases = profile.GetProperty("phases");
            var queryPlan = profile.GetProperty("query_plan");
            var queries = profile.GetProperty("queries");

            Assert.True(phases.GetArrayLength() > 0);
            Assert.True(queryPlan.GetArrayLength() > 0);
            Assert.True(queries.GetArrayLength() > 0);
            Assert.Equal("sql_1", phases[0].GetProperty("name").GetString());
            Assert.True(phases[0].GetProperty("elapsed_ms").GetDouble() >= 0);
            Assert.True(phases[0].GetProperty("rows_scanned").GetInt32() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(queryPlan[0].GetProperty("detail").GetString()));
            Assert.Contains(queries.EnumerateArray(), query =>
                query.GetProperty("sql").GetString()?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_VerboseEmitsDebugToStderrOnly_Issue1899()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_verbose");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                """
                public class AuthFixture
                {
                    public void Authenticate() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--verbose"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/auth.cs", stdout);
            Assert.Contains("DEBUG query: sql_statements=", stderr);
            Assert.Contains("rows_scanned=", stderr);
            Assert.DoesNotContain("\"_debug\"", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_VerboseJsonAppendsDebugObjectAndKeepsStderrClean_Issue1899()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_verbose_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                """
                public class AuthFixture
                {
                    public void Authenticate() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Authenticate", "--db", dbPath, "--json", "--verbose"],
                _jsonOptions));
            var lines = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, lines.Count);
            using var resultDocument = lines[0];
            using var debugDocument = lines[1];
            Assert.Equal("src/auth.cs", resultDocument.RootElement.GetProperty("path").GetString());
            var debug = debugDocument.RootElement.GetProperty("_debug");
            Assert.True(debug.GetProperty("sql_statement_count").GetInt32() > 0);
            Assert.True(debug.GetProperty("elapsed_ms").GetDouble() >= 0);
            Assert.True(debug.GetProperty("rows_scanned").GetInt32() >= 0);
            Assert.Contains("omitted", debug.GetProperty("redaction").GetString());
            Assert.DoesNotContain("SELECT", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(dbPath, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("StackTrace", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecognizesMsbuildProjectFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_msbuild_lang");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "msbuild_lang_search_4f9c2a";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.csproj",
                "msbuild",
                $$"""
                <Project>
                  <PropertyGroup>
                    <CustomToken>{{queryToken}}</CustomToken>
                  </PropertyGroup>
                </Project>
                """);

            var (msbuildExitCode, msbuildStdout, msbuildStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", "msbuild", "--json", "--count"],
                _jsonOptions));
            var (xmlExitCode, xmlStdout, xmlStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", "xml", "--count"],
                _jsonOptions));

            using var msbuildDocument = ParseJsonOutput(msbuildStdout);
            var msbuildJson = msbuildDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, msbuildExitCode);
            Assert.Equal(1, msbuildJson.GetProperty("count").GetInt32());
            Assert.Equal(1, msbuildJson.GetProperty("files").GetInt32());
            Assert.Equal(queryToken, msbuildJson.GetProperty("query").GetString());
            Assert.Equal(string.Empty, msbuildStderr);

            Assert.Equal(CommandExitCodes.Success, xmlExitCode);
            Assert.Equal("0", xmlStdout.Trim());
            Assert.Equal(string.Empty, xmlStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("xaml")]
    [InlineData("axaml")]
    public void RunSearch_RecognizesXamlLanguageAliases(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_xaml_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = $"xaml_lang_alias_{Guid.NewGuid():N}";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/MainWindow.xaml",
                "xml",
                $$"""
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Grid>
                        <TextBlock Text="{{queryToken}}" />
                    </Grid>
                </Window>
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", lang, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("rs")]
    [InlineData("r-s")]
    [InlineData("r s")]
    public void RunSearch_RecognizesRustLanguageAlias(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_rust_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = $"rust_lang_alias_{Guid.NewGuid():N}";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/lib.rs",
                "rust",
                $$"""
                pub fn hit() {
                    let _ = "{{queryToken}}";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", lang, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("c#")]
    [InlineData("cs")]
    [InlineData("cshtml")]
    [InlineData("js")]
    [InlineData("JSX")]
    [InlineData("cjs")]
    [InlineData("MJS")]
    [InlineData("Java")]
    [InlineData("kt")]
    [InlineData("kts")]
    [InlineData("razor")]
    public void RunSearch_NormalizesCommonLanguageAliases(string input)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "lang_alias_91d4b3";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                $@"public class App
{{
    public void Run()
    {{
        var marker = ""{queryToken}"";
    }}
}}");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.kt",
                "kotlin",
                $@"class App {{
    fun run() {{
        val marker = ""{queryToken}""
    }}
}}");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.java",
                "java",
                $@"class App {{
    void run() {{
        String marker = ""{queryToken}"";
    }}
}}");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.js",
                "javascript",
                $@"function run() {{
    const marker = ""{queryToken}"";
}}");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", input, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("js")]
    [InlineData("jsx")]
    [InlineData("JS")]
    [InlineData("JSX")]
    public void RunSearch_NormalizesJavascriptLangAliases(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_javascript_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = $"javascript_lang_alias_{Guid.NewGuid():N}";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.js",
                "javascript",
                $@"const marker = ""{queryToken}"";");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", lang, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("cjs")]
    [InlineData("mjs")]
    [InlineData("CJS")]
    [InlineData("MJS")]
    public void RunSearch_NormalizesJavascriptExtensionStyleLangAliases(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_javascript_extension_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = $"javascript_extension_lang_alias_{Guid.NewGuid():N}";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.mjs",
                "javascript",
                $@"const marker = ""{queryToken}"";");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", lang, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("yml")]
    [InlineData("YML")]
    public void RunSearch_NormalizesYamlLangAlias(string input)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_yaml_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "yaml_lang_alias_3d5a19";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "config/workflow.yml",
                "yaml",
                $@"name: demo
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ""{queryToken}""");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", input, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("bat")]
    [InlineData("cmd")]
    public void RunSearch_NormalizesBatchLangAliases(string input)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_batch_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "batch_lang_alias_7a24d1";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "scripts/run.bat",
                "batch",
                $"echo {queryToken}\r\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", input, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("T-SQL")]
    [InlineData("transact-sql")]
    [InlineData("transact sql")]
    public void RunSearch_NormalizesSqlDialectLangAliases(string input)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "sql_lang_alias_3f7d21";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "sql/repro.sql",
                "sql",
                $"SELECT '{queryToken}';");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--lang", input, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("tokio::spawn", "column qualifier")]
    [InlineData("AND OR", "literal-safe search")]
    [InlineData("foo\"bar", "literal-safe search")]
    public void RunSearch_RawFtsQuerySyntaxErrorsReturnUsageError(string query, string expectedHint)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_error");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void spawn() { } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath, "--fts"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Error [E006_FTS_QUERY_SYNTAX]: FTS5 query syntax:", stderr);
            Assert.Contains(expectedHint, stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsValidQueryStillWorks()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_success");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void spawn() { } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["spawn", "--db", dbPath, "--fts", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.NotEqual("0", stdout.Trim());
            Assert.DoesNotContain("Error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsTooLongQueryReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_too_long");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void spawn() { } }");
            var query = new string('a', QueryLimits.MaxQueryLength + 1);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath, "--fts"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains(QueryLimits.FormatQueryTooLongError(), stderr);
            Assert.Contains("Usage:", stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_TooLongQueryReturnsUsageError_Issue1468()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_query_too_long");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var query = new string('a', QueryLimits.MaxQueryLength + 1);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains(QueryLimits.FormatQueryTooLongError(), stderr);
            Assert.Contains("Shorten the search text", stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_LiteralTokenCountTooHighReturnsUsageError_Issue3081()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_literal_terms_too_many");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var query = string.Join(' ', Enumerable.Range(0, DbReader.MaxLiteralSearchTokenCount + 1).Select(i => $"t{i:D3}"));

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("literal search query has too many terms", stderr);
            Assert.Contains("smaller literal queries", stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsTooManyNearOperatorsReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_too_many_near");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void spawn() { } }");
            var query = string.Join(" OR ", Enumerable.Repeat("NEAR(spawn app, 5)", DbReader.MaxRawFtsNearOperators + 1));

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath, "--fts", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("raw FTS5 query is too complex", stderr);
            Assert.Contains("NEAR operators", stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsLowercaseOperatorWordsAreTerms()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_lowercase_terms");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "and or not near");
            var query = string.Join(" ", Enumerable.Repeat("and", DbReader.MaxRawFtsBooleanOperators + 1));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [query, "--db", dbPath, "--fts", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.DoesNotContain("too complex", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_TrailingWildcardActsAsPrefixShorthand()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_prefix_shorthand");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                "public class Authenticator { public bool AuthenticateUser() => true; }\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/other.cs",
                "csharp",
                "public class Other { public void Idle() { } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["auth*", "--db", dbPath, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("rb", "ruby", "package/example.rb")]
    [InlineData("fs", "fsharp", "Module.fs")]
    public void RunSearch_AcceptsRubyAndFsharpLangAliases(string alias, string canonicalLang, string filePath)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_{canonicalLang}_{alias}_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                filePath,
                canonicalLang,
                """
                public_api
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["public_api", "--db", dbPath, "--lang", alias, "--exact", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultsHumanOutputIncludesQueryFilterContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_zero_context_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "public sealed class App { }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["missing-token", "--db", dbPath, "--path", "src/**", "--lang", "csharp", "--limit", "7"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("No results found. (query: \"missing-token\", path: src/**, lang: csharp, limit: 7)", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultsJsonIncludesStructuredQueryContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_zero_context_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "public sealed class App { }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["missing-token", "--db", dbPath, "--path", "src/**", "--lang", "csharp", "--limit", "7", "--json", "--exclude-comments", "--exclude-strings", "--exclude-fixtures"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var queryContext = root.GetProperty("query_context");

            Assert.Equal(0, root.GetProperty("count").GetInt32());
            Assert.Equal("missing-token", root.GetProperty("query").GetString());
            Assert.Equal("missing-token", queryContext.GetProperty("text").GetString());
            Assert.Equal("src/**", queryContext.GetProperty("path")[0].GetString());
            Assert.Equal("csharp", queryContext.GetProperty("lang").GetString());
            Assert.Equal(7, queryContext.GetProperty("limit").GetInt32());
            Assert.True(queryContext.GetProperty("exclude_comments").GetBoolean());
            Assert.True(queryContext.GetProperty("exclude_strings").GetBoolean());
            Assert.True(queryContext.GetProperty("exclude_fixtures").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ProjectFilterFallbackJsonIncludesStructuredDiagnostic_Issue3461()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_project_fallback_json");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_search_project_fallback_{Guid.NewGuid():N}.db");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(Path.Combine(projectRoot, "CodeIndex.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/ServiceA.cs", "csharp", "public class ServiceA { }\n");

            Environment.CurrentDirectory = projectRoot;
            var expectedProjectRoot = Path.GetFullPath(Environment.CurrentDirectory);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["missing-token", "--db", dbPath, "--project", "App", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var queryContext = document.RootElement.GetProperty("query_context");

            Assert.Equal("App", queryContext.GetProperty("project")[0].GetString());
            Assert.Equal("src/App/*", queryContext.GetProperty("path")[0].GetString());
            Assert.Equal(expectedProjectRoot, queryContext.GetProperty("project_filter_root").GetString());
            Assert.Equal(QueryCommandRunner.ProjectFilterRootFallbackReasonCurrentDirectory, queryContext.GetProperty("project_filter_root_fallback_reason").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_AllowsPathValueThatLooksLikePreviewOption()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_preview_like_path_value");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["foo", "--db", dbPath, "--path=--max-line-width", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.DoesNotContain("is not supported", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_RejectsMissingFocusColumnValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_missing_focus_column");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "sample");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["README.md", "--db", dbPath, "--start", "1", "--focus-column", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            // The recognized-option guard in TryReadRawOptionValue short-circuits `--focus-column --json`
            // as a missing-value case before TryParsePositiveInt runs, so the error is "requires a value"
            // rather than the older TryParsePositiveInt-level "requires a positive integer" message.
            // `--focus-column --json` は TryReadRawOptionValue の既知オプション判定で TryParsePositiveInt
            // 実行前に値欠如として短絡するため、旧メッセージではなく "requires a value" となる。
            Assert.Contains("--focus-column requires a value", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RawFtsSyntaxErrorsAreReportedAsUsageErrors()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_raw_fts_syntax_error");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/demo.cs", "csharp", "class Demo {}\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["title:foo", "--db", dbPath, "--fts", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Error [E006_FTS_QUERY_SYNTAX]: FTS5 query syntax:", stderr);
            Assert.Contains("raw FTS5 syntax", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExplicitMissingDbReturnsUsageErrorBeforeOpeningReader_Issue2073()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2073_missing_db");
        try
        {
            var missingDb = Path.Combine(projectRoot, "missing-dir", "codeindex.db");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["QueryCommandRunner", "--db", missingDb],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Error [E001_DB_NOT_FOUND]: --db", stderr);
            Assert.Contains("does not point to an existing database file", stderr);
            Assert.Contains("Hint: create or refresh the index with `cdidx index <projectPath>`", stderr);
            Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("search")}", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--path")]
    [InlineData("--exclude-path")]
    public void RunSearch_InvalidPathGlobReturnsUsageErrorBeforeQuery_Issue2073(string optionName)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2073_invalid_glob");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["QueryCommandRunner", "--db", dbPath, optionName, "[*-z]"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"Error: {optionName} '[*-z]' is not a valid glob", stderr);
            Assert.Contains("character classes are not supported", stderr);
            Assert.Contains("Hint: fix the invalid or missing option value", stderr);
            Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("search")}", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Issue #1507: missing-value errors for CLI flags must append a per-flag `Hint:` line that
    // shows the expected value type or range (e.g. positive integer, glob pattern, language id),
    // so users do not need to consult `--help` for trivial mistakes. The hint is sourced from
    // a single per-flag metadata table so every command surfaces consistent guidance.
    // Issue #1507: 値欠如エラーには、フラグごとに期待する値の型/範囲を示す `Hint:` 行を
    // 追記する（例: 正の整数、glob、言語識別子）。`--help` を見なくてもユーザーが復旧できる。
    // ヒントは単一のメタデータテーブルから供給され、コマンド間で一貫した案内を出す。
    [Theory]
    [InlineData(new[] { "QueryCommandRunner", "--limit" }, "search", "--limit", "pass a positive integer", "--limit 20")]
    [InlineData(new[] { "QueryCommandRunner", "--top" }, "search", "--limit", "pass a positive integer", "--limit 20")]
    [InlineData(new[] { "QueryCommandRunner", "--db" }, "search", "--db", "pass a path to a CodeIndex SQLite database", ".cdidx/codeindex.db")]
    [InlineData(new[] { "QueryCommandRunner", "--lang" }, "search", "--lang", "pass a language identifier", "--lang csharp")]
    [InlineData(new[] { "QueryCommandRunner", "--path" }, "search", "--path", "pass a glob-style path pattern", "--path src/**")]
    [InlineData(new[] { "QueryCommandRunner", "--exclude-path" }, "search", "--exclude-path", "pass a glob-style path pattern to exclude", "--exclude-path tests/**")]
    [InlineData(new[] { "QueryCommandRunner", "--snippet-lines" }, "search", "--snippet-lines", "pass an integer between 1 and 20", "--snippet-lines 8")]
    [InlineData(new[] { "QueryCommandRunner", "--snippet-focus" }, "search", "--snippet-focus", "pass one of `leftmost`, `quality`, or `proximity`", "--snippet-focus quality")]
    [InlineData(new[] { "QueryCommandRunner", "--max-line-width" }, "search", "--max-line-width", "pass a non-negative integer", "--max-line-width 512")]
    public void RunSearch_MissingOptionValueAppendsPerFlagHint_Issue1507(
        string[] args,
        string command,
        string optionName,
        string expectedHintFragment,
        string expectedExampleFragment)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {optionName} requires a value.", stderr);
        Assert.Contains($"Hint: {expectedHintFragment}", stderr);
        Assert.Contains(expectedExampleFragment, stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
    }

    [Fact]
    public void RunExcerpt_MissingStartValueShowsPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --start requires a value.", stderr);
        Assert.Contains("Hint: pass a 1-based line number", stderr);
        Assert.Contains("--start 10", stderr);
    }

    // Issue #1507: when a separated `--db --foo` shape rejects the next token as a recognized
    // option, both the inline-form hint AND the per-flag hint must surface so users know why
    // the parser stopped *and* what value to pass.
    // Issue #1507: `--db --foo` のような separated dashed literal が拒否されたときは、
    // inline-form ヒント (`--db=<value>` 形式) と、フラグ別の値ヒントを両方表示する。
    [Fact]
    public void RunSearch_DbDoubleDashLiteralKeepsBothHints_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["QueryCommandRunner", "--db", "--mystery"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("Hint: if the literal value starts with `--`, pass it as `--db=<value>`.", stderr);
        Assert.Contains("Hint: pass a path to a CodeIndex SQLite database", stderr);
    }

    // Issue #1507: `find` validates its options through ValidateFindArgs (separate path from
    // ParseArgs), so the per-flag hint table must apply there too. Otherwise users running
    // `cdidx find foo --path` would still see the bare "requires a value" message.
    // Issue #1507: `find` は ValidateFindArgs 経由で値検証する独自経路を持つので、
    // この経路でもフラグ別ヒントを表示する。
    [Fact]
    public void RunFind_MissingPathValueShowsPerFlagHint_Issue1507()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue1507_find_missing_path");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["foo", "--db", dbPath, "--path"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Error: --path requires a value.", stderr);
            Assert.Contains("Hint: pass a glob-style path pattern", stderr);
            Assert.Contains("--path src/**", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Pins `find --path=<value>` with a value that starts with `--`. ParseArgs supports
    // this shape via inline `=`, but ValidateFindArgs previously saw only the bare token
    // and `PrepareFindArgs` briefly tried to normalize the inline form by splitting it into
    // two tokens — that split destroyed inline `--`-prefixed values. Locks the contract
    // that `find` honors the CLI hint (`pass it as --path=<value>`) just like the other
    // query commands.
    // `find --path=<value>` で value が `--` で始まる合法な inline 値を壊さないよう固定する
    // 回帰テスト。`PrepareFindArgs` 側で inline を分解すると `--path=--literal.txt` が
    // `--path`/`--literal.txt` に割れ、`ParseArgs` が値を option と誤認して失敗していた。
    [Fact]
    public void RunFind_PathFilterAcceptsRecognizedOptionTokenViaInlineValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_path_inline_recognized_option");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/--json-dir/Demo.cs",
                "csharp",
                "class Demo { void Alpha() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Alpha", $"--db={dbPath}", "--path=--json-dir", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // `--paht` should surface as `--path` so MCP/CLI users do not have to read full help
    // text to recover from a single-letter swap (#1582).
    // `--paht` のような 1 文字入れ替えミスから `--path` を提案できることを確認する (#1582)。
    [Fact]
    public void RunSearch_UnsupportedFlagTypo_SuggestsClosestFlag()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--paht", "src/**"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --paht is not supported for search.", stderr);
        Assert.Contains("Did you mean: --path?", stderr);
    }

    // Inline `--foo=bar` form must surface the same suggestion as the separated form.
    // ParseArgs only splits `=value` for known value-taking options, so for `--paht=...`
    // the suggester previously saw the full `--paht=src/**` token and produced no match;
    // the round-2 fix strips the `=value` portion before searching for a similar flag.
    // インライン `--foo=bar` 形式も separated 形式と同じ提案を出すこと。ParseArgs は
    // 既知の value-taking option でしか `=value` を分解しないため、`--paht=...` は
    // まるごと matcher に渡され従来は提案が出なかった。round-2 修正で `=value` を
    // 除去してから候補を探すようにした。
    [Fact]
    public void RunSearch_UnsupportedFlagTypoInInlineValueForm_SuggestsClosestFlag_Issue1582()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--paht=src/**"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("is not supported for search", stderr);
        Assert.Contains("Did you mean: --path?", stderr);
    }

    [Fact]
    public void RunSearch_UnknownFlagAfterQuery_ReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--dapth", "3"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--dapth is not supported for search", stderr);
        Assert.Contains("Did you mean: --path?", stderr);
    }

    [Fact]
    public void RunSearch_InvalidSnippetFocus_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--snippet-focus", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid --snippet-focus value", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(value, stderr);
    }

    [Fact]
    public void RunSearch_InvalidLimit_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--limit", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--limit requires an integer", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(value, stderr);
    }

    [Fact]
    public void RunSearch_InvalidJsonFormat_TruncatesOversizedInlineValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--json=" + value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json format must be one of ndjson or array", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(value, stderr);
    }

    [Fact]
    public void RunSearch_InvalidFormat_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["foo", "--format", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--format must be one of", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(value, stderr);
    }

    // `find` previously emitted only the raw `Error: unsupported option for find: --paht`
    // line — round-2 fix routes the unknown token through the same suggester so users see
    // `Did you mean: --path?`. Covers both the separated and inline `=value` forms.
    // 従来 find は `Error: unsupported option for find: --paht` だけを出していたが、
    // round-2 修正で同じ suggester を経由するようにし `Did you mean: --path?` を出す。
    // separated 形式と inline `=value` 形式の両方を確認する。
    [Fact]
    public void RunFind_UnsupportedFlagTypo_SuggestsClosestFlag_Issue1582()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard", "--paht", "src/Auth.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unsupported option for find: --paht", stderr);
        Assert.Contains("Did you mean: --path?", stderr);
    }

    [Fact]
    public void RunFind_UnsupportedFlagTypoInInlineValueForm_SuggestsClosestFlag_Issue1582()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard", "--paht=src/Auth.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unsupported option for find: --paht=src/Auth.cs", stderr);
        Assert.Contains("Did you mean: --path?", stderr);
    }

    // Regression lock for #184 follow-up: `--query` accepts dashed literals including
    // recognized flags (e.g. `--json`) as query text, because FTS-style queries can
    // legitimately contain dash-prefixed tokens. The recognized-option guard must NOT
    // short-circuit `--query`, and the downstream IsRejectedSeparatedStringValue check
    // must also skip `--query` so the flag-shaped token flows through as a literal.
    // #184 のフォローアップ回帰ロック: `--query` は `--json` のような既知フラグを含む dashed
    // literal をクエリ本文として受け入れる（FTS 風クエリには dash 付きトークンが現れ得る）。
    // recognized-option guard で `--query` を早期に短絡してはならず、後段の
    // IsRejectedSeparatedStringValue も `--query` を素通りさせて flag 形状のトークンをリテラルと
    // して扱う契約を維持する。
    [Fact]
    public void RunFind_QueryAcceptsDashedLiteralValue_Issue184()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue184_query_dashed_literal");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue184.cs",
                "csharp",
                "namespace Issue184; public class T { public void M() { } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["--query", "--json", "--path", "src/**", "--db", dbPath, "--json"], _jsonOptions));

            // Accepting `--json` as query text is the success contract; the query may or may not
            // match, but parsing must NOT fail with a "requires a value" error for --query.
            // `--json` をクエリテキストとして受け入れるのが成功契約。ヒットの有無は問わないが、
            // --query が "requires a value" で失敗してはならない。
            Assert.NotEqual(CommandExitCodes.UsageError, exitCode);
            Assert.DoesNotContain("--query requires a value", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Regression lock for #184 follow-up: `--focus-column` requires a positive integer,
    // while `--max-line-width` accepts non-negative integers so `0` can disable truncation.
    // Zero, negative, and non-numeric values must fail closed with UsageError and the
    // corresponding validation message. Earlier tests only covered the missing-value case
    // (which now short-circuits before TryParsePositiveInt), leaving these option-specific
    // numeric contracts uncovered.
    // #184 のフォローアップ回帰ロック: `--focus-column` は正の整数を要求し、
    // `--max-line-width` は切り詰め解除のため 0 を許容する非負整数。0・負数・非数値は
    // UsageError と対応する validation message で fail-close する。以前のテストは値欠如
    // （今は TryParsePositiveInt 前に短絡する）しかカバーしていなかったため、
    // これらのオプション固有の数値契約を明示的にロックする。
    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    public void RunExcerpt_RejectsInvalidFocusColumnValue(string invalidValue)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_excerpt_invalid_focus_column_{invalidValue}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "sample");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["README.md", "--db", dbPath, "--start", "1", "--focus-column", invalidValue, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--focus-column requires an integer between 1 and 100000", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // `search --lang csarp` previously emitted "No results found." with no language hint
    // — RunSearch never called WriteLangHint. Round-2 wires WriteLangHint into the zero-
    // result branch and lets it fall back to ReferenceExtractor.GetSupportedLanguages()
    // when the typo'd value matches no indexed language (#1582).
    // 従来 `search --lang csarp` は "No results found." だけ表示し、RunSearch から
    // WriteLangHint を呼んでいなかった。round-2 で zero-result 分岐に WriteLangHint を
    // 配線し、index 済み言語にマッチしない場合は ReferenceExtractor.GetSupportedLanguages()
    // にフォールバックして提案を出すようにした (#1582)。
    [Fact]
    public void RunSearch_LangTypo_SuggestsClosestLanguage_Issue1582()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_lang_typo");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["nothing_matches_xyzzy", "--db", dbPath, "--lang", "csarp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("No results found.", stderr);
            Assert.Contains("Did you mean: --lang csharp?", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // `--lang java` against a repo with no Java files used to print a confusing
    // "Did you mean: --lang java?" because the fallback ReferenceExtractor match returned
    // the exact input. Regression coverage for the round-3 fix that suppresses
    // self-suggestions in WriteLangHint (#1582).
    // Java を含まないリポジトリで `--lang java` を指定した際、フォールバックの
    // ReferenceExtractor が入力と同じ値を返すため "Did you mean: --lang java?" という
    // 紛らわしいメッセージが出ていた。round-3 で WriteLangHint が自己提案を抑止する
    // ようにしたことの回帰ロック (#1582)。
    [Fact]
    public void RunSearch_LangNotIndexedButSupported_DoesNotSelfSuggest_Issue1582()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_lang_no_self_suggest");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["nothing_matches_xyzzy", "--db", dbPath, "--lang", "java"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("No results found.", stderr);
            Assert.Contains("'java' not found in index", stderr);
            Assert.DoesNotContain("Did you mean: --lang java?", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_JsonClampsLongSingleLineContentAroundFocus()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + " TARGET " + new string('b', 320);
            var targetColumn = longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1;
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--json", "--max-line-width", "96", "--focus-column", targetColumn.ToString(), "--focus-length", "6"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("requested_start_line").GetInt32());
            Assert.Equal(1, json.GetProperty("requested_end_line").GetInt32());
            Assert.Equal(1, json.GetProperty("effective_start_line").GetInt32());
            Assert.Equal(1, json.GetProperty("effective_end_line").GetInt32());
            Assert.True(json.GetProperty("content_truncated").GetBoolean());
            var truncationReasons = json.GetProperty("content_truncation_reasons")
                .EnumerateArray()
                .Select(reason => reason.GetString())
                .ToArray();
            Assert.Contains("line_width_cap", truncationReasons);
            var recovery = json.GetProperty("content_recovery");
            Assert.Equal(1, recovery.GetProperty("start_line").GetInt32());
            Assert.Equal(1, recovery.GetProperty("end_line").GetInt32());
            var recoveryCommand = recovery.GetProperty("command").GetString();
            Assert.Contains("cdidx excerpt dist/data.txt", recoveryCommand);
            Assert.Contains("--db", recoveryCommand);
            Assert.Contains(dbPath, recoveryCommand);
            Assert.Contains("--start 1 --end 1 --max-line-width 0 --json", recoveryCommand);
            Assert.DoesNotContain(longLine, json.GetProperty("content").GetString());
            Assert.Contains("TARGET", json.GetProperty("content").GetString());
            Assert.True(json.GetProperty("content").GetString()!.Length <= 96);
            Assert.Equal("source", json.GetProperty("semantic_token_coordinate_space").GetString());
            var span = Assert.Single(json.GetProperty("content_line_spans").EnumerateArray());
            Assert.Equal(1, span.GetProperty("content_line").GetInt32());
            Assert.Equal(1, span.GetProperty("source_line").GetInt32());
            Assert.True(span.GetProperty("content_start_column").GetInt32() > 1);
            Assert.True(span.GetProperty("source_start_column").GetInt32() <= targetColumn);
            Assert.True(span.GetProperty("source_end_column").GetInt32() >= targetColumn + "TARGET".Length);
            var semanticTokens = json.GetProperty("semantic_tokens").EnumerateArray().ToArray();
            Assert.Contains(semanticTokens, token =>
                token.GetProperty("type").GetString() == "type" &&
                token.GetProperty("start_line").GetInt32() == 1 &&
                token.GetProperty("start_column").GetInt32() == targetColumn &&
                token.GetProperty("end_column").GetInt32() == targetColumn + "TARGET".Length);
            Assert.DoesNotContain(semanticTokens, token => token.GetProperty("type").GetString() == "number");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_Json_AcceptsAbsolutePathWithExplicitDbOutsideProjectRoot()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_absolute_path");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Sample.cs", "csharp", "namespace Demo;\npublic class Svc { }\n");
            var absolutePath = Path.Combine(projectRoot, "src", "Sample.cs");

            var (relativeExitCode, relativeStdout, relativeStderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["src/Sample.cs", "--db", dbPath, "--start", "1", "--end", "2", "--json"],
                _jsonOptions));
            var (absoluteExitCode, absoluteStdout, absoluteStderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                [absolutePath, "--db", dbPath, "--start", "1", "--end", "2", "--json"],
                _jsonOptions));

            using var relativeDocument = ParseJsonOutput(relativeStdout);
            using var absoluteDocument = ParseJsonOutput(absoluteStdout);

            Assert.Equal(CommandExitCodes.Success, relativeExitCode);
            Assert.Equal(CommandExitCodes.Success, absoluteExitCode);
            Assert.Equal(string.Empty, relativeStderr);
            Assert.Equal(string.Empty, absoluteStderr);
            Assert.Equal("src/Sample.cs", relativeDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal(relativeDocument.RootElement.GetProperty("path").GetString(), absoluteDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal(relativeDocument.RootElement.GetProperty("content").GetString(), absoluteDocument.RootElement.GetProperty("content").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_JsonNoSemanticTokensOmitsSemanticPayload_Issue3942()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_no_semantic_tokens_3942");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "namespace Demo;\npublic class Sample { }\npublic class Other { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["src/Sample.cs", "--db", dbPath, "--line", "2", "--context", "1", "--json", "--no-semantic-tokens"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("start_line").GetInt32());
            Assert.Equal(3, json.GetProperty("end_line").GetInt32());
            Assert.Contains("public class Sample", json.GetProperty("content").GetString());
            Assert.False(json.TryGetProperty("semantic_tokens", out _));
            Assert.True(json.TryGetProperty("content_line_spans", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_JsonClampsLongSingleLineContentWithoutFocus()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_long_line_no_focus");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--json", "--max-line-width", "96"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("content_truncated").GetBoolean());
            Assert.DoesNotContain(longLine, json.GetProperty("content").GetString());
            Assert.True(json.GetProperty("content").GetString()!.Length <= 96);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_JsonLeavesLongSingleLineContentUnclampedWhenMaxLineWidthIsZero()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_long_line_no_truncate");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--json", "--max-line-width", "0"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("content_truncated").GetBoolean());
            Assert.Equal(longLine, json.GetProperty("content").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_JsonLeavesLongSingleLineSnippetUnclampedWhenMaxLineWidthIsZero()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_long_line_no_truncate");
        try
        {
            var longLine = new string('a', 320) + " TARGET " + new string('b', 320);
            var sourcePath = Path.Combine(projectRoot, "notes.md");
            File.WriteAllText(sourcePath, longLine);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["TARGET", "--db", dbPath, "--json", "--max-line-width", "0"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();
            Assert.Single(rows);
            Assert.Equal("TARGET", rows[0].GetProperty("query").GetString());
            Assert.Contains(longLine, stdout);
            Assert.DoesNotContain("...(+", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_FocusLineWithoutFocusColumnReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_focus_dep");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--json", "--max-line-width", "96", "--focus-line", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--focus-line requires --focus-column", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_FocusLengthWithoutFocusColumnReturnsSpecificUsageError_Issue3916()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["dist/data.txt", "--start", "1", "--focus-length", "6"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--focus-length requires --focus-column", stderr);
    }

    [Fact]
    public void RunExcerpt_AcceptsPathStartEndLocationArgument_Issue3916()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_location_range");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "line one\nline two\nline three\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["README.md:2-3", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal("README.md", root.GetProperty("path").GetString());
            Assert.Equal(2, root.GetProperty("start_line").GetInt32());
            Assert.Equal(3, root.GetProperty("end_line").GetInt32());
            Assert.Contains("line two", root.GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Contains("line three", root.GetProperty("content").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_FocusLineOutsideReturnedRangeReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_focus_range");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "line one\nline two\nline three");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["README.md", "--db", dbPath, "--start", "2", "--end", "2", "--focus-line", "999", "--focus-column", "1", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--focus-line (999) must be within the returned excerpt range", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_FocusColumnOutsideFocusedLineReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_excerpt_focus_column_range");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--focus-column", "9999", "--max-line-width", "40", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--focus-column (9999) must be within the focused line length", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_JsonClampsLongSingleLineSnippet()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + "target" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/search.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["target", "--db", dbPath, "--path", "dist/search.txt", "--json", "--max-line-width", "96"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(321, json.GetProperty("column").GetInt32());
            Assert.Equal(6, json.GetProperty("length").GetInt32());
            Assert.Equal(longLine.Length, json.GetProperty("original_line_length").GetInt32());
            Assert.True(json.GetProperty("snippet_truncated").GetBoolean());
            Assert.Contains("target", json.GetProperty("snippet").GetString());
            Assert.True(json.GetProperty("snippet").GetString()!.Length <= 96);
            var truncationContext = json.GetProperty("snippet_truncation_context");
            Assert.Equal(1, truncationContext.GetProperty("line_count").GetInt32());
            var charCount = Assert.Single(truncationContext.GetProperty("char_counts").EnumerateArray());
            Assert.True(charCount.GetInt32() > 0);
            Assert.Equal(charCount.GetInt32(), truncationContext.GetProperty("total_chars").GetInt32());
            Assert.Equal("line_width", truncationContext.GetProperty("reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_JsonTreatsZeroMaxLineWidthAsUnclamped()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_long_line_zero_width");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + "target" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/search.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["target", "--db", dbPath, "--path", "dist/search.txt", "--json", "--max-line-width", "0"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("snippet_truncated").GetBoolean());
            Assert.Contains("target", json.GetProperty("snippet").GetString());
            Assert.True(json.GetProperty("snippet").GetString()!.Length > 512);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--exact", "--exact-substring")]
    [InlineData("--exact", "--exact-name")]
    [InlineData("--exact-substring", "--exact-name")]
    public void RunSearch_RejectsCombinedExactFlags(string first, string second)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_combined_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["needle", "--db", dbPath, first, second],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("pass only one of --exact, --exact-substring, --exact-name", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RejectsAllThreeExactFlagsTogether()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_triple_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["needle", "--db", dbPath, "--exact", "--exact-substring", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("pass only one of --exact, --exact-substring, --exact-name", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_WithJsonOutputsCompactSnippetMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "line 1\nline 2\nline 3\nTarget();\nline 5\nline 6");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Target", "--db", dbPath, "--json", "--snippet-lines", "3"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/app.cs", json.GetProperty("path").GetString());
            Assert.Equal(1, json.GetProperty("chunk_start_line").GetInt32());
            Assert.Equal(6, json.GetProperty("chunk_end_line").GetInt32());
            Assert.Equal(3, json.GetProperty("snippet_start_line").GetInt32());
            Assert.Equal(5, json.GetProperty("snippet_end_line").GetInt32());
            Assert.Contains("Target();", json.GetProperty("snippet").GetString());
            Assert.Equal(4, json.GetProperty("match_lines")[0].GetInt32());
            Assert.Equal(1, json.GetProperty("highlights").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringAliasMatchesBackwardCompatibleExact()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "void Run() { }\nvoid RunAsync() { Run(); }\nvoid run() { }\n");

            var exact = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run();", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));
            var alias = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run();", "--db", dbPath, "--json", "--exact-substring"],
                _jsonOptions));

            Assert.Equal(exact.Result, alias.Result);
            Assert.Equal(exact.Stdout, alias.Stdout);
            Assert.Equal(exact.Stderr, alias.Stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringJsonDeduplicatesOverlappingChunkFileLineHits_Issue2812()
    {
        static string BuildChunkContent(int startLine, int endLine)
        {
            return string.Join('\n', Enumerable.Range(startLine, (endLine - startLine) + 1)
                .Select(line => line == 75
                    ? "var CommandText = $\"SELECT 1\";"
                    : $"// filler {line}"));
        }

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_dedup_2812");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/sql.cs",
                    Lang = "csharp",
                    Size = 4096,
                    Lines = 120,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "overlap-chunk-fixture",
                });
                writer.InsertChunks(
                [
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 80,
                        Content = BuildChunkContent(1, 80),
                    },
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 1,
                        StartLine = 71,
                        EndLine = 120,
                        Content = BuildChunkContent(71, 120),
                    },
                ]);
            }

            var (dedupExitCode, dedupStdout, dedupStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--json=array", "--exact-substring", "--snippet-lines", "2"],
                _jsonOptions));
            var (rawExitCode, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CommandText = $", "--db", dbPath, "--json=array", "--exact-substring", "--snippet-lines", "2", "--no-dedup"],
                _jsonOptions));

            using var dedupDocument = ParseJsonOutput(dedupStdout);
            using var rawDocument = ParseJsonOutput(rawStdout);
            var dedupRows = dedupDocument.RootElement.EnumerateArray().ToList();
            var rawRows = rawDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, dedupExitCode);
            Assert.Equal(string.Empty, dedupStderr);
            var dedupRow = Assert.Single(dedupRows);
            Assert.Equal(75, dedupRow.GetProperty("match_lines")[0].GetInt32());

            Assert.Equal(CommandExitCodes.Success, rawExitCode);
            Assert.Equal(string.Empty, rawStderr);
            Assert.Equal(2, rawRows.Count);
            Assert.All(rawRows, row => Assert.Equal(75, row.GetProperty("match_lines")[0].GetInt32()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FtsJsonDeduplicatesOverlappingChunkFileLineHits_Issue2997()
    {
        static string BuildChunkContent(int startLine, int endLine)
        {
            return string.Join('\n', Enumerable.Range(startLine, (endLine - startLine) + 1)
                .Select(line => line == 75
                    ? "var value = JsonDocument.Parse(payload);"
                    : $"// filler {line}"));
        }

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_fts_dedup_2997");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/json.cs",
                    Lang = "csharp",
                    Size = 4096,
                    Lines = 120,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "overlap-fts-chunk-fixture",
                });
                writer.InsertChunks(
                [
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 80,
                        Content = BuildChunkContent(1, 80),
                    },
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 1,
                        StartLine = 71,
                        EndLine = 120,
                        Content = BuildChunkContent(71, 120),
                    },
                ]);
            }

            var dedup = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["JsonDocument.Parse", "--db", dbPath, "--json=array", "--snippet-lines", "2"],
                _jsonOptions));
            var raw = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["JsonDocument.Parse", "--db", dbPath, "--json=array", "--snippet-lines", "2", "--no-dedup"],
                _jsonOptions));
            var count = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["JsonDocument.Parse", "--db", dbPath, "--count"],
                _jsonOptions));
            var rawCount = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["JsonDocument.Parse", "--db", dbPath, "--count", "--no-dedup"],
                _jsonOptions));

            using var dedupDocument = ParseJsonOutput(dedup.Stdout);
            using var rawDocument = ParseJsonOutput(raw.Stdout);
            var dedupRows = dedupDocument.RootElement.EnumerateArray().ToList();
            var rawRows = rawDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, dedup.Result);
            Assert.Equal(string.Empty, dedup.Stderr);
            var dedupRow = Assert.Single(dedupRows);
            Assert.Equal(75, dedupRow.GetProperty("match_lines")[0].GetInt32());

            Assert.Equal(CommandExitCodes.Success, raw.Result);
            Assert.Equal(string.Empty, raw.Stderr);
            Assert.Equal(2, rawRows.Count);
            Assert.All(rawRows, row => Assert.Equal(75, row.GetProperty("match_lines")[0].GetInt32()));

            Assert.Equal(CommandExitCodes.Success, count.Result);
            Assert.Equal("1", count.Stdout.Trim());
            Assert.Equal(CommandExitCodes.Success, rawCount.Result);
            Assert.Equal("2", rawCount.Stdout.Trim());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_QuotedPhrasePreservesFtsPhraseSemantics_Issue2999()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_quoted_phrase_2999");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/match.cs",
                "csharp",
                """
                class Demo
                {
                    void Noise()
                    {
                        var created = new Builder();
                    }

                    void Match()
                    {
                        var matcher = Regex.Match(input, pattern);
                        var regex = new Regex(pattern);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/noise.cs",
                "csharp",
                "var created = new Builder();\nvar matcher = Regex.Match(input, pattern);\n");

            var json = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["\"new Regex\"", "--db", dbPath, "--json=array"],
                _jsonOptions));
            var count = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["\"new Regex\"", "--db", dbPath, "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(json.Stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, json.Result);
            Assert.Equal(string.Empty, json.Stderr);
            Assert.Equal("src/match.cs", row.GetProperty("path").GetString());
            var matchLines = row.GetProperty("match_lines").EnumerateArray().Select(line => line.GetInt32()).ToArray();
            Assert.Equal([11], matchLines);
            var highlight = Assert.Single(row.GetProperty("highlights").EnumerateArray());
            Assert.Equal(11, highlight.GetProperty("line").GetInt32());
            Assert.Equal("Match", row.GetProperty("enclosing_symbol_name").GetString());
            Assert.Equal(8, row.GetProperty("enclosing_symbol_start_line").GetInt32());

            Assert.Equal(CommandExitCodes.Success, count.Result);
            Assert.Equal("1", count.Stdout.Trim());
            Assert.Equal(string.Empty, count.Stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExcludeTestsSkipsPythonConftestFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_conftest_exclude");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "python_conftest_fixture_8bb7c4";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/conftest.py",
                "python",
                $"fixture_token = \"{queryToken}\"\n");

            var withoutExclude = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--count"],
                _jsonOptions));
            var withExclude = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [queryToken, "--db", dbPath, "--exclude-tests", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, withoutExclude.Result);
            Assert.Equal("1", withoutExclude.Stdout.Trim());
            Assert.Equal(string.Empty, withoutExclude.Stderr);

            Assert.Equal(CommandExitCodes.Success, withExclude.Result);
            Assert.Equal("0", withExclude.Stdout.Trim());
            Assert.Equal(string.Empty, withExclude.Stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_WithTypeScriptLangAliasesFiltersTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_typescript_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.ts",
                "typescript",
                """
                export function hit() {
                    return "TypeScript";
                }
                """);

            foreach (var langAlias in new[] { "ts", "tsx", "cts", "mts" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                    ["TypeScript", "--db", dbPath, "--lang", langAlias, "--count"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("1", stdout.Trim());
                Assert.Equal(string.Empty, stderr);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_WithJavaLangAliasFiltersJavaFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_java_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.java",
                "java",
                """
                public class App {
                    String hit() {
                        return "Java";
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Java", "--db", dbPath, "--lang", "jav", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsCSharpVerbatimQualifiedNamesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_csharp_verbatim");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                namespace Demo;

                using @Foo.@Bar;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Foo.Bar", "--db", dbPath, "--path", "src/app.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsJavaUnicodeEscapesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_java_unicode");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.java",
                "java",
                """
                public class \u0046oo
                {
                    void match() {}
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Foo", "--db", dbPath, "--path", "src/App.java", "--json", "--exact-substring", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_ExactTreatsJavaUnicodeEscapesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_exact_java_unicode");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.java",
                "java",
                """
                public class \u0046oo
                {
                    void match() {}
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Foo", "--db", dbPath, "--path", "src/App.java", "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsKotlinBacktickedIdentifiersAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_kotlin_backticks");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.kt",
                "kotlin",
                """
                fun `when`() {}
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["when", "--db", dbPath, "--path", "src/App.kt", "--json", "--exact-substring", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_ExactTreatsCSharpVerbatimQualifiedNamesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_exact_csharp_verbatim");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                namespace Demo;

                using @Foo.@Bar;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Foo.Bar", "--db", dbPath, "--path", "src/app.cs", "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsCSharpGlobalQualifiedNamesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_csharp_global");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/global.cs",
                "csharp",
                """
                namespace Demo;

                public class GlobalQualified
                {
                    public void Match()
                    {
                        var value = global::Foo.Bar;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/plain.cs",
                "csharp",
                """
                namespace Demo;

                public class PlainQualified
                {
                    public void Match()
                    {
                        var value = Foo.Bar;
                    }
                }
                """);

            var (globalExitCode, globalStdout, globalStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Foo.Bar", "--db", dbPath, "--path", "src/global.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));
            using var globalDocument = ParseJsonOutput(globalStdout);

            var (qualifiedExitCode, qualifiedStdout, qualifiedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["global::Foo.Bar", "--db", dbPath, "--path", "src/plain.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));
            using var qualifiedDocument = ParseJsonOutput(qualifiedStdout);

            Assert.Equal(CommandExitCodes.Success, globalExitCode);
            Assert.Equal(string.Empty, globalStderr);
            Assert.Equal(1, globalDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, globalDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, qualifiedExitCode);
            Assert.Equal(string.Empty, qualifiedStderr);
            Assert.Equal(1, qualifiedDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, qualifiedDocument.RootElement.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsCSharpUnicodeEscapedIdentifiersAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_csharp_unicode_escape");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/escaped.cs",
                "csharp",
                "namespace Demo;\n\n"
                + "public class EscapedIdentifiers\n"
                + "{\n"
                + "    public void Match()\n"
                + "    {\n"
                + "        var first = \\u0047lobalName;\n"
                + "        var second = \\U00000047lobalName;\n"
                + "        var keyword = @\\u0063lass.Member;\n"
                + "    }\n"
                + "}\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/plain.cs",
                "csharp",
                """
                namespace Demo;

                public class PlainIdentifiers
                {
                    public void Match()
                    {
                        var first = GlobalName;
                    }
                }
                """);

            var (plainQueryExitCode, plainQueryStdout, plainQueryStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["GlobalName", "--db", dbPath, "--path", "src/escaped.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));
            using var plainQueryDocument = ParseJsonOutput(plainQueryStdout);

            var (escapedQueryExitCode, escapedQueryStdout, escapedQueryStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["\\U00000047lobalName", "--db", dbPath, "--path", "src/plain.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));
            using var escapedQueryDocument = ParseJsonOutput(escapedQueryStdout);

            var (verbatimExitCode, verbatimStdout, verbatimStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["class.Member", "--db", dbPath, "--path", "src/escaped.cs", "--json", "--exact-substring", "--count"],
                _jsonOptions));
            using var verbatimDocument = ParseJsonOutput(verbatimStdout);

            Assert.Equal(CommandExitCodes.Success, plainQueryExitCode);
            Assert.Equal(string.Empty, plainQueryStderr);
            Assert.Equal(1, plainQueryDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, plainQueryDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, escapedQueryExitCode);
            Assert.Equal(string.Empty, escapedQueryStderr);
            Assert.Equal(1, escapedQueryDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, escapedQueryDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, verbatimExitCode);
            Assert.Equal(string.Empty, verbatimStderr);
            Assert.Equal(1, verbatimDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, verbatimDocument.RootElement.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_LiteralSafeSearchKeepsCSharpUnicodeEscapeQueriesRawForFts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_csharp_unicode_escape_fts");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/escaped.cs",
                "csharp",
                "namespace Demo;\n\n"
                + "public class EscapedIdentifiers\n"
                + "{\n"
                + "    public void Match()\n"
                + "    {\n"
                + "        var first = \\u0047lobalName;\n"
                + "    }\n"
                + "}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["\\u0047lobalName", "--db", dbPath, "--path", "src/escaped.cs", "--lang", "csharp", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_TreatsCSharpVerbatimQualifiedNamesAsCanonicalInLiteralSafeSearch()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_csharp_literal_safe_canonical");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                namespace Demo;

                public class CanonicalQualified
                {
                    public void Match()
                    {
                        var first = Foo.Bar;
                        var second = Foo.Bar;
                    }
                }
                """);

            var (globalExitCode, globalStdout, globalStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["global::Foo.Bar", "--db", dbPath, "--path", "src/app.cs", "--lang", "csharp", "--json", "--count"],
                _jsonOptions));
            using var globalDocument = ParseJsonOutput(globalStdout);

            var (verbatimExitCode, verbatimStdout, verbatimStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["@Foo.@Bar", "--db", dbPath, "--path", "src/app.cs", "--lang", "csharp", "--json", "--count"],
                _jsonOptions));
            using var verbatimDocument = ParseJsonOutput(verbatimStdout);

            Assert.Equal(CommandExitCodes.Success, globalExitCode);
            Assert.Equal(string.Empty, globalStderr);
            Assert.Equal(1, globalDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, globalDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, verbatimExitCode);
            Assert.Equal(string.Empty, verbatimStderr);
            Assert.Equal(1, verbatimDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, verbatimDocument.RootElement.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringKeepsNormalizationScopedToCSharp()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_csharp_scope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                namespace Demo;

                using @Foo.@Bar;
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "scripts/run.bat",
                "batch",
                "@Foo.@Bar\r\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Foo.Bar", "--db", dbPath, "--json", "--exact-substring", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringTreatsTsqlQualifiedNamesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_tsql_canonical");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/target.sql",
                "sql",
                """
                CREATE PROCEDURE [sales] . [usp_Target]
                AS
                SELECT 1;
                GO
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "scripts/ignored.bat",
                "batch",
                """
                [sales] . [usp_Target]
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["sales.usp_Target", "--db", dbPath, "--json", "--exact-substring", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExactSubstringHumanSnippetUsesCaseSensitiveFocusLine()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_exact_human_snippet");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "void run() { }\nvoid Run() { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run()", "--db", dbPath, "--exact-substring", "--snippet-lines", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/app.cs:", stdout);
            Assert.Contains("  void Run() { }", stdout);
            Assert.DoesNotContain("  void run() { }", stdout);
            Assert.Contains("(1 results in 1 files)", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RejectsExactNameAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_wrong_exact_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run", "--db", dbPath, "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--exact-substring", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MissingQueryUsageMentionsExactSubstringAlias()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch([], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--exact|--exact-substring", stderr);
    }

    [Fact]
    public void RunSearch_ZeroResultJson_EmitsStructuredPayloadWithFreshnessHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "class App { void Target() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingTarget", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal("MissingTarget", json.GetProperty("query").GetString());
            Assert.Equal(0, json.GetProperty("results").GetArrayLength());
            Assert.Equal(1, json.GetProperty("indexed_file_count").GetInt32());
            Assert.True(json.TryGetProperty("indexed_at", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultJson_EmptyIndexEmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_zero_json_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingTarget", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal("MissingTarget", json.GetProperty("query").GetString());
            Assert.Equal(0, json.GetProperty("results").GetArrayLength());
            Assert.Equal(0, json.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultJson_CountOnlyEmitsFreshnessHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_zero_json_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "class App { void Target() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingTarget", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal("MissingTarget", json.GetProperty("query").GetString());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("indexed_file_count").GetInt32());
            Assert.True(json.TryGetProperty("indexed_at", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultJson_CountOnlyEmptyIndexEmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_zero_json_count_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingTarget", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal("MissingTarget", json.GetProperty("query").GetString());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
            Assert.Equal(0, json.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByName_IsRejectedOutsideHotspots()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_name_reject");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Run() { } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run", "--db", dbPath, "--group-by-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--group-by-name is only supported by 'hotspots'", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupByWithoutCount_IsRejected()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_group_by_reject");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Run() { } }");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Run", "--db", dbPath, "--group-by", "file"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("search --group-by requires --count", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RequiresPathScope()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("requires at least one --path <glob> or explicit --all", stderr);
    }

    [Fact]
    public void RunFind_AllAndPathScopeFailsClosed_Issue3560()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard", "--path", "src/*.cs", "--all"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("find accepts either --path <glob> or --all, not both", stderr);
    }

    [Fact]
    public void RunFind_AllScopeJsonArrayEmitsSingleArray_Issue3896()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_json_array_3896");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/todo.txt",
                "text",
                "TODO: keep this visible\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["TODO", "--db", dbPath, "--all", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            var result = Assert.Single(root.EnumerateArray());
            Assert.Equal("src/todo.txt", result.GetProperty("path").GetString());
            Assert.Equal(1, result.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RegexJsonErrorIsStructured_Issue3896()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_regex_json_3896");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/text.txt", "text", "needle\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["[", "--db", dbPath, "--all", "--regex", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Contains("invalid regular expression", document.RootElement.GetProperty("message").GetString());
            Assert.Equal("invalid_regex", document.RootElement.GetProperty("category").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeCountJsonIncludesScanSummary_Issue3560()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_all_count_json_3560");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.txt",
                "text",
                "alpha\nbeta\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "docs/readme.txt",
                "text",
                "gamma\nalpha\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.Equal(2, json.GetProperty("file_count").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_files").GetInt32());
            Assert.Equal(2, json.GetProperty("files_scanned").GetInt32());
            Assert.Equal(4, json.GetProperty("lines_scanned").GetInt32());
            Assert.False(json.GetProperty("scan_truncated").GetBoolean());
            Assert.False(json.GetProperty("scan_cap_reached").GetBoolean());
            Assert.False(json.GetProperty("scan_timed_out").GetBoolean());
            Assert.Equal(QueryCommandRunner.FindAllCandidateFileLimit, json.GetProperty("candidate_file_limit").GetInt32());
            Assert.Equal(QueryCommandRunner.FindAllLineScanLimit, json.GetProperty("line_scan_limit").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeHumanCountIncludesScanSummary_Issue3560()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_all_count_human_3560");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.txt",
                "text",
                "alpha\nbeta\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Contains("scanned 1/1 candidate files, 2 lines", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_FormatLspCoversFullMatchSpan_Issue3930()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_lsp_span_3930");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/todo.txt", "text", "alpha TODO beta\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["TODO", "--db", dbPath, "--path", "src/todo.txt", "--format", "lsp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var location = Assert.Single(document.RootElement.EnumerateArray());
            var range = location.GetProperty("range");
            Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(6, range.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(0, range.GetProperty("end").GetProperty("line").GetInt32());
            Assert.Equal(10, range.GetProperty("end").GetProperty("character").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeRegexCountJsonIncludesScanSummary_Issue3560()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_all_regex_count_3560");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.txt",
                "text",
                "alpha\nbeta\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "docs/readme.txt",
                "text",
                "gamma\nalpha\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha|gamma", "--db", dbPath, "--all", "--regex", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_files").GetInt32());
            Assert.Equal(2, json.GetProperty("files_scanned").GetInt32());
            Assert.Equal(4, json.GetProperty("lines_scanned").GetInt32());
            Assert.False(json.GetProperty("scan_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CountFindInFiles_LineCapReportsTruncation_Issue3560()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_line_cap_3560");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.txt",
                "text",
                "alpha\nalpha\n");

            using var db = new DbContext(dbPath);
            var reader = new DbReader(db.Connection);
            var counts = reader.CountFindInFiles("alpha", maxLinesScanned: 1);

            Assert.Equal(1, counts.Count);
            Assert.Equal(1, counts.FileCount);
            Assert.Equal(1, counts.Scan.LinesScanned);
            Assert.True(counts.Scan.Truncated);
            Assert.True(counts.Scan.CapReached);
            Assert.False(counts.Scan.TimedOut);
            Assert.Equal("line_scan_limit", counts.Scan.TruncationReason);
            Assert.Equal(1, counts.Scan.LineLimit);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeCountJsonLineCapIsNonAuthoritative_Issue3566()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_all_line_cap_authority_3566");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var content = string.Concat(Enumerable.Repeat("alpha\n", QueryCommandRunner.FindAllLineScanLimit + 1));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/large.txt", "text", content);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(QueryCommandRunner.FindAllLineScanLimit, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("scan_truncated").GetBoolean());
            Assert.True(json.GetProperty("scan_cap_reached").GetBoolean());
            Assert.Equal("line_scan_limit", json.GetProperty("scan_truncation_reason").GetString());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_PathGlobsMatchExpectedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_path_glob");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.py",
                "python",
                """
                def hello():
                    return "hello"
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/app.py",
                "python",
                """
                def hello():
                    return "hello"
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.txt",
                "text",
                "greetings\n");

            var (suffixExitCode, suffixStdout, suffixStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["hello", "--db", dbPath, "--path", "*.py"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, suffixExitCode);
            Assert.Contains("4 matches in 2 files", suffixStderr);
            Assert.Contains("src/app.py", suffixStdout);
            Assert.Contains("tests/app.py", suffixStdout);
            Assert.DoesNotContain("src/app.txt", suffixStdout);

            var (prefixedExitCode, prefixedStdout, prefixedStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["hello", "--db", dbPath, "--path", "src/*.py"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, prefixedExitCode);
            Assert.Contains("2 matches in 1 file", prefixedStderr);
            Assert.Contains("src/app.py", prefixedStdout);
            Assert.DoesNotContain("tests/app.py", prefixedStdout);
            Assert.DoesNotContain("src/app.txt", prefixedStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RejectsUnsupportedFlags()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard", "--path", "src/Auth.cs", "--since", "2099-01-01"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unsupported option for find: --since", stderr);
    }

    [Fact]
    public void RunFind_RejectsInvalidNumericOptions()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["FindUsage", "--path", "src/CodeIndex/Cli/QueryCommandRunner.cs", "--before", "-1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--before requires an integer between 0 and 1000", stderr);
    }

    [Fact]
    public void RunFind_RejectsInvalidLimit()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["FindUsage", "--path", "src/CodeIndex/Cli/QueryCommandRunner.cs", "--limit", "nope"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--limit requires an integer between 1 and 10000", stderr);
    }

    [Theory]
    [InlineData("--limit", "10001", 10_000)]
    [InlineData("--before", "1001", 1_000)]
    [InlineData("--after", "1001", 1_000)]
    public void RunFind_RejectsNumericFlagAboveUpperBound_Issue1503(string flag, string value, int expectedMax)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["FindUsage", "--path", "src/CodeIndex/Cli/QueryCommandRunner.cs", flag, value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"must be less than or equal to {expectedMax}", stderr);
        Assert.Contains($"got '{value}'", stderr);
    }

    [Fact]
    public void RunFind_InvalidSinceFailsClosedInsteadOfRunning()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["guard", "--path", "src/Auth.cs", "--since", "not-a-date"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unsupported option for find: --since", stderr);
    }

    [Fact]
    public void RunFind_AllowsDashedLiteralViaQueryFlag()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_query_flag");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--json appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["--query", "--json", "--db", dbPath, "--path", "README.md", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("scanned 1/1 candidate files, 1 line", stderr);
            Assert.Equal("1", stdout.Trim());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllowsDashedLiteralViaDoubleDash()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_double_dash");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--path appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["--db", dbPath, "--path", "README.md", "--count", "--", "--path"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("scanned 1/1 candidate files, 1 line", stderr);
            Assert.Equal("1", stdout.Trim());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_QueryOptionAcceptsOptionLookingLiteral_Issue923()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue923_search_query_option");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--path appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--query", "--path", "--path", "README.md", "--db", dbPath, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_DoubleDashEscapesSingleOptionLookingQueryToken_Issue923()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue923_search_dashdash");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--path appears here\n--json appears elsewhere\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--", "--path", "--path", "README.md", "--db", dbPath, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_EndOfOptionsAcceptsOptionLookingLiteral()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue799_search_positional_literal");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--open-reports appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--", "--open-reports", "--path", "README.md", "--db", dbPath, "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_RejectsQueryOption()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/CodeIndex/Cli/QueryCommandRunner.cs", "--start", "626", "--query", "src/CodeIndex/Cli/ConsoleUi.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--query is not supported by this command", stderr);
    }

    [Fact]
    public void RunFind_ZeroResultHintDistinguishesPathMatchesFromQueryMiss_Issue1406()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_zero_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "hello world\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["qqq__no_such_token__zzz", "--db", dbPath, "--path", "README.md"],
                _jsonOptions));
            var normalizedStderr = stderr.ToLowerInvariant();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No matches found.", stderr);
            Assert.Contains("--path matched 1 file, but the query did not match their contents", stderr);
            Assert.Contains("try a broader query or check the query syntax", normalizedStderr);
            Assert.DoesNotContain("broadening --path or adding another --path value", normalizedStderr);
            Assert.DoesNotContain("try removing --lang, --path", normalizedStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_ZeroResultHintStillSuggestsBroadeningUnmatchedPath_Issue1406()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_zero_path_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "hello world\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["hello", "--db", dbPath, "--path", "src/**/*.cs"],
                _jsonOptions));
            var normalizedStderr = stderr.ToLowerInvariant();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("broadening --path or adding another --path value", normalizedStderr);
            Assert.DoesNotContain("query did not match", normalizedStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_WithJsonOutputsLineColumnAndSnippet()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Auth.cs",
                "csharp",
                "class Auth\n{\n    void Guard() {}\n    void Next() {}\n}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["guard", "--db", dbPath, "--path", "src/Auth.cs", "--json", "--before", "1", "--after", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/Auth.cs", json.GetProperty("path").GetString());
            Assert.Equal(3, json.GetProperty("line").GetInt32());
            Assert.Equal(10, json.GetProperty("column").GetInt32());
            Assert.Equal(5, json.GetProperty("length").GetInt32());
            Assert.Equal("    void Guard() {}".Length, json.GetProperty("original_line_length").GetInt32());
            Assert.Equal(2, json.GetProperty("start_line").GetInt32());
            Assert.Equal(4, json.GetProperty("end_line").GetInt32());
            Assert.Contains("void Guard()", json.GetProperty("snippet").GetString());
            Assert.Contains("void Next()", json.GetProperty("snippet").GetString());
            var truncationContext = json.GetProperty("snippet_truncation_context");
            Assert.Equal(0, truncationContext.GetProperty("line_count").GetInt32());
            Assert.Empty(truncationContext.GetProperty("char_counts").EnumerateArray());
            Assert.Equal(0, truncationContext.GetProperty("total_chars").GetInt32());
            Assert.False(truncationContext.TryGetProperty("reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_WithJsonReportsSpanMetadataForMultipleMatchesInOneFile_Issue3561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_span_metadata_3561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/search.txt",
                "text",
                "alpha target\nmiddle\nsecond target here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["target", "--db", dbPath, "--path", "src/search.txt", "--json"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("src/search.txt", row.GetProperty("path").GetString());
                Assert.Equal(6, row.GetProperty("length").GetInt32());
                Assert.False(row.GetProperty("snippet_truncated").GetBoolean());
                var truncationContext = row.GetProperty("snippet_truncation_context");
                Assert.Equal(0, truncationContext.GetProperty("line_count").GetInt32());
                Assert.Empty(truncationContext.GetProperty("char_counts").EnumerateArray());
                Assert.Equal(0, truncationContext.GetProperty("total_chars").GetInt32());
                Assert.False(truncationContext.TryGetProperty("reason", out _));
            });
            Assert.Equal(1, rows[0].GetProperty("line").GetInt32());
            Assert.Equal(7, rows[0].GetProperty("column").GetInt32());
            Assert.Equal("alpha target".Length, rows[0].GetProperty("original_line_length").GetInt32());
            Assert.Equal(3, rows[1].GetProperty("line").GetInt32());
            Assert.Equal(8, rows[1].GetProperty("column").GetInt32());
            Assert.Equal("second target here".Length, rows[1].GetProperty("original_line_length").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_SnippetLinesControlsMatchContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_snippet_lines");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Auth.cs",
                "csharp",
                "line one\nline two\nvoid Guard() {}\nline four\nline five\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Guard", "--db", dbPath, "--path", "src/Auth.cs", "--snippet-lines", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("line one", stdout);
            Assert.Contains("line five", stdout);
            Assert.Contains("1 matches in 1 file", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_FocusLineAndColumnRestrictMatch()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_focus");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Auth.cs",
                "csharp",
                "target here\nno match\nother target\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["target", "--db", dbPath, "--path", "src/Auth.cs", "--focus-line", "3", "--focus-column", "8"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/Auth.cs:3:7", stdout);
            Assert.DoesNotContain("src/Auth.cs:1:1", stdout);
            Assert.Contains("1 matches in 1 file", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RegexMatchesAnchors()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_regex");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Auth.cs", "csharp", "alpha\nGuard()\nnot Guard()\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["^Guard", "--regex", "--db", dbPath, "--path", "src/Auth.cs"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/Auth.cs:2:1", stdout);
            Assert.DoesNotContain("src/Auth.cs:3:5", stdout);
            Assert.Contains("1 matches in 1 file", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RegexMatcherUsesSharedTimeoutAndCultureInvariant_Issue3559()
    {
        var method = typeof(DbReader).GetMethod("CreateFindRegexMatcher", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var defaultTimeout = typeof(CodeIndex.Indexer.SymbolExtractor).Assembly
            .GetType("CodeIndex.Indexer.BoundedRegex")!
            .GetField("DefaultMatchTimeout", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

        var insensitive = Assert.IsType<Regex>(method.Invoke(null, ["needle", false]));
        Assert.Equal(defaultTimeout, insensitive.MatchTimeout);
        Assert.True((insensitive.Options & RegexOptions.IgnoreCase) != 0);
        Assert.True((insensitive.Options & RegexOptions.CultureInvariant) != 0);

        var exact = Assert.IsType<Regex>(method.Invoke(null, ["needle", true]));
        Assert.Equal(defaultTimeout, exact.MatchTimeout);
        Assert.False((exact.Options & RegexOptions.IgnoreCase) != 0);
        Assert.True((exact.Options & RegexOptions.CultureInvariant) != 0);
    }

    [Fact]
    public void RunFind_RegexPathologicalInputReturnsTimeoutJson_Issue4058()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_regex_timeout_4058");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Auth.cs",
                "csharp",
                new string('a', 4096) + "!\n");

            try
            {
                DbReader.FindRegexMatchTimeoutForTesting = TimeSpan.FromMilliseconds(1);

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                    ["^(a+)+$", "--regex", "--db", dbPath, "--path", "src/Auth.cs", "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal("E014_REGEX_MATCH_TIMEOUT", json.GetProperty("error_code").GetString());
                Assert.Equal("regex_timeout", json.GetProperty("category").GetString());
            }
            finally
            {
                DbReader.FindRegexMatchTimeoutForTesting = null;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_RegexTimeoutWritesRuntimeErrorJsonMetadata_Issue3559()
    {
        var timeout = new RegexMatchTimeoutException("aaaaaaaaaaaaaaaa!", "^(a+)+$", TimeSpan.FromMilliseconds(25));

        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            QueryCommandRunner.WriteFindRegexTimeoutError(timeout, _jsonOptions, json: true));

        Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("E014_REGEX_MATCH_TIMEOUT", json.GetProperty("error_code").GetString());
        Assert.Equal("regex_timeout", json.GetProperty("category").GetString());
        Assert.Contains("timed out", json.GetProperty("message").GetString());
        Assert.DoesNotContain("invalid regular expression", json.GetProperty("message").GetString());
        Assert.Contains("--regex", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void RunFind_CountOnlyRegexAndFocusUseSameMatchingSemantics()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_count_regex_focus");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Auth.cs", "csharp", "Guard()\nnot Guard()\nGuardAgain()\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["^Guard", "--regex", "--db", dbPath, "--path", "src/Auth.cs", "--focus-line", "3", "--focus-column", "5", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_CountOnlyJsonUsesFilesAsCanonicalCountAndKeepsDeprecatedAlias_Issue1423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Auth.cs",
                "csharp",
                "guard one\nline two\nguard three\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["guard", "--db", dbPath, "--path", "src/Auth.cs", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
            Assert.Equal(json.GetProperty("files").GetInt32(), json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_StreamsOverlappingChunksOnce_Issue3099()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_stream_chunks");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/overlap.txt",
                    Lang = "text",
                    Size = "first\nalpha one\nshared alpha\nalpha after\nlast".Length,
                    Lines = 5,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "overlapping-chunks",
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 3,
                        Content = "first\nalpha one\nshared alpha",
                    },
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 1,
                        StartLine = 3,
                        EndLine = 5,
                        Content = "shared alpha\nalpha after\nlast",
                    }
                ]);
            }

            var (findExitCode, findStdout, findStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["shared", "--db", dbPath, "--path", "src/overlap.txt", "--json", "--before", "1", "--after", "1"],
                _jsonOptions));
            using var findDocument = ParseJsonOutput(findStdout);
            var findJson = findDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, findExitCode);
            Assert.Equal(string.Empty, findStderr);
            Assert.Equal(3, findJson.GetProperty("line").GetInt32());
            Assert.Equal(1, findJson.GetProperty("column").GetInt32());
            Assert.Equal(2, findJson.GetProperty("start_line").GetInt32());
            Assert.Equal(4, findJson.GetProperty("end_line").GetInt32());
            Assert.Contains("alpha one", findJson.GetProperty("snippet").GetString());
            Assert.Contains("alpha after", findJson.GetProperty("snippet").GetString());

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--path", "src/overlap.txt", "--json", "--count"],
                _jsonOptions));
            using var countDocument = ParseJsonOutput(countStdout);
            var countJson = countDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(3, countJson.GetProperty("count").GetInt32());
            Assert.Equal(1, countJson.GetProperty("files").GetInt32());
            Assert.Equal(1, countJson.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_ExactTreatsCSharpGlobalQualifiedNamesAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_exact_csharp_global");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/global.cs",
                "csharp",
                """
                namespace Demo;

                public class GlobalQualified
                {
                    public void Match()
                    {
                        var value = global::Foo.Bar;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Foo.Bar", "--db", dbPath, "--path", "src/global.cs", "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_ExactTreatsKotlinBacktickedIdentifiersAsCanonical()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_exact_kotlin_backticks");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.kt",
                "kotlin",
                """
                fun `when`() {
                    println("ok")
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["when", "--db", dbPath, "--path", "src/App.kt", "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_CountOnlyJsonCountsEverySameLineOccurrence()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_multi_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "alpha alpha alpha\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--path", "src/Sample.cs", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_CountOnlyJsonCountsOverlappingOccurrences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_find_overlap_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "// banana\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["ana", "--db", dbPath, "--path", "src/Sample.cs", "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_RequiresStartLine()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/app.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: excerpt requires --start <line>", stderr);
    }

    [Fact]
    public void RunExcerpt_RejectsStartGreaterThanEnd()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/app.cs", "--start", "5", "--end", "3"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--start (5) must be less than or equal to --end (3)", stderr);
    }

    [Fact]
    public void RunExcerpt_AcceptsMcpStyleStartAndEndLineAliases()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/app.cs", "--start-line", "5", "--end-line", "3"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--start (5) must be less than or equal to --end (3)", stderr);
        Assert.DoesNotContain("unsupported option", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunFind_WhitespaceQueryReturnsDistinctUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["   ", "--path", "src/**/*.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: find query cannot be empty or whitespace-only", stderr);
        Assert.DoesNotContain("find requires a query argument", stderr);
    }

    [Fact]
    public void RunFind_MissingQueryStillReportsRequiresArgument()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["--path", "src/**/*.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: find requires a query argument", stderr);
        Assert.DoesNotContain("query cannot be empty or whitespace-only", stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunFind_QueryTooLongReturnsUsageError_Issue3100(bool countOnly)
    {
        var args = new List<string>
        {
            new('x', QueryLimits.MaxQueryLength + 1),
            "--path",
            "src/**/*.cs",
        };
        if (countOnly)
            args.Add("--count");

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            [.. args],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {QueryLimits.FormatQueryTooLongError()}", stderr);
        Assert.Contains("Hint: Shorten the find text", stderr);
        Assert.Contains("Usage: cdidx find", stderr);
    }

    [Fact]
    public void RunSearch_ZeroResultsHonorsStaleAfterEnvironment()
    {
        var prior = Environment.GetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable);
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search_stale_after_env");
        try
        {
            Environment.SetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable, "1m");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE files SET indexed_at = @indexedAt";
                cmd.Parameters.AddWithValue("@indexedAt", DateTime.UtcNow.AddMinutes(-5).ToString("O"));
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingSymbol", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("threshold: 1m", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable, prior);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MatchOriginAndResultKindFiltersKeepCodeCallSites_Issues3748And3749()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_origin_kind_filters");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/code.cs",
                "csharp",
                """
                public class Demo
                {
                    void Run()
                    {
                        Directory.Delete(path);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.cs", "csharp", "// Directory.Delete(path) only in a comment\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Directory.Delete", "--db", dbPath, "--exact-substring", "--match-origin", "code", "--result-kind", "call_site", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.Contains("code", row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("call_site", row.GetProperty("result_kinds").EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_OriginAliasAndExcludeOriginFilterExactSubstring_Issue3680()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_origin_exclude_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/code.cs",
                "csharp",
                """
                public class Demo
                {
                    void Run()
                    {
                        Directory.Delete(path);
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/comment.cs", "csharp", "// Directory.Delete(path) only in a comment\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/string.cs", "csharp", "var text = \"Directory.Delete(path)\";\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Directory.Delete", "--db", dbPath, "--exact-substring", "--origin", "code", "--exclude-origin", "comment,string_literal", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/code.cs", row.GetProperty("path").GetString());
            Assert.Contains("code", row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain("comment", row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain("string_literal", row.GetProperty("match_origins").EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_CountByOriginReturnsAggregatedJson_Issue3729()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_count_by_origin");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/mixed.cs",
                "csharp",
                """
                public class Demo { void Run() { OriginCountNeedle(); } }
                // OriginCountNeedle in comment
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["OriginCountNeedle", "--db", dbPath, "--exact-substring", "--count-by", "origin", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal("count_by", document.RootElement.GetProperty("mode").GetString());
            var groups = document.RootElement.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Contains(groups, group => group.GetProperty("key").GetString() == "code");
            Assert.Contains(groups, group => group.GetProperty("key").GetString() == "comment");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_CountByFileLimitCapsReturnedGroups_Issue4119()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_count_by_file_limit_4119");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/alpha.cs", "csharp", "public class Alpha { void Run() { CountByLimitNeedle(); } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/beta.cs", "csharp", "public class Beta { void Run() { CountByLimitNeedle(); } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/gamma.cs", "csharp", "public class Gamma { void Run() { CountByLimitNeedle(); } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CountByLimitNeedle", "--db", dbPath, "--exact-substring", "--count-by", "file", "--json", "--limit", "2"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var groups = root.GetProperty("groups").EnumerateArray().ToArray();

            Assert.Equal("count_by", root.GetProperty("mode").GetString());
            Assert.Equal(3, root.GetProperty("count").GetInt32());
            Assert.Equal(3, root.GetProperty("files").GetInt32());
            Assert.Equal(2, root.GetProperty("returned_groups").GetInt32());
            Assert.Equal(3, root.GetProperty("total_groups").GetInt32());
            Assert.True(root.GetProperty("groups_truncated").GetBoolean());
            Assert.Equal(2, root.GetProperty("group_limit").GetInt32());
            Assert.Equal(2, groups.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GroupedFormatBoundsRepresentativeMatches_Issue3788()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_grouped_output");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/a.cs",
                    Lang = "csharp",
                    Size = "GroupedNeedle();\nGroupedNeedle();\nGroupedNeedle();\n".Length,
                    Lines = 3,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "grouped-output-a",
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = "GroupedNeedle();",
                    },
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 1,
                        StartLine = 2,
                        EndLine = 2,
                        Content = "GroupedNeedle();",
                    },
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 2,
                        StartLine = 3,
                        EndLine = 3,
                        Content = "GroupedNeedle();",
                    }
                ]);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/b.cs", "csharp", "GroupedNeedle();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["GroupedNeedle", "--db", dbPath, "--exact-substring", "--format", "grouped", "--per-file-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(2, document.RootElement.GetProperty("returned_groups").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("per_file_limit").GetInt32());
            var groups = document.RootElement.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal("src/a.cs", groups[0].GetProperty("path").GetString());
            Assert.Equal(3, groups[0].GetProperty("count").GetInt32());
            Assert.Equal("src/b.cs", groups[1].GetProperty("path").GetString());
            foreach (var group in groups)
                Assert.True(group.GetProperty("results").EnumerateArray().Count() <= 1);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_SearchFieldsResultsOnlyOmitsDoneRecord_Issue3728()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_projection_results_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/projected.cs", "csharp", "public class Demo { void Run() { ProjectedNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["ProjectedNeedle", "--db", dbPath, "--exact-substring", "--search-fields", "path,line,symbol,origin", "--results-only"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var line = Assert.Single(lines);
            using var document = JsonDocument.Parse(line);
            Assert.Equal("src/projected.cs", document.RootElement.GetProperty("path").GetString());
            Assert.True(document.RootElement.TryGetProperty("line", out _));
            Assert.False(document.RootElement.TryGetProperty("done", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_SearchFieldsJsonArrayHonorsMaxJsonBytes_Issue4119()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_projection_array_byte_cap_4119");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/projected.cs", "csharp", "public class Demo { void Run() { ProjectedNeedle(); } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["ProjectedNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--search-fields", "path,line", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("projected search result array JSON output", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_FirstPerFileSampleAndMaxJsonBytesInterruptsNdjson_Issue3768()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_sampling_byte_cap");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a.cs", "csharp", "SampleNeedle();\nSampleNeedle();\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/b.cs", "csharp", "SampleNeedle();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["SampleNeedle", "--db", dbPath, "--exact-substring", "--first-per-file", "--sample", "2", "--json=ndjson", "--max-json-bytes", "2"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var done = Assert.Single(lines);
            using var document = JsonDocument.Parse(done);
            Assert.False(document.RootElement.GetProperty("done").GetBoolean());
            Assert.True(document.RootElement.GetProperty("interrupted").GetBoolean());
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ZeroResultsSuggestsDirectoryGlobPath_Issue3814()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_path_glob_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/CodeIndex/Foo.cs", "csharp", "public class Foo {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingNeedle", "--db", dbPath, "--path", "src/CodeIndex", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var hint = document.RootElement.GetProperty("path_filter_hint");
            Assert.Equal("path_filter_looks_like_directory", hint.GetProperty("reason").GetString());
            Assert.Contains("src/CodeIndex/**", hint.GetProperty("suggested_action").GetString());

            var (exactFileExitCode, exactFileStdout, exactFileStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingNeedle", "--db", dbPath, "--path", "src/CodeIndex/Foo.cs", "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, exactFileExitCode);
            Assert.Equal(string.Empty, exactFileStderr);
            using var exactFileDocument = ParseJsonOutput(exactFileStdout);
            Assert.False(exactFileDocument.RootElement.TryGetProperty("path_filter_hint", out _));

            var (alreadyGlobbedExitCode, alreadyGlobbedStdout, alreadyGlobbedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingNeedle", "--db", dbPath, "--path", "src/CodeIndex/**", "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, alreadyGlobbedExitCode);
            Assert.Equal(string.Empty, alreadyGlobbedStderr);
            using var alreadyGlobbedDocument = ParseJsonOutput(alreadyGlobbedStdout);
            Assert.False(alreadyGlobbedDocument.RootElement.TryGetProperty("path_filter_hint", out _));

            var (nonexistentExitCode, nonexistentStdout, nonexistentStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingNeedle", "--db", dbPath, "--path", "src/Missing", "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, nonexistentExitCode);
            Assert.Equal(string.Empty, nonexistentStderr);
            using var nonexistentDocument = ParseJsonOutput(nonexistentStdout);
            Assert.False(nonexistentDocument.RootElement.TryGetProperty("path_filter_hint", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_MultiTokenCodePhraseSuggestsExactSubstring_Issue3664()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_code_phrase_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "throw new Exception();\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["throw new Exception", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("--exact-substring", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_NextStepsAddsStructuredJsonHintsForOneManyAndZeroResults_Issue3802()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_next_steps");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/next.cs",
                "csharp",
                """
                public class Demo
                {
                    void Run()
                    {
                        NextStepNeedle();
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["NextStepNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--next-steps"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            var nextSteps = row.GetProperty("next_steps").EnumerateArray().ToArray();
            Assert.Contains(nextSteps, step => step.GetProperty("command").GetString()!.Contains("cdidx inspect", StringComparison.Ordinal));
            Assert.Contains(nextSteps, step => step.GetProperty("command").GetString()!.Contains("cdidx excerpt", StringComparison.Ordinal));

            for (var i = 0; i < 11; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/many{i}.cs",
                    "csharp",
                    $"public class Many{i} {{ void Run() {{ ManyNextStepNeedle(); }} }}\n");
            }

            var (manyExitCode, manyStdout, manyStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["ManyNextStepNeedle", "--db", dbPath, "--exact-substring", "--json=array", "--next-steps", "--limit", "20"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, manyExitCode);
            Assert.Equal(string.Empty, manyStderr);
            using var manyDocument = ParseJsonOutput(manyStdout);
            var manyRows = manyDocument.RootElement.EnumerateArray().ToArray();
            Assert.Equal(11, manyRows.Length);
            Assert.Equal(10, manyRows.Count(result => result.TryGetProperty("next_steps", out _)));
            Assert.All(manyRows.Take(10), result => Assert.True(result.GetProperty("next_steps_truncated").GetBoolean()));
            Assert.False(manyRows[10].TryGetProperty("next_steps", out _));

            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["MissingNextStepNeedle", "--db", dbPath, "--json", "--next-steps"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, zeroExitCode);
            Assert.Equal(string.Empty, zeroStderr);
            using var zeroDocument = ParseJsonOutput(zeroStdout);
            Assert.False(zeroDocument.RootElement.TryGetProperty("next_steps", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string QuoteReplayShellArgForAssertion(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '='))
            return arg;
        return "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed class IssueDraftRepositoryLabelsHandler : HttpMessageHandler
    {
        internal List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            var json = request.RequestUri.AbsolutePath.EndsWith("/labels", StringComparison.Ordinal)
                ? """
                  [
                    {"name":"security"},
                    {"name":"bug"}
                  ]
                  """
                : "[]";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
