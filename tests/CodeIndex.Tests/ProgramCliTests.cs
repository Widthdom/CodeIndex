using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Tests;

/// <summary>
/// Black-box CLI tests for Program entrypoint behavior.
/// Program エントリポイント挙動のブラックボックステスト。
/// </summary>
[Collection("Console sensitive")]
public class ProgramCliTests
{
    [ProductionRuntimeFact]
    public void ExcerptRecovery_ShowPathsPreservesPrefixAndReplaysOptionLikeMetacharacterArgv_Issue4567_Issue4860()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_recovery_argv_4567");
        try
        {
            var dbRoot = TestProjectHelper.CreateDirectory(projectRoot, "db space 'quote' $dollar &meta");
            var dbPath = TestProjectHelper.CreateProjectDb(dbRoot);
            const string indexedPath = "--space 'quote' $dollar &meta.py";
            var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
            var focusColumn = longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1;
            TestProjectHelper.InsertIndexedFile(dbPath, indexedPath, "python", longLine);

            var (exitCode, stdout, stderr) = RunCliInSubprocess([
                "excerpt",
                "--",
                indexedPath,
                "--db",
                dbPath,
                "--start",
                "1",
                "--end",
                "1",
                "--max-line-width",
                "96",
                "--focus-column",
                focusColumn.ToString(CultureInfo.InvariantCulture),
                "--focus-length",
                "6",
                "--json",
                "--show-paths",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.TryGetProperty("content_recovery", out var recovery), stdout);
            var argv = recovery.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!).ToArray();

            Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(argv[0]), ignoreCase: true);
            Assert.Equal(Path.GetFullPath(GetBuiltCliDllPath()), argv[1]);
            Assert.Equal("excerpt", argv[2]);
            Assert.Equal("--", argv[3]);
            Assert.Equal(indexedPath, argv[4]);
            Assert.Equal("--db", argv[5]);
            Assert.Equal(Path.GetFullPath(dbPath), argv[6]);
            Assert.Equal("--start", argv[7]);
            Assert.Equal("1", argv[8]);
            Assert.Equal("--end", argv[9]);
            Assert.Equal("1", argv[10]);
            Assert.Equal("--max-line-width", argv[11]);
            Assert.Equal("0", argv[12]);
            Assert.Equal("--json", argv[13]);
            Assert.Equal(OperatingSystem.IsWindows() ? "powershell" : "posix-sh", recovery.GetProperty("command_shell").GetString());
            Assert.False(recovery.GetProperty("command_display_only").GetBoolean());
            Assert.False(recovery.GetProperty("paths_redacted").GetBoolean());
            Assert.False(recovery.GetProperty("requires_local_path_substitution").GetBoolean());
            Assert.False(recovery.GetProperty("command").GetString()!.StartsWith("cdidx ", StringComparison.Ordinal));

            var (replayExitCode, replayStdout, replayStderr) = RunCliInSubprocess(argv.Skip(2).ToArray());

            Assert.Equal(CommandExitCodes.Success, replayExitCode);
            Assert.Equal(string.Empty, replayStderr);
            using var replayDocument = JsonDocument.Parse(replayStdout);
            Assert.Equal(indexedPath, replayDocument.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeTheory]
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

    [ProductionRuntimeFact]
    public void Mcp_UnsupportedOptionReturnUsageError()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --json is not supported for mcp; MCP already speaks JSON-RPC", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
    }

    [ProductionRuntimeFact]
    public void Mcp_HttpOversizedLimitEnvironmentReturnsUsageError()
    {
        var oversized = (HttpMcpTransport.MaxConfiguredRequestBodyBytes + 1).ToString(CultureInfo.InvariantCulture);
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?>
            {
                [HttpMcpTransport.MaxRequestBodyBytesEnvVar] = oversized,
                [ProgramRunner.McpHttpTokenEnvVar] = "test-token",
            });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(HttpMcpTransport.MaxRequestBodyBytesEnvVar, stderr);
        Assert.Contains(
            $"between 1 and {HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}",
            stderr,
            StringComparison.Ordinal);
        Assert.Contains("HTTP limits:", stderr);
        Assert.DoesNotContain("HTTP transport listening", stderr);
    }

    [ProductionRuntimeTheory]
    [InlineData("not-an-integer")]
    [InlineData("0")]
    public void Mcp_HttpPresentInvalidLimitEnvironmentReturnsUsageError(string configuredValue)
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?>
            {
                [HttpMcpTransport.MaxConcurrentHandlersEnvVar] = configuredValue,
                [ProgramRunner.McpHttpTokenEnvVar] = "test-token",
            });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(HttpMcpTransport.MaxConcurrentHandlersEnvVar, stderr, StringComparison.Ordinal);
        Assert.Contains(
            $"integer between 1 and {HttpMcpTransport.MaxConfiguredConcurrentHandlers.ToString(CultureInfo.InvariantCulture)}",
            stderr,
            StringComparison.Ordinal);
        Assert.Contains("HTTP limits:", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP transport listening", stderr, StringComparison.Ordinal);
    }

    [ProductionRuntimeFact]
    public void Mcp_HttpRequestBodyBudgetBelowPerRequestLimitReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?>
            {
                [HttpMcpTransport.MaxRequestBodyBytesEnvVar] = "1024",
                [HttpMcpTransport.MaxInFlightRequestBodyBytesEnvVar] = "1023",
                [ProgramRunner.McpHttpTokenEnvVar] = "test-token",
            });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(HttpMcpTransport.MaxRequestBodyBytesEnvVar, stderr, StringComparison.Ordinal);
        Assert.Contains(HttpMcpTransport.MaxInFlightRequestBodyBytesEnvVar, stderr, StringComparison.Ordinal);
        Assert.Contains("must be greater than or equal", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP transport listening", stderr, StringComparison.Ordinal);
    }

    [ProductionRuntimeTheory]
    [InlineData(HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar, "not-an-integer")]
    [InlineData(HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar, "0")]
    [InlineData(HttpMcpTransport.RequestLifetimeTimeoutMillisecondsEnvVar, "-1")]
    [InlineData(HttpMcpTransport.RequestLifetimeTimeoutMillisecondsEnvVar, "typo")]
    public void Mcp_HttpInvalidRequestDeadlineEnvironmentReturnsUsageError(string variable, string value)
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?>
            {
                [variable] = value,
                [ProgramRunner.McpHttpTokenEnvVar] = "test-token",
            });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(variable, stderr, StringComparison.Ordinal);
        Assert.Contains("integer between 1 and", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP transport listening", stderr, StringComparison.Ordinal);
    }

    [ProductionRuntimeFact]
    public void Mcp_HttpRequestDeadlineBelowBodyIdleDeadlineReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(
            ["mcp", "--transport", "http"],
            new Dictionary<string, string?>
            {
                [HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar] = "200",
                [HttpMcpTransport.RequestLifetimeTimeoutMillisecondsEnvVar] = "100",
                [ProgramRunner.McpHttpTokenEnvVar] = "test-token",
            });

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar, stderr, StringComparison.Ordinal);
        Assert.Contains(HttpMcpTransport.RequestLifetimeTimeoutMillisecondsEnvVar, stderr, StringComparison.Ordinal);
        Assert.Contains("must be greater than or equal", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP transport listening", stderr, StringComparison.Ordinal);
    }

    [ProductionRuntimeFact]
    public void Mcp_DbAcceptsLeadingDoubleDashPathValueViaInlineLiteral()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db=--tmp.db"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("requires a value", stderr);
    }

    [ProductionRuntimeFact]
    public void Mcp_DbAcceptsRecognizedOptionTokenViaInlineValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db=--json"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("requires a value", stderr);
    }

    [ProductionRuntimeFact]
    public void Mcp_DbRejectsSeparatedUnknownDoubleDashValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db", "--mystery"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("`--db=<value>`", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
    }

    [ProductionRuntimeFact]
    public void Mcp_DbRejectsEmptyInlineValue()
    {
        var (exitCode, _, stderr) = RunCliInSubprocess(["mcp", "--db="]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --db requires a value.", stderr);
        Assert.Contains("Usage: cdidx mcp [--db <path>]", stderr);
    }

    [ProductionRuntimeFact]
    public void Symbols_NameHelpLikeValueReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["symbols", "--name", "-h"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--name requires a value", stderr);
        Assert.DoesNotContain("██████╗", stderr);
    }

    [ProductionRuntimeTheory]
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

    [ProductionRuntimeFact]
    public void Symbols_TrailingQuietFlagsPreserveResultStdout_Issue4748()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_program_symbols_quiet_4748");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "class App { void Alpha() {} void Beta() {} }\n");

            var outputModes = new[]
            {
                Array.Empty<string>(),
                new[] { "--json" },
                new[] { "--json=array" },
            };
            var quietFlags = new[] { "--quiet", "-q", "--silent" };

            foreach (var outputMode in outputModes)
            {
                var baselineArgs = new List<string>
                {
                    "symbols",
                    "--lang",
                    "csharp",
                    "--exclude-tests",
                    "--limit",
                    "5",
                    "--db",
                    dbPath,
                };
                baselineArgs.AddRange(outputMode);

                var (baselineExitCode, baselineStdout, baselineStderr) = RunCliInSubprocess(baselineArgs.ToArray());

                Assert.Equal(CommandExitCodes.Success, baselineExitCode);
                Assert.NotEqual(string.Empty, baselineStdout);
                if (outputMode.Length == 0)
                    Assert.NotEqual(string.Empty, baselineStderr);

                foreach (var quietFlag in quietFlags)
                {
                    var quietArgs = new List<string>(baselineArgs) { quietFlag };

                    var (quietExitCode, quietStdout, quietStderr) = RunCliInSubprocess(quietArgs.ToArray());

                    Assert.Equal(CommandExitCodes.Success, quietExitCode);
                    Assert.Equal(baselineStdout, quietStdout);
                    Assert.Equal(string.Empty, quietStderr);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void QueryQuietFlag_PreservesErrorLines()
    {
        var missingDbPath = Path.Combine(Path.GetTempPath(), $"cdidx_missing_{Guid.NewGuid():N}.db");

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--quiet", "search", "Run", "--db", missingDbPath]);

        Assert.NotEqual(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains($"Error [{CommandErrorCodes.DbNotFound}]:", stderr);
        Assert.DoesNotContain("Hint:", stderr);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeTheory]
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

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Completions_HelpLikeValueReturnsCompletionsError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "-h"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("requires a shell value, got option-like token '-h'", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Completions_JsonFlagReturnsStructuredUnsupportedError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("--json is not supported for completions.", document.RootElement.GetProperty("message").GetString());
        Assert.Contains("powershell", document.RootElement.GetProperty("hint").GetString());
    }

    [ProductionRuntimeTheory]
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
    [InlineData("excerpt", "cdidx excerpt <path[:line|:start-end]>")]
    [InlineData("hotspots", "cdidx hotspots")]
    [InlineData("deps", "cdidx deps")]
    [InlineData("map", "cdidx map")]
    [InlineData("status", "cdidx status")]
    [InlineData("export", "cdidx export <archive>")]
    [InlineData("import", "cdidx import <archive>")]
    [InlineData("doctor", "cdidx doctor")]
    [InlineData("mcp", "cdidx mcp")]
    [InlineData("lsp", "cdidx lsp")]
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
        if (command is "index" or "mcp" or "lsp" or "completions" or "references" or "callers" or "callees" or "backfill-fold" or "excerpt" or "inspect" or "status" or "symbols" or "search" or "find" or "map")
        {
            Assert.Contains("Notes:", stdout);
            if (command is "mcp" or "completions")
                Assert.Contains("--json is not supported", stdout);
            if (command is "mcp")
            {
                Assert.Contains("LF-delimited line", stdout);
                Assert.Contains("lifecycle diagnostics go to stderr", stdout);
            }
            if (command is "lsp")
            {
                Assert.Contains("Content-Length framing", stdout);
                Assert.Contains("unsupported optional methods", stdout);
                Assert.Contains("index-backed symbol completion", stdout);
            }
            if (command is "references" or "callers" or "callees")
                Assert.Contains("--json and --format json emit JSON Lines", stdout);
        }
        else
        {
            Assert.DoesNotContain("Notes:", stdout);
        }
        Assert.DoesNotContain("Commands:", stdout);
        Assert.DoesNotContain("Index and update options:", stdout);
        Assert.DoesNotContain("██████╗", stdout);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void ExportImportArchive_SharesMetadataRichPristineAcrossSuccessPaths_Issue3549()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_archive_success_paths");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, sourceDbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {DbContext.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}";
                cmd.ExecuteNonQuery();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.CdidxWriterVersionMetaKey, "test-writer");
                writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, "main");
                writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, "2026-06-11T00:00:00Z");
                writer.SetMeta(DbContext.CodeIndexMetaSchemaVersionMetaKey, "1");
                writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "2");
                writer.SetMeta(DbContext.SqlGraphContractVersionMetaKey, "1");
                writer.SetMeta(DbContext.HotspotFamilyVersionMetaKey, "2");
                writer.SetMeta(DbContext.UnknownExtensionFileCountMetaKey, "2");
                writer.SetMeta(DbContext.UnknownExtensionFilePathsMetaKey, "[\"tools/custom.foo\",\"docs/archive.bar\"]");
                writer.SetMeta(DbContext.UnknownExtensionFilesTruncatedMetaKey, "false");
                writer.SetMeta(DbContext.UnknownExtensionFilePathLimitMetaKey, "50");
            }

            var pristineArchivePath = Path.Combine(projectRoot, "pristine.cdidx.zip");
            var defaultImportDbPath = Path.Combine(projectRoot, "default-import", "codeindex.db");
            var legacyArchivePath = Path.Combine(projectRoot, "legacy.cdidx.zip");
            var legacyImportDbPath = Path.Combine(projectRoot, "legacy-import", "codeindex.db");
            var noBackupDbPath = Path.Combine(projectRoot, "no-backup-replacement", "codeindex.db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", pristineArchivePath, "--db", sourceDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.Equal(string.Empty, exportStderr);

            using (var archive = ZipFile.OpenRead(pristineArchivePath))
            {
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidOperationException("manifest.json entry was not found");
                using var manifestStream = manifestEntry.Open();
                using var document = JsonDocument.Parse(manifestStream);
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("file_count").GetInt64());
                Assert.True(root.GetProperty("chunk_count").GetInt64() >= 1);
                Assert.True(root.GetProperty("symbol_count").GetInt64() >= 1);
                Assert.True(root.GetProperty("reference_count").GetInt64() >= 0);
                Assert.Equal("test-writer", root.GetProperty("index_writer_version").GetString());
                Assert.Equal("main", root.GetProperty("indexed_head_branch").GetString());
                Assert.Equal("2026-06-11T00:00:00Z", root.GetProperty("indexed_head_timestamp").GetString());
                Assert.Equal(1, root.GetProperty("codeindex_meta_schema_version").GetInt32());
                Assert.Equal(2, root.GetProperty("csharp_symbol_name_contract_version").GetInt32());
                Assert.Equal(1, root.GetProperty("sql_graph_contract_version").GetInt32());
                Assert.Equal(2, root.GetProperty("hotspot_family_version").GetInt32());
                Assert.Equal(2, root.GetProperty("unknown_extension_file_count").GetInt64());
                Assert.False(root.GetProperty("unknown_extension_files_truncated").GetBoolean());
                Assert.Equal(50, root.GetProperty("unknown_extension_file_path_limit").GetInt32());
                Assert.Equal("tools/custom.foo", root.GetProperty("unknown_extension_files")[0].GetString());
                Assert.Equal(JsonValueKind.True, root.GetProperty("graph_ready").ValueKind);
                Assert.Equal(JsonValueKind.True, root.GetProperty("issues_ready").ValueKind);
                Assert.Equal(JsonValueKind.True, root.GetProperty("fold_ready").ValueKind);
            }

            var (defaultImportExit, defaultImportStdout, defaultImportStderr) = RunCliInSubprocess([
                "import", pristineArchivePath, "--db", defaultImportDbPath
            ]);

            Assert.True(defaultImportExit == 0, defaultImportStderr);
            Assert.Equal(string.Empty, defaultImportStderr);
            Assert.Contains("Imported CodeIndex database", defaultImportStdout);
            Assert.True(File.Exists(defaultImportDbPath));
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(defaultImportDbPath, out _, out _));

            File.Copy(pristineArchivePath, legacyArchivePath);
            RemoveManifestProperties(
                legacyArchivePath,
                "file_count",
                "chunk_count",
                "symbol_count",
                "reference_count",
                "graph_ready",
                "issues_ready",
                "fold_ready",
                "index_writer_version",
                "indexed_head_branch",
                "indexed_head_timestamp",
                "codeindex_meta_schema_version",
                "csharp_symbol_name_contract_version",
                "sql_graph_contract_version",
                "hotspot_family_version",
                "unknown_extension_file_count",
                "unknown_extension_files",
                "unknown_extension_files_truncated",
                "unknown_extension_file_path_limit",
                "unknown_extension_file_sample_count",
                "unknown_extension_file_sample_limit",
                "unknown_extension_file_sample_truncated");
            var (legacyImportExit, legacyImportStdout, legacyImportStderr) = RunCliInSubprocess([
                "import", legacyArchivePath, "--db", legacyImportDbPath, "--json"
            ]);

            Assert.True(legacyImportExit == CommandExitCodes.Success, legacyImportStdout);
            Assert.Equal(string.Empty, legacyImportStderr);
            Assert.True(File.Exists(legacyImportDbPath));
            using (var legacyDocument = JsonDocument.Parse(legacyImportStdout))
            {
                var legacyRoot = legacyDocument.RootElement;
                Assert.Equal("1", legacyRoot.GetProperty("api_version").GetString());
                Assert.Equal("success", legacyRoot.GetProperty("status").GetString());
                Assert.Equal(Path.GetFullPath(legacyArchivePath), legacyRoot.GetProperty("archive_path").GetString());
                Assert.Equal(Path.GetFullPath(legacyImportDbPath), legacyRoot.GetProperty("db_path").GetString());
                Assert.Equal("import", legacyRoot.GetProperty("mode").GetString());
                Assert.False(legacyRoot.GetProperty("dry_run").GetBoolean());
                var phases = legacyRoot.GetProperty("validation_phases")
                    .EnumerateArray()
                    .ToDictionary(
                        phase => phase.GetProperty("phase").GetString()!,
                        phase => phase.GetProperty("status").GetString()!,
                        StringComparer.Ordinal);
                Assert.Equal("success", phases["open_archive"]);
                Assert.Equal("success", phases["manifest"]);
                Assert.Equal("success", phases["database_entry"]);
                Assert.Equal("success", phases["sha256"]);
                Assert.Equal("success", phases["sqlite_validate"]);
                Assert.Equal("success", phases["replace_db"]);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(noBackupDbPath)!);
            File.WriteAllText(noBackupDbPath, "old");
            File.WriteAllText(noBackupDbPath + "-wal", "old wal");
            File.WriteAllText(noBackupDbPath + "-shm", "old shm");
            var (noBackupImportExit, _, noBackupImportStderr) = RunCliInSubprocess([
                "import", pristineArchivePath, "--db", noBackupDbPath, "--no-backup"
            ]);

            Assert.True(noBackupImportExit == 0, noBackupImportStderr);
            Assert.False(File.Exists(noBackupDbPath + "-wal"));
            Assert.False(File.Exists(noBackupDbPath + "-shm"));
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(noBackupDbPath, out _, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void ImportArchive_RejectsCopiedManifestCountHashAndUserVersionMutations_Issue3549()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_archive_rejections");
        var replacementRoot = TestProjectHelper.CreateTempProject("cdidx_import_hash_replacement");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var replacementDbPath = TestProjectHelper.CreateProjectDb(replacementRoot);
            TestProjectHelper.InsertIndexedFile(
                replacementDbPath,
                "src/other.cs",
                "csharp",
                "class Other { void Run() {} }\n",
                releasePoolForFileAccess: true);
            var pristineArchivePath = Path.Combine(projectRoot, "pristine.cdidx.zip");
            var countArchivePath = Path.Combine(projectRoot, "manifest-count.cdidx.zip");
            var hashArchivePath = Path.Combine(projectRoot, "database-hash.cdidx.zip");
            var userVersionArchivePath = Path.Combine(projectRoot, "user-version.cdidx.zip");
            var countDbPath = Path.Combine(projectRoot, "imported-count", "codeindex.db");
            var hashDbPath = Path.Combine(projectRoot, "imported-hash", "codeindex.db");
            var userVersionDbPath = Path.Combine(projectRoot, "imported-user-version", "codeindex.db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", pristineArchivePath, "--db", sourceDbPath]);

            Assert.True(exportExit == 0, exportStderr);
            File.Copy(pristineArchivePath, countArchivePath);
            File.Copy(pristineArchivePath, hashArchivePath);
            File.Copy(pristineArchivePath, userVersionArchivePath);

            ReplaceManifestNumber(countArchivePath, "file_count", 999);
            ReplaceZipEntryWithFile(hashArchivePath, "codeindex.db", replacementDbPath);
            ReplaceManifestUserVersion(userVersionArchivePath, newUserVersion: 1);

            var (countExit, countStdout, countStderr) = RunCliInSubprocess([
                "import", countArchivePath, "--db", countDbPath, "--json"
            ]);
            var (hashExit, _, hashStderr) = RunCliInSubprocess([
                "import", hashArchivePath, "--db", hashDbPath
            ]);
            var (userVersionExit, _, userVersionStderr) = RunCliInSubprocess([
                "import", userVersionArchivePath, "--db", userVersionDbPath
            ]);

            Assert.Equal(CommandExitCodes.UsageError, countExit);
            Assert.Equal(string.Empty, countStderr);
            using var countDocument = JsonDocument.Parse(countStdout);
            Assert.Equal("sqlite_validate", countDocument.RootElement.GetProperty("phase").GetString());
            Assert.Equal("import_manifest_mismatch", countDocument.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("file_count", countDocument.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(countDbPath));

            Assert.Equal(CommandExitCodes.UsageError, hashExit);
            Assert.Contains("database_sha256 does not match codeindex.db", hashStderr);
            Assert.False(File.Exists(hashDbPath));

            Assert.Equal(CommandExitCodes.UsageError, userVersionExit);
            Assert.Contains("user_version", userVersionStderr);
            Assert.False(File.Exists(userVersionDbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(replacementRoot);
        }
    }

    [ProductionRuntimeFact]
    public void ImportArchive_DryRunAndCheckJsonSharePristineExport_Issues3550And4328()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_validation_modes");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(projectRoot, "pristine.cdidx.zip");
            var dryRunDbPath = Path.Combine(projectRoot, "dry-run", "codeindex.db");
            var checkDbPath = Path.Combine(projectRoot, "check", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dryRunDbPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(checkDbPath)!);
            File.WriteAllText(dryRunDbPath, "existing dry-run db");
            File.WriteAllText(dryRunDbPath + "-wal", "existing dry-run wal");
            File.WriteAllText(dryRunDbPath + "-shm", "existing dry-run shm");
            File.WriteAllText(checkDbPath, "existing check db");

            var (exportExit, _, exportStderr) = RunCliInSubprocess(["export", archivePath, "--db", sourceDbPath]);
            var (dryRunExit, dryRunStdout, dryRunStderr) = RunCliInSubprocess([
                "import", archivePath, "--db", dryRunDbPath, "--prune-paths", "--no-backup", "--dry-run", "--json"
            ]);
            var (checkExit, checkStdout, checkStderr) = RunCliInSubprocess([
                "import", archivePath, "--db", checkDbPath, "--no-backup", "--check", "--json"
            ]);

            Assert.True(exportExit == 0, exportStderr);
            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal(string.Empty, dryRunStderr);
            Assert.Equal("existing dry-run db", File.ReadAllText(dryRunDbPath));
            Assert.Equal("existing dry-run wal", File.ReadAllText(dryRunDbPath + "-wal"));
            Assert.Equal("existing dry-run shm", File.ReadAllText(dryRunDbPath + "-shm"));

            using var dryRunDocument = JsonDocument.Parse(dryRunStdout);
            var dryRunRoot = dryRunDocument.RootElement;
            Assert.Equal("success", dryRunRoot.GetProperty("status").GetString());
            Assert.Equal("dry_run", dryRunRoot.GetProperty("mode").GetString());
            Assert.True(dryRunRoot.GetProperty("dry_run").GetBoolean());
            Assert.True(dryRunRoot.GetProperty("pruned_paths").GetBoolean());
            Assert.True(dryRunRoot.GetProperty("replacement_would_be_allowed").GetBoolean());
            var phases = dryRunRoot.GetProperty("validation_phases")
                .EnumerateArray()
                .ToDictionary(
                    phase => phase.GetProperty("phase").GetString()!,
                    phase => phase.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal("success", phases["open_archive"]);
            Assert.Equal("success", phases["manifest"]);
            Assert.Equal("success", phases["database_entry"]);
            Assert.Equal("success", phases["sha256"]);
            Assert.Equal("success", phases["sqlite_validate"]);
            Assert.Equal("success", phases["prune_paths"]);
            Assert.Equal("skipped", phases["replace_db"]);

            Assert.Equal(CommandExitCodes.Success, checkExit);
            Assert.Equal(string.Empty, checkStderr);
            Assert.Equal("existing check db", File.ReadAllText(checkDbPath));

            using var checkDocument = JsonDocument.Parse(checkStdout);
            var checkRoot = checkDocument.RootElement;
            Assert.Equal("success", checkRoot.GetProperty("status").GetString());
            Assert.Equal("check", checkRoot.GetProperty("mode").GetString());
            Assert.True(checkRoot.GetProperty("dry_run").GetBoolean());
            var replaceDbPhase = checkRoot.GetProperty("validation_phases")
                .EnumerateArray()
                .Single(phase => phase.GetProperty("phase").GetString() == "replace_db");
            Assert.Equal("skipped", replaceDbPhase.GetProperty("status").GetString());
            Assert.Contains("check mode", replaceDbPhase.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void ImportArchive_InvalidArchiveJsonReportsRootCause_Issue4328()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_import_invalid_archive");
        try
        {
            var archivePath = Path.Combine(projectRoot, "not-an-archive.zip");
            var dbPath = Path.Combine(projectRoot, "imported", "codeindex.db");
            File.WriteAllText(archivePath, "not a zip archive");

            var (exitCode, stdout, stderr) = RunCliInSubprocess([
                "import", archivePath, "--db", dbPath, "--json"
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.Equal("import", root.GetProperty("command").GetString());
            Assert.Equal("open_archive", root.GetProperty("phase").GetString());
            Assert.Equal("import_failed", root.GetProperty("error_code").GetString());
            Assert.Equal("invalid_archive", root.GetProperty("root_cause").GetString());
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Doctor_PrintsRedactedEnvironmentSummary()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(
            ["doctor"],
            new Dictionary<string, string?>
            {
                ["CDIDX_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "cdidx-doctor-data"),
                ["CDIDX_GITHUB_TOKEN"] = "secret-token-value",
                [GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable] = "true",
                ["CDIDX_PRIVATE_KEY"] = "private-key-value",
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("cdidx doctor", stdout);
        Assert.Contains("version", stdout);
        Assert.Contains("rid", stdout);
        Assert.Contains("terminal:", stdout);
        Assert.Contains("paths:", stdout);
        Assert.Contains("github:", stdout);
        Assert.Contains("proxy_default_credentials", stdout);
        Assert.Contains("enabled", stdout);
        Assert.Contains("max_request_timeout_s", stdout);
        Assert.Contains("cdidx_env:", stdout);
        Assert.Contains("CDIDX_DATA_DIR", stdout);
        Assert.Contains("CDIDX_GITHUB_TOKEN", stdout);
        Assert.Contains(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, stdout);
        Assert.Contains("CDIDX_PRIVATE_KEY", stdout);
        Assert.Contains("<redacted>", stdout);
        Assert.DoesNotContain("secret-token-value", stdout);
        Assert.DoesNotContain("= true", stdout);
        Assert.DoesNotContain("private-key-value", stdout);
    }

    [ProductionRuntimeFact]
    public void TopLevelHelp_DefaultIsBriefAndExtendedHelpKeepsFullReference()
    {
        var (briefExit, briefStdout, briefStderr) = RunCliInSubprocess(["--help"]);
        var (fullExit, fullStdout, fullStderr) = RunCliInSubprocess(["--help-all"]);
        var (aliasExit, aliasStdout, aliasStderr) = RunCliInSubprocess(["help-all"]);

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

        Assert.Equal(0, aliasExit);
        Assert.Equal(string.Empty, aliasStderr);
        Assert.Contains("Index and update options:", aliasStdout);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeTheory]
    [InlineData("completions")]
    [InlineData("completions", "bash", "extra")]
    public void CompletionsCommand_ErrorsUseCommandUsage(params string[] args)
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Usage: cdidx completions <shell>", stderr);
        Assert.DoesNotContain("Usage: cdidx --completions <shell>", stderr);
    }

    [ProductionRuntimeTheory]
    [InlineData(CommandExitCodes.UsageError, "license supports --json only; --json=<format> is not supported.", "license", "--json=array")]
    [InlineData(CommandExitCodes.InvalidArgument, "Unknown license argument: --bogus", "license", "--json", "--bogus")]
    [InlineData(CommandExitCodes.UsageError, "--json is not supported for completions.", "completions", "--json")]
    [InlineData(CommandExitCodes.UsageError, "--json is not supported for completions.", "completions", "zsh", "--json")]
    [InlineData(CommandExitCodes.InvalidArgument, "config show supports --json only; --json=<format> is not supported.", "config", "show", "--json=array")]
    public void UtilityCommands_JsonFlagsReturnStructuredUnsupportedErrors(int expectedExitCode, string expectedMessage, params string[] args)
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(args);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("message").GetString());
    }

    [ProductionRuntimeFact]
    public void License_JsonPrintsStableLicenseTrademarkAndCommercialUseContract_Issue4713()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["license", "--json"]);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("api_version").GetString());
        Assert.Equal("FSL-1.1-ALv2", root.GetProperty("license").GetProperty("identifier").GetString());
        Assert.Equal("Apache-2.0", root.GetProperty("license").GetProperty("future_license").GetString());
        Assert.True(root.GetProperty("commercial_use").GetProperty("non_competing_use_allowed").GetBoolean());
        Assert.True(root.GetProperty("commercial_use").GetProperty("competing_products_or_services_require_separate_agreement").GetBoolean());
        Assert.False(root.GetProperty("trademark").GetProperty("derivative_branding_allowed").GetBoolean());
        Assert.False(root.GetProperty("trademark").GetProperty("endorsement_branding_allowed").GetBoolean());
        Assert.Contains(
            root.GetProperty("trademark").GetProperty("names").EnumerateArray(),
            element => element.GetString() == "cdidx");
        Assert.Contains(
            root.GetProperty("documents").EnumerateArray(),
            element => element.GetString() == "TRADEMARKS.md");
    }

    [ProductionRuntimeFact]
    public void Mcp_JsonFlagReturnsExplicitUnsupportedError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["mcp", "--json"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json is not supported for mcp", stderr);
        Assert.Contains("Usage: cdidx mcp", stderr);
        Assert.Contains("Note: --json is not supported", stderr);
        Assert.Contains("LF-delimited line", stderr);
        Assert.Contains("lifecycle diagnostics are written to stderr", stderr);
    }

    [ProductionRuntimeFact]
    public void Completions_ExtraArgsReturnUsageError()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["--completions", "bash", "extra"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("accepts exactly one shell value", stderr);
        Assert.Contains("powershell", stderr);
        Assert.Contains("Usage: cdidx --completions <shell>", stderr);
    }

    [ProductionRuntimeTheory]
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

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Suggestions_AddHelpDocumentsWriteContract_Issue4422()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "add", "--help"]);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Usage: cdidx suggestions add", stdout);
        Assert.Contains("symbol_extraction", stdout);
        Assert.Contains("default: other", stdout);
        Assert.Contains("deduplicated by normalized category, language, and description", stdout);
        Assert.Contains("apply only to `suggestions export --format issue-drafts`", stdout);
        Assert.Contains("Examples:", stdout);
    }

    [ProductionRuntimeFact]
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
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("total_count").GetInt32());
        Assert.Equal(1, root.GetProperty("returned_count").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, root.GetProperty("next_offset").GetInt32());
        var item = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal(middle.Hash, item.GetProperty("id").GetString());
        Assert.Equal("Middle suggestion", item.GetProperty("title").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_QuerySearchesRedactedHistoryBeforePagination_Issue5061()
    {
        using var fixture = SuggestionFixture.Create();
        var title = fixture.Add(
            "title_query_category",
            "title_query_language",
            "Unrelated description",
            submitted: false,
            sampledTitle: "Ｆｕｌｌｗｉｄｔｈ　Ｔｉｔｌｅ");
        var description = fixture.Add("description_query_category", null, "Unique description needle 5061", submitted: false);
        var context = fixture.Add(
            "context_query_category",
            null,
            "Unrelated context description",
            submitted: false,
            context: "Unique context needle 5061");
        var evidence = fixture.Add(
            "evidence_query_category",
            null,
            "Unrelated evidence description",
            submitted: false,
            evidencePaths: ["src/UniqueEvidenceNeedle5061.cs"]);
        var category = fixture.Add("unique_category_needle_5061", null, "Unrelated category description", submitted: false);
        var language = fixture.Add("language_query_category", "unique_language_needle_5061", "Unrelated language description", submitted: false);

        AssertQueryReturns("fullwidth title", title);
        AssertQueryReturns("DESCRIPTION NEEDLE 5061", description);
        AssertQueryReturns("context needle 5061", context);
        AssertQueryReturns("uniqueevidenceneedle5061", evidence);
        AssertQueryReturns("unique_category_needle_5061", category);
        AssertQueryReturns("unique_language_needle_5061", language);
        AssertQueryReturns(title.Hash[..16], title);

        var olderMatch = fixture.Add("output_format", "csharp", "Pagination needle 5061 older", submitted: false);
        fixture.Add("output_format", "csharp", "Newest but unrelated", submitted: false);
        fixture.Add("output_format", "csharp", "Pagination needle 5061 newer", submitted: false);
        var (pageExitCode, pageStdout, pageStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath,
            "--category", "output_format", "--language", "csharp",
            "--query", "pagination NEEDLE 5061", "--limit", "1", "--offset", "1", "--json"
        ]);

        Assert.Equal(CommandExitCodes.Success, pageExitCode);
        Assert.Equal(string.Empty, pageStderr);
        using var pageDoc = JsonDocument.Parse(pageStdout);
        Assert.Equal(2, pageDoc.RootElement.GetProperty("total_count").GetInt32());
        var pageItem = Assert.Single(pageDoc.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(olderMatch.Hash, pageItem.GetProperty("id").GetString());

        void AssertQueryReturns(string query, SuggestionRecord expected)
        {
            var (exitCode, stdout, stderr) = RunCliInSubprocess([
                "suggestions", "list", "--db", fixture.DbPath, "--query", query, "--json"
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(string.IsNullOrEmpty(stderr), stderr);
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(1, doc.RootElement.GetProperty("total_count").GetInt32());
            var result = Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(expected.Hash, result.GetProperty("id").GetString());
        }
    }

    [ProductionRuntimeFact]
    public void Suggestions_QueryProjectionsAreBoundedAndRedacted_Issue5061()
    {
        using var fixture = SuggestionFixture.Create();
        const string rawSecret = "query_secret_5061";
        for (var i = 0; i < 24; i++)
        {
            fixture.Add(
                i % 2 == 0 ? "output_format" : "language_support",
                i % 3 == 0 ? "csharp" : "rust",
                $"bulk-marker-5061 suggestion {i:D2} {new string((char)('a' + i % 26), 1024)}",
                submitted: false,
                sampledTitle: i == 0 ? $"api-token={rawSecret}" : $"Bulk suggestion {i:D2}",
                evidencePaths: i == 0 ? [$"src/api-token={rawSecret}.cs"] : [$"src/Bulk{i:D2}.cs"]);
        }

        var (countExitCode, countStdout, countStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--query", "BULK-MARKER-5061", "--count", "--json"
        ]);
        var (summaryExitCode, summaryStdout, summaryStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--query", "bulk-marker-5061", "--summary-only"
        ]);
        var (compactExitCode, compactStdout, compactStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "json",
            "--query", "bulk-marker-5061", "--compact", "--limit", "24"
        ]);
        var fullCompactBytes = Encoding.UTF8.GetByteCount(compactStdout);
        var byteBudget = fullCompactBytes - 256;
        var (boundedExitCode, boundedStdout, boundedStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "json",
            "--query", "bulk-marker-5061", "--compact", "--limit", "24",
            "--max-json-bytes", byteBudget.ToString(CultureInfo.InvariantCulture)
        ]);
        var (secretExitCode, secretStdout, secretStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath,
            "--query", $"api-token={rawSecret}", "--count", "--json"
        ]);
        var (zeroPageExitCode, zeroPageStdout, zeroPageStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath,
            "--query", "bulk-marker-5061", "--compact", "--limit", "0"
        ]);

        Assert.Equal(CommandExitCodes.Success, countExitCode);
        Assert.Equal(string.Empty, countStderr);
        using var countDoc = JsonDocument.Parse(countStdout);
        Assert.Equal("count", countDoc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(24, countDoc.RootElement.GetProperty("count").GetInt32());
        Assert.True(countDoc.RootElement.GetProperty("total_count_authoritative").GetBoolean());
        Assert.Equal(0, countDoc.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal(0, countDoc.RootElement.GetProperty("pagination_omitted_count").GetInt32());
        Assert.Equal(24, countDoc.RootElement.GetProperty("projection_omitted_count").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(countStdout) < 1024);

        Assert.Equal(CommandExitCodes.Success, summaryExitCode);
        Assert.Equal(string.Empty, summaryStderr);
        using var summaryDoc = JsonDocument.Parse(summaryStdout);
        Assert.Equal("summary", summaryDoc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(24, summaryDoc.RootElement.GetProperty("total_count").GetInt32());
        var summary = summaryDoc.RootElement.GetProperty("summary");
        Assert.Equal(24, summary.GetProperty("by_status").GetProperty("counts").GetProperty("draft").GetInt32());
        Assert.Equal(12, summary.GetProperty("by_category").GetProperty("counts").GetProperty("output_format").GetInt32());
        Assert.Equal(12, summary.GetProperty("by_category").GetProperty("counts").GetProperty("language_support").GetInt32());
        Assert.Equal(0, summaryDoc.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal(0, summaryDoc.RootElement.GetProperty("pagination_omitted_count").GetInt32());
        Assert.Equal(24, summaryDoc.RootElement.GetProperty("projection_omitted_count").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(summaryStdout) < 4096);

        Assert.Equal(CommandExitCodes.Success, compactExitCode);
        Assert.Equal(string.Empty, compactStderr);
        Assert.DoesNotContain(rawSecret, compactStdout, StringComparison.Ordinal);
        using var compactDoc = JsonDocument.Parse(compactStdout);
        var compactItem = compactDoc.RootElement.GetProperty("results")[0];
        Assert.Equal(4, compactItem.EnumerateObject().Count());
        Assert.False(compactItem.TryGetProperty("description", out _));
        Assert.False(compactItem.TryGetProperty("category", out _));
        Assert.False(compactItem.TryGetProperty("language", out _));
        Assert.Contains(
            compactDoc.RootElement.GetProperty("results").EnumerateArray(),
            item => item.GetProperty("title").GetString()!.Contains("redact", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(CommandExitCodes.Success, boundedExitCode);
        Assert.Equal(string.Empty, boundedStderr);
        Assert.True(Encoding.UTF8.GetByteCount(boundedStdout) <= byteBudget);
        using var boundedDoc = JsonDocument.Parse(boundedStdout);
        Assert.True(boundedDoc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(boundedDoc.RootElement.GetProperty("byte_limit_omitted_count").GetInt32() > 0);
        Assert.True(boundedDoc.RootElement.GetProperty("returned_count").GetInt32() < 24);
        Assert.True(boundedDoc.RootElement.GetProperty("has_more").GetBoolean());
        Assert.True(boundedDoc.RootElement.GetProperty("next_offset").GetInt32() > 0);
        Assert.Contains("--offset", boundedDoc.RootElement.GetProperty("recovery_guidance").GetString());

        Assert.Equal(CommandExitCodes.Success, secretExitCode);
        Assert.Equal(string.Empty, secretStderr);
        using var secretDoc = JsonDocument.Parse(secretStdout);
        Assert.Equal(0, secretDoc.RootElement.GetProperty("count").GetInt32());
        Assert.DoesNotContain(rawSecret, secretStdout, StringComparison.Ordinal);

        Assert.Equal(CommandExitCodes.UsageError, zeroPageExitCode);
        Assert.Equal(string.Empty, zeroPageStderr);
        Assert.Contains("--limit 0", zeroPageStdout, StringComparison.Ordinal);
    }

    [ProductionRuntimeFact]
    public void Suggestions_ListDefaultVerbAcceptsTopLevelJsonFlags_Issue4171()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("symbol_extraction", "csharp", "Oldest suggestion", submitted: false);
        var middle = fixture.Add("language_support", "rust", "Middle suggestion", submitted: false);
        fixture.Add("output_format", "python", "Newest suggestion", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "--db", fixture.DbPath, "--json", "--limit", "1", "--offset", "1"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var item = Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(middle.Hash, item.GetProperty("id").GetString());
        Assert.Equal("Middle suggestion", item.GetProperty("title").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ListJsonEmptyReturnsArray_Issue3896()
    {
        using var fixture = SuggestionFixture.Create();

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--json"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("total_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("returned_count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ListDefaultVerbJsonEmptyReturnsArray_Issue4171()
    {
        using var fixture = SuggestionFixture.Create();

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "--db", fixture.DbPath, "--json", "--limit", "120"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("total_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ListUsesSharedDataDirResolutionWhenDbIsOmitted()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("symbol_extraction", "csharp", "Shared data-dir suggestion", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(
            ["suggestions", "list", "--json"],
            new Dictionary<string, string?>
            {
                [DbPathResolver.DataDirEnvironmentVariable] = Path.GetDirectoryName(fixture.DbPath)!,
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(record.Hash, doc.RootElement.GetProperty("results")[0].GetProperty("id").GetString());
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Suggestions_ShowJsonMissingIdReturnsStructuredError_Issue3896()
    {
        using var fixture = SuggestionFixture.Create();

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "show", "missing", "--db", fixture.DbPath, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Suggestion not found: missing", doc.RootElement.GetProperty("message").GetString());
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Suggestions_AddJsonCreatesLocalDraftAndDeduplicates_Issue4310()
    {
        using var fixture = SuggestionFixture.Create();
        string[] addArgs =
        [
            "suggestions", "add",
            "--db", fixture.DbPath,
            "--json",
            "--description", "Record local dogfood finding before opening GitHub issues",
            "--category", "output_format",
            "--language", "csharp",
            "--agent", "codex",
            "--context", "Observed while triaging local audit output.",
            "--title", "Local dogfood finding store",
            "--evidence-path", "src/CodeIndex/Cli/SuggestionsCommandRunner.cs",
        ];

        var (firstExitCode, firstStdout, firstStderr) = RunCliInSubprocess(addArgs);
        var (secondExitCode, secondStdout, secondStderr) = RunCliInSubprocess(addArgs);
        var (listExitCode, listStdout, listStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--json", "--status", "draft"
        ]);

        Assert.Equal(0, firstExitCode);
        Assert.Equal(0, secondExitCode);
        Assert.Equal(0, listExitCode);
        Assert.Equal(string.Empty, firstStderr);
        Assert.Equal(string.Empty, secondStderr);
        Assert.Equal(string.Empty, listStderr);
        using var firstDoc = JsonDocument.Parse(firstStdout);
        using var secondDoc = JsonDocument.Parse(secondStdout);
        using var listDoc = JsonDocument.Parse(listStdout);
        var first = firstDoc.RootElement;
        var second = secondDoc.RootElement;
        var suggestion = first.GetProperty("suggestion");
        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.True(second.GetProperty("duplicate").GetBoolean());
        Assert.Equal(suggestion.GetProperty("id").GetString(), second.GetProperty("suggestion").GetProperty("id").GetString());
        Assert.NotEqual(suggestion.GetProperty("id").GetString(), suggestion.GetProperty("revision_hash").GetString());
        Assert.NotEqual(
            SuggestionStore.ComputeHash("output_format", "csharp", "Record local dogfood finding before opening GitHub issues"),
            suggestion.GetProperty("id").GetString());
        Assert.Equal("draft", suggestion.GetProperty("status").GetString());
        Assert.Equal("output_format", suggestion.GetProperty("category").GetString());
        Assert.Equal("csharp", suggestion.GetProperty("language").GetString());
        Assert.Equal("codex", suggestion.GetProperty("agent").GetString());
        Assert.Equal("Observed while triaging local audit output.", suggestion.GetProperty("context").GetString());
        Assert.Equal("Local dogfood finding store", suggestion.GetProperty("sampled_title").GetString());
        Assert.Equal("src/CodeIndex/Cli/SuggestionsCommandRunner.cs", suggestion.GetProperty("evidence_paths")[0].GetString());
        Assert.Equal(suggestion.GetProperty("id").GetString(), listDoc.RootElement.GetProperty("results")[0].GetProperty("id").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_AddExplicitDbInSharedTempUsesPrivateSidecar_Issue4589()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var sharedTempRoot = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "/private/tmp" : "/tmp";
        if (!Directory.Exists(sharedTempRoot))
            return;

        var dbPath = Path.Combine(sharedTempRoot, $"cdidx-issue4589-{Guid.NewGuid():N}.db");
        var dbName = Path.GetFileNameWithoutExtension(dbPath);
        var sidecarDirectory = DataDirectorySecurity.ResolveSensitiveSidecarDirectoryForDatabase(dbPath, "suggestions");
        var sidecarPath = Path.Combine(sidecarDirectory, $"suggestions-{dbName}.json");
        var adjacentSidecarPath = Path.Combine(sharedTempRoot, $"suggestions-{dbName}.json");
        try
        {
            File.WriteAllBytes(dbPath, []);

            var (addExitCode, addStdout, addStderr) = RunCliInSubprocess([
                "suggestions", "add", "Shared temporary database sidecar regression", "--category", "bug", "--db", dbPath, "--json"
            ]);
            var (listExitCode, listStdout, listStderr) = RunCliInSubprocess([
                "suggestions", "list", "--db", dbPath, "--json"
            ]);

            Assert.Equal(CommandExitCodes.Success, addExitCode);
            Assert.Equal(CommandExitCodes.Success, listExitCode);
            Assert.Equal(string.Empty, addStderr);
            Assert.Equal(string.Empty, listStderr);
            using var addDocument = JsonDocument.Parse(addStdout);
            using var listDocument = JsonDocument.Parse(listStdout);
            Assert.True(addDocument.RootElement.GetProperty("created").GetBoolean());
            Assert.Single(listDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(File.Exists(sidecarPath));
            Assert.False(File.Exists(adjacentSidecarPath));
            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(sidecarDirectory) & DataDirectorySecurity.PermissionBits);
            Assert.Equal(
                DataDirectorySecurity.PrivateFileMode,
                File.GetUnixFileMode(sidecarPath) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(sidecarDirectory);
            TestProjectHelper.DeleteFile(dbPath);
            TestProjectHelper.DeleteFile(adjacentSidecarPath);
            TestProjectHelper.DeleteFile(Path.Combine(sharedTempRoot, $"suggestions-{dbName}.lock"));
            TestProjectHelper.DeleteFile(Path.Combine(sharedTempRoot, $"suggestions-{dbName}.archive.jsonl"));
        }
    }

    [ProductionRuntimeFact]
    public void Suggestions_AddUnusableSidecarPathReturnsStructuredFilesystemError_Issue4589()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_suggestion_store_error");
        var blockedParent = Path.Combine(root, "not-a-directory");
        var dbPath = Path.Combine(blockedParent, "codeindex.db");
        try
        {
            File.WriteAllText(blockedParent, "file blocks directory creation");

            var (exitCode, stdout, stderr) = RunCliInSubprocess([
                "suggestions", "add", "Structured storage error regression", "--db", dbPath, "--json"
            ]);

            Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.SuggestionStoreUnavailable, document.RootElement.GetProperty("error_code").GetString());
            Assert.Equal("io_error", document.RootElement.GetProperty("category").GetString());
            Assert.Contains("--db", document.RootElement.GetProperty("hint").GetString());
            Assert.DoesNotContain(dbPath, stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [ProductionRuntimeFact]
    public void Suggestions_AddHelpIncludesLocalDraftFlags_Issue4310()
    {
        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "add", "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("cdidx suggestions add <description>", stdout);
        Assert.Contains("--description <text>", stdout);
        Assert.Contains("--evidence-path <path>", stdout);
    }

    [ProductionRuntimeFact]
    public void Suggestions_UpdatePreservesStableIdAcrossShowExportAndDelete_Issue4588()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("bug", "csharp", "Malformed draft description", submitted: false, context: "bad context", sampledTitle: "Stale title");

        var (updateExitCode, updateStdout, updateStderr) = RunCliInSubprocess([
            "suggestions", "update", record.Hash[..12], "--db", fixture.DbPath, "--json",
            "--description", "Corrected draft description", "--context", "correct context", "--title", "Corrected title",
            "--evidence-path", "src/CodeIndex/Cli/SuggestionsCommandRunner.cs"
        ]);
        var (showExitCode, showStdout, showStderr) = RunCliInSubprocess([
            "suggestions", "show", record.Hash[..12], "--db", fixture.DbPath, "--json"
        ]);
        var (exportExitCode, exportStdout, exportStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "json"
        ]);
        var (deleteExitCode, deleteStdout, deleteStderr) = RunCliInSubprocess([
            "suggestions", "delete", record.Hash[..12], "--db", fixture.DbPath, "--json"
        ]);

        Assert.Equal(CommandExitCodes.Success, updateExitCode);
        Assert.Equal(CommandExitCodes.Success, showExitCode);
        Assert.Equal(CommandExitCodes.Success, exportExitCode);
        Assert.Equal(CommandExitCodes.Success, deleteExitCode);
        Assert.Equal(string.Empty, updateStderr);
        Assert.Equal(string.Empty, showStderr);
        Assert.Equal(string.Empty, exportStderr);
        Assert.Equal(string.Empty, deleteStderr);
        using var updateDoc = JsonDocument.Parse(updateStdout);
        using var showDoc = JsonDocument.Parse(showStdout);
        using var exportDoc = JsonDocument.Parse(exportStdout);
        using var deleteDoc = JsonDocument.Parse(deleteStdout);
        var suggestion = updateDoc.RootElement.GetProperty("suggestion");
        var revisionHash = suggestion.GetProperty("revision_hash").GetString();
        Assert.Equal("updated", updateDoc.RootElement.GetProperty("action").GetString());
        Assert.Equal("Corrected draft description", suggestion.GetProperty("description").GetString());
        Assert.Equal("correct context", suggestion.GetProperty("context").GetString());
        Assert.Equal("Corrected title", suggestion.GetProperty("sampled_title").GetString());
        Assert.Equal("src/CodeIndex/Cli/SuggestionsCommandRunner.cs", suggestion.GetProperty("evidence_paths")[0].GetString());
        Assert.Equal(record.Hash, suggestion.GetProperty("id").GetString());
        Assert.NotEqual(record.Hash, revisionHash);
        Assert.Equal(record.CreatedAt, suggestion.GetProperty("created_at").GetDateTime());
        Assert.Equal(record.Hash, showDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal(revisionHash, showDoc.RootElement.GetProperty("revision_hash").GetString());
        var exported = Assert.Single(exportDoc.RootElement.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(record.Hash, exported.GetProperty("id").GetString());
        Assert.Equal(revisionHash, exported.GetProperty("revision_hash").GetString());
        Assert.Equal(record.Hash, deleteDoc.RootElement.GetProperty("suggestion").GetProperty("id").GetString());
        Assert.Equal(revisionHash, deleteDoc.RootElement.GetProperty("suggestion").GetProperty("revision_hash").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_UpdateStatusPersistsAuditMetadataAndValidatesLifecycle_Issue4719()
    {
        using var fixture = SuggestionFixture.Create();
        var draft = fixture.Add("bug", "csharp", "Lifecycle state needs maintainer curation", submitted: false);
        var submitted = fixture.Add("bug", "csharp", "Upstream issue has been resolved", submitted: true);

        var (transitionExitCode, transitionStdout, transitionStderr) = RunCliInSubprocess([
            "suggestions", "update", draft.Hash[..12], "--db", fixture.DbPath, "--json",
            "--status", "wont_fix", "--actor", "widthdom", "--reason", "Outside the supported scope"
        ]);
        var (filterExitCode, filterStdout, filterStderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--json", "--status", "wont_fix"
        ]);
        var (resolvedExitCode, resolvedStdout, resolvedStderr) = RunCliInSubprocess([
            "suggestions", "update", submitted.Hash[..12], "--db", fixture.DbPath, "--json",
            "--status", "resolved_in_upstream", "--actor", "widthdom"
        ]);
        var (invalidExitCode, invalidStdout, invalidStderr) = RunCliInSubprocess([
            "suggestions", "update", draft.Hash[..12], "--db", fixture.DbPath,
            "--status", "submitted_pending_triage"
        ]);

        Assert.Equal(CommandExitCodes.Success, transitionExitCode);
        Assert.Equal(CommandExitCodes.Success, filterExitCode);
        Assert.Equal(CommandExitCodes.Success, resolvedExitCode);
        Assert.Equal(CommandExitCodes.UsageError, invalidExitCode);
        Assert.Equal(string.Empty, transitionStderr);
        Assert.Equal(string.Empty, filterStderr);
        Assert.Equal(string.Empty, resolvedStderr);
        Assert.Equal(string.Empty, invalidStdout);
        Assert.Contains("managed by GitHub submission", invalidStderr);

        using var transitionDoc = JsonDocument.Parse(transitionStdout);
        var transitioned = transitionDoc.RootElement.GetProperty("suggestion");
        Assert.Equal("status_changed", transitionDoc.RootElement.GetProperty("action").GetString());
        Assert.Equal("wont_fix", transitioned.GetProperty("status").GetString());
        Assert.Equal("draft", transitioned.GetProperty("previous_status").GetString());
        Assert.Equal("widthdom", transitioned.GetProperty("status_changed_by").GetString());
        Assert.Equal("Outside the supported scope", transitioned.GetProperty("status_change_reason").GetString());
        Assert.NotEqual(default, transitioned.GetProperty("status_changed_at").GetDateTime());

        using var filterDoc = JsonDocument.Parse(filterStdout);
        var filtered = Assert.Single(filterDoc.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(draft.Hash, filtered.GetProperty("id").GetString());
        Assert.Equal("wont_fix", filtered.GetProperty("status").GetString());
        Assert.False(filtered.GetProperty("submitted_to_github").GetBoolean());

        using var resolvedDoc = JsonDocument.Parse(resolvedStdout);
        var resolved = resolvedDoc.RootElement.GetProperty("suggestion");
        Assert.Equal("resolved_in_upstream", resolved.GetProperty("status").GetString());
        Assert.Equal("submitted_pending_triage", resolved.GetProperty("previous_status").GetString());
        Assert.Equal(resolved.GetProperty("status_changed_at").GetDateTime(), resolved.GetProperty("resolved_at").GetDateTime());
    }

    [ProductionRuntimeFact]
    public void Suggestions_DeleteRemovesDraft_Issue4441()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("bug", "csharp", "Draft to remove", submitted: false);

        var (deleteExitCode, deleteStdout, deleteStderr) = RunCliInSubprocess([
            "suggestions", "delete", record.Hash[..12], "--db", fixture.DbPath, "--json"
        ]);
        var (showExitCode, _, _) = RunCliInSubprocess([
            "suggestions", "show", record.Hash[..12], "--db", fixture.DbPath, "--json"
        ]);

        Assert.Equal(CommandExitCodes.Success, deleteExitCode);
        Assert.Equal(string.Empty, deleteStderr);
        using var doc = JsonDocument.Parse(deleteStdout);
        Assert.Equal("deleted", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(record.Hash, doc.RootElement.GetProperty("suggestion").GetProperty("id").GetString());
        Assert.Equal(CommandExitCodes.NotFound, showExitCode);
    }

    [ProductionRuntimeFact]
    public void Suggestions_MutationsRejectSubmittedRecords_Issue4441()
    {
        using var fixture = SuggestionFixture.Create();
        var record = fixture.Add("bug", "csharp", "Submitted suggestion", submitted: true);

        var (updateExitCode, updateStdout, updateStderr) = RunCliInSubprocess([
            "suggestions", "update", record.Hash[..12], "--db", fixture.DbPath, "--description", "Changed"
        ]);
        var (deleteExitCode, deleteStdout, deleteStderr) = RunCliInSubprocess([
            "suggestions", "delete", record.Hash[..12], "--db", fixture.DbPath
        ]);
        var (showExitCode, showStdout, _) = RunCliInSubprocess([
            "suggestions", "show", record.Hash[..12], "--db", fixture.DbPath, "--json"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, updateExitCode);
        Assert.Equal(CommandExitCodes.UsageError, deleteExitCode);
        Assert.Equal(string.Empty, updateStdout);
        Assert.Equal(string.Empty, deleteStdout);
        Assert.Contains("not an editable draft", updateStderr);
        Assert.Contains("not an editable draft", deleteStderr);
        Assert.Equal(CommandExitCodes.Success, showExitCode);
        Assert.Contains(record.Hash, showStdout);
    }

    [ProductionRuntimeFact]
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
        var item = doc.RootElement.GetProperty("results")[0];
        Assert.Equal(2, item.GetProperty("submit_attempt_count").GetInt32());
        Assert.Equal(attemptedAt, item.GetProperty("last_submit_attempt").GetDateTime());
        Assert.Equal("API 422: validation failed", item.GetProperty("last_submit_error").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ListJsonUsesSampledTitle_Issue4432()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "output_format",
            "csharp",
            "A longer prose description that should not be exposed as the concise list title",
            submitted: false,
            sampledTitle: "Concise sampled title");

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "list", "--db", fixture.DbPath, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Concise sampled title", doc.RootElement.GetProperty("results")[0].GetProperty("title").GetString());
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Suggestions_ExportMarkdownOutputIsAtomicAndRequiresExplicitOverwrite_Issue4719()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("output_format", "csharp", "Write suggestions to a bounded file", submitted: false);
        var outputPath = fixture.GetPath("exports", "suggestions.md");

        var (createExitCode, createStdout, createStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown", "--output", outputPath
        ]);
        var originalBytes = File.ReadAllBytes(outputPath);
        var (refuseExitCode, refuseStdout, refuseStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown", "--output", outputPath
        ]);
        fixture.Add("documentation", null, "Document explicit overwrite behavior", submitted: false);
        var (overwriteExitCode, overwriteStdout, overwriteStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown",
            "--output", outputPath, "--overwrite"
        ]);
        var jsonOutputPath = fixture.GetPath("exports", "suggestions-summary.md");
        var (jsonExitCode, jsonStdout, jsonStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown",
            "--output", jsonOutputPath, "--json"
        ]);

        Assert.Equal(CommandExitCodes.Success, createExitCode);
        Assert.Equal(CommandExitCodes.UsageError, refuseExitCode);
        Assert.Equal(CommandExitCodes.Success, overwriteExitCode);
        Assert.Equal(CommandExitCodes.Success, jsonExitCode);
        Assert.Equal(string.Empty, createStderr);
        Assert.Equal(string.Empty, refuseStdout);
        Assert.Equal(string.Empty, overwriteStderr);
        Assert.Equal(string.Empty, jsonStderr);
        Assert.Contains("Wrote 1 suggestion", createStdout);
        Assert.Contains("already exists", refuseStderr);
        Assert.Contains("--overwrite", refuseStderr);
        Assert.Contains("Wrote 2 suggestions", overwriteStdout);
        Assert.False(originalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("Write suggestions to a bounded file", Encoding.UTF8.GetString(originalBytes));
        Assert.Contains("Document explicit overwrite behavior", File.ReadAllText(outputPath, Encoding.UTF8));
        using var jsonSummary = JsonDocument.Parse(jsonStdout);
        Assert.Equal("success", jsonSummary.RootElement.GetProperty("status").GetString());
        Assert.Equal("markdown", jsonSummary.RootElement.GetProperty("format").GetString());
        Assert.Equal(Path.GetFullPath(jsonOutputPath), jsonSummary.RootElement.GetProperty("output_path").GetString());
        Assert.Contains("Document explicit overwrite behavior", File.ReadAllText(jsonOutputPath, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outputPath)!, "*.tmp"));
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportRejectsProtectedDatabaseThroughDirectoryAlias_Issue4719()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("output_format", "csharp", "Protect export source aliases", submitted: false);
        File.WriteAllText(fixture.DbPath, "database sentinel", Encoding.UTF8);
        var databaseDirectory = Path.GetDirectoryName(fixture.DbPath)!;
        var aliasDirectory = fixture.GetPath("database-alias");
        try
        {
            Directory.CreateSymbolicLink(aliasDirectory, databaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var originalBytes = File.ReadAllBytes(fixture.DbPath);
        try
        {
            var aliasPath = Path.Combine(aliasDirectory, Path.GetFileName(fixture.DbPath));
            var (exitCode, stdout, stderr) = RunCliInSubprocess([
                "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown",
                "--output", aliasPath, "--overwrite"
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("must not replace", stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(fixture.DbPath));
        }
        finally
        {
            Directory.Delete(aliasDirectory);
        }
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportIssueDraftOutputWritesJsonAndRejectsStoreTargets_Issue4719()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "output_format",
            "csharp",
            "Write issue drafts to a file",
            submitted: false,
            sampledTitle: "Add file output");
        var openIssuesPath = fixture.WriteOpenIssuesJson("[]");
        var outputPath = fixture.GetPath("exports", "issue-drafts.json");

        var (writeExitCode, writeStdout, writeStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts",
            "--open-issues", openIssuesPath, "--output", outputPath, "--json"
        ]);
        var (dbExitCode, dbStdout, dbStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown",
            "--output", fixture.DbPath, "--overwrite"
        ]);
        var (storeExitCode, storeStdout, storeStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown",
            "--output", fixture.StorePath, "--overwrite"
        ]);

        Assert.Equal(CommandExitCodes.Success, writeExitCode);
        Assert.Equal(CommandExitCodes.UsageError, dbExitCode);
        Assert.Equal(CommandExitCodes.UsageError, storeExitCode);
        Assert.Equal(string.Empty, writeStderr);
        Assert.Equal(string.Empty, dbStdout);
        Assert.Equal(string.Empty, storeStdout);
        Assert.Contains("must not replace the selected database", dbStderr);
        Assert.Contains("must not replace the selected database", storeStderr);
        using var summary = JsonDocument.Parse(writeStdout);
        Assert.Equal("success", summary.RootElement.GetProperty("status").GetString());
        Assert.Equal("issue-drafts", summary.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, summary.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(Path.GetFullPath(outputPath), summary.RootElement.GetProperty("output_path").GetString());
        Assert.True(summary.RootElement.GetProperty("bytes").GetInt32() > 0);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath, Encoding.UTF8));
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            "[AI Suggestion] output_format: Add file output",
            document.RootElement.GetProperty("drafts")[0].GetProperty("title").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportMarkdownJsonFlagReturnsStructuredError_Issue4319()
    {
        using var fixture = SuggestionFixture.Create();

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown", "--json"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains("--format markdown", root.GetProperty("message").GetString());
        Assert.Contains("--format json", root.GetProperty("hint").GetString());
        Assert.DoesNotContain("# cdidx Suggestions", stdout);
    }

    [ProductionRuntimeFact]
    public void Suggestions_PreCommandPrettyPreservesStructuredFormatError_Issue4562()
    {
        using var fixture = SuggestionFixture.Create();

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "--pretty", "suggestions", "export", "--db", fixture.DbPath, "--format", "markdown", "--json"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains("--format markdown", root.GetProperty("message").GetString());
        Assert.Contains("--format json", root.GetProperty("hint").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportJsonCapsDetailedBodyFields()
    {
        using var fixture = SuggestionFixture.Create();
        var sentinel = "JSON_EXPORT_TAIL_SHOULD_NOT_APPEAR";
        var longDescription = new string('d', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel;
        var longContext = new string('c', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel;
        var longToolInvocation = new string('t', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel;
        fixture.Add(
            "output_format",
            "csharp",
            longDescription,
            submitted: false,
            context: longContext,
            toolInvocationContext: longToolInvocation);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "export", "--db", fixture.DbPath]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain(sentinel, stdout);
        using var doc = JsonDocument.Parse(stdout);
        var suggestion = doc.RootElement.GetProperty("suggestions")[0];
        AssertCappedSuggestionText(suggestion.GetProperty("description").GetString());
        AssertCappedSuggestionText(suggestion.GetProperty("context").GetString());
        AssertCappedSuggestionText(suggestion.GetProperty("tool_invocation_context").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportMarkdownCapsDetailedBodyFields()
    {
        using var fixture = SuggestionFixture.Create();
        var sentinel = "MARKDOWN_EXPORT_TAIL_SHOULD_NOT_APPEAR";
        fixture.Add(
            "output_format",
            "csharp",
            new string('d', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel,
            submitted: false,
            context: new string('c', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel,
            toolInvocationContext: new string('t', SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength + 256) + sentinel);

        var (exitCode, stdout, stderr) = RunCliInSubprocess(["suggestions", "export", "--db", fixture.DbPath, "--format", "markdown"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain(sentinel, stdout);
        Assert.Contains("[truncated]", stdout);
    }

    [ProductionRuntimeFact]
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
        Assert.Equal("medium", root.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
        Assert.Equal(0.45, root.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
        var draft = root.GetProperty("drafts")[0];
        Assert.Equal(record.Hash, draft.GetProperty("suggestion_id").GetString());
        Assert.Equal("enhancement", draft.GetProperty("labels")[0].GetString());
        Assert.Equal("src/CodeIndex/Cli/SuggestionsCommandRunner.cs", draft.GetProperty("evidence_paths")[0].GetString());
        var triage = draft.GetProperty("triage");
        var body = draft.GetProperty("body").GetString();
        Assert.Equal("medium", triage.GetProperty("severity").GetString());
        Assert.Equal("medium", triage.GetProperty("confidence").GetString());
        Assert.Equal(2, triage.GetProperty("evidence_count").GetInt32());
        Assert.Contains("merge evidence", triage.GetProperty("duplicate_guidance").GetString(), StringComparison.Ordinal);
        Assert.Contains("## Evidence paths", body);
        Assert.Contains("## Triage metadata", body);
        Assert.Contains("evidence_count: `2`", body);
        var preflight = draft.GetProperty("duplicate_preflight");
        Assert.Equal(1, preflight.GetProperty("match_count").GetInt32());
        Assert.Equal(2878, preflight.GetProperty("matches")[0].GetProperty("number").GetInt32());
        Assert.Equal("title_exact", preflight.GetProperty("matches")[0].GetProperty("reason").GetString());
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportIssueDraftsUsesAvailableGitHubTitleCapacity_Issue4462()
    {
        using var fixture = SuggestionFixture.Create();
        var sampledTitle = "Target StringBuilder materialization operations instead of building intermediate collections when exporting issue draft candidates";
        fixture.Add(
            "search_ranking",
            "csharp",
            "Issue draft title should retain the differentiating end of the sampled title",
            submitted: false,
            sampledTitle: sampledTitle);
        var openIssuesPath = fixture.WriteOpenIssuesJson("[]");

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var title = doc.RootElement.GetProperty("drafts")[0].GetProperty("title").GetString();
        Assert.Equal($"[AI Suggestion] search_ranking: {sampledTitle}", title);
        Assert.True(title!.Length <= GitHubIssueReporter.MaxGitHubIssueTitleLength);
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportIssueDraftsDuplicateThresholdFiltersMatches_Issue3827()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "output_format",
            "csharp",
            "Issue draft duplicate threshold should filter weaker title candidates",
            submitted: false,
            sampledTitle: "Tune duplicate thresholds",
            evidencePaths: ["src/CodeIndex/Cli/SuggestionsCommandRunner.cs"]);
        var openIssuesPath = fixture.WriteOpenIssuesJson("""
        [
          {
            "number": 3827,
            "title": "[AI Suggestion] output_format: Tune duplicate thresholds follow-up review backlog noisy candidate validation scheduler evidence report",
            "url": "https://github.com/Widthdom/CodeIndex/issues/3827",
            "labels": [{ "name": "enhancement" }]
          }
        ]
        """);

        var (highExitCode, highStdout, highStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts",
            "--open-issues", openIssuesPath, "--duplicate-confidence", "high"
        ]);
        var (customExitCode, customStdout, customStderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts",
            "--open-issues", openIssuesPath, "--duplicate-threshold", "0.4"
        ]);

        Assert.Equal(0, highExitCode);
        Assert.Equal(string.Empty, highStderr);
        using var highDoc = JsonDocument.Parse(highStdout);
        var highRoot = highDoc.RootElement;
        var highDraft = highRoot.GetProperty("drafts")[0];
        Assert.Equal("high", highRoot.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
        Assert.Equal(0.7, highRoot.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
        Assert.Equal(0, highDraft.GetProperty("duplicate_preflight").GetProperty("match_count").GetInt32());

        Assert.Equal(0, customExitCode);
        Assert.Equal(string.Empty, customStderr);
        using var customDoc = JsonDocument.Parse(customStdout);
        var customRoot = customDoc.RootElement;
        var customDraft = customRoot.GetProperty("drafts")[0];
        var customMatch = customDraft.GetProperty("duplicate_preflight").GetProperty("matches")[0];
        Assert.Equal("custom", customRoot.GetProperty("duplicate_preflight").GetProperty("confidence").GetString());
        Assert.Equal(0.4, customRoot.GetProperty("duplicate_preflight").GetProperty("minimum_score").GetDouble());
        Assert.Equal(1, customDraft.GetProperty("duplicate_preflight").GetProperty("match_count").GetInt32());
        Assert.Equal(3827, customMatch.GetProperty("number").GetInt32());
        Assert.Equal("title_label_contains", customMatch.GetProperty("reason").GetString());
        Assert.Equal(0.45, customMatch.GetProperty("score").GetDouble());
    }

    [ProductionRuntimeFact]
    public void Suggestions_DuplicateTuningRequiresIssueDraftExport_Issue3827()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add("output_format", "csharp", "Duplicate tuning should not be ignored by list.", submitted: false);

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "list", "--db", fixture.DbPath, "--format", "issue-drafts", "--duplicate-threshold", "0.4"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("suggestions export --format issue-drafts", stderr);
    }

    [ProductionRuntimeFact]
    public void Suggestions_ExportIssueDraftsGitHubOpenIssuesRequiresRepository_Issue3449()
    {
        using var fixture = SuggestionFixture.Create();
        fixture.Add(
            "output_format",
            "csharp",
            "Issue draft export should fetch duplicate preflight issues from GitHub",
            submitted: false,
            sampledTitle: "Fetch duplicate preflight issues from GitHub");

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", "github"
        ]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--open-issues github requires --repo", stderr);
        Assert.DoesNotContain("could not read --open-issues file 'github'", stderr);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
    public void Suggestions_ExportIssueDraftsCapsRenderedBody()
    {
        using var fixture = SuggestionFixture.Create();
        var sentinel = "ISSUE_DRAFT_TAIL_SHOULD_NOT_APPEAR";
        fixture.Add(
            "output_format",
            "csharp",
            new string('d', 10_000) + sentinel,
            submitted: false,
            sampledTitle: "Cap long issue draft bodies",
            context: new string('c', 10_000) + sentinel,
            toolInvocationContext: new string('t', 10_000) + sentinel);
        var openIssuesPath = fixture.WriteOpenIssuesJson("[]");

        var (exitCode, stdout, stderr) = RunCliInSubprocess([
            "suggestions", "export", "--db", fixture.DbPath, "--format", "issue-drafts", "--open-issues", openIssuesPath
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain(sentinel, stdout);
        using var doc = JsonDocument.Parse(stdout);
        var body = doc.RootElement.GetProperty("drafts")[0].GetProperty("body").GetString();
        Assert.NotNull(body);
        Assert.True(body!.Length <= SuggestionsCommandRunner.MaxSuggestionIssueDraftBodyLength);
        Assert.Contains("[truncated]", body);
    }

    [ProductionRuntimeFact]
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

    [ProductionRuntimeFact]
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
        Assert.Contains("JsonReaderException", stderr);
        Assert.DoesNotContain("maximum configured depth", stderr);
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

    private static void AssertCappedSuggestionText(string? value)
    {
        Assert.NotNull(value);
        Assert.True(value!.Length <= SuggestionsCommandRunner.MaxSuggestionExportTextFieldLength);
        Assert.Contains("[truncated]", value);
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
        => RepositoryTestPaths.Root;

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

    private static void ReplaceManifestNumber(string archivePath, string propertyName, long newValue)
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
        var oldValue = document.RootElement.GetProperty(propertyName).GetRawText();
        var updatedManifestJson = manifestJson.Replace(
            $"\"{propertyName}\":{oldValue}",
            $"\"{propertyName}\":{newValue.ToString(CultureInfo.InvariantCulture)}",
            StringComparison.Ordinal);

        entry.Delete();
        var replacementEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(replacementEntry.Open());
        writer.Write(updatedManifestJson);
    }

    private static void RemoveManifestProperties(string archivePath, params string[] propertyNames)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json entry was not found");

        JsonObject manifest;
        using (var reader = new StreamReader(entry.Open()))
        {
            manifest = JsonNode.Parse(reader.ReadToEnd())?.AsObject()
                ?? throw new InvalidOperationException("manifest.json did not contain an object");
        }

        foreach (var propertyName in propertyNames)
            manifest.Remove(propertyName);

        entry.Delete();
        var replacementEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(replacementEntry.Open());
        writer.Write(manifest.ToJsonString());
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

        public string StorePath => Path.Combine(_root, ".cdidx", "suggestions-codeindex.json");

        public static SuggestionFixture Create()
        {
            var root = TestProjectHelper.CreateTempProject("cdidx_suggestions_cli");
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
            string[]? evidencePaths = null,
            string? context = null,
            string? toolInvocationContext = null)
        {
            var record = new SuggestionRecord
            {
                Category = category,
                Language = language,
                Description = description,
                Context = context ?? "Agent noticed this during repository triage.",
                Hash = SuggestionStore.ComputeHash(category, language, description),
                CreatedAt = new DateTime(2026, 5, 16, 12, _records.Count, 0, DateTimeKind.Utc),
                SubmittedToGitHub = submitted,
                GitHubIssueUrl = submitted ? "https://github.com/Widthdom/CodeIndex/issues/99" : null,
                LastSubmitAttempt = lastSubmitAttempt,
                SubmitAttemptCount = submitAttemptCount,
                LastSubmitError = lastSubmitError,
                SampledTitle = sampledTitle,
                EvidencePaths = evidencePaths,
                ToolInvocationContext = toolInvocationContext,
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

        public string GetPath(params string[] segments)
            => segments.Aggregate(_root, Path.Combine);

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
            TestProjectHelper.DeleteDirectory(_root);
        }
    }
}
