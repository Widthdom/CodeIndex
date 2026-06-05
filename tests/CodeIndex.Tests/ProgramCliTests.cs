using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace CodeIndex.Tests;

/// <summary>
/// Black-box CLI tests for Program entrypoint behavior.
/// Program エントリポイント挙動のブラックボックステスト。
/// </summary>
public class ProgramCliTests
{
    [Theory]
    [InlineData("mcp", "--db", "Error: --db requires a value.")]
    [InlineData("mcp", "--db", "--json", "Error: --db requires a value.")]
    [InlineData("mcp", "--since", "nope", "Error: could not parse --since value 'nope' as a date/time.")]
    public void Mcp_InvalidArgumentsReturnUsageError(string command, string arg1, string arg2OrExpected, string? expectedError = null)
    {
        var args = expectedError == null
            ? new[] { command, arg1 }
            : new[] { command, arg1, arg2OrExpected };
        var expected = expectedError ?? arg2OrExpected;

        var (exitCode, _, stderr) = RunCliInSubprocess(args);

        Assert.Equal(1, exitCode);
        Assert.Contains(expected, stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
        Assert.DoesNotContain("MCP server running", stderr);
    }

    [Fact]
    public void Mcp_UnsupportedOptionReturnUsageError()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --json is not supported for mcp; MCP already speaks JSON-RPC", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
    }

    [Fact]
    public void Mcp_HttpOversizedLimitEnvironmentReturnsUsageError()
    {
        var oversized = (HttpMcpTransport.MaxConfiguredRequestBodyBytes + 1).ToString(CultureInfo.InvariantCulture);
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?> { [HttpMcpTransport.MaxRequestBodyBytesEnvVar] = oversized });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(HttpMcpTransport.MaxRequestBodyBytesEnvVar, stderr);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}",
            stderr,
            StringComparison.Ordinal);
        Assert.Contains("HTTP limits:", stderr);
        Assert.DoesNotContain("HTTP transport listening", stderr);
    }

    [Fact]
    public void Mcp_DbAcceptsLeadingDoubleDashPathValueViaInlineLiteral()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db=--tmp.db"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("requires a value", stderr);
    }

    [Fact]
    public void Mcp_DbAcceptsRecognizedOptionTokenViaInlineValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db=--json"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("requires a value", stderr);
    }

    [Fact]
    public void Mcp_DbRejectsSeparatedUnknownDoubleDashValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db", "--mystery"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("`--db=<value>`", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
    }

    [Fact]
    public void Mcp_DbRejectsEmptyInlineValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db="]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
    }

    [Fact]
    public void Symbols_NameHelpLikeValueReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["symbols", "--name", "-h"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--name requires a value", stderr);
        Assert.DoesNotContain("██████╗", stderr);
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("-q")]
    [InlineData("--silent")]
    public void QueryQuietFlag_SuppressesInformationalStderrOnZeroResults(string quietFlag)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_program_quiet_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = RunCliInSubprocess([quietFlag, "search", "definitely_missing_query", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void QueryQuietEnvironment_SuppressesVerboseStderr()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_program_quiet_env");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = RunCliInSubprocess(
                ["search", "definitely_missing_query", "--verbose", "--db", dbPath],
                new Dictionary<string, string?> { [ProgramRunner.QuietEnvironmentVariable] = "1" });

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void QueryQuietFlag_PreservesErrorLines()
    {
        var missingDbPath = Path.Combine(Path.GetTempPath(), $"cdidx_missing_{Guid.NewGuid():N}.db");

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--quiet", "search", "Run", "--db", missingDbPath]);

        Assert.NotEqual(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains($"Error [{CommandErrorCodes.DbNotFound}]:", stderr);
        Assert.DoesNotContain("Hint:", stderr);
    }

    [Fact]
    public void Run_UnhandledExceptionReturnsUnhandledExitCode()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                var exitCode = ProgramRunner.Run(
                    ["status"],
                    appVersion: "1.0.0-test",
                    beforeDispatchForTesting: () => throw new InvalidOperationException("boom"));

                Assert.Equal(CommandExitCodes.UnhandledException, exitCode);
                Assert.Contains("Error: command failed before it could complete.", stderr.ToString());
                Assert.DoesNotContain("InvalidOperationException", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void Run_UnhandledSqliteTransientExceptionReturnsTransientDatabaseExitCode(int sqliteErrorCode)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                var exitCode = ProgramRunner.Run(
                    ["status"],
                    appVersion: "1.0.0-test",
                    beforeDispatchForTesting: () => throw new SqliteException("database unavailable", sqliteErrorCode));

                Assert.Equal(CommandExitCodes.TransientDatabaseError, exitCode);
                Assert.Contains("Error: command failed before it could complete.", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void Run_UnhandledPermanentSqliteExceptionReturnsDatabaseExitCode()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                var exitCode = ProgramRunner.Run(
                    ["status"],
                    appVersion: "1.0.0-test",
                    beforeDispatchForTesting: () => throw new SqliteException("database disk image is malformed", 11));

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Contains("Error: command failed before it could complete.", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void Completions_HelpLikeValueReturnsCompletionsError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "-h"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("requires a shell value, got option-like token '-h'", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
    }

    [Fact]
    public void Completions_MissingShellReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--completions requires a shell value", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
        Assert.DoesNotContain("Unknown command: --completions", stderr);
    }

    [Fact]
    public void Completions_OptionLikeShellTokenReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json is not supported for completions", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
        Assert.DoesNotContain("Unknown shell", stderr);
    }

    [Theory]
    [InlineData("index", "cdidx index <projectPath>")]
    [InlineData("search", "cdidx search <query>")]
    [InlineData("references", "cdidx references <query>")]
    [InlineData("callers", "cdidx callers <query>")]
    [InlineData("callees", "cdidx callees <query>")]
    [InlineData("impact", "cdidx impact <query>")]
    [InlineData("unused", "cdidx unused")]
    [InlineData("validate", "cdidx validate")]
    [InlineData("backfill-fold", "cdidx backfill-fold")]
    [InlineData("outline", "cdidx outline <path>")]
    [InlineData("inspect", "cdidx inspect <query>")]
    [InlineData("definition", "cdidx definition <query>")]
    [InlineData("find", "cdidx find <query>")]
    [InlineData("excerpt", "cdidx excerpt <path>")]
    [InlineData("hotspots", "cdidx hotspots")]
    [InlineData("deps", "cdidx deps")]
    [InlineData("map", "cdidx map")]
    [InlineData("status", "cdidx status")]
    [InlineData("export", "cdidx export <archive>")]
    [InlineData("import", "cdidx import <archive>")]
    [InlineData("doctor", "cdidx doctor")]
    [InlineData("mcp", "cdidx mcp")]
    [InlineData("completions", "cdidx completions <shell>")]
    [InlineData("license", "cdidx license")]
    public void SubcommandHelp_PrintsCommandSpecificUsage(string command, string expectedUsage)
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess([command, "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Usage:", stdout);
        Assert.Contains(expectedUsage, stdout);
        Assert.Contains("Run `cdidx --help`", stdout);
        if (command is "mcp" or "completions")
        {
            Assert.Contains("Notes:", stdout);
            Assert.Contains("--json is not supported", stdout);
        }
        else
        {
            Assert.DoesNotContain("Notes:", stdout);
        }
        Assert.DoesNotContain("Commands:", stdout);
        Assert.DoesNotContain("Index and update options:", stdout);
        Assert.DoesNotContain("██████╗", stdout);
    }

    [Fact]
    public void ExportCtags_WritesTagsFileFromIndexedSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_export_ctags");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var tagsPath = Path.Combine(projectRoot, "tags");

            var (exitCode, stdout, stderr) = RunCliInSubprocess(["export", "ctags", "--db", dbPath, "--output", tagsPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Exported ctags", stdout);
            var tags = File.ReadAllText(tagsPath);
            Assert.Contains("!_TAG_FILE_FORMAT\t2", tags);
            Assert.Contains("App\tsrc/app.cs\t1;\"", tags);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ExportImportArchive_RestoresCodeIndexDatabase()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_export_archive");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var importedDbPath = Path.Combine(projectRoot, "imported", "codeindex.db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", archivePath, "--db", sourceDbPath]);
            var (importExit, importStdout, importStderr) = RunCliInSubprocess(["import", archivePath, "--db", importedDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.Equal(string.Empty, exportStderr);
            Assert.True(importExit == 0, importStderr);
            Assert.Equal(string.Empty, importStderr);
            Assert.Contains("Imported CodeIndex database", importStdout);
            Assert.True(File.Exists(importedDbPath));
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(importedDbPath, out _, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ImportArchive_RejectsDatabaseHashMismatch()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_hash_mismatch");
        var replacementRoot = TestProjectHelper.CreateTempProject("cdidx_import_hash_replacement");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var replacementDbPath = TestProjectHelper.CreateProjectDb(replacementRoot);
            TestProjectHelper.InsertIndexedFile(replacementDbPath, "src/other.cs", "csharp", "class Other { void Run() {} }\n");
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var importedDbPath = Path.Combine(projectRoot, "imported", "codeindex.db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", archivePath, "--db", sourceDbPath]);
            ReplaceZipEntryWithFile(archivePath, "codeindex.db", replacementDbPath);
            var (importExit, _, importStderr) = RunCliInSubprocess(["import", archivePath, "--db", importedDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.Equal(CommandExitCodes.UsageError, importExit);
            Assert.Contains("database_sha256 does not match codeindex.db", importStderr);
            Assert.False(File.Exists(importedDbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(replacementRoot);
        }
    }

    [Fact]
    public void ImportArchive_RejectsManifestUserVersionMismatch()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_user_version_mismatch");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var importedDbPath = Path.Combine(projectRoot, "imported", "codeindex.db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", archivePath, "--db", sourceDbPath]);
            ReplaceManifestUserVersion(archivePath, newUserVersion: 1);
            var (importExit, _, importStderr) = RunCliInSubprocess(["import", archivePath, "--db", importedDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.Equal(CommandExitCodes.UsageError, importExit);
            Assert.Contains("user_version", importStderr);
            Assert.False(File.Exists(importedDbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ExportArchive_RejectsSourceDatabaseAsOutput()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_export_same_db");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");

            var (exitCode, _, stderr) = RunCliInSubprocess(["export", dbPath, "--db", dbPath]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("must not be the source database", stderr);
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(dbPath, out _, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ImportArchive_RemovesStaleDestinationSidecars()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_sidecars");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var destinationDbPath = Path.Combine(projectRoot, "destination", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDbPath)!);
            File.WriteAllText(destinationDbPath, "old");
            File.WriteAllText(destinationDbPath + "-wal", "old wal");
            File.WriteAllText(destinationDbPath + "-shm", "old shm");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", archivePath, "--db", sourceDbPath]);
            var (importExit, _, importStderr) = RunCliInSubprocess(["import", archivePath, "--db", destinationDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.True(importExit == 0, importStderr);
            Assert.False(File.Exists(destinationDbPath + "-wal"));
            Assert.False(File.Exists(destinationDbPath + "-shm"));
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(destinationDbPath, out _, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Doctor_PrintsRedactedEnvironmentSummary()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(
            ["doctor"],
            new Dictionary<string, string?>
            {
                ["CDIDX_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "cdidx-doctor-data"),
                ["CDIDX_GITHUB_TOKEN"] = "secret-token-value",
                ["CDIDX_PRIVATE_KEY"] = "private-key-value",
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("cdidx doctor", stdout);
        Assert.Contains("version", stdout);
        Assert.Contains("rid", stdout);
        Assert.Contains("terminal:", stdout);
        Assert.Contains("paths:", stdout);
        Assert.Contains("cdidx_env:", stdout);
        Assert.Contains("CDIDX_DATA_DIR", stdout);
        Assert.Contains("CDIDX_GITHUB_TOKEN", stdout);
        Assert.Contains("CDIDX_PRIVATE_KEY", stdout);
        Assert.Contains("<redacted>", stdout);
        Assert.DoesNotContain("secret-token-value", stdout);
        Assert.DoesNotContain("private-key-value", stdout);
    }

    [Fact]
    public void TopLevelHelp_DefaultIsBriefAndExtendedHelpKeepsFullReference()
    {
        var (briefExit, briefStdout, briefStderr) = RunCliInSubprocess(["--help"]);
        var (fullExit, fullStdout, fullStderr) = RunCliInSubprocess(["--help-all"]);

        Assert.Equal(0, briefExit);
        Assert.Equal(string.Empty, briefStderr);
        Assert.Contains("cdidx --help-all", briefStdout);
        Assert.Contains("cdidx --help-flags", briefStdout);
        Assert.DoesNotContain("Index and update options:", briefStdout);

        Assert.Equal(0, fullExit);
        Assert.Equal(string.Empty, fullStderr);
        Assert.Contains("Index and update options:", fullStdout);
        Assert.Contains("cdidx index <projectPath> --commits <commit-ref>", fullStdout);
        Assert.Contains("--limit <n>, --top <n>", fullStdout);
    }

    [Fact]
    public void HelpFlags_PrintsFlagReferenceOnly()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--help-flags"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Index and update options:", stdout);
        Assert.Contains("Query options:", stdout);
        Assert.Contains("--limit <n>, --top <n>", stdout);
        Assert.DoesNotContain("Commands:", stdout);
    }

    [Theory]
    [InlineData("completions")]
    [InlineData("completions", "--json")]
    [InlineData("completions", "bash", "extra")]
    public void CompletionsCommand_ErrorsUseCommandUsage(params string[] args)
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Usage: cdidx completions <shell>", stderr);
        Assert.DoesNotContain("Usage: cdidx --completions <shell>", stderr);
        if (args is ["completions", "--json"])
            Assert.Contains("--json is not supported for completions", stderr);
    }

    [Fact]
    public void Mcp_JsonFlagReturnsExplicitUnsupportedError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["mcp", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json is not supported for mcp", stderr);
        Assert.Contains("Usage: cdidx mcp", stderr);
        Assert.Contains("Note: --json is not supported", stderr);
    }

    [Fact]
    public void Completions_ExtraArgsReturnUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "bash", "extra"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("accepts exactly one shell value", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
    }

    [Theory]
    [InlineData("license")]
    [InlineData("--license")]
    public void License_PrintsLicenseSummary(string arg)
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess([arg]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)", stdout);
        Assert.Contains("non-competing purposes", stdout);
        Assert.Contains("Competing commercial products or services require a separate written agreement", stdout);
        Assert.Contains("separate written agreement", stdout);
        Assert.Contains("LICENSES/Apache-2.0.txt", stdout);
        Assert.Contains("INTEGRATION_POLICY.md", stdout);
    }

    [Fact]
    public void Suggestions_ListFiltersAndPrintsStoredSuggestions()
    {
        using var fixture = SuggestionFixture.Create();
        var csharp = fixture.Add("symbol_extraction", "csharp", "Missing record extraction", submitted: false);
        fixture.Add("language_support", "rust", "Improve macro handling", submitted: true);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "list", "--db", fixture.DbPath, "--category", "symbol_extraction"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(csharp.Hash[..12], stdout);
        Assert.Contains("draft", stdout);
        Assert.Contains("Missing record extraction", stdout);
        Assert.DoesNotContain("Improve macro handling", stdout);
    }

    [Fact]
    public void Suggestions_ListJsonSupportsLimitAndOffset()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("symbol_extraction", "csharp", "Oldest suggestion", submitted: false);
        var middle = fixture.Add("language_support", "rust", "Middle suggestion", submitted: false);
        fixture.Add("output_format", "python", "Newest suggestion", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--json", "--limit", "1", "--offset", "1"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(middle.Hash, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Middle suggestion", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Suggestions_ShowJsonResolvesShortId()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("output_format", "python", "JSON export needed", submitted: true);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "show", record.Hash[..12], "--db", fixture.DbPath, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(record.Hash, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("submitted_pending_triage", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("JSON export needed", doc.RootElement.GetProperty("description").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("submit_attempt_count").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("last_submit_attempt", out _));
        Assert.False(doc.RootElement.TryGetProperty("last_submit_error", out _));
    }

    [Fact]
    public void Suggestions_ShowRejectsPaginationFlags()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("output_format", "python", "JSON export needed", submitted: true);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "show", record.Hash[..12], "--db", fixture.DbPath, "--limit", "1"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--limit and --offset can only be used", stderr);
    }

    [Fact]
    public void Suggestions_ListJsonIncludesSubmitDiagnostics()
    {
        using var fixture = SuggestionFixture.Create();
        var attemptedAt = new DateTime(2026, 5, 17, 4, 3, 2, DateTimeKind.Utc);
        fixture.Add(
            "output_format",
            "python",
            "JSON export failed",
            submitted: false,
            lastSubmitAttempt: attemptedAt,
            submitAttemptCount: 2,
            lastSubmitError: "API 422: validation failed");

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "list", "--db", fixture.DbPath, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("submit_attempt_count").GetInt32());
        Assert.Equal(attemptedAt, doc.RootElement.GetProperty("last_submit_attempt").GetDateTime());
        Assert.Equal("API 422: validation failed", doc.RootElement.GetProperty("last_submit_error").GetString());
    }

    [Fact]
    public void Suggestions_ExportJsonSupportsLimitAndOffset()
    {
        using var fixture = SuggestionFixture.Create();
        var oldest = fixture.Add("symbol_extraction", "csharp", "Oldest export", submitted: false);
        var middle = fixture.Add("language_support", "rust", "Middle export", submitted: false);
        fixture.Add("output_format", "python", "Newest export", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "json", "--limit=2", "--offset=1"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var suggestions = doc.RootElement.GetProperty("suggestions");
        Assert.Equal(middle.Hash, suggestions[0].GetProperty("id").GetString());
        Assert.Equal(oldest.Hash, suggestions[1].GetProperty("id").GetString());
    }

    [Fact]
    public void Suggestions_ListRejectsInvalidLimit()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("output_format", "python", "JSON export needed", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--limit", "many"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--limit must be a non-negative integer", stderr);
    }

    [Fact]
    public void Suggestions_ExportMarkdownIncludesFilteredSuggestions()
    {
        using var fixture = SuggestionFixture.Create();
        var attemptedAt = new DateTime(2026, 5, 17, 4, 3, 2, DateTimeKind.Utc);
        fixture.Add(
            "output_format",
            "csharp",
            "Share triage notes",
            submitted: false,
            lastSubmitAttempt: attemptedAt,
            submitAttemptCount: 1,
            lastSubmitError: "HttpRequestException: network unavailable");
        fixture.Add("language_support", "ruby", "Add parser support", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "export", "--db", fixture.DbPath, "--language", "csharp", "--format", "markdown"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("# cdidx Suggestions", stdout);
        Assert.Contains("Share triage notes", stdout);
        Assert.Contains("- last_submit_attempt: `2026-05-17T04:03:02.0000000Z`", stdout);
        Assert.Contains("- submit_attempt_count: `1`", stdout);
        Assert.Contains("- last_submit_error: `HttpRequestException: network unavailable`", stdout);
        Assert.DoesNotContain("Add parser support", stdout);
    }

    [Fact]
    public void Suggestions_ExportIssueDraftsIncludesEvidenceAndDuplicatePreflight()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add(
            "output_format",
            "csharp",
            "Issue draft export should preserve structured triage evidence",
            submitted: false,
            sampledTitle: "Add issue draft export",
            evidencePaths: ["src/CodeIndex/Cli/SuggestionsCommandRunner.cs", "tests/CodeIndex.Tests/ProgramCliTests.cs"]);
        var openIssuesPath = fixture.WriteOpenIssuesJson($$"""
        [
          {
            "number": 2878,
            "title": "[AI Suggestion] output_format: Add issue draft export",
            "url": "https://github.com/Widthdom/CodeIndex/issues/2878",
            "labels": [{ "name": "enhancement" }]
          }
        ]
        """);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("duplicate_preflight").GetProperty("checked").GetBoolean());
        Assert.Equal(1, root.GetProperty("duplicate_preflight").GetProperty("open_issue_count").GetInt32());
        var draft = root.GetProperty("drafts")[0];
        Assert.Equal(record.Hash, draft.GetProperty("suggestion_id").GetString());
        Assert.Equal("enhancement", draft.GetProperty("labels")[0].GetString());
        Assert.Equal("src/CodeIndex/Cli/SuggestionsCommandRunner.cs", draft.GetProperty("evidence_paths")[0].GetString());
        Assert.Contains("## Evidence paths", draft.GetProperty("body").GetString());
        var preflight = draft.GetProperty("duplicate_preflight");
        Assert.Equal(1, preflight.GetProperty("match_count").GetInt32());
        Assert.Equal(2878, preflight.GetProperty("matches")[0].GetProperty("number").GetInt32());
        Assert.Equal("title_exact", preflight.GetProperty("matches")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Suggestions_ExportIssueDraftsRedactsSensitiveSampledTitle()
    {
        using var fixture = SuggestionFixture.Create();
        var secret = $"issue-draft-secret-{Guid.NewGuid():N}";
        fixture.Add(
            "output_format",
            "csharp",
            "Issue draft export should redact sampled metadata",
            submitted: false,
            sampledTitle: $"Leaked api_key={secret}");
        var openIssuesPath = fixture.WriteOpenIssuesJson("[]");

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain(secret, stdout);
        using var doc = JsonDocument.Parse(stdout);
        var title = doc.RootElement.GetProperty("drafts")[0].GetProperty("title").GetString();
        Assert.Contains("REDACTED:credential", title!);
    }

    [Fact]
    public void Suggestions_ExportIssueDraftsRejectsOversizedOpenIssuesPreflight()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "security",
            "csharp",
            "Issue draft export should reject oversized duplicate preflight files",
            submitted: false,
            sampledTitle: "Reject oversized duplicate preflight files");
        var openIssuesPath = fixture.WriteOpenIssuesJson(new string(' ', SuggestionsCommandRunner.MaxOpenIssuesJsonBytes + 1));

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--open-issues file", stderr);
        Assert.Contains("exceeds maximum supported size", stderr);
    }

    [Fact]
    public void Suggestions_ExportIssueDraftsRejectsTooDeepOpenIssuesPreflight()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "security",
            "csharp",
            "Issue draft export should reject deeply nested duplicate preflight files",
            submitted: false,
            sampledTitle: "Reject deeply nested duplicate preflight files");
        var nesting = SuggestionsCommandRunner.MaxOpenIssuesJsonDepth + 1;
        var openIssuesPath = fixture.WriteOpenIssuesJson(new string('[', nesting) + new string(']', nesting));

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("could not read --open-issues file", stderr);
        Assert.Contains("maximum configured depth", stderr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCliInSubprocess(string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(GetBuiltCliDllPath());
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                if (value == null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cdidx subprocess / cdidx サブプロセスの起動に失敗");
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
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

    private static void ReplaceZipEntryWithFile(string archivePath, string entryName, string sourcePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var source = File.OpenRead(sourcePath);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static void ReplaceManifestUserVersion(string archivePath, int newUserVersion)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json entry was not found");

        string manifestJson;
        using (var reader = new StreamReader(entry.Open()))
        {
            manifestJson = reader.ReadToEnd();
        }

        using var document = JsonDocument.Parse(manifestJson);
        var oldUserVersion = document.RootElement.GetProperty("user_version").GetInt32();
        var replacementUserVersion = newUserVersion == oldUserVersion
            ? (oldUserVersion == 0 ? 1 : 0)
            : newUserVersion;
        var updatedManifestJson = manifestJson.Replace(
            $"\"user_version\":{oldUserVersion}",
            $"\"user_version\":{replacementUserVersion}",
            StringComparison.Ordinal);

        entry.Delete();
        var replacementEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(replacementEntry.Open());
        writer.Write(updatedManifestJson);
    }

    private sealed class SuggestionFixture : IDisposable
    {
        private readonly string _root;
        private readonly List<SuggestionRecord> _records = new();

        private SuggestionFixture(string root)
        {
            _root = root;
            DbPath = Path.Combine(root, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        }

        public string DbPath { get; }

        public static SuggestionFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_suggestions_cli_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new SuggestionFixture(root);
        }

        public SuggestionRecord Add(
            string category,
            string? language,
            string description,
            bool submitted,
            DateTime? lastSubmitAttempt = null,
            int submitAttemptCount = 0,
            string? lastSubmitError = null,
            string? sampledTitle = null,
            string[]? evidencePaths = null)
        {
            var record = new SuggestionRecord
            {
                Category = category,
                Language = language,
                Description = description,
                Context = "Agent noticed this during repository triage.",
                Hash = SuggestionStore.ComputeHash(category, language, description),
                CreatedAt = new DateTime(2026, 5, 16, 12, _records.Count, 0, DateTimeKind.Utc),
                SubmittedToGitHub = submitted,
                GitHubIssueUrl = submitted ? "https://github.com/Widthdom/CodeIndex/issues/99" : null,
                LastSubmitAttempt = lastSubmitAttempt,
                SubmitAttemptCount = submitAttemptCount,
                LastSubmitError = lastSubmitError,
                SampledTitle = sampledTitle,
                EvidencePaths = evidencePaths,
            };
            _records.Add(record);
            Write();
            return record;
        }

        public string WriteOpenIssuesJson(string json)
        {
            var path = Path.Combine(_root, "open-issues.json");
            File.WriteAllText(path, json);
            return path;
        }

        private void Write()
        {
            var path = Path.Combine(_root, ".cdidx", "suggestions-codeindex.json");
            var json = JsonSerializer.Serialize(_records, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true,
            });
            File.WriteAllText(path, json);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
