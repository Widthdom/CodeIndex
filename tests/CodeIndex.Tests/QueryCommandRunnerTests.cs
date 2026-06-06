using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for query-style CLI command execution.
/// クエリ系CLIコマンド実行のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class QueryCommandRunnerTests
{
    private static readonly string[] ChildCliEnvironmentVariablesToRemove =
    [
        QueryCommandRunner.DefaultLimitEnvironmentVariable,
        QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
        QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable,
        QueryCommandRunner.StaleAfterEnvironmentVariable,
        IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable,
        IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable,
        DbPathResolver.DataDirEnvironmentVariable,
        CdidxConfigFile.DisableEnvVar,
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
    ];

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ParseArgs_ParsesFiltersFlagsAndAcceptsMaxSnippetLines()
    {
        var options = QueryCommandRunner.ParseArgs(
        [
            "RunSearch",
            "--db", "/tmp/query.db",
            "--limit", "7",
            "--lang", "csharp",
            "--kind", "function",
            "--fts",
            "--body",
            "--path", "src/**",
            "--exclude-path", "tests/**",
            "--exclude-path", "docs/**",
            "--exclude-tests",
            "--start", "12",
            "--end", "18",
            "--before", "2",
            "--after", "4",
            "--focus-line", "9",
            "--focus-column", "33",
            "--focus-length", "6",
            "--snippet-lines", $"{SearchSnippetFormatter.MaxSnippetLines}",
            "--snippet-focus", "proximity",
            "--max-line-width", "77",
            "--profile",
            "--verbose",
            "--slow-query-ms", "500",
            "--no-visibility-rank",
        ], jsonDefault: true);

        Assert.Equal("/tmp/query.db", options.DbPath);
        Assert.True(options.Json);
        Assert.Equal(7, options.Limit);
        Assert.Equal("csharp", options.Lang);
        Assert.Equal("function", options.Kind);
        Assert.Equal("RunSearch", options.Query);
        Assert.True(options.RawFts);
        Assert.True(options.IncludeBody);
        Assert.Equal(new[] { "src/**" }, options.PathPatterns);
        Assert.Equal(["tests/**", "docs/**"], options.ExcludePaths);
        Assert.True(options.ExcludeTests);
        Assert.Equal(12, options.StartLine);
        Assert.Equal(18, options.EndLine);
        Assert.Equal(2, options.ContextBefore);
        Assert.Equal(4, options.ContextAfter);
        Assert.Equal(9, options.FocusLine);
        Assert.Equal(33, options.FocusColumn);
        Assert.Equal(6, options.FocusLength);
        Assert.Equal(SearchSnippetFormatter.MaxSnippetLines, options.SnippetLines);
        Assert.Equal(SearchSnippetFocusMode.Proximity, options.SnippetFocus);
        Assert.Equal(77, options.MaxLineWidth);
        Assert.True(options.Profile);
        Assert.True(options.Verbose);
        Assert.Equal(500, options.SlowQueryMs);
        Assert.True(options.NoVisibilityRank);
    }

    [Fact]
    public void ParseArgs_AllowsZeroMaxLineWidth()
    {
        var options = QueryCommandRunner.ParseArgs(["RunSearch", "--max-line-width", "0"], jsonDefault: false, allowNamedQuery: true);

        Assert.Equal("RunSearch", options.Query);
        Assert.Equal(0, options.MaxLineWidth);
    }

    [Fact]
    public void ParseArgs_InvalidPathGlob_FlattensControlCharacters_Issue3092()
    {
        var value = "src/[bad\nforged\tvalue";

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", "--path", value],
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--path 'src/[bad forged value' is not a valid glob", options.ParseError);
        Assert.DoesNotContain(value, options.ParseError);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("compact")]
    [InlineData("csv")]
    [InlineData("tsv")]
    public void ParseArgs_AcceptsLightweightOutputFormats(string format)
    {
        var options = QueryCommandRunner.ParseArgs(["RunSearch", "--format", format], jsonDefault: false, allowNamedQuery: true);

        Assert.True(options.Json);
        Assert.Equal(format, options.OutputFormat);
        Assert.Null(options.ParseError);
    }



    [Fact]
    public void ParseArgs_UsesNumericDefaultEnvironmentVariables()
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "42");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "6");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "120");

        var options = QueryCommandRunner.ParseArgs(["RunSearch"], jsonDefault: false, allowNamedQuery: true);

        Assert.Equal(42, options.Limit);
        Assert.Equal(6, options.SnippetLines);
        Assert.Equal(120, options.MaxLineWidth);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_CliNumericFlagsOverrideDefaultEnvironmentVariables()
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "42");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "6");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "120");

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", "--limit", "7", "--snippet-lines", "3", "--max-line-width", "80"],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Equal(7, options.Limit);
        Assert.Equal(3, options.SnippetLines);
        Assert.Equal(80, options.MaxLineWidth);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_InvalidNumericDefaultEnvironmentVariableReportsParseError()
    {
        using var env = EnvironmentVariableScope.Capture(QueryCommandRunner.DefaultLimitEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "0");

        var options = QueryCommandRunner.ParseArgs(["RunSearch"], jsonDefault: false, allowNamedQuery: true);

        Assert.Contains(QueryCommandRunner.DefaultLimitEnvironmentVariable, options.ParseError);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("snippet-lines")]
    [InlineData("max-line-width")]
    public void ParseArgs_CliNumericFlagsOverrideInvalidDefaultEnvironmentVariables(string option)
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "-1");

        var args = option switch
        {
            "limit" => new[] { "RunSearch", "--limit", "7", "--snippet-lines", "3", "--max-line-width", "80" },
            "snippet-lines" => new[] { "RunSearch", "--limit", "7", "--snippet-lines", "3", "--max-line-width", "80" },
            _ => new[] { "RunSearch", "--limit", "7", "--snippet-lines", "3", "--max-line-width", "80" },
        };

        var options = QueryCommandRunner.ParseArgs(args, jsonDefault: false, allowNamedQuery: true);

        Assert.Equal(7, options.Limit);
        Assert.Equal(3, options.SnippetLines);
        Assert.Equal(80, options.MaxLineWidth);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunLanguages_IgnoresInvalidNumericDefaultEnvironmentVariables()
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "-1");

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages([], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains("Language", stdout);
        Assert.DoesNotContain(QueryCommandRunner.DefaultLimitEnvironmentVariable, stderr);
    }

    [Fact]
    public void ParseArgs_ScopesNumericDefaultValidationByOption()
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "0");
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "-1");

        var limitOnly = QueryCommandRunner.ParseArgs(
            [],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        Assert.Contains(QueryCommandRunner.DefaultLimitEnvironmentVariable, limitOnly.ParseError);
        Assert.DoesNotContain(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, limitOnly.ParseError);
        Assert.DoesNotContain(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, limitOnly.ParseError);

        var maxLineWidthOnly = QueryCommandRunner.ParseArgs(
            [],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false);
        Assert.Contains(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, maxLineWidthOnly.ParseError);
        Assert.DoesNotContain(QueryCommandRunner.DefaultLimitEnvironmentVariable, maxLineWidthOnly.ParseError);
        Assert.DoesNotContain(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, maxLineWidthOnly.ParseError);

        var none = QueryCommandRunner.ParseArgs(
            [],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        Assert.Null(none.ParseError);
    }

    [Fact]
    public void ParseArgs_ProjectFilterExpandsSolutionProjectToPathGlob_Issue1707()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_filter");
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

            Environment.CurrentDirectory = projectRoot;
            var options = QueryCommandRunner.ParseArgs(["Auth", "--project", "App"], jsonDefault: false, allowNamedQuery: true);

            Assert.Equal("Auth", options.Query);
            Assert.Equal(["App"], options.ProjectFilters);
            Assert.Equal(["src/App/*"], options.PathPatterns);
            Assert.Null(options.ParseError);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_ProjectFilterUsesIndexedProjectRootForExplicitDb_Issue3189()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_filter_explicit_db");
        var otherRoot = TestProjectHelper.CreateTempProject("cdidx_solution_filter_other_cwd");
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
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            Environment.CurrentDirectory = otherRoot;
            var options = QueryCommandRunner.ParseArgs(
                ["Auth", "--db", dbPath, "--project", "App"],
                jsonDefault: false,
                allowNamedQuery: true);

            Assert.Equal("Auth", options.Query);
            Assert.Equal(["App"], options.ProjectFilters);
            Assert.Equal(["src/App/*"], options.PathPatterns);
            Assert.Null(options.ParseError);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(otherRoot);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_LspFormatUsesIndexedProjectRootForExplicitDb_Issue3151()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_explicit_db_root");
        var otherRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_other_cwd");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App/Service.cs",
                "csharp",
                """
                public class Service
                {
                    public void Run() { }
                }
                """);

            Environment.CurrentDirectory = otherRoot;
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Service", "--db", dbPath, "--format", "lsp", "--exact-name", "--lang", "csharp", "--kind", "class"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var location = Assert.Single(document.RootElement.EnumerateArray());
            var expectedUri = new Uri(Path.Combine(projectRoot, "src", "App", "Service.cs")).AbsoluteUri;
            Assert.Equal(expectedUri, location.GetProperty("uri").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(otherRoot);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("30m", 30 * 60)]
    [InlineData("2h", 2 * 60 * 60)]
    [InlineData("7d", 7 * 24 * 60 * 60)]
    [InlineData(QueryCommandRunner.MaxStaleAfterDisplay, 30 * 24 * 60 * 60)]
    public void TryParseStaleAfter_AcceptsCompactDurations(string value, int expectedSeconds)
    {
        Assert.True(QueryCommandRunner.TryParseStaleAfter(value, out var staleAfter, out var error));
        Assert.Null(error);
        Assert.Equal(expectedSeconds, (int)staleAfter.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24")]
    [InlineData("0h")]
    [InlineData("-1h")]
    [InlineData("abc")]
    public void TryParseStaleAfter_RejectsInvalidDurations(string value)
    {
        Assert.False(QueryCommandRunner.TryParseStaleAfter(value, out _, out var error));
        Assert.Contains("stale-after", error);
    }

    [Theory]
    [InlineData("31d")]
    [InlineData("721h")]
    public void TryParseStaleAfter_RejectsDurationsAboveMaximum_Issue3176(string value)
    {
        Assert.False(QueryCommandRunner.TryParseStaleAfter(value, out _, out var error));
        Assert.Contains(QueryCommandRunner.MaxStaleAfterDisplay, error);
    }

    [Fact]
    public void TryParseStaleAfter_MaxDuration_FlattensControlCharacters_Issue3092()
    {
        var value = "31d\n\t";

        Assert.False(QueryCommandRunner.TryParseStaleAfter(value, out _, out var error));
        Assert.Contains("stale-after value '31d  '", error);
        Assert.DoesNotContain(value, error);
    }

    [Fact]
    public void ParseArgs_StatusStaleAfterStoresDuration()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--check", "--stale-after=2h"],
            jsonDefault: false,
            allowStatusCheck: true);

        Assert.True(options.CheckWorkspace);
        Assert.Equal(TimeSpan.FromHours(2), options.StaleAfter);
    }

    [Fact]
    public void RunStatusConfig_PrintsEffectiveConfigWithoutOpeningDb()
    {
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + QueryCommandRunner.DefaultLimitEnvironmentVariable);
        Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, "33");
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_missing_{Guid.NewGuid():N}.db");
        var parsed = QueryCommandRunner.ParseArgs(["--config", "--db", missingDb, "--json"], jsonDefault: false, allowStatusCheck: true);
        Assert.True(parsed.StatusConfig);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--config", "--db", missingDb, "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var effective = document.RootElement.GetProperty("effective_config");
        Assert.Equal(missingDb, effective.GetProperty("db_path").GetProperty("value").GetString());
        Assert.Equal("flag", effective.GetProperty("db_path").GetProperty("source").GetString());
        Assert.Equal(33, effective.GetProperty("limit").GetProperty("value").GetInt32());
        Assert.Equal($"env:{QueryCommandRunner.DefaultLimitEnvironmentVariable}", effective.GetProperty("limit").GetProperty("source").GetString());
    }

    [Fact]
    public void RunStatusConfig_ReportsConfigFileSourceForSearchDefaults()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_config_source");
        using var env = EnvironmentVariableScope.Capture(
            QueryCommandRunner.DefaultLimitEnvironmentVariable,
            QueryCommandRunner.StaleAfterEnvironmentVariable,
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + QueryCommandRunner.DefaultLimitEnvironmentVariable,
            CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + QueryCommandRunner.StaleAfterEnvironmentVariable,
            CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR");
        try
        {
            var configDir = Path.Combine(projectRoot, ".cdidx");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "config.json");
            var logDir = Path.Combine(projectRoot, "logs");
            File.WriteAllText(configPath, $$"""
                {
                  "search": { "limit": 44 },
                  "stale_after": "2h",
                  "global_tool_log_dir": {{JsonSerializer.Serialize(logDir)}}
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["status", "--config", "--json"],
                appVersion: "test-version",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var limit = document.RootElement.GetProperty("effective_config").GetProperty("limit");
            Assert.Equal(44, limit.GetProperty("value").GetInt32());
            Assert.Equal($"config:{configPath}", limit.GetProperty("source").GetString());
            var staleAfter = document.RootElement.GetProperty("effective_config").GetProperty("stale_after");
            Assert.Equal("2h", staleAfter.GetProperty("value").GetString());
            Assert.Equal($"config:{configPath}", staleAfter.GetProperty("source").GetString());
            var logPath = document.RootElement.GetProperty("effective_config").GetProperty("global_tool_log_dir");
            Assert.Equal(logDir, logPath.GetProperty("value").GetString());
            Assert.Equal($"config:{configPath}", logPath.GetProperty("source").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusJson_ReportsSqlitePageMetrics_Issue1631()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_page_metrics");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App { }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var settings = document.RootElement.GetProperty("db_pragma_settings");
            Assert.True(settings.GetProperty("page_count").GetInt64() > 0);
            Assert.True(settings.GetProperty("page_size").GetInt64() > 0);
            Assert.True(settings.GetProperty("freelist_count").GetInt64() >= 0);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunVacuum_RejectsMissingDatabase_Issue1631()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_vacuum_missing_db");
        try
        {
            var dbPath = Path.Combine(projectRoot, "missing.db");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunVacuum(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(CommandErrorCodes.DbNotFound, stderr);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunVacuum_RejectsNonCodeIndexDatabase_Issue1631()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_vacuum_foreign_db");
        try
        {
            var dbPath = Path.Combine(projectRoot, "foreign.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE user_data(id INTEGER PRIMARY KEY, value TEXT);";
                command.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunVacuum(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(CommandErrorCodes.DbError, stderr);
            Assert.Contains("not an existing CodeIndex DB", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunVacuum_RejectsLookalikeNonCodeIndexDatabase_Issue1631()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_vacuum_lookalike_db");
        try
        {
            var dbPath = Path.Combine(projectRoot, "lookalike.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE files (id INTEGER PRIMARY KEY);
                    CREATE TABLE chunks (id INTEGER PRIMARY KEY);
                    CREATE TABLE symbols (id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunVacuum(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(CommandErrorCodes.DbError, stderr);
            Assert.Contains("not an existing CodeIndex DB", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunVacuum_RejectsReadOnlyUriWithNeutralWritableMessage_Issue1631()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_vacuum_readonly_uri");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunVacuum(
                ["--db", dbUri],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(CommandErrorCodes.DbError, stderr);
            Assert.Contains("database must be writable", stderr);
            Assert.DoesNotContain("backfill-fold", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }










    [Fact]
    public void RunBatch_ReusesDatabaseForJsonLineQueryCommands_Issue2119()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_query_reuse");
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

            var input = """
            ["search","Authenticate","--json","--exact"]
            ["symbols","AuthFixture","--json","--exact-name"]

            """;
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));
            var lines = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, lines.Count);
            using var searchDocument = lines[0];
            using var symbolDocument = lines[1];
            Assert.Equal("src/auth.cs", searchDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal("AuthFixture", symbolDocument.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_EmptySqliteFileRejectedBeforeQuery_Issue2037()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2037_batch_empty_sqlite");
        try
        {
            var dbPath = Path.Combine(projectRoot, "empty.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
            }

            var (exitCode, _, stderr) = CaptureConsoleWithInput(
                "[\"status\",\"--json\"]\n",
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("does not appear to be a valid CodeIndex database", stderr);
            Assert.Contains("missing required table `files`", stderr);
            Assert.Contains("Hint: rebuild with `cdidx index <projectPath> --db <path>`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_LineExceedsLimit_SkipsParsingAndContinues_Issue2891()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var input = new string('x', QueryCommandRunner.BatchMaxLineChars + 1)
                + "\n[\"status\",\"--json\"]\n";

            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"exceeds the {QueryCommandRunner.BatchMaxLineChars} character limit", stderr);
            var lines = ParseJsonLines(stdout);
            Assert.Single(lines);
            using var statusDocument = lines[0];
            Assert.True(statusDocument.RootElement.TryGetProperty("files", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_ArgumentCountExceedsLimit_ReturnsUsageError_Issue2891()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_too_many_args");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var values = Enumerable
                .Range(0, QueryCommandRunner.BatchMaxArgumentCount + 2)
                .Select(i => i == 0 ? "search" : $"arg{i}")
                .ToArray();
            var input = JsonSerializer.Serialize(values) + "\n";

            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"at most {QueryCommandRunner.BatchMaxArgumentCount} command arguments", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_ArgumentAtLimitParsesBeforeDispatch_Issue3231()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_arg_at_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var commandName = new string('x', QueryCommandRunner.BatchMaxArgumentChars);
            var input = JsonSerializer.Serialize(new[] { commandName }) + "\n";

            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("batch only supports query commands", stderr);
            Assert.DoesNotContain("character limit", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_ArgumentExceedsLimitReturnsUsageError_Issue3231()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_arg_too_long");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var commandName = new string('x', QueryCommandRunner.BatchMaxArgumentChars + 1);
            var input = JsonSerializer.Serialize(new[] { commandName }) + "\n";

            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"argument 1 exceeds the {QueryCommandRunner.BatchMaxArgumentChars} character limit", stderr);
            Assert.DoesNotContain("batch only supports query commands", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_TooDeepJsonLine_ReturnsUsageError_Issue3022()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_json_depth");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var nestedPrefix = string.Concat(Enumerable.Repeat("""{"next":""", QueryCommandRunner.BatchMaxJsonDepth + 1));
            var nested = nestedPrefix + "0" + new string('}', QueryCommandRunner.BatchMaxJsonDepth + 1);
            var input = $$"""["status",{{nested}}]""" + "\n";

            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("is not valid JSON", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_ImpactDepthZeroIsRetainedWhenExplicit()
    {
        var options = QueryCommandRunner.ParseArgs(["RunImpact", "--depth", "0"], jsonDefault: false, allowNamedQuery: true);

        Assert.Equal("RunImpact", options.Query);
        Assert.Equal(0, options.ContextAfter);
        Assert.True(options.ContextAfterExplicit);
    }

    [Fact]
    public void ParseArgs_CountFlagParsed()
    {
        var options = QueryCommandRunner.ParseArgs(["myquery", "--count"], jsonDefault: false);
        Assert.True(options.CountOnly);
        Assert.Equal("myquery", options.Query);
    }

    [Theory]
    [InlineData("weighted", ReferenceRankMode.Weighted)]
    [InlineData("count", ReferenceRankMode.Count)]
    [InlineData("kind", ReferenceRankMode.Kind)]
    public void ParseArgs_RankByFlagParsed(string value, ReferenceRankMode expected)
    {
        var options = QueryCommandRunner.ParseArgs(["Target", "--rank-by", value], jsonDefault: false);

        Assert.Equal(expected, options.RankMode);
        Assert.Equal("Target", options.Query);
    }

    [Fact]
    public void ParseArgs_InvalidRankByReportsParseError()
    {
        var options = QueryCommandRunner.ParseArgs(["Target", "--rank-by", "frequency"], jsonDefault: false);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--rank-by", options.ParseError);
        Assert.Contains("weighted", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MinEntrypointConfidenceFlagParsed()
    {
        var options = QueryCommandRunner.ParseArgs(["--min-entrypoint-confidence", "0.65"], jsonDefault: false);

        Assert.Equal(0.65, options.MinEntrypointConfidence);
    }

    [Fact]
    public void ParseArgs_InvalidMinEntrypointConfidenceReportsParseError()
    {
        var options = QueryCommandRunner.ParseArgs(["--min-entrypoint-confidence", "1.5"], jsonDefault: false);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--min-entrypoint-confidence", options.ParseError);
        Assert.Contains("0.0 through 1.0", options.ParseError);
    }

    [Fact]
    public void ParseArgs_AllowsZeroMaxLineWidthForNoTruncation()
    {
        var options = QueryCommandRunner.ParseArgs(["myquery", "--max-line-width", "0"], jsonDefault: false);

        Assert.Equal(0, options.MaxLineWidth);
        Assert.Equal("myquery", options.Query);
    }

    [Theory]
    [InlineData("c#", "csharp")]
    [InlineData("blazor", "csharp")]
    [InlineData("c++", "cpp")]
    [InlineData("fs", "fsharp")]
    [InlineData("py", "python")]
    [InlineData("py3", "python")]
    [InlineData("rb", "ruby")]
    [InlineData("python3", "python")]
    [InlineData("sqlserver", "sql")]
    [InlineData("asm", "assembly")]
    [InlineData("assembler", "assembly")]
    [InlineData("GNU assembler", "assembly")]
    public void ParseArgs_NormalizesCommonLangAliases(string input, string expected)
    {
        var options = QueryCommandRunner.ParseArgs(["RunSearch", "--lang", input], jsonDefault: false, allowNamedQuery: true);

        Assert.Equal("RunSearch", options.Query);
        Assert.Equal(expected, options.Lang);
    }

    [Fact]
    public void GetLanguageAliases_ReportsSqlDialectAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("sql");

        Assert.Contains("tsql", aliases);
        Assert.Contains("t-sql", aliases);
        Assert.Contains("transact-sql", aliases);
        Assert.Contains("transactsql", aliases);
        Assert.Contains("sqlserver", aliases);
        Assert.Contains("mssql", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsRazorBlazorAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("csharp");

        Assert.Contains("cshtml", aliases);
        Assert.Contains("razor", aliases);
        Assert.Contains("blazor", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsTypeScriptAlias()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("typescript");

        Assert.Contains("ts", aliases);
        Assert.Contains("tsx", aliases);
        Assert.Contains("cts", aliases);
        Assert.Contains("mts", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsRustAlias()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("rust");

        Assert.Contains("rs", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsJavaAlias()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("java");

        Assert.Contains("jav", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsAssemblyAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("assembly");

        Assert.Contains("asm", aliases);
        Assert.Contains("assembler", aliases);
        Assert.Contains("nasm", aliases);
        Assert.Contains("gas", aliases);
        Assert.Contains("gnuasm", aliases);
        Assert.Contains("gnu assembler", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsFsharpAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("fsharp");

        Assert.Contains("f#", aliases);
        Assert.Contains("fs", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsJavascriptAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("javascript");

        Assert.Contains("js", aliases);
        Assert.Contains("jsx", aliases);
        Assert.Contains("cjs", aliases);
        Assert.Contains("mjs", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsXmlAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("xml");

        Assert.Contains("xaml", aliases);
        Assert.Contains("axaml", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsPythonAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("python");

        Assert.Contains("py", aliases);
        Assert.Contains("py3", aliases);
        Assert.Contains("python3", aliases);
    }

    [Fact]
    public void GetLanguageAliases_ReportsRubyAliases()
    {
        var aliases = QueryCommandRunner.GetLanguageAliases("ruby");

        Assert.Contains("rb", aliases);
    }

    [Theory]
    [InlineData("transact-sql", "sql")]
    [InlineData("transact sql", "sql")]
    [InlineData("sqlserver", "sql")]
    [InlineData("mssql", "sql")]
    [InlineData("c#", "csharp")]
    [InlineData("blazor", "csharp")]
    [InlineData("c++", "cpp")]
    [InlineData("f#", "fsharp")]
    [InlineData("vb.net", "vb")]
    [InlineData("visual-basic", "vb")]
    [InlineData("visual_basic", "vb")]
    [InlineData("vbs", "vb")]
    [InlineData("vbscript", "vb")]
    [InlineData("py3", "python")]
    [InlineData("assembler", "assembly")]
    [InlineData("gas", "assembly")]
    [InlineData("gnu asm", "assembly")]
    public void NormalizeQueryLanguage_MapsCommonAliasesToCanonicalLanguages(string input, string expected)
    {
        Assert.Equal(expected, DbReader.NormalizeQueryLanguage(input));
        Assert.Equal(expected, QueryCommandRunner.NormalizeLangFilterValue(input));
        Assert.Equal(expected == "sql", DbReader.IsSqlLanguage(input));
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("tsx")]
    [InlineData("cts")]
    [InlineData("mts")]
    public void NormalizeQueryLanguage_MapsTypeScriptShorthands(string input)
    {
        Assert.Equal("typescript", DbReader.NormalizeQueryLanguage(input));
        Assert.False(DbReader.IsSqlLanguage(input));
    }

    [Theory]
    [InlineData("xaml")]
    [InlineData("axaml")]
    public void NormalizeQueryLanguage_MapsXamlShorthandsToXml(string input)
    {
        Assert.Equal("xml", DbReader.NormalizeQueryLanguage(input));
    }

    [Theory]
    [InlineData("rs")]
    [InlineData("r-s")]
    [InlineData("r s")]
    public void NormalizeQueryLanguage_MapsRustShorthand(string input)
    {
        Assert.Equal("rust", DbReader.NormalizeQueryLanguage(input));
    }

    [Theory]
    [InlineData("jav")]
    [InlineData("Java")]
    [InlineData("JAVA")]
    [InlineData("j-a-v")]
    [InlineData("j av")]
    public void NormalizeQueryLanguage_MapsJavaSpelling(string input)
    {
        Assert.Equal("java", DbReader.NormalizeQueryLanguage(input));
    }

    [Fact]
    public void ParseArgs_EndOfOptionsAllowsDashPrefixedPositionalQueryLiteralWithOptions()
    {
        var options = QueryCommandRunner.ParseArgs(["--", "--open-reports", "--db", "query.db"], jsonDefault: false, allowNamedQuery: true);

        Assert.Null(options.ParseError);
        Assert.Equal("--open-reports", options.Query);
        Assert.Equal("query.db", options.DbPath);
    }




    [Theory]
    [InlineData("bat", "batch")]
    [InlineData("cmd", "batch")]
    [InlineData("JS", "javascript")]
    [InlineData("jsx", "javascript")]
    [InlineData("cjs", "javascript")]
    [InlineData("MJS", "javascript")]
    [InlineData("C#", "csharp")]
    [InlineData("cs", "csharp")]
    [InlineData("Java", "java")]
    [InlineData("Python", "python")]
    [InlineData("py", "python")]
    [InlineData("PY3", "python")]
    [InlineData("pyi", "python")]
    [InlineData("pyw", "python")]
    [InlineData("yml", "yaml")]
    [InlineData("kt", "kotlin")]
    [InlineData("KTS", "kotlin")]
    [InlineData("tsx", "typescript")]
    [InlineData("CTS", "typescript")]
    [InlineData("mts", "typescript")]
    [InlineData("T-SQL", "sql")]
    [InlineData("transact-sql", "sql")]
    [InlineData("transact sql", "sql")]
    public void ParseArgs_NormalizesLangAliases(string input, string expected)
    {
        var options = QueryCommandRunner.ParseArgs(["needle", "--lang", input], jsonDefault: false);

        Assert.Equal(expected, options.Lang);
    }
















    [Fact]
    public void RunSearchAndSymbols_ExactQueriesSeePythonInitAllExports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_python_init_all_exports");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "package/__init__.py",
                "python",
                """
                __all__ = [
                    "public_api",
                ]
                """);

            var (searchExitCode, searchStdout, searchStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["public_api", "--db", dbPath, "--exact", "--count"],
                _jsonOptions));
            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["public_api", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, searchExitCode);
            Assert.Equal("1", searchStdout.Trim());
            Assert.Equal(string.Empty, searchStderr);

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal("1", symbolsStdout.Trim());
            Assert.Equal(string.Empty, symbolsStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearchAndSymbols_AcceptPythonPyLangAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_python_py_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "package/__init__.py",
                "python",
                """
                __all__ = [
                    "public_api",
                ]
                """);

            var (searchExitCode, searchStdout, searchStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["public_api", "--db", dbPath, "--lang", "py", "--exact", "--count"],
                _jsonOptions));
            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["public_api", "--db", dbPath, "--lang", "py", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, searchExitCode);
            Assert.Equal("1", searchStdout.Trim());
            Assert.Equal(string.Empty, searchStderr);

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal("1", symbolsStdout.Trim());
            Assert.Equal(string.Empty, symbolsStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }









    [Fact]
    public void RunPublishedTrimmedCli_SerializesQueryJsonAndErrorJson()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_trimmed_publish");
        var publishDir = Path.Combine(Path.GetTempPath(), $"cdidx_query_trimmed_publish_{Guid.NewGuid():N}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = "TrimmedPublishNeedle_1a2b3c";
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", $"class App {{ void Run() {{ var {queryToken} = 1; }} }}\n");

            var publishedDll = PublishTrimmedCli(publishDir);

            var (searchExitCode, searchStdOut, searchStdErr) = RunPublishedCli(publishedDll, publishDir, "search", queryToken, "--db", dbPath, "--count", "--json");
            Assert.Equal(CommandExitCodes.Success, searchExitCode);
            Assert.Equal(string.Empty, searchStdErr);
            using (var searchJson = JsonDocument.Parse(searchStdOut))
            {
                Assert.True(searchJson.RootElement.GetProperty("count").GetInt32() >= 1);
                Assert.Equal(1, searchJson.RootElement.GetProperty("files").GetInt32());
            }

            var (findExitCode, findStdOut, findStdErr) = RunPublishedCli(publishedDll, publishDir, "find", queryToken, "--db", dbPath, "--path", "src/**", "--count", "--json");
            Assert.Equal(CommandExitCodes.Success, findExitCode);
            Assert.Equal(string.Empty, findStdErr);
            using (var findJson = JsonDocument.Parse(findStdOut))
            {
                Assert.True(findJson.RootElement.GetProperty("count").GetInt32() >= 1);
                Assert.Equal(1, findJson.RootElement.GetProperty("files").GetInt32());
                Assert.Equal(1, findJson.RootElement.GetProperty("file_count").GetInt32());
            }

            var (symbolsExitCode, symbolsStdOut, symbolsStdErr) = RunPublishedCli(publishedDll, publishDir, "symbols", "@", "--db", dbPath, "--lang", "csharp", "--exact", "--count", "--json");
            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStdErr);
            using (var symbolsJson = JsonDocument.Parse(symbolsStdOut))
            {
                Assert.Equal(0, symbolsJson.RootElement.GetProperty("count").GetInt32());
                Assert.Equal(0, symbolsJson.RootElement.GetProperty("files").GetInt32());
            }

            var (validateExitCode, validateStdOut, validateStdErr) = RunPublishedCli(publishedDll, publishDir, "validate", "--db", dbPath, "--json");
            Assert.Equal(CommandExitCodes.Success, validateExitCode);
            Assert.Equal(string.Empty, validateStdErr);
            using (var validateJson = JsonDocument.Parse(validateStdOut))
            {
                Assert.Equal(0, validateJson.RootElement.GetProperty("count").GetInt32());
                Assert.True(validateJson.RootElement.GetProperty("issues").ValueKind is JsonValueKind.Array);
            }

            var (outlineExitCode, outlineStdOut, outlineStdErr) = RunPublishedCli(publishedDll, publishDir, "outline", "src/missing.cs", "--db", dbPath, "--json");
            Assert.Equal(CommandExitCodes.NotFound, outlineExitCode);
            Assert.Equal(string.Empty, outlineStdErr);
            using (var outlineJson = JsonDocument.Parse(outlineStdOut))
            {
                Assert.Equal("src/missing.cs", outlineJson.RootElement.GetProperty("path").GetString());
                Assert.Equal("file not found in index", outlineJson.RootElement.GetProperty("error").GetString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(publishDir);
        }
    }

    [SkipOnMacOsArm64Theory]
    [InlineData("cshtml")]
    [InlineData("razor")]
    public void RunPublishedTrimmedCli_SearchSupportsCSharpRazorAliases(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_trimmed_lang_alias_publish");
        var publishDir = Path.Combine(Path.GetTempPath(), $"cdidx_query_trimmed_lang_alias_publish_{Guid.NewGuid():N}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryToken = $"TrimmedPublishLangAliasNeedle_{Guid.NewGuid():N}";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "web/Views/Home/Index.cshtml",
                "csharp",
                $$"""
                @{
                    var marker = "{{queryToken}}";
                }
                """);

            var publishedDll = PublishTrimmedCli(publishDir);

            var (exitCode, stdout, stderr) = RunPublishedCli(publishedDll, publishDir, "search", queryToken, "--db", dbPath, "--lang", lang, "--count", "--json");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("files").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(publishDir);
        }
    }

    [Theory]
    [InlineData("definition", "--focus-column", "10")]
    [InlineData("definition", "--max-line-width", "10")]
    [InlineData("search", "--focus-column", "10")]
    [InlineData("symbols", "--max-line-width", "10")]
    public void QueryCommands_RejectPreviewOptionsWhenUnsupported(string command, string option, string value)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_preview_reject_{command}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var args = new List<string>();
            switch (command)
            {
                case "definition":
                case "search":
                case "symbols":
                    args.AddRange(["QueryCommandRunner", "--db", dbPath, option, value, "--count"]);
                    break;
            }

            var (exitCode, _, stderr) = command switch
            {
                "definition" => CaptureConsole(() => QueryCommandRunner.RunDefinition([.. args], _jsonOptions)),
                "search" => CaptureConsole(() => QueryCommandRunner.RunSearch([.. args], _jsonOptions)),
                "symbols" => CaptureConsole(() => QueryCommandRunner.RunSymbols([.. args], _jsonOptions)),
                _ => throw new InvalidOperationException($"Unexpected command: {command}")
            };

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"{option} is not supported for {command}", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }













    [Fact]
    public void ParseArgs_InvalidNumbersAndUnknownOptionsAccumulateParseErrors()
    {
        var (options, _, stderr) = CaptureConsole(() => QueryCommandRunner.ParseArgs(
        [
            "RunSearch",
            "--limit", "0",
            "--start", "0",
            "--end", "-1",
            "--before", "-2",
            "--after", "-3",
            "--snippet-lines", "0",
            "--mystery",
        ], jsonDefault: false));

        Assert.Equal(20, options.Limit);
        Assert.Null(options.StartLine);
        Assert.Null(options.EndLine);
        Assert.Equal(0, options.ContextBefore);
        Assert.Equal(0, options.ContextAfter);
        Assert.Equal(SearchSnippetFormatter.DefaultSnippetLines, options.SnippetLines);
        Assert.NotNull(options.ParseError);
        Assert.Contains("Error: --limit requires an integer between 1 and 10000", options.ParseError);
        Assert.Contains("Hint: retry with `--limit 1` or another value up to 10000.", options.ParseError);
        Assert.Contains("Error: --start requires an integer between 1 and 10000000", options.ParseError);
        Assert.Contains("Error: --end requires an integer between 1 and 10000000", options.ParseError);
        Assert.Contains("Error: --before requires an integer between 0 and 1000", options.ParseError);
        Assert.Contains("Hint: retry with `--before 0` or another value up to 1000.", options.ParseError);
        Assert.Contains("Error: --after requires an integer between 0 and 1000", options.ParseError);
        Assert.Contains("Error: --snippet-lines requires an integer between 1 and 20", options.ParseError);
        Assert.Equal(string.Empty, stderr);
    }

    [Theory]
    [InlineData("--limit", "not-a-number", "between 1 and 10000")]
    [InlineData("--snippet-lines", "0", "between 1 and 20")]
    [InlineData("--max-line-width", "-1", "between 0 and 4096")]
    public void ParseArgs_InvalidNumericOptionsIncludeBoundsContext_Issue2071(string flag, string value, string expectedRange)
    {
        var options = QueryCommandRunner.ParseArgs(
            ["needle", flag, value],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.NotNull(options.ParseError);
        Assert.Contains(flag, options.ParseError!);
        Assert.Contains(expectedRange, options.ParseError!);
        Assert.Contains($"got '{value}'", options.ParseError!);
    }

    // Regression lock for #1503: numeric CLI flags must reject values above the documented
    // per-flag upper bound with a clear message naming the cap, instead of silently allowing
    // `int.MaxValue` or quietly clamping (e.g. `--snippet-lines 999999` previously folded down
    // to 20). The cap surfaces typos as parse errors rather than huge allocations or hidden clamps.
    // #1503 の回帰ロック: 数値 CLI フラグは documented な上限を超えた値を、上限を明示した
    // エラーで拒否しなければならない。以前は `int.MaxValue` まで通したり silent に clamp
    // していた（例: `--snippet-lines 999999` が黙って 20 に丸められていた）。上限を超えた
    // 入力をパーズエラーとして見せることで、巨大確保や隠れた clamp ではなくユーザーのタイポ
    // として顕在化させる。
    [Theory]
    [InlineData("--limit", "10001", 10_000)]
    [InlineData("--snippet-lines", "21", 20)]
    [InlineData("--snippet-lines", "999999", 20)]
    [InlineData("--start", "10000001", 10_000_000)]
    [InlineData("--end", "10000001", 10_000_000)]
    [InlineData("--focus-line", "10000001", 10_000_000)]
    [InlineData("--focus-column", "100001", 100_000)]
    [InlineData("--focus-length", "100001", 100_000)]
    public void ParseArgs_PositiveIntOverUpperBoundIsRejected_Issue1503(string flag, string value, int expectedMax)
    {
        var options = QueryCommandRunner.ParseArgs(
            ["needle", flag, value],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.NotNull(options.ParseError);
        Assert.Contains($"{flag} must be less than or equal to {expectedMax}", options.ParseError!);
        Assert.Contains($"got '{value}'", options.ParseError!);
    }

    [Theory]
    [InlineData("--depth", "65", 64)]
    [InlineData("--before", "1001", 1_000)]
    [InlineData("--after", "1001", 1_000)]
    [InlineData("--max-line-width", "4097", 4096)]
    public void ParseArgs_NonNegativeIntOverUpperBoundIsRejected_Issue1503(string flag, string value, int expectedMax)
    {
        var options = QueryCommandRunner.ParseArgs(
            ["needle", flag, value],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.NotNull(options.ParseError);
        Assert.Contains($"{flag} must be less than or equal to {expectedMax}", options.ParseError!);
        Assert.Contains($"got '{value}'", options.ParseError!);
    }

    [Theory]
    [InlineData("--limit", "10000")]
    [InlineData("--snippet-lines", "20")]
    [InlineData("--depth", "64")]
    [InlineData("--before", "1000")]
    [InlineData("--after", "1000")]
    public void ParseArgs_AtUpperBoundIsAccepted_Issue1503(string flag, string value)
    {
        var options = QueryCommandRunner.ParseArgs(
            ["needle", flag, value],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_ExactMaxSnippetLinesIsNotRejected_Issue1503()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["needle", "--snippet-lines", $"{SearchSnippetFormatter.MaxSnippetLines}"],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Null(options.ParseError);
        Assert.Equal(SearchSnippetFormatter.MaxSnippetLines, options.SnippetLines);
    }

    [Fact]
    public void ParseArgs_ParsesExactAliases()
    {
        var search = QueryCommandRunner.ParseArgs(["needle", "--exact-substring"], jsonDefault: false);
        Assert.True(search.ExactSubstring);
        Assert.False(search.ExactName);

        var symbols = QueryCommandRunner.ParseArgs(["Run", "--exact-name"], jsonDefault: false);
        Assert.True(symbols.ExactName);
        Assert.False(symbols.ExactSubstring);
    }

    [Fact]
    public void ParseArgs_NameFlagCollectsValuesAndRejectsMissingValue()
    {
        var ok = QueryCommandRunner.ParseArgs(
            ["first", "--name", "Alpha", "--name", "Beta", "extraPositional"],
            jsonDefault: false);
        Assert.Null(ok.ParseError);
        Assert.Equal("first", ok.Query);
        Assert.Equal(new[] { "Alpha", "Beta", "extraPositional" }, ok.ExtraNames);

        // --name swallowing a following flag as data is a silent trust failure; must be rejected.
        // --name が直後のフラグを値として飲み込むのは暗黙の誤動作。拒否する。
        var bad = QueryCommandRunner.ParseArgs(
            ["--name", "--lang", "csharp"],
            jsonDefault: false);
        Assert.NotNull(bad.ParseError);
        Assert.Contains("--name requires a value", bad.ParseError!);

        var badTail = QueryCommandRunner.ParseArgs(["--name"], jsonDefault: false);
        Assert.NotNull(badTail.ParseError);
    }

    [Theory]
    [InlineData("search-limit-tail", "search", "Error: --limit requires a value.")]
    [InlineData("search-top-tail", "search", "Error: --limit requires a value.")]
    [InlineData("search-db-tail", "search", "Error: --db requires a value.")]
    [InlineData("search-db-swallow", "search", "Error: --db requires a value.")]
    [InlineData("search-db-unknown-double-dash", "search", "Error: --db requires a value.")]
    [InlineData("search-db-recognized-double-dash", "search", "Error: --db requires a value.")]
    [InlineData("search-lang-swallow", "search", "Error: --lang requires a value.")]
    [InlineData("search-lang-unknown-double-dash", "search", "Error: --lang requires a value.")]
    [InlineData("search-path-swallow", "search", "Error: --path requires a value.")]
    [InlineData("search-exclude-path-swallow", "search", "Error: --exclude-path requires a value.")]
    [InlineData("definition-kind-swallow", "definition", "Error: --kind requires a value.")]
    public void QueryEntrypoints_MissingOrSwallowedOptionValuesReturnUsageError(string scenario, string command, string expectedError)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithMissingOrSwallowedValue(scenario));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(expectedError, stderr);
        Assert.Contains("Hint: fix the invalid or missing option value", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        Assert.DoesNotContain("database not found", stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
    }

    [Theory]
    [InlineData("search-db-inline-empty", "search", "Error: --db requires a value.")]
    [InlineData("search-lang-inline-empty", "search", "Error: --lang requires a value.")]
    [InlineData("search-path-inline-empty", "search", "Error: --path requires a value.")]
    [InlineData("search-exclude-path-inline-empty", "search", "Error: --exclude-path requires a value.")]
    public void QueryEntrypoints_EmptyInlineStringOptionValuesReturnUsageError(string scenario, string command, string expectedError)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithEmptyInlineStringValue(scenario));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(expectedError, stderr);
        Assert.Contains("Hint: fix the invalid or missing option value", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }











    // Issue #1507: `WithDb` surfaces the missing --db error when ParseArgs accepted an empty
    // DbPath (e.g. via the CLI default fallback being cleared). Even at this late site the
    // per-flag hint must come from the same metadata table, not a hard-coded one-off string.
    // Issue #1507: WithDb 経路の missing --db エラーも、同じメタデータテーブル由来のヒントを使う。
    [Fact]
    public void WithDb_BlankDbPathSurfacesPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["QueryCommandRunner", "--db="], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("Hint: pass a path to a CodeIndex SQLite database", stderr);
    }

    [Fact]
    public void WithDb_InvalidSqliteFileSurfacesSqliteCategory_Issue2072()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2072_invalid_sqlite");
        try
        {
            var dbPath = Path.Combine(projectRoot, "not-a-codeindex.db");
            File.WriteAllText(dbPath, "this is not a sqlite database");
            var dbUri = new Uri(dbPath).AbsoluteUri + "?mode=ro&immutable=1;Pooling=False";

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbUri],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains($"Error [{CommandErrorCodes.DbError}]: SQLite database error", stderr);
            Assert.Contains("Hint: check `--db`, verify the index was written by a compatible cdidx version", stderr);
            Assert.DoesNotContain("database error:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WithDb_EmptySqliteFileRejectedBeforeQuery_Issue2037()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2037_empty_sqlite");
        try
        {
            var dbPath = Path.Combine(projectRoot, "empty.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
            }

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("does not appear to be a valid CodeIndex database", stderr);
            Assert.Contains("missing required table `files`", stderr);
            Assert.Contains("Hint: rebuild with `cdidx index <projectPath> --db <path>`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WithDb_MalformedFileUriSurfacesDbPathParseError_Issue1990()
    {
        const string malformedUri = "file:///tmp/codeindex%ZZ.db?immutable=1";

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--db", malformedUri],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Contains($"Error [{CommandErrorCodes.DbError}]: invalid --db file URI:", stderr);
        Assert.Contains("file:///absolute/path/to/codeindex.db?immutable=1", stderr);
        Assert.Contains($"the --db value resolved to: {malformedUri}", stderr);
    }

    [Fact]
    public void WithDb_OversizedFileUriQueryReturnsBoundedDiagnostics_Issue3140()
    {
        var longQuery = new string('a', SqliteFileUri.MaxQueryLength + 1);
        var dbUri = "file:///tmp/codeindex.db?" + longQuery;

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--db", dbUri],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", stderr);
        Assert.Contains("...(truncated,", stderr);
        Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stderr);
    }

    [Fact]
    public void WithDb_ReadOnlyOversizedFileUriQueryReturnsBoundedDiagnostics_Issue3140()
    {
        var longQuery = new string('a', SqliteFileUri.MaxQueryLength + 1);
        var dbUri = "file:///tmp/codeindex.db?" + longQuery;

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--read-only", "--db", dbUri],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", stderr);
        Assert.Contains("...(truncated,", stderr);
        Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stderr);
    }

    [Fact]
    public void RunBatch_UnsupportedOptionTruncatesOversizedToken()
    {
        var token = "--" + new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunBatch(
            [token],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("is not supported for batch", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(token, stderr);
    }

    [Fact]
    public void RunBatch_UnsupportedOptionFlattensMultilineToken()
    {
        var token = "--bad\nforged\tvalue";

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunBatch(
            [token],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--bad forged value is not supported for batch", stderr);
        Assert.DoesNotContain(token, stderr);
    }

    [Fact]
    public void WithDb_MissingOversizedPathReturnsBoundedDiagnostics_Issue3093()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue3093_missing_db");
        try
        {
            var missingDbPath = Path.Combine(
                projectRoot,
                Path.Combine(Enumerable.Repeat("segment", 40).ToArray()),
                "codeindex.db");
            var resolvedPath = Path.GetFullPath(missingDbPath);
            Assert.True(resolvedPath.Length > SqliteFileUri.MaxDiagnosticValueLength);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", missingDbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"Error [{CommandErrorCodes.DbNotFound}]: --db '", stderr);
            Assert.Contains("does not point to an existing database file", stderr);
            Assert.Contains("...(truncated,", stderr);
            Assert.DoesNotContain(resolvedPath, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_MissingOversizedDbPathReturnsBoundedDiagnostics_Issue3093()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue3093_batch_db");
        try
        {
            var missingDbPath = Path.Combine(
                projectRoot,
                Path.Combine(Enumerable.Repeat("segment", 40).ToArray()),
                "codeindex.db");
            var resolvedPath = Path.GetFullPath(missingDbPath);
            Assert.True(resolvedPath.Length > SqliteFileUri.MaxDiagnosticValueLength);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunBatch(
                ["--db", missingDbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains($"Error [{CommandErrorCodes.DbNotFound}]: database not found at ", stderr);
            Assert.Contains("...(truncated,", stderr);
            Assert.DoesNotContain(resolvedPath, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WithDb_SqliteCantOpenSurfacesAccessOpenCategory_Issue2072()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2072_cantopen");
        try
        {
            var missingParent = Path.Combine(projectRoot, "missing-parent");
            var dbUri = new Uri(Path.Combine(missingParent, "codeindex.db")).AbsoluteUri + "?mode=ro";

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbUri],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {Path.GetFullPath(Path.Combine(missingParent, "codeindex.db"))}", stderr);
            Assert.Contains("Hint: the --db path resolved to:", stderr);
            Assert.DoesNotContain("database access/open denied", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("search-extra", "unexpected extra positional 1 argument for search")]
    [InlineData("excerpt-extra", "unexpected extra positional 1 argument for excerpt")]
    [InlineData("map-extra", "map does not accept positional arguments")]
    [InlineData("outline-extra", "outline does not accept positional arguments")]
    [InlineData("status-extra", "status does not accept positional arguments")]
    [InlineData("validate-extra", "validate does not accept positional arguments")]
    [InlineData("languages-extra", "languages does not accept positional arguments")]
    public void QueryEntrypoints_UnexpectedPositionalsReturnUsageError(string scenario, string expectedError)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithUnexpectedPositionals(scenario));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(expectedError, stderr);
        Assert.DoesNotContain("database not found", stderr);
    }






    [Theory]
    [InlineData("--path", "src")]
    [InlineData("--mystery")]
    public void RunLanguages_UnsupportedOptionsReturnUsageError(string flag, string? value = null)
    {
        var args = value == null
            ? new[] { flag }
            : new[] { flag, value };

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(args, _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {flag} is not supported for languages.", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("languages")}", stderr);
        Assert.DoesNotContain("requires a value", stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
    }

    [Fact]
    public void RunLanguages_JsonIndexedOnlyListsLanguagesPresentInDatabase()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_languages_indexed_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "class App { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "# App\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                QueryCommandRunner.RunLanguages(["--json", "--indexed-only", "--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement.GetProperty("languages").EnumerateArray()
                .Select(lang => lang.GetProperty("lang").GetString())
                .ToList();

            Assert.Equal(["csharp", "markdown"], names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunLanguages_JsonIndexedOnlyCombinesWithCapabilityFilter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_languages_indexed_capability");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "class App { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "# App\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                QueryCommandRunner.RunLanguages(["--json", "--indexed-only", "--capability", "graph", "--db", dbPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement.GetProperty("languages").EnumerateArray()
                .Select(lang => lang.GetProperty("lang").GetString())
                .ToList();

            Assert.Equal(["csharp"], names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("references")]
    public void RunLanguages_JsonCapabilityGraphFiltersGraphSupport(string capability)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            QueryCommandRunner.RunLanguages(["--json", "--capability", capability], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages").EnumerateArray().ToList();

        Assert.NotEmpty(languages);
        Assert.Contains(languages, lang => lang.GetProperty("lang").GetString() == "csharp");
        Assert.All(languages, lang => Assert.True(lang.GetProperty("graph_queries").GetBoolean()));
    }

    [Fact]
    public void RunLanguages_JsonCapabilitySymbolsFiltersSymbolSupport()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            QueryCommandRunner.RunLanguages(["--json", "--capability", "symbols"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages").EnumerateArray().ToList();

        Assert.NotEmpty(languages);
        Assert.Contains(languages, lang => lang.GetProperty("lang").GetString() == "html");
        Assert.DoesNotContain(languages, lang => lang.GetProperty("lang").GetString() == "msbuild");
        Assert.All(languages, lang => Assert.True(lang.GetProperty("symbol_extraction").GetBoolean()));
    }

    [Fact]
    public void RunLanguages_InvalidCapabilityReturnsUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() =>
            QueryCommandRunner.RunLanguages(["--capability", "lint"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unsupported --capability value 'lint'", stderr);
        Assert.Contains("graph, symbols, or references", stderr);
    }

    [Fact]
    public void RunLanguages_MissingCapabilityReturnsUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() =>
            QueryCommandRunner.RunLanguages(["--capability"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--capability", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("languages")}", stderr);
    }

    [Fact]
    public void RunLanguages_JsonListsModernNodeModuleExtensions()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(["--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages");
        var javascript = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "javascript");
        var typescript = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "typescript");
        var objc = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "objc");

        Assert.Contains(".cjs", javascript.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
        Assert.Contains(".mjs", javascript.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
        Assert.Contains("js", javascript.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()));
        Assert.Contains("jsx", javascript.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()));
        Assert.Contains(".cts", typescript.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
        Assert.Contains(".mts", typescript.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
        Assert.Contains(".m", objc.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
        Assert.Contains(".mm", objc.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
    }

    [Fact]
    public void RunLanguages_JsonListsHtmlWithSymbolExtractionAndAllExtensions()
    {
        // Pin the #215 surface: `cdidx languages --json` must report html with
        // symbol_extraction=true and list all four extensions (.html, .htm, .xhtml, .shtml)
        // so AI tools can discover HTML symbol support without indexing first.
        // #215 の表面契約を pin: `cdidx languages --json` は html を symbol_extraction=true で
        // 返し、`.html` / `.htm` / `.xhtml` / `.shtml` の 4 拡張子を列挙する必要がある。
        // AI ツールがインデックス前でも HTML のシンボル対応を検出できるようにするため。
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(["--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages");
        var html = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "html");

        Assert.True(html.GetProperty("symbol_extraction").GetBoolean());
        var extensions = html.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()).ToList();
        Assert.Contains(".html", extensions);
        Assert.Contains(".htm", extensions);
        Assert.Contains(".xhtml", extensions);
        Assert.Contains(".shtml", extensions);
    }

    [Fact]
    public void RunLanguages_JsonListsAssemblyWithSymbolExtractionGraphAndAliases()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(["--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages");
        var assembly = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "assembly");

        Assert.True(assembly.GetProperty("symbol_extraction").GetBoolean());
        Assert.True(assembly.GetProperty("graph_queries").GetBoolean());

        var extensions = assembly.GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()).ToList();
        Assert.Contains(".s", extensions);
        Assert.Contains(".S", extensions);
        Assert.Contains(".asm", extensions);
        Assert.Contains(".nasm", extensions);

        var aliases = assembly.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()).ToList();
        Assert.Contains("asm", aliases);
        Assert.Contains("assembler", aliases);
        Assert.Contains("gas", aliases);
        Assert.Contains("gnuasm", aliases);
        Assert.Contains("gnu assembler", aliases);
    }

    [Fact]
    public void RunLanguages_JsonListsCSharpRazorAliases()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(["--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages");
        var csharp = languages.EnumerateArray().First(lang => lang.GetProperty("lang").GetString() == "csharp");
        var aliases = csharp.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()).ToList();

        Assert.Contains("cshtml", aliases);
        Assert.Contains("razor", aliases);
    }

    [Fact]
    public void RunLanguages_Json_SearchOnlyBucketsAdvertiseZeroSymbolAndGraphSupport()
    {
        // Search-only languages that intentionally live outside the Python / CSS extractors
        // (Cython .pyx/.pxd, Sass .sass, Stylus .styl, and the newly added extension-only
        // languages) must advertise
        // symbol_extraction=false / graph_queries=false so AI clients can tell the difference
        // between "indexed with symbols" and "indexed for search only".
        // 意図的に Python / CSS 抽出器の対象外になっている search-only 言語
        // （Cython の .pyx/.pxd、Sass の .sass、Stylus の .styl、そして新規追加の
        // 拡張子ベース言語）は、
        // symbol_extraction=false / graph_queries=false で広告しなければならない。
        // こうしないと、AI クライアントが「シンボル付きインデックス」と「検索のみインデックス」を区別できない。
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages(["--json"], _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = ParseJsonOutput(stdout);
        var languages = document.RootElement.GetProperty("languages").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("lang").GetString()!, entry => entry);

        foreach (var searchOnly in new[] { "cython", "sass", "stylus", "msbuild" })
        {
            Assert.True(languages.ContainsKey(searchOnly), $"expected '{searchOnly}' to be listed");
            var entry = languages[searchOnly];
            Assert.False(entry.GetProperty("symbol_extraction").GetBoolean(),
                $"{searchOnly} must advertise symbol_extraction=false");
            Assert.False(entry.GetProperty("graph_queries").GetBoolean(),
                $"{searchOnly} must advertise graph_queries=false");
        }

        foreach (var searchOnly in new[] { "crystal", "clojure", "d", "erlang", "julia", "nim", "ocaml", "solidity", "tcl" })
        {
            Assert.True(languages.ContainsKey(searchOnly), $"expected '{searchOnly}' to be listed");
            var entry = languages[searchOnly];
            Assert.False(entry.GetProperty("symbol_extraction").GetBoolean(),
                $"{searchOnly} must advertise symbol_extraction=false");
            Assert.False(entry.GetProperty("graph_queries").GetBoolean(),
                $"{searchOnly} must advertise graph_queries=false");
        }

        var yamlAliases = languages["yaml"].GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetString()).ToList();
        Assert.Contains("yml", yamlAliases);

        Assert.True(languages.ContainsKey("perl"), "expected 'perl' to be listed");
        Assert.True(languages["perl"].GetProperty("symbol_extraction").GetBoolean(),
            "perl must advertise symbol_extraction=true");
        Assert.True(languages["perl"].GetProperty("graph_queries").GetBoolean(),
            "perl must advertise graph_queries=true");

        // Cython owns .pyx / .pxd exclusively; python keeps .py / .pyi / .pyw and Bazel filenames.
        // Cython は .pyx / .pxd を専有し、python は .py / .pyi / .pyw と Bazel ファイル名を維持。
        var cythonExts = languages["cython"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.Contains(".pyx", cythonExts);
        Assert.Contains(".pxd", cythonExts);

        var pythonExts = languages["python"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.DoesNotContain(".pyx", pythonExts);
        Assert.DoesNotContain(".pxd", pythonExts);
        Assert.Contains(".py", pythonExts);
        Assert.Contains(".pyi", pythonExts);

        var ocamlExts = languages["ocaml"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.Contains(".ml", ocamlExts);
        Assert.Contains(".mli", ocamlExts);

        var clojureExts = languages["clojure"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.Contains(".clj", clojureExts);
        Assert.Contains(".cljs", clojureExts);
        Assert.Contains(".cljc", clojureExts);
        Assert.Contains(".edn", clojureExts);

        var perlExts = languages["perl"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.Contains(".pl", perlExts);
        Assert.Contains(".pm", perlExts);
        Assert.Contains(".pod", perlExts);
        Assert.Contains(".t", perlExts);

        var msbuildExts = languages["msbuild"].GetProperty("extensions").EnumerateArray()
            .Select(ext => ext.GetString()).ToList();
        Assert.Contains(".csproj", msbuildExts);
        Assert.Contains(".fsproj", msbuildExts);
        Assert.Contains(".vbproj", msbuildExts);
        Assert.Contains(".props", msbuildExts);
        Assert.Contains(".targets", msbuildExts);
        Assert.DoesNotContain(".csproj", languages["xml"].GetProperty("extensions").EnumerateArray().Select(ext => ext.GetString()));
    }

    [Fact]
    public void RunLanguages_HumanOutput_WideExtensionListSpillsOntoContinuationLine()
    {
        // The human-readable table must not let long extension lists (dockerfile / makefile /
        // python / ruby / xml / msbuild) swallow the Symbols / Graph columns. Instead, spill onto a
        // continuation line so the row is still readable.
        // 人間向けテーブルは、長い拡張子リスト（dockerfile / makefile / python / ruby / xml / msbuild）が
        // Symbols / Graph 列を食い潰さないようにし、継続行へ退避させて可読性を保つこと。
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunLanguages([], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains("languages)", stderr);

        var lines = stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        // Rows with long extension / alias lists must spill onto a continuation line so the
        // Symbols / Graph columns stay readable.
        // 拡張子や alias が長い行は継続行に退避し、Symbols / Graph 列の可読性を保つ。
        var wideLangs = new[] { "csharp", "dockerfile", "makefile", "python", "ruby", "msbuild" };
        foreach (var wide in wideLangs)
        {
            var headerIndex = Array.FindIndex(lines, line => line.StartsWith($"{wide} ", StringComparison.Ordinal));
            Assert.True(headerIndex >= 0, $"expected row for {wide}");
            var header = lines[headerIndex];
            // Header row carries only lang + sym + graph, never the extension list itself.
            // ヘッダ行には言語名・シンボル・グラフのみが含まれ、拡張子文字列は含まれない。
            Assert.DoesNotContain("Dockerfile", header);
            Assert.DoesNotContain("Makefile", header);
            Assert.DoesNotContain("WORKSPACE", header);
            Assert.DoesNotContain("Gemfile", header);
            Assert.DoesNotContain(".csproj", header);
            var continuation = lines[headerIndex + 1];
            Assert.StartsWith("  Extensions: ", continuation);
        }

        var yamlIndex = Array.FindIndex(lines, line => line.StartsWith("yaml ", StringComparison.Ordinal));
        Assert.True(yamlIndex >= 0, "expected row for yaml");
        Assert.Contains("yml", lines[yamlIndex]);

        // Spot-check: the continuation line for dockerfile contains both the bare filename and the
        // `<Prefix><suffix>` pseudo-entry added for Issue #189 follow-up.
        // dockerfile 継続行に完全一致ファイル名と `<Prefix><suffix>` 擬似エントリが両方入る。
        var dockerIndex = Array.FindIndex(lines, line => line.StartsWith("dockerfile ", StringComparison.Ordinal));
        var dockerContinuation = lines[dockerIndex + 1];
        Assert.Contains("Dockerfile ", dockerContinuation);
        Assert.Contains("Dockerfile.<suffix>", dockerContinuation);
        Assert.Contains("Containerfile", dockerContinuation);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("definition")]
    [InlineData("symbols")]
    [InlineData("files")]
    public void QueryEntrypoints_InvalidSinceReturnUsageError(string command)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithInvalidSince(command));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: could not parse --since value 'nope' as a date/time.", stderr);
        Assert.Contains("Hint: fix the invalid or missing option value", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        Assert.DoesNotContain("No ", stderr);
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("excerpt")]
    [InlineData("map")]
    [InlineData("inspect")]
    [InlineData("outline")]
    [InlineData("status")]
    [InlineData("impact")]
    [InlineData("deps")]
    [InlineData("hotspots")]
    [InlineData("unused")]
    [InlineData("validate")]
    public void QueryEntrypoints_UnsupportedSinceReturnUsageError(string command)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithUnsupportedSince(command, "nope"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: --since is not supported for {command}.", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        Assert.DoesNotContain("could not parse --since value", stderr);
        Assert.DoesNotContain("database not found", stderr);
    }

    [Theory]
    [InlineData("search", "--no-json")]
    [InlineData("map", "--count")]
    [InlineData("inspect", "--count")]
    [InlineData("status", "--count")]
    [InlineData("validate", "--exact")]
    [InlineData("validate", "--count")]
    [InlineData("validate", "--lang", "javascript")]
    [InlineData("validate", "--exclude-path", "src/")]
    [InlineData("validate", "--exclude-tests")]
    public void QueryEntrypoints_UnsupportedOptionsReturnUsageError(string command, string flag, string? value = null)
    {
        var args = value == null
            ? new[] { flag }
            : new[] { flag, value };

        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithUnsupportedOption(command, args));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {flag} is not supported for {command}.", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        if (flag is "--limit" or "--top")
            Assert.DoesNotContain("requires a positive integer", stderr);
        Assert.DoesNotContain("database not found", stderr);
    }




    [Fact]
    public void ParseArgs_EndOfOptionsAllowsDashPrefixedQueryLiteral()
    {
        var options = QueryCommandRunner.ParseArgs(["--", "--baar"], jsonDefault: false, allowNamedQuery: true);

        Assert.Null(options.ParseError);
        Assert.Equal("--baar", options.Query);
    }



    [Theory]
    [InlineData("search-limit", "search", "--limit requires an integer between 1 and 10000")]
    [InlineData("search-top", "search", "--limit requires an integer between 1 and 10000")]
    [InlineData("search-snippet-lines", "search", "--snippet-lines requires an integer between 1 and 20")]
    [InlineData("impact-depth", "impact", "--depth requires an integer between 0 and 64")]
    [InlineData("excerpt-start", "excerpt", "--start requires an integer between 1 and 10000000")]
    [InlineData("excerpt-end", "excerpt", "--end requires an integer between 1 and 10000000")]
    [InlineData("excerpt-before", "excerpt", "--before requires an integer between 0 and 1000")]
    [InlineData("excerpt-after", "excerpt", "--after requires an integer between 0 and 1000")]
    public void QueryEntrypoints_InvalidNumericOptionsReturnUsageError(string scenario, string command, string expectedErrorFragment)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunCommandWithInvalidNumeric(scenario));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(expectedErrorFragment, stderr);
        Assert.Contains("Hint: fix the invalid or missing option value", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        Assert.DoesNotContain("database not found", stderr);
    }


    // Regression lock for #161: valid integers that fail the positive / non-negative
    // range check used to be swallowed silently, leaving the command to run with the
    // default value and write real results to stdout with exit 0. Every case below
    // MUST abort with UsageError and leak NO stdout output.
    // Uses a real indexed DB + real file on disk so that if ParseArgs ever regresses to
    // "swallow invalid value and continue with default", the stdout-empty invariant has
    // real teeth — the command could actually produce results, and the test would catch
    // them. Without a real fixture the invariant could be preserved by accident (missing
    // DB / missing file making the command fail at a later stage).
    // #161 の回帰ロック: 数値として parse できるが positive / non-negative 制約を満たさない値は、
    // かつて parseError を立てずにデフォルトへ差し替えられ、本物の結果を stdout に出して exit 0 にしていた。
    // 各ケースは UsageError で停止し、stdout に 1 バイトも漏らしてはならない。
    // 実在の index 済み DB と実在ファイルを用意することで、ParseArgs が「不正値を既定値に差し替えて続行」へ
    // 退行した場合に本当に stdout へ結果が漏れる経路を作り、stdout 空のアサーションが偶然通ってしまう逃げ道を塞ぐ。
    [Theory]
    [InlineData("symbols", "0", "--limit", "--limit requires an integer between 1 and 10000")]
    [InlineData("symbols", "-5", "--limit", "--limit requires an integer between 1 and 10000")]
    [InlineData("symbols", "0", "--top", "--limit requires an integer between 1 and 10000")]
    [InlineData("symbols", "-5", "--top", "--limit requires an integer between 1 and 10000")]
    [InlineData("search", "0", "--limit", "--limit requires an integer between 1 and 10000")]
    [InlineData("search", "-5", "--limit", "--limit requires an integer between 1 and 10000")]
    [InlineData("search", "0", "--top", "--limit requires an integer between 1 and 10000")]
    [InlineData("search", "-5", "--top", "--limit requires an integer between 1 and 10000")]
    [InlineData("search", "0", "--snippet-lines", "--snippet-lines requires an integer between 1 and 20")]
    [InlineData("impact", "-1", "--max-hops", "--max-hops requires an integer between 0 and 64")]
    [InlineData("impact", "-1", "--depth", "--depth requires an integer between 0 and 64")]
    [InlineData("excerpt", "0", "--start", "--start requires an integer between 1 and 10000000")]
    [InlineData("excerpt", "-5", "--start", "--start requires an integer between 1 and 10000000")]
    [InlineData("excerpt", "0", "--end", "--end requires an integer between 1 and 10000000")]
    [InlineData("excerpt", "-1", "--before", "--before requires an integer between 0 and 1000")]
    [InlineData("excerpt", "-1", "--after", "--after requires an integer between 0 and 1000")]
    public void QueryEntrypoints_OutOfRangeNumericOptionsFailClosed_Issue161(string command, string value, string option, string expectedErrorFragment)
    {
        var projectRoot = TestProjectHelper.CreateTempProject(
            $"cdidx_issue161_{command}_{option.Trim('-')}_{value.Replace('-', 'n')}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            // Seed indexed content so `search "Issue161"`, `symbols "Issue161"`, and
            // `impact "Issue161Callee"` would all return real matches if ParseArgs regressed.
            // Issue161Caller.Run calls Issue161Target.Issue161Callee, giving `impact` a live edge.
            // `search "Issue161"` / `symbols "Issue161"` が本物の結果を返せるよう、index 済み内容をシードする。
            // Issue161Caller.Run が Issue161Target.Issue161Callee を呼ぶことで `impact` のエッジも張る。
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue161Target.cs",
                "csharp",
                """"
                namespace Issue161;
                public class Issue161Target
                {
                    public void Issue161Callee() { }
                }
                public class Issue161Caller
                {
                    public void Run()
                    {
                        var target = new Issue161Target();
                        target.Issue161Callee();
                    }
                }
                """");
            MarkGraphAndFoldReady(dbPath);

            // Real file on disk so `excerpt` would actually read and print content
            // if any of its numeric options regressed to their defaults.
            // `excerpt` が既定値退行時に本当にファイル内容を読んで出力できるよう、実在ファイルも用意する。
            var excerptDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(excerptDir);
            var excerptFilePath = Path.Combine(excerptDir, "Issue161Excerpt.cs");
            File.WriteAllText(
                excerptFilePath,
                "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\n");

            string[] args = command switch
            {
                "symbols" => ["Issue161", "--db", dbPath, "--json", option, value],
                "search" => ["Issue161", "--db", dbPath, "--json", option, value],
                "impact" => ["Issue161Callee", "--db", dbPath, "--json", option, value],
                "excerpt" when option == "--start" => [excerptFilePath, "--json", option, value],
                "excerpt" => [excerptFilePath, "--json", "--start", "1", option, value],
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => command switch
            {
                "symbols" => QueryCommandRunner.RunSymbols(args, _jsonOptions),
                "search" => QueryCommandRunner.RunSearch(args, _jsonOptions),
                "impact" => QueryCommandRunner.RunImpact(args, _jsonOptions),
                "excerpt" => QueryCommandRunner.RunExcerpt(args, _jsonOptions),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
            });

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            // The #161 bug was exactly this: real results on stdout alongside the stderr Error.
            // stdout must stay empty so callers that branch on exit code alone cannot consume
            // silently-defaulted output as if it were valid data.
            // #161 の本質はここ。exit code だけを見る呼び出し元がデフォルト差し替えの出力を
            // 正当な結果として消費しないよう、stdout は空でなければならない。
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(expectedErrorFragment, stderr);
            Assert.Contains($"got '{value}'", stderr);
            Assert.Contains("Hint: fix the invalid or missing option value", stderr);
            Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_ImpactMaxHopsCanonicalAndDepthDeprecatedAliasShareValue()
    {
        var maxHops = QueryCommandRunner.ParseArgs(["Run", "--max-hops", "2"], jsonDefault: false);
        Assert.Equal(2, maxHops.ContextAfter);
        Assert.True(maxHops.ContextAfterExplicit);
        Assert.False(maxHops.ImpactDeprecatedDepthUsed);

        var depthAlias = QueryCommandRunner.ParseArgs(["Run", "--depth", "3"], jsonDefault: false);
        Assert.Equal(3, depthAlias.ContextAfter);
        Assert.True(depthAlias.ContextAfterExplicit);
        Assert.True(depthAlias.ImpactDeprecatedDepthUsed);
    }

    // Regression lock for #184: when a value-taking option is followed by the next recognized
    // `--flag` token (e.g. `--limit --lang rust`), the parser must NOT consume the next flag as
    // this option's value. Previously `--limit --lang rust` silently stole `--lang` as the
    // `--limit` value, failed TryParsePositiveInt, and surfaced a confusing error — while
    // `--lang` itself was dropped. Now the parser detects the recognized-option token and
    // reports "requires a value" cleanly, matching the contract that a missing value fails
    // closed instead of swallowing neighboring flags.
    // #184 の回帰ロック: value-taking オプションの直後に別の `--flag` が来た場合
    // （例: `--limit --lang rust`）、パーサはその次のフラグを値として取り込んではならない。
    // 以前は `--limit --lang rust` が silent に `--lang` を `--limit` の値として奪っており、
    // TryParsePositiveInt が失敗して分かりにくいエラーが出る一方で `--lang` も落ちていた。
    // 今は recognized-option token を検知して "requires a value" でクリーンに失敗させ、値欠落は
    // 黙って隣接フラグを飲み込まず fail-close する、という契約に合わせる。
    [Theory]
    [InlineData(new[] { "search", "hello", "--limit", "--lang", "rust" }, "--limit requires a value.")]
    [InlineData(new[] { "search", "hello", "--lang", "--limit", "5" }, "--lang requires a value.")]
    [InlineData(new[] { "search", "hello", "--snippet-lines", "--limit", "5" }, "--snippet-lines requires a value.")]
    [InlineData(new[] { "search", "hello", "--snippet-focus", "--limit", "5" }, "--snippet-focus requires a value.")]
    [InlineData(new[] { "search", "hello", "--max-line-width", "--limit", "5" }, "--max-line-width requires a value.")]
    [InlineData(new[] { "symbols", "hello", "--kind", "--lang", "rust" }, "--kind requires a value.")]
    [InlineData(new[] { "impact", "hello", "--depth", "--lang", "rust" }, "--depth requires a value.")]
    public void QueryEntrypoints_RecognizedOptionAsValueFailsClosed_Issue184(string[] commandAndArgs, string expectedFragment)
    {
        var command = commandAndArgs[0];
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_issue184_{command}_{expectedFragment.GetHashCode():x}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue184.cs",
                "csharp",
                "namespace Issue184; public class T { public void M() { } }");
            MarkGraphAndFoldReady(dbPath);

            var args = commandAndArgs.Skip(1).Concat(new[] { "--db", dbPath, "--json" }).ToArray();
            var (exitCode, stdout, stderr) = CaptureConsole(() => command switch
            {
                "search" => QueryCommandRunner.RunSearch(args, _jsonOptions),
                "symbols" => QueryCommandRunner.RunSymbols(args, _jsonOptions),
                "impact" => QueryCommandRunner.RunImpact(args, _jsonOptions),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
            });

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains(expectedFragment, stderr);
            Assert.Contains("Hint: fix the invalid or missing option value", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Regression lock for #184: non-repeatable value-taking options specified more than once
    // (e.g. `--db /A --db /B`, `--limit 5 --limit 10`) must emit a stderr warning so the
    // override is visible. The last value wins (backward compatible), but users and AI callers
    // can spot a copy/paste or scripted mistake instead of the previous silent take-last.
    // `--top` is canonicalized to `--limit`, so `--limit 5 --top 10` also triggers.
    // Repeatable options (`--path`, `--exclude-path`, `--name`) must NOT warn.
    // #184 の回帰ロック: 非 repeatable な value-taking オプションが 2 回以上指定された場合
    // （例: `--db /A --db /B`、`--limit 5 --limit 10`）、上書きを stderr に警告する。最後の値が
    // 採用されるのは後方互換だが、従来の silent な take-last ではスクリプトやコピペのミスに
    // ユーザーや AI 呼び出し側が気付けない。`--top` は `--limit` と canonical 共有。repeatable
    // な `--path` / `--exclude-path` / `--name` は警告しない。
    [Fact]
    public void QueryEntrypoints_DuplicateSingleValueOptionsWarn_Issue184()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue184_dup_single_value");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue184.cs",
                "csharp",
                "namespace Issue184; public class T { public void M() { } }");

            // Warnings must fire even when the command itself returns zero results (NotFound);
            // the duplicate flag warning is an argv-parsing concern, not a result-set concern.
            // NotFound 等のゼロ件応答でも warn は出るべき。重複フラグは argv 解析段階の関心事で、
            // 検索結果の有無とは独立している。

            // --limit appears twice with different values: rightmost CLI value wins, warning emitted.
            var (exitLimit, _, stderrLimit) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue184", "--db", dbPath, "--json", "--limit", "5", "--limit", "10"], _jsonOptions));
            Assert.NotEqual(CommandExitCodes.UsageError, exitLimit);
            Assert.Contains("Warning: --limit specified more than once; the rightmost CLI value '10' takes precedence over earlier CLI values and any environment/config default.", stderrLimit);

            // --top is canonicalized to --limit, so `--limit 5 --top 10` also warns.
            var (exitTop, _, stderrTop) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue184", "--db", dbPath, "--json", "--limit", "5", "--top", "10"], _jsonOptions));
            Assert.NotEqual(CommandExitCodes.UsageError, exitTop);
            Assert.Contains("Warning: --limit specified more than once; the rightmost CLI value '10' takes precedence over earlier CLI values and any environment/config default.", stderrTop);

            // Single --limit must NOT warn.
            var (exitSingle, _, stderrSingle) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue184", "--db", dbPath, "--json", "--limit", "10"], _jsonOptions));
            Assert.NotEqual(CommandExitCodes.UsageError, exitSingle);
            Assert.DoesNotContain("specified more than once", stderrSingle);

            // Repeatable --path must NOT warn on repetition.
            var (exitPath, _, stderrPath) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue184", "--db", dbPath, "--json", "--path", "src/**", "--path", "tests/**"], _jsonOptions));
            Assert.NotEqual(CommandExitCodes.UsageError, exitPath);
            Assert.DoesNotContain("specified more than once", stderrPath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Regression lock for #184 follow-up: options that legitimately accept separated
    // dash-prefixed literal values (`--db`, `--path`, `--exclude-path`) must preserve the
    // pre-existing "requires a value" error WITH the inline-form hint when followed by a
    // double-dash token. The recognized-option guard added for `--lang --limit 5` style
    // cases must NOT short-circuit these dashed-literal options; instead their error must
    // still guide the user to the `--db=<value>` disambiguation form. `--query` keeps
    // accepting dashed literals as query text (no hint needed).
    // #184 のフォローアップ回帰ロック: separated dashed literal を正当に受け入れる
    // `--db` / `--path` / `--exclude-path` は、double-dash 値が続いた場合 "requires a value" と
    // 同時に inline-form ヒントを返す既存契約を維持する。`--lang --limit 5` 系のために追加した
    // recognized-option guard でこれらを早期に短絡させてはならず、ヒントで `--db=<value>` 形式を
    // ユーザーに案内する。`--query` は引き続き dashed literal を query テキストとして受け入れる。
    [Theory]
    [InlineData("--db")]
    [InlineData("--path")]
    [InlineData("--exclude-path")]
    public void QueryEntrypoints_DashedLiteralOptionsKeepHint_Issue184(string optionName)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_issue184_dashed_literal_{optionName.TrimStart('-')}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue184.cs",
                "csharp",
                "namespace Issue184; public class T { public void M() { } }");

            string[] args = optionName == "--db"
                ? ["Issue184", optionName, "--json"]
                : ["Issue184", "--db", dbPath, optionName, "--json"];

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"{optionName} requires a value", stderr);
            Assert.Contains($"pass it as `{optionName}=<value>`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }




    // Regression lock for #196 follow-up: CLI `--max-line-width` above the shared ceiling
    // (LineWidthFormatter.MaxAllowedLineWidth = 4096) must fail closed with UsageError and
    // empty stdout, matching the MCP `maxLineWidth` contract. Previously the CLI silently
    // clamped the value via LineWidthFormatter.ClampMaxLineWidth and ran to success, while
    // MCP rejected the same 4097. Diverging contracts break automation that wires CLI and
    // MCP together on a fail-close assumption.
    // #196 のフォローアップ回帰ロック: CLI の `--max-line-width` が共有上限
    // (LineWidthFormatter.MaxAllowedLineWidth = 4096) を超えた場合、UsageError と空 stdout で
    // fail-close しなければならず、MCP の `maxLineWidth` 契約と一致させる。以前は
    // LineWidthFormatter.ClampMaxLineWidth で黙って丸めて成功していたため、MCP は 4097 を拒否する
    // 一方で CLI は通るという不整合があり、fail-close 前提の自動化を壊していた。
    [Theory]
    [InlineData("search", "4097", false)]
    [InlineData("search", "8192", false)]
    [InlineData("references", "4097", false)]
    [InlineData("inspect", "4097", false)]
    [InlineData("find", "4097", false)]
    [InlineData("excerpt", "4097", false)]
    // Inline `--max-line-width=<value>` form: catches the silent-clamp regression on
    // the `=`-attached path too. `find` historically rejected every inline `=` form at
    // ValidateFindArgs with `unsupported option for find: --max-line-width=4097`, so
    // this row also pins the `PrepareFindArgs` inline-`=` normalization.
    // インライン `--max-line-width=<value>` 形式の回帰ロック。`find` は歴史的に
    // ValidateFindArgs の段階で inline `=` を全拒否していたため、`PrepareFindArgs` の
    // inline-`=` 正規化もここで固定する。
    [InlineData("search", "4097", true)]
    [InlineData("references", "4097", true)]
    [InlineData("inspect", "4097", true)]
    [InlineData("find", "4097", true)]
    [InlineData("excerpt", "4097", true)]
    public void QueryEntrypoints_MaxLineWidthAboveCeilingFailClosed_Issue196(string command, string value, bool useInlineEquals)
    {
        var projectRoot = TestProjectHelper.CreateTempProject(
            $"cdidx_issue196_maxlinewidth_{command}_{value}_{(useInlineEquals ? "inline" : "space")}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue196Target.cs",
                "csharp",
                """"
                namespace Issue196;
                public class Issue196Target
                {
                    public void Issue196Callee() { }
                }
                public class Issue196Caller
                {
                    public void Run()
                    {
                        var target = new Issue196Target();
                        target.Issue196Callee();
                    }
                }
                """");
            MarkGraphAndFoldReady(dbPath);

            var excerptDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(excerptDir);
            var excerptFilePath = Path.Combine(excerptDir, "Issue196Excerpt.cs");
            File.WriteAllText(
                excerptFilePath,
                "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\n");

            string[] maxLineWidthArgs = useInlineEquals
                ? [$"--max-line-width={value}"]
                : ["--max-line-width", value];
            string[] args = command switch
            {
                "search" => ["Issue196", "--db", dbPath, "--json", .. maxLineWidthArgs],
                "references" => ["Issue196Callee", "--db", dbPath, "--json", .. maxLineWidthArgs],
                "inspect" => ["Issue196Target", "--db", dbPath, "--json", .. maxLineWidthArgs],
                "find" => ["Issue196", "--path", excerptFilePath, "--db", dbPath, "--json", .. maxLineWidthArgs],
                "excerpt" => [excerptFilePath, "--db", dbPath, "--start", "1", "--end", "1", "--json", .. maxLineWidthArgs],
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => command switch
            {
                "search" => QueryCommandRunner.RunSearch(args, _jsonOptions),
                "references" => QueryCommandRunner.RunReferences(args, _jsonOptions),
                "inspect" => QueryCommandRunner.RunInspect(args, _jsonOptions),
                "find" => QueryCommandRunner.RunFind(args, _jsonOptions),
                "excerpt" => QueryCommandRunner.RunExcerpt(args, _jsonOptions),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
            });

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"--max-line-width must be less than or equal to {LineWidthFormatter.MaxAllowedLineWidth}", stderr);
            Assert.Contains($"got '{value}'", stderr);
            Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }









    private void AssertBodyExcerpt(
        Func<string[], JsonSerializerOptions, int> command,
        string[] args,
        string expectedContent)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => command(args, _jsonOptions));

        Assert.True(exitCode == CommandExitCodes.Success, $"exit={exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        Assert.Contains(expectedContent, document.RootElement.GetProperty("body_content").GetString());
    }

    private static int CountLines(string text) => text.Split('\n').Length;



















    [Fact]
    public void RunFindThenExcerpt_JsonKeepsMatchedTokenVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_excerpt_flow");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
            TestProjectHelper.InsertIndexedFile(dbPath, "dist/data.txt", "text", longLine);

            var (findExitCode, findStdout, findStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["TARGET", "--db", dbPath, "--path", "dist/data.txt", "--json", "--exact", "--max-line-width", "96"],
                _jsonOptions));

            using var findDocument = ParseJsonOutput(findStdout);
            var findJson = findDocument.RootElement;
            var line = findJson.GetProperty("line").GetInt32();
            var column = findJson.GetProperty("column").GetInt32();

            var (excerptExitCode, excerptStdout, excerptStderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["dist/data.txt", "--db", dbPath, "--start", line.ToString(), "--end", line.ToString(), "--json", "--max-line-width", "96", "--focus-column", column.ToString(), "--focus-length", "6"],
                _jsonOptions));

            using var excerptDocument = ParseJsonOutput(excerptStdout);
            var excerptJson = excerptDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, findExitCode);
            Assert.Equal(string.Empty, findStderr);
            Assert.Equal(CommandExitCodes.Success, excerptExitCode);
            Assert.Equal(string.Empty, excerptStderr);
            Assert.Contains("TARGET", excerptJson.GetProperty("content").GetString());
            Assert.True(excerptJson.GetProperty("content_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }




















    [Theory]
    [InlineData("callers", "attribute")]
    [InlineData("callers", "annotation")]
    [InlineData("callers", "type_reference")]
    [InlineData("callers", "import")]
    [InlineData("callees", "attribute")]
    [InlineData("callees", "annotation")]
    [InlineData("callees", "type_reference")]
    [InlineData("callees", "import")]
    public void RunCallersCallees_RejectNonCallGraphKind_WithUsageError(string command, string kind)
    {
        // issue #293 + issue #444: `callers` / `callees` must reject non-call-graph reference
        // kinds at the CLI boundary. Metadata (`attribute` / `annotation`) rows are attributed
        // to the enclosing body-range symbol rather than the annotated target, so `callers
        // Obsolete --kind attribute` would return `[Obsolete] void M()` under the enclosing class
        // instead of `M`, and file-level targets like `[assembly: Foo]` drop entirely because
        // `container_name` is NULL. `type_reference` rows are compile-time type-position edges
        // (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) and
        // are not runtime calls, so `callers Foo --kind type_reference` would misreport type
        // mentions as caller edges. `import` rows are structural dependency edges rather than
        // runtime calls, so callers/callees cannot answer them as graph edges. The correct path
        // for these kinds is `references <name> --kind attribute|annotation|type_reference|import`.
        // issue #293 + issue #444 補足: `callers` / `callees` は CLI 境界で非 call-graph な
        // reference kind を必ず弾く。metadata (`attribute` / `annotation`) 行は注釈対象ではなく
        // body-range の外側シンボルに帰属するため、`callers Obsolete --kind attribute` では
        // `[Obsolete] void M()` が `M` ではなく外側クラスに寄り、`[assembly: Foo]` のような
        // file-level target は `container_name = NULL` で完全に脱落する。`type_reference` は
        // 宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な
        // 型言及であり実行時呼び出しではないので、`callers Foo --kind type_reference` は型言及を
        // caller edge として誤って返す。`import` 行は runtime call ではなく構造的な dependency
        // edge なので callers/callees では graph edge として答えられない。正しい経路は
        // `references <name> --kind attribute|annotation|type_reference|import`。
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_{command}_reject_kind_{kind}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var args = new[] { "Symbol", "--db", dbPath, "--kind", kind };

            var (exitCode, _, stderr) = command switch
            {
                "callers" => CaptureConsole(() => QueryCommandRunner.RunCallers(args, _jsonOptions)),
                "callees" => CaptureConsole(() => QueryCommandRunner.RunCallees(args, _jsonOptions)),
                _ => throw new InvalidOperationException($"Unexpected command: {command}")
            };

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"'--kind {kind}' is not supported on '{command}'", stderr);
            Assert.Contains($"references <name> --kind {kind}", stderr);
            if (kind == "import")
                Assert.Contains("Import references are structural dependency edges, not runtime calls", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }



































































    [Fact]
    public void RunExactNameQueries_StaleCSharpVerbatimCanonicalNamesReportDegradedState_Issue628()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_verbatim_stale_issue628");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Verbatim.cs",
                "csharp",
                """
                using Outer.@class;
                using System.Collections.Generic;

                namespace Outer.@class;

                public class Target
                {
                }

                public class C
                {
                    public static implicit operator List<@class>(C value) => new();
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GlobalType.cs",
                "csharp",
                """
                public class @class
                {
                }
                """);

            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET name = 'Outer.@class',
                        name_folded = 'outer.@class'
                    WHERE kind = 'namespace' AND name = 'Outer.class';
                    UPDATE symbols
                    SET name = 'Outer.@class',
                        name_folded = 'outer.@class'
                    WHERE kind = 'import' AND name = 'Outer.class';
                    UPDATE symbols
                    SET name = '@class',
                        name_folded = '@class'
                    WHERE kind = 'class' AND name = 'class';
                    UPDATE symbols
                    SET container_name = 'Outer.@class'
                    WHERE container_name = 'Outer.class';
                    UPDATE symbols
                    SET name = 'implicit operator List<@class>',
                        name_folded = 'implicit operator list<@class>'
                    WHERE kind = 'operator' AND name = 'implicit operator List<class>';
                    DELETE FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var degradedReasonToken = "verbatim identifier";

            var (classExitCode, classStdout, classStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "class", "--name", "class", "--exact-name", "--count"],
                _jsonOptions));

            using var classDocument = ParseJsonOutput(classStdout);
            var classJson = classDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, classExitCode);
            Assert.Equal(string.Empty, classStderr);
            Assert.Equal(0, classJson.GetProperty("count").GetInt32());
            Assert.False(classJson.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("csharp_symbol_name_ready=false", classJson.GetProperty("degraded_reason").GetString());
            Assert.Contains(degradedReasonToken, classJson.GetProperty("degraded_reason").GetString());

            var (namespaceExitCode, namespaceStdout, namespaceStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Outer.class", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--kind", "namespace", "--count"],
                _jsonOptions));

            using var namespaceDocument = ParseJsonOutput(namespaceStdout);
            var namespaceJson = namespaceDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, namespaceExitCode);
            Assert.Equal(string.Empty, namespaceStderr);
            Assert.Equal(0, namespaceJson.GetProperty("count").GetInt32());
            Assert.False(namespaceJson.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("csharp_symbol_name_ready=false", namespaceJson.GetProperty("degraded_reason").GetString());
            Assert.Contains(degradedReasonToken, namespaceJson.GetProperty("degraded_reason").GetString());

            var (operatorExitCode, operatorStdout, operatorStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "implicit operator List<class>", "--exact-name", "--count"],
                _jsonOptions));

            using var operatorDocument = ParseJsonOutput(operatorStdout);
            var operatorJson = operatorDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, operatorExitCode);
            Assert.Equal(string.Empty, operatorStderr);
            Assert.Equal(0, operatorJson.GetProperty("count").GetInt32());
            Assert.False(operatorJson.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("csharp_symbol_name_ready=false", operatorJson.GetProperty("degraded_reason").GetString());
            Assert.Contains(degradedReasonToken, operatorJson.GetProperty("degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }




    [Fact]
    public void RunSymbolsOutlineAndInspect_CSharpInterfaceAndStructContainerMetadataRoundTrips()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_container_metadata_roundtrip_issue474");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace EventMods
                {
                    using System;

                    public interface IBus
                    {
                        event EventHandler Regular;
                        static abstract event EventHandler StaticAbs;
                        static virtual event EventHandler StaticVirt { add { } remove { } }
                    }
                }

                namespace Demo
                {
                    public struct S
                    {
                        public int P { get; set; }
                        public event System.EventHandler E;
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (interfaceExitCode, interfaceStdout, interfaceStderr) = RunBuiltCli(
                ["symbols", "StaticVirt", "--db", dbPath, "--json", "--kind", "event", "--exact-name", "--lang", "csharp"]);
            var (structPropertyExitCode, structPropertyStdout, structPropertyStderr) = RunBuiltCli(
                ["symbols", "P", "--db", dbPath, "--json", "--kind", "property", "--exact-name", "--lang", "csharp"]);
            var (structEventExitCode, structEventStdout, structEventStderr) = RunBuiltCli(
                ["symbols", "E", "--db", dbPath, "--json", "--kind", "event", "--exact-name", "--lang", "csharp"]);
            var (outlineExitCode, outlineStdout, outlineStderr) = RunBuiltCli(
                ["outline", "src/fixture.cs", "--db", dbPath, "--json"]);
            var (inspectExitCode, inspectStdout, inspectStderr) = RunBuiltCli(
                ["inspect", "StaticVirt", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"]);

            var interfaceRow = Assert.Single(ParseJsonLines(interfaceStdout)).RootElement;
            var structPropertyRow = Assert.Single(ParseJsonLines(structPropertyStdout)).RootElement;
            var structEventRow = Assert.Single(ParseJsonLines(structEventStdout)).RootElement;
            using var outlineDocument = ParseJsonOutput(outlineStdout);
            using var inspectDocument = ParseJsonOutput(inspectStdout);
            var outlineJson = outlineDocument.RootElement;
            var inspectJson = inspectDocument.RootElement;
            var outlineSymbols = outlineJson.GetProperty("symbols").EnumerateArray().ToArray();
            var inspectDefinition = Assert.Single(inspectJson.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, interfaceExitCode);
            Assert.Equal(string.Empty, interfaceStderr);
            Assert.Equal(CommandExitCodes.Success, structPropertyExitCode);
            Assert.Equal(string.Empty, structPropertyStderr);
            Assert.Equal(CommandExitCodes.Success, structEventExitCode);
            Assert.Equal(string.Empty, structEventStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);

            Assert.Equal("StaticVirt", interfaceRow.GetProperty("name").GetString());
            Assert.Equal("event", interfaceRow.GetProperty("kind").GetString());
            Assert.Equal("interface", interfaceRow.GetProperty("container_kind").GetString());
            Assert.Equal("IBus", interfaceRow.GetProperty("container_name").GetString());

            Assert.Equal("P", structPropertyRow.GetProperty("name").GetString());
            Assert.Equal("property", structPropertyRow.GetProperty("kind").GetString());
            Assert.Equal("struct", structPropertyRow.GetProperty("container_kind").GetString());
            Assert.Equal("S", structPropertyRow.GetProperty("container_name").GetString());

            Assert.Equal("E", structEventRow.GetProperty("name").GetString());
            Assert.Equal("event", structEventRow.GetProperty("kind").GetString());
            Assert.Equal("struct", structEventRow.GetProperty("container_kind").GetString());
            Assert.Equal("S", structEventRow.GetProperty("container_name").GetString());

            Assert.Contains(
                outlineSymbols,
                symbol => symbol.TryGetProperty("name", out var name)
                    && symbol.TryGetProperty("kind", out var kind)
                    && symbol.TryGetProperty("container_kind", out var containerKind)
                    && symbol.TryGetProperty("container_name", out var containerName)
                    && name.GetString() == "StaticVirt"
                    && kind.GetString() == "event"
                    && containerKind.GetString() == "interface"
                    && containerName.GetString() == "IBus");
            Assert.Contains(
                outlineSymbols,
                symbol => symbol.TryGetProperty("name", out var name)
                    && symbol.TryGetProperty("kind", out var kind)
                    && symbol.TryGetProperty("container_kind", out var containerKind)
                    && symbol.TryGetProperty("container_name", out var containerName)
                    && name.GetString() == "P"
                    && kind.GetString() == "property"
                    && containerKind.GetString() == "struct"
                    && containerName.GetString() == "S");

            Assert.Equal("StaticVirt", inspectDefinition.GetProperty("name").GetString());
            Assert.Equal("interface", inspectDefinition.GetProperty("container_kind").GetString());
            Assert.Equal("IBus", inspectDefinition.GetProperty("container_name").GetString());
            Assert.Contains(
                inspectJson.GetProperty("nearby_symbols").EnumerateArray(),
                symbol => symbol.TryGetProperty("container_kind", out var containerKind)
                    && symbol.TryGetProperty("container_name", out var containerName)
                    && containerKind.GetString() == "interface"
                    && containerName.GetString() == "IBus");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinitionAndInspect_CSharpExactInterpolatedStringCallSites_OnlyReturnRealDefinition()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_inspect_csharp_interpolated_callsite_issue790");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """""
                namespace Demo;

                public sealed class ReporterContext
                {
                    public string ReportsFolderAbsolutePath { get; set; } = string.Empty;
                }

                public sealed class AuditLogGenerateService
                {
                    internal static string DescribeState(string label, string? pathOrCommand)
                        => $"{label}:{pathOrCommand}";

                    public void WriteAuditLog(ReporterContext context, string auditLogPath)
                    {
                        var message = $"""
                            Failed to write audit log for reports folder {context.ReportsFolderAbsolutePath}
                            to {auditLogPath}
                            current state {
                                DescribeState("ReportsFolder", context.ReportsFolderAbsolutePath)}
                            """;
                    }
                }
                """"");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["DescribeState", "--db", dbPath, "--json", "--exact", "--lang", "csharp"],
                _jsonOptions));
            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["DescribeState", "--db", dbPath, "--json", "--exact", "--lang", "csharp"],
                _jsonOptions));

            var definitionRows = ParseJsonLines(definitionStdout);
            using var inspectDocument = ParseJsonOutput(inspectStdout);
            var inspectDefinitions = inspectDocument.RootElement.GetProperty("definitions").EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);

            var definition = Assert.Single(definitionRows).RootElement;
            Assert.Equal("DescribeState", definition.GetProperty("name").GetString());
            Assert.Equal("AuditLogGenerateService", definition.GetProperty("container_name").GetString());
            Assert.Equal("string", definition.GetProperty("return_type").GetString());

            var inspectDefinition = Assert.Single(inspectDefinitions);
            Assert.Equal("DescribeState", inspectDefinition.GetProperty("name").GetString());
            Assert.Equal("AuditLogGenerateService", inspectDefinition.GetProperty("container_name").GetString());
            Assert.Equal("string", inspectDefinition.GetProperty("return_type").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }


    [Fact]
    public void RunOutlineAndDefinition_CSharpMultilineExpressionBodiedPartialProperty_PreservesRangeAndContent()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_partial_property_range");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public partial class Model
                {
                    public partial int Count
                        => 42;
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));
            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Count", "--db", dbPath, "--json", "--exact-name", "--body"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            using var definitionDocument = ParseJsonOutput(definitionStdout);
            var outlineJson = outlineDocument.RootElement;
            var definitionJson = definitionDocument.RootElement;
            var property = Assert.Single(outlineJson.GetProperty("symbols").EnumerateArray().Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "Count"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(5, property.GetProperty("start_line").GetInt32());
            Assert.Equal(6, property.GetProperty("end_line").GetInt32());
            Assert.Equal(5, definitionJson.GetProperty("start_line").GetInt32());
            Assert.Equal(6, definitionJson.GetProperty("end_line").GetInt32());
            Assert.Contains("=> 42;", definitionJson.GetProperty("content").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }



    [Fact]
    public void RunSymbolsAndOutline_CSharpLongRawStringFields_PreserveFullSignature()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_long_raw_string_fields");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var content = string.Join(
                "\n",
                [
                    "namespace Demo;",
                    "",
                    "public class Fixture",
                    "{",
                    "    private static readonly string",
                    "        StaticScript = \"\"\"",
                    .. Enumerable.Range(1, 18).Select(i => $"line{i:00}"),
                    "\"\"\";",
                    "",
                    "    private const string ConstScript = \"\"\"",
                    .. Enumerable.Range(1, 18).Select(i => $"const{i:00}"),
                    "\"\"\";",
                    "}"
                ]);
            File.WriteAllText(Path.Combine(projectRoot, "src", "fixture.cs"), content);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (staticExitCode, staticStdout, staticStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function", "--name", "StaticScript", "--exact-name"],
                _jsonOptions));
            var (constExitCode, constStdout, constStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function", "--name", "ConstScript", "--exact-name"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var staticDocument = ParseJsonOutput(staticStdout);
            using var constDocument = ParseJsonOutput(constStdout);
            using var outlineDocument = ParseJsonOutput(outlineStdout);

            var staticSignature = staticDocument.RootElement.GetProperty("signature").GetString();
            var constSignature = constDocument.RootElement.GetProperty("signature").GetString();
            var outlineSymbols = outlineDocument.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            var outlineStatic = Assert.Single(outlineSymbols.Where(symbol => symbol.GetProperty("name").GetString() == "StaticScript"));
            var outlineConst = Assert.Single(outlineSymbols.Where(symbol => symbol.GetProperty("name").GetString() == "ConstScript"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, staticExitCode);
            Assert.Equal(string.Empty, staticStderr);
            Assert.Equal(CommandExitCodes.Success, constExitCode);
            Assert.Equal(string.Empty, constStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);

            Assert.Contains("StaticScript = \"\"\"", staticSignature);
            Assert.Contains("\"\"\";", staticSignature);
            Assert.Contains("ConstScript = \"\"\"", constSignature);
            Assert.Contains("\"\"\";", constSignature);
            Assert.Contains("StaticScript = \"\"\"", outlineStatic.GetProperty("signature").GetString());
            Assert.Contains("\"\"\";", outlineStatic.GetProperty("signature").GetString());
            Assert.Contains("ConstScript = \"\"\"", outlineConst.GetProperty("signature").GetString());
            Assert.Contains("\"\"\";", outlineConst.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }


    [Fact]
    public void RunOutlineAndDefinition_CSharpMultilineSwitchExpressionProperty_PreservesFullBodyRange()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_switch_expression_property");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public partial class Model
                {
                    public partial int Count
                        => DateTime.Now.Day switch
                        {
                            > 15 => 2,
                            _ => 1
                        };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));
            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Count", "--db", dbPath, "--json", "--exact-name", "--body"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            using var definitionDocument = ParseJsonOutput(definitionStdout);
            var outlineJson = outlineDocument.RootElement;
            var definitionJson = definitionDocument.RootElement;
            var property = Assert.Single(outlineJson.GetProperty("symbols").EnumerateArray().Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "Count"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(5, property.GetProperty("start_line").GetInt32());
            Assert.Equal(10, property.GetProperty("end_line").GetInt32());
            Assert.Equal(5, definitionJson.GetProperty("start_line").GetInt32());
            Assert.Equal(10, definitionJson.GetProperty("end_line").GetInt32());
            Assert.Contains("> 15 => 2,", definitionJson.GetProperty("content").GetString());
            Assert.Contains("_ => 1", definitionJson.GetProperty("content").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }



























    [Fact]
    public void CSharpVerbatimNameNormalizer_StripsGlobalQualifierOnlyAtNamespaceStarts()
    {
        Assert.Equal("Foo.Bar", CSharpVerbatimNameNormalizer.Normalize("global::Foo.Bar"));
        Assert.Equal("Foo.global::Bar", CSharpVerbatimNameNormalizer.Normalize("Foo.global::Bar"));
    }

    [Fact]
    public void ExactSourceSearchNormalizer_DecodesCSharpUnicodeEscapesBeforeVerbatimCleanup()
    {
        const string text = "global::@\\u0063lass.\\U00000046oo";

        var normalized = ExactSourceSearchNormalizer.Normalize(text, "csharp", out var rawIndexMap);

        Assert.Equal("class.Foo", normalized);
        Assert.Equal(normalized.Length, rawIndexMap.Length);
        Assert.Equal(text.IndexOf('\\'), rawIndexMap[0]);
        Assert.Equal(text.LastIndexOf('\\'), rawIndexMap[6]);
    }











    [Theory]
    [InlineData("search", "results")]
    [InlineData("files", "files")]
    public void ZeroResultJson_SymbolAndTextCommands_EmitEnvelopeAndFreshness(string command, string resultsKey)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_zero_json_{command}");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => RunZeroResultCommand(command, dbPath));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, resultsKey);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void ZeroResultJson_GraphCommands_EmitEnvelopeAndFreshness(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_zero_json_{command}");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot, markGraphReady: true);
            var (exitCode, stdout, stderr) = CaptureConsole(() => RunZeroResultCommand(command, dbPath));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, command);
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("deps", "edges")]
    [InlineData("hotspots", "hotspots")]
    public void ZeroResultJson_AggregateCommands_EmitEnvelopeAndFreshness(string command, string resultsKey)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_zero_json_{command}");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot, markGraphReady: true);
            var (exitCode, stdout, stderr) = CaptureConsole(() => RunZeroResultCommand(command, dbPath));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, resultsKey);
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

























    [Fact]
    public void ParseArgs_GraphFormatOutsideDeps_ReturnsParseError()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--format", "json-graph"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--format must be one of text", options.ParseError);
    }

    [Fact]
    public void FindDependencyCycles_ReturnsStronglyConnectedFileComponents()
    {
        var edges = new List<FileDependencyResult>
        {
            new() { SourcePath = "a.cs", TargetPath = "b.cs", ReferenceCount = 1 },
            new() { SourcePath = "b.cs", TargetPath = "a.cs", ReferenceCount = 1 },
            new() { SourcePath = "c.cs", TargetPath = "d.cs", ReferenceCount = 1 },
        };

        var cycles = QueryCommandRunner.FindDependencyCycles(edges);

        var cycle = Assert.Single(cycles);
        Assert.Equal(["a.cs", "b.cs"], cycle);
    }

    [Fact]
    public void RunDeps_CyclesUsesGraphBudgetBeyondDisplayLimit_Issue3185()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_cycle_budget");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            InsertFileWithSymbol(dbPath, "src/HighTarget.cs", "HighTarget");
            InsertFileWithReferences(dbPath, "src/HighCaller.cs", Enumerable.Repeat("HighTarget", 5).ToArray());
            InsertFileWithSymbolsAndReferences(dbPath, "src/CycleA.cs", ["CycleA"], ["CycleB"]);
            InsertFileWithSymbolsAndReferences(dbPath, "src/CycleB.cs", ["CycleB"], ["CycleA"]);
            InsertFileWithSymbolsAndReferences(dbPath, "src/CycleC.cs", ["CycleC"], ["CycleD"]);
            InsertFileWithSymbolsAndReferences(dbPath, "src/CycleD.cs", ["CycleD"], ["CycleC"]);
            MarkDependencyGraphReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--cycles", "--limit", "1", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var cycle = Assert.Single(document.RootElement.GetProperty("cycles").EnumerateArray());
            var nodes = cycle.GetProperty("nodes").EnumerateArray().Select(node => node.GetString()).ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(2, nodes.Length);
            Assert.All(nodes, node => Assert.StartsWith("src/Cycle", node));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_ZeroJson_StaleSqlGraphContractIncludesDegradedStateWhenSqlScopeIsEmpty()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }







    [Fact]
    public void RunDeps_Json_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonGraph_WritesValidGraphPayload()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_json_graph");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--format", "json-graph", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("nodes").GetArrayLength() >= 2);
            Assert.True(json.GetProperty("edges").GetArrayLength() >= 1);
            Assert.True(json.GetProperty("edges")[0].TryGetProperty("reference_count", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_WorkspaceDbJson_AggregatesAndTagsMemberDatabaseEdges()
    {
        var primaryRoot = TestProjectHelper.CreateTempProject("cdidx_deps_workspace_primary");
        var memberRoot = TestProjectHelper.CreateTempProject("cdidx_deps_workspace_member");
        try
        {
            var primaryDb = TestProjectHelper.CreateProjectDb(primaryRoot);
            var memberDb = TestProjectHelper.CreateProjectDb(memberRoot);
            InsertFileWithReference(primaryDb, "src/PrimaryCaller.cs", "SharedTarget");
            InsertFileWithSymbol(memberDb, "src/SharedTarget.cs", "SharedTarget");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", primaryDb, "--workspace-db", memberDb, "--json", "--limit", "10", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var edges = json.GetProperty("edges").EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.NotNull(stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            var edge = Assert.Single(edges);
            Assert.Equal("src/PrimaryCaller.cs", edge.GetProperty("source_path").GetString());
            Assert.Equal("src/SharedTarget.cs", edge.GetProperty("target_path").GetString());
            Assert.Equal(Path.GetFullPath(primaryDb), edge.GetProperty("source_db").GetString());
            Assert.Equal(Path.GetFullPath(memberDb), edge.GetProperty("target_db").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(primaryRoot);
            TestProjectHelper.DeleteDirectory(memberRoot);
        }
    }

    [Fact]
    public void RunDeps_WorkspaceDbJson_CapsCrossDatabaseSymbolSample_Issue3155()
    {
        var primaryRoot = TestProjectHelper.CreateTempProject("cdidx_deps_workspace_symbols_primary");
        var memberRoot = TestProjectHelper.CreateTempProject("cdidx_deps_workspace_symbols_member");
        try
        {
            var primaryDb = TestProjectHelper.CreateProjectDb(primaryRoot);
            var memberDb = TestProjectHelper.CreateProjectDb(memberRoot);
            var symbolNames = Enumerable
                .Range(0, DbReader.DependencySymbolSampleLimit + 5)
                .Select(index => $"SharedTarget{index:D2}")
                .ToArray();
            InsertFileWithReferences(primaryDb, "src/PrimaryCaller.cs", symbolNames);
            InsertFileWithSymbols(memberDb, "src/SharedTargets.cs", symbolNames);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", primaryDb, "--workspace-db", memberDb, "--json", "--limit", "10", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var edge = Assert.Single(document.RootElement.GetProperty("edges").EnumerateArray());
            var sampledSymbols = edge.GetProperty("symbols").GetString()!.Split(',');

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.NotNull(stderr);
            Assert.Equal(symbolNames.Length, edge.GetProperty("reference_count").GetInt32());
            Assert.Equal(DbReader.DependencySymbolSampleLimit, sampledSymbols.Length);
            Assert.DoesNotContain(symbolNames[^1], sampledSymbols);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(primaryRoot);
            TestProjectHelper.DeleteDirectory(memberRoot);
        }
    }

    [Fact]
    public void RunDeps_WorkspaceDbTooManyDistinctDatabases_ReturnsUsageError_Issue3154()
    {
        var primaryRoot = TestProjectHelper.CreateTempProject("cdidx_deps_workspace_fanout_primary");
        try
        {
            var primaryDb = TestProjectHelper.CreateProjectDb(primaryRoot);
            var args = new List<string> { "--db", primaryDb, "--json" };
            for (var i = 0; i < QueryCommandRunner.MaxWorkspaceDependencyDatabaseCount; i++)
                args.AddRange(["--workspace-db", Path.Combine(Path.GetTempPath(), $"cdidx_member_{Guid.NewGuid():N}.db")]);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                args.ToArray(),
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("deps --workspace-db accepts at most", stderr);
            Assert.Contains("ordered cross-database pairs", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(primaryRoot);
        }
    }

    private static void InsertFileWithSymbol(string dbPath, string path, string symbolName)
        => InsertFileWithSymbols(dbPath, path, [symbolName]);

    private static void InsertFileWithSymbols(string dbPath, string path, IReadOnlyList<string> symbolNames)
        => InsertFileWithSymbolsAndReferences(dbPath, path, symbolNames, []);

    private static void InsertFileWithSymbolsAndReferences(
        string dbPath,
        string path,
        IReadOnlyList<string> symbolNames,
        IReadOnlyList<string> referenceNames)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(symbolNames.Select((symbolName, index) =>
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = symbolName,
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            }).ToArray());
        writer.InsertReferences(referenceNames.Select((symbolName, index) =>
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = symbolName,
                ReferenceKind = "type_reference",
                Line = index + 1,
                Column = 1,
                Context = symbolName,
            }).ToArray());
    }

    private static void InsertFileWithReference(string dbPath, string path, string symbolName)
        => InsertFileWithReferences(dbPath, path, [symbolName]);

    private static void InsertFileWithReferences(string dbPath, string path, IReadOnlyList<string> symbolNames)
        => InsertFileWithSymbolsAndReferences(dbPath, path, [], symbolNames);

    private static void MarkDependencyGraphReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkCSharpSymbolNameContractReady();
    }
















    [Theory]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("symbols", false)]
    [InlineData("symbols", true)]
    [InlineData("definition", false)]
    [InlineData("definition", true)]
    [InlineData("references", false)]
    [InlineData("references", true)]
    [InlineData("callers", false)]
    [InlineData("callers", true)]
    [InlineData("callees", false)]
    [InlineData("callees", true)]
    [InlineData("files", false)]
    [InlineData("files", true)]
    [InlineData("find", false)]
    [InlineData("find", true)]
    public void CountOnlyJson_IgnoresLimitAndReturnsTrueTotal(string command, bool useExplicitLimit)
    {
        var (projectRoot, dbPath) = CreateCountOnlyTotalFixtureDb();
        try
        {
            var args = BuildTrueCountArgs(command, dbPath, useExplicitLimit);
            var (exitCode, stdout, stderr) = CaptureConsole(() => RunCountCommand(command, args));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(25, json.GetProperty("count").GetInt32());

            switch (command)
            {
                case "search":
                case "symbols":
                case "definition":
                case "references":
                case "callers":
                case "callees":
                    Assert.Equal(25, json.GetProperty("files").GetInt32());
                    break;
                case "find":
                    Assert.Equal(1, json.GetProperty("files").GetInt32());
                    Assert.Equal(1, json.GetProperty("file_count").GetInt32());
                    break;
                case "files":
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }






















































































    [Fact]
    public void RunGraphCounts_ExactJson_CssScssVariableAliasAppliesToCallersAndCallees()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_css_scss_variable_alias_counts");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "styles"));
            File.WriteAllText(
                Path.Combine(projectRoot, "styles", "theme.scss"),
                """
                $primary: #3366cc;
                $spacing-base: 8px;

                @mixin rounded($radius) {
                  border-radius: $radius;
                }

                %button-base {
                  padding: 4px;
                }

                .button {
                  color: $primary;
                  padding: $spacing-base * 2;
                  @include rounded(4px);
                }

                .card {
                  @extend %button-base;
                  border: 1px solid $primary;
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (referencesCountExitCode, referencesCountStdout, referencesCountStderr) = RunBuiltCli(["references", "$primary", "--db", dbPath, "--json", "--lang", "css", "--exact-name", "--count"]);
            var (callersExitCode, callersStdout, callersStderr) = RunBuiltCli(["callers", "$primary", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);
            var (callersCountExitCode, callersCountStdout, callersCountStderr) = RunBuiltCli(["callers", "$primary", "--db", dbPath, "--json", "--lang", "css", "--exact-name", "--count"]);
            var (calleesExitCode, calleesStdout, calleesStderr) = RunBuiltCli(["callees", "$rounded", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);
            var (calleesCountExitCode, calleesCountStdout, calleesCountStderr) = RunBuiltCli(["callees", "$rounded", "--db", dbPath, "--json", "--lang", "css", "--exact-name", "--count"]);

            using var referencesCountDocument = ParseJsonOutput(referencesCountStdout);
            using var callersCountDocument = ParseJsonOutput(callersCountStdout);
            using var calleesCountDocument = ParseJsonOutput(calleesCountStdout);

            var callersRows = ParseJsonLines(callersStdout);
            var calleesRows = ParseJsonLines(calleesStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, referencesCountExitCode);
            Assert.Equal(string.Empty, referencesCountStderr);
            Assert.Equal(2, referencesCountDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, referencesCountDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal(2, callersRows.Count);
            Assert.All(callersRows, row => Assert.Contains(row.RootElement.GetProperty("caller_name").GetString(), [".button", ".card"]));
            Assert.All(callersRows, row => Assert.Equal("primary", row.RootElement.GetProperty("callee_name").GetString()));
            Assert.All(callersRows, row => Assert.Equal("invoke", row.RootElement.GetProperty("reference_kind").GetString()));

            Assert.Equal(CommandExitCodes.Success, callersCountExitCode);
            Assert.Equal(string.Empty, callersCountStderr);
            Assert.Equal(2, callersCountDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, callersCountDocument.RootElement.GetProperty("files").GetInt32());

            Assert.Equal(CommandExitCodes.Success, calleesExitCode);
            Assert.Equal(string.Empty, calleesStderr);
            var calleesRow = Assert.Single(calleesRows);
            Assert.Equal("rounded", calleesRow.RootElement.GetProperty("caller_name").GetString());
            Assert.Equal("radius", calleesRow.RootElement.GetProperty("callee_name").GetString());
            Assert.Equal("invoke", calleesRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, calleesCountExitCode);
            Assert.Equal(string.Empty, calleesCountStderr);
            Assert.Equal(1, calleesCountDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(1, calleesCountDocument.RootElement.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }



























































































































































































    [Fact]
    public void BuildExactZeroHint_NoRelaxedMatch_DoesNotRunSampleQuery()
    {
        var sampleInvoked = false;

        var result = QueryCommandRunner.BuildExactZeroHint(
            shouldProbe: true,
            anyRelaxedMatch: () => false,
            relaxedCountQuery: () => throw new InvalidOperationException("count should not run"),
            relaxedSampleQuery: () =>
            {
                sampleInvoked = true;
                return new List<string> { "should_not_run" };
            },
            nameSelector: name => name);

        Assert.Null(result);
        Assert.False(sampleInvoked);
    }


    [Fact]
    public void RunSymbolsAndDefinition_ExactNameAtOnly_ReturnsZeroWithoutBroadening()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_exact_name_at_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class Foo
                {
                    public int Bar() => 0;
                }
                """);

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--name", "@", "--exact-name", "--count"],
                _jsonOptions));
            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            var symbolsJson = symbolsDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(0, symbolsJson.GetProperty("count").GetInt32());
            Assert.Equal(0, symbolsJson.GetProperty("files").GetInt32());

            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["@", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));
            using var definitionDocument = ParseJsonOutput(definitionStdout);
            var definitionJson = definitionDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(0, definitionJson.GetProperty("count").GetInt32());
            Assert.Equal(0, definitionJson.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }





































    [Theory]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("symbols")]
    [InlineData("files")]
    [InlineData("inspect")]
    [InlineData("impact")]
    public void QueryCommands_AcceptNamedQueryEscapeForOptionLookingLiterals_Issue923(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_issue923_named_query_{command}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Probe.cs",
                "csharp",
                "namespace Issue923; public class Probe { public void Run() { } } // --path\n");

            var args = new[] { "--query", "--path", "--db", dbPath, "--limit", "1" };
            var (exitCode, _, stderr) = CaptureConsole(() => RunIssue923NamedQueryCommand(command, args));

            Assert.NotEqual(CommandExitCodes.UsageError, exitCode);
            Assert.DoesNotContain("requires a value", stderr);
            Assert.DoesNotContain("is not supported", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }



















    [Theory]
    [InlineData("search")]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("impact")]
    public void QueryCommands_WhitespaceQueryReturnUsageErrorWithDistinctMessage(string commandName)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunQueryCommand(commandName, ["   "]));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {commandName} query cannot be empty or whitespace-only", stderr);
        Assert.DoesNotContain($"{commandName} requires a", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(commandName)}", stderr);
    }

    [Theory]
    [InlineData("search", "search requires a query argument")]
    [InlineData("definition", "definition requires a symbol query argument")]
    [InlineData("references", "references requires a symbol query argument")]
    [InlineData("callers", "callers requires a symbol query argument")]
    [InlineData("callees", "callees requires a caller query argument")]
    [InlineData("impact", "impact requires a symbol query argument")]
    public void QueryCommands_MissingQueryStillReportsRequiresArgument(string commandName, string expectedMessage)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => RunQueryCommand(commandName, []));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains($"Error: {expectedMessage}", stderr);
        Assert.DoesNotContain("query cannot be empty or whitespace-only", stderr);
    }



    private int RunQueryCommand(string commandName, string[] args) => commandName switch
    {
        "search" => QueryCommandRunner.RunSearch(args, _jsonOptions),
        "definition" => QueryCommandRunner.RunDefinition(args, _jsonOptions),
        "references" => QueryCommandRunner.RunReferences(args, _jsonOptions),
        "callers" => QueryCommandRunner.RunCallers(args, _jsonOptions),
        "callees" => QueryCommandRunner.RunCallees(args, _jsonOptions),
        "impact" => QueryCommandRunner.RunImpact(args, _jsonOptions),
        _ => throw new InvalidOperationException($"Unsupported command: {commandName}"),
    };























































    [Fact]
    public void ParseArgs_NormalizesLangAndKindToLowercase()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["QueryCommandRunner", "--lang", "Python", "--kind", "FUNCTION"],
            jsonDefault: false);

        Assert.Equal("python", options.Lang);
        Assert.Equal("function", options.Kind);
    }



    [Theory]
    [InlineData("tsql")]
    [InlineData("t-sql")]
    [InlineData("mssql")]
    [InlineData("sqlserver")]
    public void RunSearchSymbolsReferencesCallersAndCallees_AcceptCommonSqlServerLanguageAliases(string lang)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_tsql_lang_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "schema_target.tsql",
                "sql",
                """
                CREATE PROCEDURE dbo.usp_Target
                AS
                SELECT 1;
                GO
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "schema_caller.tsql",
                "sql",
                """
                CREATE PROCEDURE sales.usp_Caller
                AS
                BEGIN
                    EXEC dbo.usp_Target;
                END
                GO
                """);
            MarkGraphAndFoldReady(dbPath);

            var (searchExitCode, searchStdout, searchStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CREATE PROCEDURE dbo.usp_Target", "--db", dbPath, "--json", "--lang", lang, "--exact-substring"],
                _jsonOptions));
            var (searchCountExitCode, searchCountStdout, searchCountStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["CREATE PROCEDURE dbo.usp_Target", "--db", dbPath, "--lang", lang, "--exact-substring", "--count"],
                _jsonOptions));
            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", lang, "--exact-name", "dbo.usp_Target"],
                _jsonOptions));
            var (referencesExitCode, referencesStdout, referencesStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["--db", dbPath, "--json", "--lang", lang, "--exact-name", "dbo.usp_Target"],
                _jsonOptions));

            var searchRows = ParseJsonLines(searchStdout).Select(document => document.RootElement).ToList();
            var symbolsRows = ParseJsonLines(symbolsStdout).Select(document => document.RootElement).ToList();
            var referencesRows = ParseJsonLines(referencesStdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, searchExitCode);
            Assert.Equal(CommandExitCodes.Success, searchCountExitCode);
            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(CommandExitCodes.Success, referencesExitCode);
            Assert.Equal(string.Empty, searchStderr);
            Assert.Equal(string.Empty, searchCountStderr);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(string.Empty, referencesStderr);

            Assert.Single(searchRows);
            Assert.Equal("schema_target.tsql", searchRows[0].GetProperty("path").GetString());
            Assert.Equal("1", searchCountStdout.Trim());

            Assert.Single(symbolsRows);
            Assert.Equal("dbo.usp_Target", symbolsRows[0].GetProperty("name").GetString());

            Assert.Single(referencesRows);
            Assert.Equal("sales.usp_Caller", referencesRows[0].GetProperty("container_name").GetString());

            var (callersExitCode, callersStdout, callersStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["dbo.usp_Target", "--db", dbPath, "--json", "--lang", lang, "--exact-name"],
                _jsonOptions));
            var (calleesExitCode, calleesStdout, calleesStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["sales.usp_Caller", "--db", dbPath, "--json", "--lang", lang, "--exact-name"],
                _jsonOptions));

            var callersRows = ParseJsonLines(callersStdout).Select(document => document.RootElement).ToList();
            var calleesRows = ParseJsonLines(calleesStdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(CommandExitCodes.Success, calleesExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal(string.Empty, calleesStderr);

            Assert.Single(callersRows);
            Assert.Equal("sales.usp_Caller", callersRows[0].GetProperty("caller_name").GetString());
            Assert.Equal("usp_Target", callersRows[0].GetProperty("callee_name").GetString());

            Assert.Single(calleesRows);
            Assert.Equal("sales.usp_Caller", calleesRows[0].GetProperty("caller_name").GetString());
            Assert.Equal("usp_Target", calleesRows[0].GetProperty("callee_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }








    private static (T Result, string Stdout, string Stderr) CaptureConsole<T>(Func<T> action)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var result = action();
        return (result, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private static (T Result, string Stdout, string Stderr) CaptureConsoleWithInput<T>(string input, Func<T> action)
    {
        using var stdin = new StringReader(input);
        using var capture = ConsoleCapture.StartWithInput(stdin, captureOut: true, captureError: true);
        var result = action();
        return (result, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private int RunCommandWithInvalidSince(string command)
    {
        return command switch
        {
            "search" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--since", "nope"], _jsonOptions),
            "definition" => QueryCommandRunner.RunDefinition(["QueryCommandRunner", "--since", "nope"], _jsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(["QueryCommandRunner", "--since", "nope"], _jsonOptions),
            "files" => QueryCommandRunner.RunFiles(["QueryCommandRunner", "--since", "nope"], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private int RunCommandWithUnsupportedSince(string command, string sinceValue)
    {
        return command switch
        {
            "references" => QueryCommandRunner.RunReferences(["QueryCommandRunner", "--since", sinceValue, "--count"], _jsonOptions),
            "callers" => QueryCommandRunner.RunCallers(["QueryCommandRunner", "--since", sinceValue, "--count"], _jsonOptions),
            "callees" => QueryCommandRunner.RunCallees(["QueryCommandRunner", "--since", sinceValue, "--count"], _jsonOptions),
            "excerpt" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start", "1", "--since", sinceValue], _jsonOptions),
            "map" => QueryCommandRunner.RunMap(["--since", sinceValue], _jsonOptions),
            "inspect" => QueryCommandRunner.RunInspect(["QueryCommandRunner", "--since", sinceValue], _jsonOptions),
            "outline" => QueryCommandRunner.RunOutline(["src/CodeIndex/Program.cs", "--since", sinceValue], _jsonOptions),
            "status" => QueryCommandRunner.RunStatus(["--since", sinceValue], _jsonOptions),
            "impact" => QueryCommandRunner.RunImpact(["QueryCommandRunner", "--since", sinceValue, "--count"], _jsonOptions),
            "deps" => QueryCommandRunner.RunDeps(["--since", sinceValue], _jsonOptions),
            "hotspots" => QueryCommandRunner.RunHotspots(["--since", sinceValue, "--count"], _jsonOptions),
            "unused" => QueryCommandRunner.RunUnused(["--since", sinceValue, "--count"], _jsonOptions),
            "validate" => QueryCommandRunner.RunValidate(["--since", sinceValue], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private int RunCommandWithUnsupportedOption(string command, string[] args)
    {
        return command switch
        {
            "search" => QueryCommandRunner.RunSearch(["QueryCommandRunner", .. args], _jsonOptions),
            "map" => QueryCommandRunner.RunMap(args, _jsonOptions),
            "inspect" => QueryCommandRunner.RunInspect(["QueryCommandRunner", .. args], _jsonOptions),
            "status" => QueryCommandRunner.RunStatus(args, _jsonOptions),
            "validate" => QueryCommandRunner.RunValidate(args, _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private int RunCommandWithMissingOrSwallowedValue(string scenario)
    {
        return scenario switch
        {
            "search-limit-tail" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--limit"], _jsonOptions),
            "search-top-tail" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--top"], _jsonOptions),
            "search-db-tail" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--db"], _jsonOptions),
            "search-db-swallow" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--db", "--count"], _jsonOptions),
            "search-db-unknown-double-dash" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--db", "--mystery"], _jsonOptions),
            "search-db-recognized-double-dash" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--db", "--lang", "--count"], _jsonOptions),
            "search-lang-swallow" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--lang", "--count"], _jsonOptions),
            "search-lang-unknown-double-dash" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--lang", "--mystery", "--count"], _jsonOptions),
            "search-path-swallow" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--path", "--count"], _jsonOptions),
            "search-exclude-path-swallow" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--exclude-path", "--count"], _jsonOptions),
            "definition-kind-swallow" => QueryCommandRunner.RunDefinition(["QueryCommandRunner", "--kind", "--count"], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    private int RunCommandWithEmptyInlineStringValue(string scenario)
    {
        return scenario switch
        {
            "search-db-inline-empty" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--db="], _jsonOptions),
            "search-lang-inline-empty" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--lang="], _jsonOptions),
            "search-path-inline-empty" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--path="], _jsonOptions),
            "search-exclude-path-inline-empty" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--exclude-path="], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    private int RunCommandWithUnexpectedPositionals(string scenario)
    {
        return scenario switch
        {
            "search-extra" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "extra"], _jsonOptions),
            "excerpt-extra" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "extra", "--start", "1"], _jsonOptions),
            "map-extra" => QueryCommandRunner.RunMap(["stray"], _jsonOptions),
            "outline-extra" => QueryCommandRunner.RunOutline(["src/CodeIndex/Program.cs", "extra"], _jsonOptions),
            "status-extra" => QueryCommandRunner.RunStatus(["stray"], _jsonOptions),
            "validate-extra" => QueryCommandRunner.RunValidate(["stray"], _jsonOptions),
            "languages-extra" => QueryCommandRunner.RunLanguages(["stray"], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    private int RunCommandWithInvalidNumeric(string scenario)
    {
        return scenario switch
        {
            "search-limit" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--limit", "nope"], _jsonOptions),
            "search-top" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--top", "nope"], _jsonOptions),
            "search-snippet-lines" => QueryCommandRunner.RunSearch(["QueryCommandRunner", "--snippet-lines", "nope"], _jsonOptions),
            "impact-depth" => QueryCommandRunner.RunImpact(["QueryCommandRunner", "--depth", "nope", "--count"], _jsonOptions),
            "excerpt-start" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start", "nope"], _jsonOptions),
            "excerpt-end" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start", "1", "--end", "nope"], _jsonOptions),
            "excerpt-before" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start", "1", "--before", "nope"], _jsonOptions),
            "excerpt-after" => QueryCommandRunner.RunExcerpt(["src/CodeIndex/Program.cs", "--start", "1", "--after", "nope"], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    private static int RunGraphCommand(string command, string[] args, JsonSerializerOptions jsonOptions) => command switch
    {
        "references" => QueryCommandRunner.RunReferences(args, jsonOptions),
        "callers" => QueryCommandRunner.RunCallers(args, jsonOptions),
        "callees" => QueryCommandRunner.RunCallees(args, jsonOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported graph command"),
    };

    private static string[] GetExactZeroArgs(string command, string dbPath, int limit, string? queryOverride, bool countOnly = false)
    {
        var query = queryOverride ?? command switch
        {
            "references" => "Target",
            "callers" => "Target",
            "callees" => "Caller",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported graph command"),
        };

        return countOnly
            ? [query, "--db", dbPath, "--json", "--count", "--exact", "--limit", limit.ToString()]
            : [query, "--db", dbPath, "--json", "--exact", "--limit", limit.ToString()];
    }

    private static void SeedGraphExactZeroFixture(string dbPath, string command)
    {
        var content = command switch
        {
            "references" or "callers" => """
                public class App
                {
                    public void TargetWork1() { }
                    public void TargetWork2() { }
                    public void TargetWork3() { }
                    public void TargetWork4() { }
                    public void TargetWork5() { }
                    public void TargetWork6() { }
                    public void TargetWork7() { }

                    public void Caller1() { TargetWork1(); }
                    public void Caller2() { TargetWork2(); }
                    public void Caller3() { TargetWork3(); }
                    public void Caller4() { TargetWork4(); }
                    public void Caller5() { TargetWork5(); }
                    public void Caller6() { TargetWork6(); }
                    public void Caller7() { TargetWork7(); }
                }
                """,
            "callees" => """
                public class App
                {
                    public void Called1() { }
                    public void Called2() { }
                    public void Called3() { }
                    public void Called4() { }
                    public void Called5() { }
                    public void Called6() { }
                    public void Called7() { }

                    public void CallerWork1() { Called1(); }
                    public void CallerWork2() { Called2(); }
                    public void CallerWork3() { Called3(); }
                    public void CallerWork4() { Called4(); }
                    public void CallerWork5() { Called5(); }
                    public void CallerWork6() { Called6(); }
                    public void CallerWork7() { Called7(); }
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported graph command"),
        };

        TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
    }

    private static void MarkStatusReadinessReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
        Assert.True(writer.MarkFoldReady());
        writer.MarkCSharpSymbolNameContractReady();
        writer.MarkMetadataTargetReady("csharp");
        writer.MarkSqlGraphContractReady();
        writer.MarkHotspotFamilyReady("csharp", "test");
    }

    private static void ExecuteNonQuery(DbContext db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // --- TryParseIso8601Since tests / TryParseIso8601Sinceテスト ---

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("2024-01-15T10:30")]              // minute precision / 分精度
    [InlineData("2024-01-15T10:30Z")]
    [InlineData("2024-01-15T10:30+09:00")]
    [InlineData("2024-01-15T10:30:00")]
    [InlineData("2024-01-15T10:30:00Z")]
    [InlineData("2024-01-15T10:30:00+09:00")]
    [InlineData("2024-01-15T10:30:00.000Z")]
    [InlineData("2024-01-15T10:30:00.123")]       // offsetless fractional / オフセットなし小数秒
    [InlineData("2024-01-15T10:30:00.1234567Z")]
    [InlineData("2024-01-15T10:30:00.1Z")]        // 1-digit fraction / 1桁小数
    public void TryParseIso8601Since_AcceptsValidIsoFormats(string input)
    {
        var ok = QueryCommandRunner.TryParseIso8601Since(input, out var result);
        Assert.True(ok, $"Expected '{input}' to be accepted as ISO 8601");
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Theory]
    [InlineData("01/02/2024")]        // ambiguous locale-dependent / ロケール依存の曖昧な形式
    [InlineData("1/2/2024")]
    [InlineData("02-Jan-2024")]
    [InlineData("Jan 15, 2024")]
    [InlineData("not-a-date")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void TryParseIso8601Since_RejectsNonIsoFormats(string input)
    {
        var ok = QueryCommandRunner.TryParseIso8601Since(input, out _);
        Assert.False(ok, $"Expected '{input}' to be rejected as non-ISO 8601");
    }

    [Fact]
    public void TryParseIso8601Since_DateOnlyTreatedAsUtc()
    {
        // Issue #1545: offsetless inputs resolve to the same UTC instant in every timezone, so
        // teammates running the same `--since 2024-06-15` query agree on the cutoff. Append `Z`
        // or an explicit offset to opt out.
        // Issue #1545: オフセットなしの入力はどのタイムゾーンでも同じUTC時点になり、`--since 2024-06-15`
        // が地域差で揺れない。明示したい場合は `Z` または `+09:00` を付ける。
        QueryCommandRunner.TryParseIso8601Since("2024-06-15", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void TryParseIso8601Since_OffsetlessTimestampTreatedAsUtc()
    {
        // Issue #1545: offsetless date-times must not shift by the caller's local TZ offset.
        // Issue #1545: オフセットなしの日時はローカルTZオフセットの分だけずれてはならない。
        QueryCommandRunner.TryParseIso8601Since("2024-06-15T12:00:00", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void TryParseIso8601Since_ExplicitUtcTimestamp()
    {
        QueryCommandRunner.TryParseIso8601Since("2024-06-15T12:00:00Z", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void TryParseIso8601Since_ConvertsTimezoneOffsetToUtc()
    {
        QueryCommandRunner.TryParseIso8601Since("2024-06-15T12:00:00+09:00", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 3, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ParseArgs_RejectsSinceWithAmbiguousDate()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since", "01/02/2024"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("could not parse", options.ParseError);
    }

    [Fact]
    public void ParseArgs_AcceptsSinceWithIsoDate()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since", "2024-01-02"], jsonDefault: false);
        Assert.Null(options.ParseError);
        Assert.NotNull(options.Since);
        // Issue #1545: offsetless date → UTC midnight, independent of caller TZ /
        // Issue #1545: オフセットなし → UTC深夜（呼び出し側TZに依存しない）
        Assert.Equal(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), options.Since.Value);
    }

    [Fact]
    public void ParseArgs_RejectsBareSinceWithNoValue()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("--since requires a value", options.ParseError);
    }

    [Fact]
    public void ParseArgs_RejectsBareSinceForFiles()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["files", "--since"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("--since requires a value", options.ParseError);
    }

    private static JsonDocument ParseJsonOutput(string stdout)
    {
        var jsonLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last(line =>
            {
                using var document = JsonDocument.Parse(line);
                return !IsJsonStreamDoneSentinel(document.RootElement);
            });
        return JsonDocument.Parse(jsonLine);
    }

    private static void SanitizeChildCliEnvironment(System.Diagnostics.ProcessStartInfo psi)
    {
        // Keep subprocess CLI tests from inheriting temporary cdidx knobs set by parallel tests.
        // subprocess CLI テストが並列テストの一時的な cdidx 設定を継承しないようにする。
        foreach (var name in ChildCliEnvironmentVariablesToRemove)
            psi.Environment.Remove(name);

        var configSourceNames = new List<string>();
        foreach (var name in psi.Environment.Keys)
        {
            if (name.StartsWith(CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix, StringComparison.Ordinal))
                configSourceNames.Add(name);
        }

        foreach (var name in configSourceNames)
            psi.Environment.Remove(name);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunBuiltCli(string[] args, string? workingDirectory = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory ?? GetRepositoryRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(GetBuiltCliDllPath());
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        SanitizeChildCliEnvironment(psi);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cdidx subprocess / cdidx サブプロセスの起動に失敗");
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPublishedCli(string publishedDll, string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(publishedDll);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        SanitizeChildCliEnvironment(psi);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start published cdidx subprocess / 公開済み cdidx サブプロセスの起動に失敗");
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static string PublishTrimmedCli(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var buildOutputDir = Path.Combine(outputDir, "bin", "publish") + Path.DirectorySeparatorChar;
        var intermediateDir = Path.Combine(outputDir, "obj", "publish") + Path.DirectorySeparatorChar;

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add(Path.Combine("src", "CodeIndex", "CodeIndex.csproj"));
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--runtime");
        psi.ArgumentList.Add(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("-p:PublishTrimmed=true");
        psi.ArgumentList.Add("-p:SelfContained=true");
        psi.ArgumentList.Add("-p:PublishSingleFile=false");
        psi.ArgumentList.Add($"-p:OutputPath={buildOutputDir}");
        psi.ArgumentList.Add($"-p:IntermediateOutputPath={intermediateDir}");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish / dotnet publish の起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed: {stdout}{stderr}".Trim());

        var publishedDll = Path.Combine(outputDir, "cdidx.dll");
        if (!File.Exists(publishedDll))
            throw new InvalidOperationException($"Published cdidx.dll not found at {publishedDll}");

        return publishedDll;
    }

    private static string GetBuiltCliDllPath()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var fallbackTfms = new[] { tfm, "net8.0" }.Distinct(StringComparer.Ordinal);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        var fallbackConfigurations = new[] { configuration, "Debug", "Release" }
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var candidateConfiguration in fallbackConfigurations)
            {
                foreach (var candidateTfm in fallbackTfms)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "CodeIndex", "bin", candidateConfiguration!, candidateTfm, "cdidx.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate built cdidx.dll from test output path / テスト出力パスから cdidx.dll を特定できませんでした");
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src", "CodeIndex")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }

    private static List<JsonDocument> ParseJsonLines(string stdout)
    {
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line))
            .Where(document => !IsJsonStreamDoneSentinel(document.RootElement))
            .ToList();
    }

    private static bool IsJsonStreamDoneSentinel(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("done", out var done)
            && done.ValueKind is JsonValueKind.True
            && element.TryGetProperty("interrupted", out _)
            && element.TryGetProperty("count", out _);

    private static (string ProjectRoot, string DbPath) CreateUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_confidence");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/config/unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "PathResolver",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "public class PathResolver",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AdoptionService",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public class AdoptionService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "TokenService",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public class TokenService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AppSettings",
                Line = 9,
                StartLine = 9,
                EndLine = 11,
                Signature = "public class AppSettings",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ApplyConfiguration",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public void ApplyConfiguration()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UseIOptions",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public void UseIOptions()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ConnectionString",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string ConnectionString { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreatePlainCliOptionsUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_cli_options");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/cli_options_fixture.cs",
            Lang = "csharp",
            Size = 180,
            Lines = 6,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "CliOptions",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public sealed class CliOptions",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ShowHelp",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public bool ShowHelp { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ProjectPath",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public string? ProjectPath { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateReflectionUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_reflection");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateReflectionDiversifiedUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_reflection_diversified");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_diversified_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 12,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                    public void Run() { Hidden(); }
                    private void Hidden() { }
                    internal void InternalOnly() { }
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateReflectionCommentedUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_reflection_commented");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_fixture.cs",
            Lang = "csharp",
            Size = 220,
            Lines = 9,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /// Bound from JSON payload.
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateQualifiedReflectionUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_reflection_qualified");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_qualified_fixture.cs",
            Lang = "csharp",
            Size = 360,
            Lines = 12,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [global::System.Text.Json.Serialization.JsonPropertyName("full_name")]
                    public string QualifiedName { get; set; } = string.Empty;
                    [JsonPropertyNameAttribute("display_name")]
                    public string SuffixedName { get; set; } = string.Empty;
                    [System.Text.Json.Serialization.JsonIgnoreAttribute]
                    public string IgnoredName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 10,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "QualifiedName",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string QualifiedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "SuffixedName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string SuffixedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredName",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string IgnoredName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateBlockCommentReflectionUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_reflection_block_comment");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 280,
            Lines = 10,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /* bound from payload
                       via serializer */
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateUnsupportedLanguageUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_text_json");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "script.txt",
            Lang = "text",
            Size = 64,
            Lines = 6,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "helper",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "helper() {",
            },
        ]);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string ProjectRoot, string DbPath) CreateLargePublicUnusedFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_large_public");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/large_public_unused_fixture.cs",
            Lang = "csharp",
            Size = 16000,
            Lines = 2600,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "public class PublicNoise0000 { }",
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2500; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = $"public class PublicNoise{i:D4} {{ }}",
                Visibility = "public",
            });
        }
        writer.InsertSymbols(symbols);
        writer.MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static void MarkGraphAndFoldReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
    }

    private static string CreateSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/target.sql",
            "sql",
            """
            CREATE FUNCTION dbo.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/caller.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                SELECT dbo.fn_Target();
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateMixedSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/mixed.cs",
            "csharp",
            """
            public class MixedCalls
            {
                public void N() { }

                public void M()
                {
                    N();
                }
            }
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateMixedSqlGraphContractCountFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/a.cs",
            "csharp",
            """
            public class C
            {
                public void Target() { }

                public void Caller()
                {
                    Target();
                }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/z.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.SqlCaller
            AS
            BEGIN
                EXEC dbo.Target;
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateSqlGraphContractZeroResultFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/a.cs",
            "csharp",
            """
            public class C
            {
                public void M() { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/b.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.Target
            AS
            BEGIN
                SELECT 1;
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static void DowngradeSqlGraphContractRows(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE symbol_references
            SET symbol_name = 'fn_Target',
                symbol_name_folded = 'fn_target',
                column_number = 1
            WHERE symbol_name = 'dbo.fn_Target';
            DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
            """;
        cmd.ExecuteNonQuery();
    }

    private static void DowngradeMixedSqlGraphContractCountRows(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE symbol_references
            SET symbol_name = 'Target',
                symbol_name_folded = 'target',
                column_number = 1
            WHERE symbol_name = 'dbo.Target';
            DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
            """;
        cmd.ExecuteNonQuery();
    }

    private static void DowngradeSqlGraphContractVersion(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';";
        cmd.ExecuteNonQuery();
    }

    private static (string ProjectRoot, string DbPath) CreateCountOnlyTotalFixtureDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_true_count");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

        for (int i = 1; i <= 25; i++)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/search/Search{i:D2}.txt",
                "text",
                $"needletoken hit {i}\n");

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/symbols/CountTarget{i:D2}.cs",
                "csharp",
                $$"""
                public class CountTarget{{i:D2}}
                {
                    public void Value{{i:D2}}() { }
                }
                """);

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/graph/CountCaller{i:D2}.cs",
                "csharp",
                $$"""
                public class CountCaller{{i:D2}}
                {
                    public void Invoke()
                    {
                        CountRun();
                    }
                }
                """);

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/files/CountFile{i:D2}.txt",
                "text",
                $"file fixture {i}\n");
        }

        var findContent = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"guard {i:D2}")) + "\n";
        TestProjectHelper.InsertIndexedFile(dbPath, "src/find/Sample.cs", "csharp", findContent);
        MarkGraphAndFoldReady(dbPath);
        return (projectRoot, dbPath);
    }

    private string[] BuildTrueCountArgs(string command, string dbPath, bool useExplicitLimit)
    {
        var args = command switch
        {
            "search" => new List<string> { "needletoken", "--db", dbPath, "--json", "--count" },
            "symbols" => new List<string> { "CountTarget", "--db", dbPath, "--json", "--count" },
            "definition" => new List<string> { "CountTarget", "--db", dbPath, "--json", "--count" },
            "references" => new List<string> { "CountRun", "--db", dbPath, "--json", "--count", "--exact" },
            "callers" => new List<string> { "CountRun", "--db", dbPath, "--json", "--count", "--exact" },
            "callees" => new List<string> { "Invoke", "--db", dbPath, "--json", "--count", "--exact" },
            "files" => new List<string> { "CountFile", "--db", dbPath, "--json", "--count" },
            "find" => new List<string> { "guard", "--db", dbPath, "--json", "--count", "--path", "src/find/Sample.cs" },
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

        if (useExplicitLimit)
            args.AddRange(["--limit", "5"]);

        return [.. args];
    }

    private int RunCountCommand(string command, string[] args)
    {
        return command switch
        {
            "search" => QueryCommandRunner.RunSearch(args, _jsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(args, _jsonOptions),
            "definition" => QueryCommandRunner.RunDefinition(args, _jsonOptions),
            "references" => QueryCommandRunner.RunReferences(args, _jsonOptions),
            "callers" => QueryCommandRunner.RunCallers(args, _jsonOptions),
            "callees" => QueryCommandRunner.RunCallees(args, _jsonOptions),
            "files" => QueryCommandRunner.RunFiles(args, _jsonOptions),
            "find" => QueryCommandRunner.RunFind(args, _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private int RunIssue923NamedQueryCommand(string command, string[] args)
    {
        return command switch
        {
            "definition" => QueryCommandRunner.RunDefinition(args, _jsonOptions),
            "references" => QueryCommandRunner.RunReferences(args, _jsonOptions),
            "callers" => QueryCommandRunner.RunCallers(args, _jsonOptions),
            "callees" => QueryCommandRunner.RunCallees(args, _jsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(args, _jsonOptions),
            "files" => QueryCommandRunner.RunFiles(args, _jsonOptions),
            "inspect" => QueryCommandRunner.RunInspect(args, _jsonOptions),
            "impact" => QueryCommandRunner.RunImpact(args, _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private static string CreateIndexedDbWithSingleFile(string projectRoot, bool markGraphReady = false)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/app.cs",
            "csharp",
            """
            public class App
            {
                public void HandleRequest() { }
            }
            """);

        if (markGraphReady)
        {
            using var db = new DbContext(dbPath);
            var writer = new DbWriter(db.Connection);
            writer.MarkGraphReady();
        }

        return dbPath;
    }

    private static string CreateHotspotFamilyFixtureDb(string projectRoot, bool markHotspotFamilyReady)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Api.Part1.cs",
            "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Api.Part2.cs",
            "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Caller.cs",
            "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        if (markHotspotFamilyReady)
            writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
        return dbPath;
    }

    private static string CreateLegacyDbWithoutIndexedAt(string projectRoot)
    {
        var dbPath = Path.Combine(projectRoot, "legacy.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL UNIQUE,
                    lang TEXT,
                    size INTEGER,
                    lines INTEGER,
                    modified DATETIME
                );
                CREATE TABLE symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id INTEGER NOT NULL,
                    name TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO files (path, lang, size, lines, modified)
                VALUES ('src/legacy.cs', 'csharp', 42, 3, '2026-01-01T00:00:00Z');
                """;
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private int RunZeroResultCommand(string command, string dbPath)
    {
        return command switch
        {
            "search" => QueryCommandRunner.RunSearch(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "files" => QueryCommandRunner.RunFiles(["definitely-missing-path", "--db", dbPath, "--json"], _jsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "definition" => QueryCommandRunner.RunDefinition(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "references" => QueryCommandRunner.RunReferences(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "callers" => QueryCommandRunner.RunCallers(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "callees" => QueryCommandRunner.RunCallees(["DefinitelyMissingSymbol", "--db", dbPath, "--json"], _jsonOptions),
            "deps" => QueryCommandRunner.RunDeps(["--db", dbPath, "--json"], _jsonOptions),
            "unused" => QueryCommandRunner.RunUnused(["--db", dbPath, "--json", "--kind", "delegate"], _jsonOptions),
            "hotspots" => QueryCommandRunner.RunHotspots(["--db", dbPath, "--json", "--kind", "delegate"], _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    private static void AssertZeroResultPayload(JsonElement json, string resultsKey)
    {
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.True(json.TryGetProperty(resultsKey, out var results));
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Equal(0, results.GetArrayLength());
        Assert.True(json.TryGetProperty("indexed_file_count", out var indexedFileCount));
        Assert.True(indexedFileCount.GetInt64() > 0);
        Assert.True(json.GetProperty("freshness_available").GetBoolean());
        Assert.True(json.TryGetProperty("indexed_at", out var indexedAt));
        Assert.Equal(JsonValueKind.String, indexedAt.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(indexedAt.GetString()));
    }

    private static void DropGraphExactFallbackIndexes(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbol_refs_name_nocase;
            DROP INDEX IF EXISTS idx_symbol_refs_container_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static (string ProjectRoot, string ReadOnlyUri) CreateReadOnlyMissingGraphTableDb(string projectName)
    {
        var projectRoot = TestProjectHelper.CreateTempProject(projectName);
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        using (var db = new DbContext(dbPath))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = """
                DROP TABLE symbol_references;
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return (projectRoot, new Uri(dbPath).AbsoluteUri + "?immutable=1");
    }

    private static void DropSymbolExactFallbackIndex(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbols_name_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static void ForceLegacyExactFallbackMode(string dbPath)
    {
        using var db = new DbContext(dbPath);
        db.ClearReadyFlags();
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
    }

    private static void DropGraphTable(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
                DROP TABLE IF EXISTS symbol_references;
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
        cmd.ExecuteNonQuery();
    }

    private static void DropChunksTables(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS fts_chunks;
            DROP TABLE IF EXISTS chunks;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void RunImpactPartialClassZeroResultIteration(int iteration)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_impact_partial_stability_{iteration}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Worker.Part1.cs", "csharp",
                """
                public partial class Worker
                {
                    public void Start() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Worker.Part2.cs", "csharp",
                """
                public partial class Worker
                {
                    public void Stop() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Worker", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, "callers");
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.True(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal("multiple_definition_files", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private void RunImpactImportOnlyZeroResultIteration(int iteration)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_impact_import_stability_{iteration}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.py", "python",
                """
                import requests
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["requests", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, "callers");
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("definition_count").GetInt32());
            Assert.Equal("non_callable_symbol_kind", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbolsAndReferences_JsTemplateLiteral_SuppressesPhantomsButKeepsInterpolationCalls()
    {
        // Issue #291 CLI integration: a multi-line JS template literal must not
        // surface phantom function/class symbols for code-shaped body text, while
        // a real call inside a ${...} interpolation hole must still produce a
        // reference edge via the call-graph pipeline.
        // issue #291 の CLI 統合: 複数行 JS テンプレートリテラルはコード風本文から
        // phantom な function/class を発生させず、${...} ホール内の本物の呼び出しは
        // 参照グラフに edge として残ること。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_js_template_literal_masking");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.js"),
                """
                function caller() {
                    const src = `
                function fakeFromTemplate() {}
                class FakeClassInTemplate {}
                    ${runTask()} trailing
                    `;
                    realCall();
                }

                function runTask() {}
                function realCall() {}
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (phantomFnExit, _, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["fakeFromTemplate", "--db", dbPath, "--json", "--exact-name", "--lang", "javascript"],
                _jsonOptions));
            var (phantomClsExit, _, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["FakeClassInTemplate", "--db", dbPath, "--json", "--exact-name", "--lang", "javascript"],
                _jsonOptions));

            var (refsExit, refsStdout, refsStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["runTask", "--db", dbPath, "--json", "--exact-name", "--lang", "javascript"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            // Phantom symbols must not exist; zero-result queries now exit successfully by default.
            // phantom シンボルは存在してはならない。0 件クエリは既定で成功終了する。
            Assert.Equal(CommandExitCodes.Success, phantomFnExit);
            Assert.Equal(CommandExitCodes.Success, phantomClsExit);

            // The real call inside the `${...}` interpolation hole must still be visible.
            // ${...} 補間ホール内の本物の呼び出しは参照として残っていること。
            Assert.Equal(CommandExitCodes.Success, refsExit);
            Assert.Equal(string.Empty, refsStderr);
            var referenceDocuments = ParseJsonLines(refsStdout);
            try
            {
                Assert.Contains(referenceDocuments, doc =>
                    doc.RootElement.GetProperty("symbol_name").GetString() == "runTask"
                    && doc.RootElement.GetProperty("reference_kind").GetString() == "call"
                    && doc.RootElement.GetProperty("container_name").GetString() == "caller");
            }
            finally
            {
                foreach (var doc in referenceDocuments)
                {
                    doc.Dispose();
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }









    private static void SetIndexedFileSize(string dbPath, string path, long size)
    {
        using var db = new DbContext(dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "UPDATE files SET size = $size WHERE path = $path";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }
}
