using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class MetricsSinkTests
{
    [Fact]
    public void TryConsumeMetricsFlag_StripsSeparatedFormAndReturnsPath()
    {
        var args = new[] { "search", "--metrics", "/tmp/metrics.jsonl", "foo" };

        Assert.True(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Empty(error);
        Assert.Equal("/tmp/metrics.jsonl", path);
        Assert.Equal(new[] { "search", "foo" }, args);
    }

    [Fact]
    public void TryConsumeMetricsFlag_StripsEqualsFormAndReturnsPath()
    {
        var args = new[] { "search", "--metrics=/tmp/m.jsonl", "foo" };

        Assert.True(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Empty(error);
        Assert.Equal("/tmp/m.jsonl", path);
        Assert.Equal(new[] { "search", "foo" }, args);
    }

    [Fact]
    public void TryConsumeMetricsFlag_MissingValue_ReturnsUsageError()
    {
        var args = new[] { "search", "--metrics" };

        Assert.False(ProgramRunner.TryConsumeMetricsFlag(ref args, out var path, out var error));
        Assert.Null(path);
        Assert.Contains("--metrics requires a path value", error);
    }

    [Fact]
    public void TryConsumeMetricsFlag_EmptyEqualsValue_ReturnsUsageError()
    {
        var args = new[] { "search", "--metrics=" };

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

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                return (action(), stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
