using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for `cdidx db --integrity-check` (issue #1517).
/// `cdidx db --integrity-check` のテスト (issue #1517)。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DbCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ParseArgs_IntegrityCheckFlagSetsFlag()
    {
        var options = DbCommandRunner.ParseArgs(["--integrity-check"]);

        Assert.True(options.IntegrityCheck);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_HelpFlagSetsShowHelp()
    {
        var options = DbCommandRunner.ParseArgs(["--help"]);

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void ParseArgs_UnknownOptionRecordsParseError()
    {
        var options = DbCommandRunner.ParseArgs(["--bogus"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--bogus", options.ParseError);
    }

    [Fact]
    public void ParseArgs_PositionalArgRecordsParseError()
    {
        var options = DbCommandRunner.ParseArgs(["something"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("positional", options.ParseError);
    }

    [Fact]
    public void Run_WithoutModeFlag_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams([]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("db requires a mode flag", stderr);
        Assert.Contains("--integrity-check", stderr);
    }

    [Fact]
    public void Run_WithUnknownOption_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams(["--bogus"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--bogus", stderr);
    }

    [Fact]
    public void Run_MissingDb_ReturnsNotFoundWithHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_db_missing_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", missingDb]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Contains("database not found", stderr);
        Assert.Contains("cdidx index <projectPath>", stderr);
    }

    [Fact]
    public void Run_MissingDb_JsonShapeIncludesHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_db_missing_{Guid.NewGuid():N}.db");

        var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", missingDb, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("database not found", json.GetProperty("message").GetString());
        Assert.Contains("cdidx index <projectPath>", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_CleanDb_ReturnsOk()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_clean_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, stdout, _) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Integrity check", stdout);
            Assert.Contains("ok", stdout);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_CleanDb_JsonReportsOkTrueAndEmptyIssues()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_clean_json_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal(0, json.GetProperty("issues").GetArrayLength());
            Assert.Equal(Path.GetFullPath(dbPath), json.GetProperty("db_path").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_CorruptedDb_ReturnsDatabaseError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_corrupt_{Guid.NewGuid():N}.db");
        try
        {
            // Write bytes that begin with a valid SQLite header so the file is recognized
            // as a database, then garbage after that triggers an integrity_check failure.
            // SQLite ヘッダで始めつつ後続をゴミにすることで integrity_check に検出させる。
            var header = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            var bytes = new byte[4096];
            Array.Copy(header, bytes, header.Length);
            for (var i = header.Length; i < bytes.Length; i++)
                bytes[i] = 0xFF;
            File.WriteAllBytes(dbPath, bytes);

            var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath]);

            // Either the pragma raises an exception (caught as DatabaseError) or it returns
            // non-"ok" rows; both paths must produce DatabaseError, never Success.
            // PRAGMA が例外を投げるか non-"ok" 行を返すかのいずれでも DatabaseError を返すべき。
            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.NotEmpty(stderr);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunAndCaptureStreams(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }
}
