using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for `cdidx report --output <path>` (issue #1552). The command must
/// produce a redacted `.tar.gz` containing version + OS + schema + a tail of
/// the lifecycle log, while never embedding indexed source content or unredacted
/// command-line arguments.
/// `cdidx report --output <path>` のテスト (issue #1552)。匿名化された
/// tarball にバージョン・OS・スキーマ・ログ末尾のみが入り、ソース内容や
/// 生の引数が混入しないことを担保する。
/// </summary>
[Collection("SQLite pool sensitive")]
public class ReportCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ParseArgs_OutputFlagCapturesValue()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "bundle.tgz"]);

        Assert.Equal("bundle.tgz", options.OutputPath);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_ShortOutputAliasCapturesValue()
    {
        var options = ReportCommandRunner.ParseArgs(["-o", "bundle.tgz"]);

        Assert.Equal("bundle.tgz", options.OutputPath);
    }

    [Fact]
    public void ParseArgs_NoLogTurnsOffLogInclusion()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--no-log"]);

        Assert.False(options.IncludeLog);
    }

    [Fact]
    public void ParseArgs_IncludeArgsOptsInToLiteralLog()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--include-args"]);

        Assert.True(options.IncludeArgs);
    }

    [Fact]
    public void ParseArgs_LogLinesParsesPositive()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--log-lines", "50"]);

        Assert.Equal(50, options.LogLines);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_LogLinesNegativeReportsError()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--log-lines", "-3"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("non-negative", options.ParseError);
    }

    [Fact]
    public void ParseArgs_UnknownOptionRecordsParseError()
    {
        var options = ReportCommandRunner.ParseArgs(["--bogus"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--bogus", options.ParseError);
    }

    [Fact]
    public void ParseArgs_PositionalArgRecordsParseError()
    {
        var options = ReportCommandRunner.ParseArgs(["extra"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("positional", options.ParseError);
    }

    [Fact]
    public void Run_MissingOutputFlag_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams([]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--output", stderr);
    }

    [Fact]
    public void Run_MissingOutputFlag_JsonShapeIncludesHint()
    {
        var (exitCode, json) = RunAndCaptureJson(["--json"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("--output", json.GetProperty("message").GetString());
        Assert.Contains("--output", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_NoDbAndNoLog_StillProducesBundleWithMetadata()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var missingDb = Path.Combine(workDir, "missing.db");

            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", missingDb,
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(File.Exists(output));

            var entries = ReadTarGzEntries(output);
            Assert.Contains("metadata.json", entries.Keys);
            Assert.Contains("version.txt", entries.Keys);
            Assert.Contains("env.txt", entries.Keys);
            Assert.Contains("schema.txt", entries.Keys);
            Assert.Contains("README.md", entries.Keys);
            Assert.DoesNotContain("log/stderr-recent.log", entries.Keys);

            var schemaText = Encoding.UTF8.GetString(entries["schema.txt"]);
            Assert.Contains("no SQLite index found", schemaText);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_WithRealDb_SchemaTxtListsTablesAndRowCounts()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", dbPath,
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            var schemaText = Encoding.UTF8.GetString(entries["schema.txt"]);
            Assert.Contains("tables", schemaText);
            Assert.Contains("files", schemaText);
            Assert.Contains("symbols", schemaText);
            Assert.Contains("row_count", schemaText);
            Assert.DoesNotContain("no SQLite index found", schemaText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_WithLogDirOverride_IncludesRedactedTail()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260516.log"),
            string.Join('\n',
                "2026-05-16T03:00:00Z [INFO] session_start pid=1 version=1.21.0",
                "2026-05-16T03:00:00Z [INFO] cwd=/Users/widthdom/secret",
                "2026-05-16T03:00:00Z [INFO] args=query \"SELECT * FROM secret\"",
                "2026-05-16T03:00:01Z [ERROR] sample error",
                ""));

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "20",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            Assert.True(entries.ContainsKey("log/stderr-recent.log"));
            var logText = Encoding.UTF8.GetString(entries["log/stderr-recent.log"]);
            Assert.Contains("args=[redacted]", logText);
            Assert.Contains("cwd=[redacted]", logText);
            Assert.DoesNotContain("/Users/widthdom/secret", logText);
            Assert.DoesNotContain("SELECT * FROM secret", logText);
            Assert.Contains("session_start", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_IncludeArgs_PreservesLiteralArgsAndCwd()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260516.log"),
            "2026-05-16T03:00:00Z [INFO] cwd=/tmp/keep-this\n2026-05-16T03:00:00Z [INFO] args=index .\n");

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "20",
                "--include-args",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            var logText = Encoding.UTF8.GetString(entries["log/stderr-recent.log"]);
            Assert.Contains("cwd=/tmp/keep-this", logText);
            Assert.Contains("args=index .", logText);
            Assert.DoesNotContain("[redacted]", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_JsonMode_PrintsSummaryEnvelope()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, json) = RunAndCaptureJson([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--no-log",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(Path.GetFullPath(output), json.GetProperty("output_path").GetString());
            Assert.True(json.GetProperty("files").GetInt32() >= 4);
            Assert.False(json.GetProperty("log_included").GetBoolean());
            Assert.False(json.GetProperty("db_included").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RedactSensitiveFields_RedactsCwdLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] cwd=/private/foo/secret-project");

        Assert.Contains("cwd=[redacted]", redacted);
        Assert.DoesNotContain("/private/foo/secret-project", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsArgsLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] args=query \"SELECT * FROM secret\"");

        Assert.Contains("args=[redacted]", redacted);
        Assert.DoesNotContain("SELECT * FROM secret", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_LinesWithoutSensitiveKeysPassThrough()
    {
        var line = "2026-05-16T03:00:00Z [INFO] session_start pid=1 version=1.21.0";
        var redacted = ReportCommandRunner.RedactSensitiveFields(line);

        Assert.Equal(line, redacted);
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
                var exitCode = ReportCommandRunner.Run(args, _jsonOptions, appVersion: "test");
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
                var exitCode = ReportCommandRunner.Run(args, _jsonOptions, appVersion: "test");
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static string CreateWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_report_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for tests / テスト用ベストエフォート削除。
        }
    }

    private static Dictionary<string, byte[]> ReadTarGzEntries(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var fileStream = File.OpenRead(path);
        using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
                continue;
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            entries[entry.Name] = buffer.ToArray();
        }
        return entries;
    }
}
