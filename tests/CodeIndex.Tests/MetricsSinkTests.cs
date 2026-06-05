using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class MetricsSinkTests
{
    [Fact]
    public void TryConsumeMetricsFlag_StripsSeparatedFormAndReturnsPath()
    {
        var args = new[] { "search", "foo", "--metrics", "/tmp/metrics.jsonl" };

        Assert.True(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Empty(error);
        Assert.Equal("/tmp/metrics.jsonl", path);
        Assert.Equal(new[] { "search", "foo" }, args);
    }

    [Fact]
    public void TryConsumeMetricsFlag_StripsEqualsFormAndReturnsPath()
    {
        var args = new[] { "search", "foo", "--metrics=/tmp/m.jsonl" };

        Assert.True(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Empty(error);
        Assert.Equal("/tmp/m.jsonl", path);
        Assert.Equal(new[] { "search", "foo" }, args);
    }

    [Fact]
    public void TryConsumeMetricsFlag_MissingValue_ReturnsUsageError()
    {
        var args = new[] { "search", "foo", "--metrics" };

        Assert.False(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Null(path);
        Assert.Contains("--metrics requires a path value", error);
    }

    [Fact]
    public void TryConsumeMetricsFlag_EmptyEqualsValue_ReturnsUsageError()
    {
        var args = new[] { "search", "foo", "--metrics=" };

        Assert.False(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Null(path);
        Assert.Contains("non-empty path value", error);
    }

    [Fact]
    public void TryConsumeMetricsFlag_AfterDoubleDash_PreservesQueryEscape()
    {
        // `--` is the query-escape sentinel for subcommands; tokens after it must stay
        // literal so a `cdidx search -- --metrics=foo` query string is not consumed.
        var args = new[] { "search", "--", "--metrics=foo" };

        Assert.True(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Null(path);
        Assert.Empty(error);
        Assert.Equal(new[] { "search", "--", "--metrics=foo" }, args);
    }

    [Fact]
    public void TryParseLanguageFromArgs_ReturnsValueWhenPresent()
    {
        Assert.Equal("csharp", ProgramRunner.TryParseLanguageFromArgs(["search", "foo", "--lang", "csharp"]));
        Assert.Equal("python", ProgramRunner.TryParseLanguageFromArgs(["symbols", "--lang=python"]));
        Assert.Null(ProgramRunner.TryParseLanguageFromArgs(["search", "foo"]));
        Assert.Null(ProgramRunner.TryParseLanguageFromArgs(["search", "--", "--lang=csharp"]));
    }

    [Fact]
    public void Run_WithMetricsFlag_AppendsJsonlRecordForEachInvocation()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_metrics_{Guid.NewGuid():N}.jsonl");
        try
        {
            var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
                ["--metrics", metricsPath, "definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            var lines = File.ReadAllLines(metricsPath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal("definitely-not-a-command", root.GetProperty("tool").GetString());
            Assert.Equal("cli", root.GetProperty("source").GetString());
            Assert.Equal(CommandExitCodes.UsageError, root.GetProperty("exit_code").GetInt32());
            Assert.True(root.GetProperty("elapsed_ms").GetDouble() >= 0);
            Assert.True(root.TryGetProperty("timestamp", out _));
            Assert.False(root.TryGetProperty("language", out _));
        }
        finally
        {
            if (File.Exists(metricsPath))
                File.Delete(metricsPath);
        }
    }

    [Fact]
    public void Run_WithMetricsFlag_OnUnixCreatesPrivateFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_metrics_private_{Guid.NewGuid():N}.jsonl");
        try
        {
            var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
                ["--metrics", metricsPath, "definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(metricsPath));
        }
        finally
        {
            if (File.Exists(metricsPath))
                File.Delete(metricsPath);
        }
    }

    [Fact]
    public void Record_RotatesMetricsLogAtMaxBytes()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_metrics_rotate_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024);
            Assert.NotNull(session);

            MetricsSink.Record(new MetricsEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "search",
                Source: "cli",
                ElapsedMs: 1.0,
                ExitCode: 1,
                Error: new string('x', 2000)));
            MetricsSink.Record(new MetricsEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "status",
                Source: "cli",
                ElapsedMs: 1.0,
                ExitCode: 0));

            Assert.True(File.Exists(metricsPath + ".1"));
            Assert.True(File.Exists(metricsPath));
            Assert.False(File.Exists(metricsPath + ".3"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(metricsPath + ".1"));
                Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(metricsPath));
            }
        }
        finally
        {
            foreach (var path in new[] { metricsPath, metricsPath + ".1", metricsPath + ".2", metricsPath + ".3" })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    [Fact]
    public void Run_WithEnvVarFallback_StillEmitsMetrics()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_metrics_env_{Guid.NewGuid():N}.jsonl");
        var original = Environment.GetEnvironmentVariable(MetricsSink.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(MetricsSink.EnvVarName, metricsPath);

            var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command", "--lang", "csharp"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            var lines = File.ReadAllLines(metricsPath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal("definitely-not-a-command", root.GetProperty("tool").GetString());
            Assert.Equal("csharp", root.GetProperty("language").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetricsSink.EnvVarName, original);
            if (File.Exists(metricsPath))
                File.Delete(metricsPath);
        }
    }

    [Fact]
    public void Run_WithoutMetricsConfiguration_DoesNotCreateFile()
    {
        // Sanity check: with no --metrics flag and no env var, MetricsSink stays inert and
        // the command completes normally without writing anywhere.
        // フラグも環境変数も無い場合は MetricsSink は無効のまま、ファイルも作成されない。
        var original = Environment.GetEnvironmentVariable(MetricsSink.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(MetricsSink.EnvVarName, null);
            Assert.False(MetricsSink.IsActive);

            var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.False(MetricsSink.IsActive);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetricsSink.EnvVarName, original);
        }
    }

    [Fact]
    public void Run_InvalidMetricsFlag_ReportsInvalidArgument()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--metrics"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("--metrics requires a path value", stderr);
    }

    [Fact]
    public void Run_WithMetricsFlag_BadDirectory_DoesNotBreakCommand()
    {
        // A metrics path under a non-writable / non-existent location must not break the
        // underlying command — sink failure is best-effort and silently degrades.
        // 書き込めない場所でも本体コマンドは継続する。
        var badPath = Path.Combine("/", "definitely-not-a-real-mount", "metrics.jsonl");
        var (exitCode, _, _) = CaptureConsole(() => ProgramRunner.Run(
            ["--metrics", badPath, "definitely-not-a-command"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
    }

    [Fact]
    public void SerializeEvent_OmitsNullOptionalFields()
    {
        var evt = new MetricsEvent(
            Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
            Tool: "search",
            Source: "cli",
            ElapsedMs: 12.5,
            ExitCode: 0);

        var json = MetricsSink.SerializeEvent(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("search", root.GetProperty("tool").GetString());
        Assert.Equal("cli", root.GetProperty("source").GetString());
        Assert.Equal(12.5, root.GetProperty("elapsed_ms").GetDouble());
        Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
        Assert.False(root.TryGetProperty("language", out _));
        Assert.False(root.TryGetProperty("bytes_read", out _));
        Assert.False(root.TryGetProperty("bytes_written", out _));
        Assert.False(root.TryGetProperty("wal_checkpoint_ms", out _));
        Assert.False(root.TryGetProperty("files_indexed", out _));
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void SerializeEvent_IncludesAllOptionalFieldsWhenSet()
    {
        var evt = new MetricsEvent(
            Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
            Tool: "index",
            Source: "cli",
            ElapsedMs: 1234.5,
            ExitCode: 0,
            Language: "csharp",
            BytesRead: 4096,
            BytesWritten: 8192,
            WalCheckpointMs: 1.234,
            FilesIndexed: 42,
            Error: null);

        var json = MetricsSink.SerializeEvent(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("csharp", root.GetProperty("language").GetString());
        Assert.Equal(4096, root.GetProperty("bytes_read").GetInt64());
        Assert.Equal(8192, root.GetProperty("bytes_written").GetInt64());
        Assert.Equal(1.234, root.GetProperty("wal_checkpoint_ms").GetDouble());
        Assert.Equal(42, root.GetProperty("files_indexed").GetInt32());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void SerializeEvent_TruncatesOversizedStringFieldsAndWritesMetadata()
    {
        var oversizedTool = new string('t', MetricsSink.MaxStringFieldChars + 11);
        var oversizedSource = new string('s', MetricsSink.MaxStringFieldChars + 12);
        var oversizedLanguage = new string('l', MetricsSink.MaxStringFieldChars + 13);
        var oversizedError = new string('e', MetricsSink.MaxStringFieldChars + 14);
        var evt = new MetricsEvent(
            Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
            Tool: oversizedTool,
            Source: oversizedSource,
            ElapsedMs: 1.25,
            ExitCode: 1,
            Language: oversizedLanguage,
            Error: oversizedError);

        var json = MetricsSink.SerializeEvent(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(MetricsSink.MaxStringFieldChars, root.GetProperty("tool").GetString()!.Length);
        Assert.Equal(oversizedTool.Length, root.GetProperty("tool_length").GetInt32());
        Assert.True(root.GetProperty("tool_truncated").GetBoolean());
        Assert.Equal(MetricsSink.MaxStringFieldChars, root.GetProperty("source").GetString()!.Length);
        Assert.Equal(oversizedSource.Length, root.GetProperty("source_length").GetInt32());
        Assert.True(root.GetProperty("source_truncated").GetBoolean());
        Assert.Equal(MetricsSink.MaxStringFieldChars, root.GetProperty("language").GetString()!.Length);
        Assert.Equal(oversizedLanguage.Length, root.GetProperty("language_length").GetInt32());
        Assert.True(root.GetProperty("language_truncated").GetBoolean());
        Assert.Equal(MetricsSink.MaxStringFieldChars, root.GetProperty("error").GetString()!.Length);
        Assert.Equal(oversizedError.Length, root.GetProperty("error_length").GetInt32());
        Assert.True(root.GetProperty("error_truncated").GetBoolean());
    }

    [Fact]
    public void Record_OversizedEscapedEventWritesSingleBoundedJsonlLine()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_metrics_bounded_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            var oversizedEscaped = new string('\u0001', 50_000);

            MetricsSink.Record(new MetricsEvent(
                Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
                Tool: oversizedEscaped,
                Source: oversizedEscaped,
                ElapsedMs: 1.0,
                ExitCode: 1,
                Language: oversizedEscaped,
                Error: oversizedEscaped));

            var line = Assert.Single(File.ReadAllLines(metricsPath));
            Assert.True(
                Encoding.UTF8.GetByteCount(line) <= MetricsSink.MaxSerializedEventBytes,
                $"Metrics event was {Encoding.UTF8.GetByteCount(line)} bytes.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("tool_truncated").GetBoolean());
            Assert.True(root.GetProperty("source_truncated").GetBoolean());
            Assert.True(root.GetProperty("language_truncated").GetBoolean());
            Assert.True(root.GetProperty("error_truncated").GetBoolean());
            Assert.Equal(oversizedEscaped.Length, root.GetProperty("error_length").GetInt32());
        }
        finally
        {
            if (File.Exists(metricsPath))
                File.Delete(metricsPath);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);
}
