using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_FormatCompactEmitsFileLineOnly_Issue1642()
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
            Assert.Equal("src/app.cs", row.GetProperty("file").GetString());
            Assert.True(row.GetProperty("line").GetInt32() > 0);
            Assert.False(row.TryGetProperty("snippet", out _));
            Assert.False(row.TryGetProperty("name", out _));
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
            Assert.Equal("file,line,column,label", lines[0]);
            Assert.Contains("src/app.cs", lines[1]);
            Assert.Contains("search match: Authenticate", lines[1]);
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
                ["missing-token", "--db", dbPath, "--path", "src/**", "--lang", "csharp", "--limit", "7", "--json"],
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
        }
        finally
        {
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
            var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", longLine);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", "1", "--end", "1", "--json", "--max-line-width", "96", "--focus-column", (longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1).ToString(), "--focus-length", "6"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("content_truncated").GetBoolean());
            Assert.DoesNotContain(longLine, json.GetProperty("content").GetString());
            Assert.Contains("TARGET", json.GetProperty("content").GetString());
            Assert.True(json.GetProperty("content").GetString()!.Length <= 96);
            var semanticTokens = json.GetProperty("semantic_tokens").EnumerateArray().ToArray();
            Assert.Contains(semanticTokens, token =>
                token.GetProperty("type").GetString() == "variable" &&
                token.GetProperty("start_line").GetInt32() == 1 &&
                token.GetProperty("start_column").GetInt32() > 0);
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
            Assert.Contains("--focus-line and --focus-length require --focus-column", stderr);
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
            Assert.True(json.GetProperty("snippet_truncated").GetBoolean());
            Assert.Contains("target", json.GetProperty("snippet").GetString());
            Assert.True(json.GetProperty("snippet").GetString()!.Length <= 96);
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
    public void RunSearch_GroupBy_IsRejectedOutsideHotspots()
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
            Assert.Contains("--group-by is only supported by 'hotspots'", stderr);
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
        Assert.Contains("requires at least one --path", stderr);
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
            Assert.Equal(string.Empty, stderr);
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
            Assert.Equal(string.Empty, stderr);
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
            Assert.Equal(2, json.GetProperty("start_line").GetInt32());
            Assert.Equal(4, json.GetProperty("end_line").GetInt32());
            Assert.Contains("void Guard()", json.GetProperty("snippet").GetString());
            Assert.Contains("void Next()", json.GetProperty("snippet").GetString());
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
}
