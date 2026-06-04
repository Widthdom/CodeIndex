using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class GlobalToolLogTests
{
    [Fact]
    public void PrivateLogFile_OpenAppend_OnUnixCreatesPrivateFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_{Guid.NewGuid():N}.log");
        try
        {
            using (var stream = PrivateLogFile.OpenAppend(path))
            {
                stream.WriteByte((byte)'x');
            }

            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void FormatArgs_RedactsSensitiveArgumentsByDefault()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);

        var formatted = GlobalToolLog.FormatArgs([
            "--token=abc123",
            "--password",
            "hunter2",
            "https://user:pass@example.test/repo.git",
            "0123456789abcdef0123456789abcdef",
        ]);

        Assert.Contains("--token=<redacted>", formatted);
        Assert.Contains("--password <redacted>", formatted);
        Assert.Contains("https://user:<redacted>@example.test/repo.git", formatted);
        Assert.DoesNotContain("hunter2", formatted);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", formatted);
    }

    [Fact]
    public void FormatArgs_RedactsUnderscoreSeparatedSecretArguments()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);

        var formatted = GlobalToolLog.FormatArgs([
            "--api_key=api-secret",
            "--access_key",
            "access-secret",
        ]);

        Assert.Contains("--api_key=<redacted>", formatted);
        Assert.Contains("--access_key <redacted>", formatted);
        Assert.DoesNotContain("api-secret", formatted);
        Assert.DoesNotContain("access-secret", formatted);
    }

    [Fact]
    public void FormatArgs_TruncatesOverlongArgumentBeforeRedaction()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);
        var tailSecret = "tail-secret-value-should-not-survive";
        var argument = "safe:" + new string('!', GlobalToolLog.RedactionArgumentLengthLimit) + tailSecret;

        var formatted = GlobalToolLog.FormatArgs([argument]);

        Assert.Contains(GlobalToolLog.RedactionTruncationMarker, formatted);
        Assert.DoesNotContain(tailSecret, formatted);
        Assert.True(formatted.Length < argument.Length);
    }

    [Fact]
    public void FormatArgs_RedactsOverlongUriUserInfoBeforeTruncation()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);
        var argument = "https://user:" + new string('!', GlobalToolLog.RedactionArgumentLengthLimit) + "@example.test/repo.git";

        var formatted = GlobalToolLog.FormatArgs([argument]);

        Assert.Equal("<redacted>", formatted);
        Assert.DoesNotContain("user:", formatted);
    }

    [Fact]
    public void FormatArgs_RedactsOverlongSensitiveAssignment()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);
        var argument = "--api_key=" + new string('x', GlobalToolLog.RedactionArgumentLengthLimit * 2);

        var formatted = GlobalToolLog.FormatArgs([argument]);

        Assert.Equal("--api_key=<redacted>", formatted);
    }

    [Fact]
    public void FormatArgs_AllowsExplicitNoRedaction()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", "none");

        var formatted = GlobalToolLog.FormatArgs(["--token=abc123"]);

        Assert.Equal("--token=abc123", formatted);
    }

    [Fact]
    public void LogOptionsFromEnvironment_AcceptsMaximumMbValue()
    {
        using var env = EnvironmentVariableScope.Capture(
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, GlobalToolLog.MaxLogSizeMb.ToString(CultureInfo.InvariantCulture));
        env.Set(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, null);

        var options = GlobalToolLog.LogOptions.FromEnvironment();

        Assert.Equal(GlobalToolLog.MaxLogSizeBytes, options.MaxSizeBytes);
    }

    [Fact]
    public void LogOptionsFromEnvironment_MbAboveMaximumUsesDefault()
    {
        using var env = EnvironmentVariableScope.Capture(
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);
        var tooLarge = GlobalToolLog.MaxLogSizeMb + 1;
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, tooLarge.ToString(CultureInfo.InvariantCulture));
        env.Set(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, (GlobalToolLog.MaxLogSizeBytes / 2).ToString(CultureInfo.InvariantCulture));

        var options = GlobalToolLog.LogOptions.FromEnvironment();

        Assert.Equal(50L * 1024L * 1024L, options.MaxSizeBytes);
    }

    [Fact]
    public void LogOptionsFromEnvironment_AcceptsMaximumBytesValue()
    {
        using var env = EnvironmentVariableScope.Capture(
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, null);
        env.Set(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, GlobalToolLog.MaxLogSizeBytes.ToString(CultureInfo.InvariantCulture));

        var options = GlobalToolLog.LogOptions.FromEnvironment();

        Assert.Equal(GlobalToolLog.MaxLogSizeBytes, options.MaxSizeBytes);
    }

    [Fact]
    public void LogOptionsFromEnvironment_BytesAboveMaximumUsesDefault()
    {
        using var env = EnvironmentVariableScope.Capture(
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, null);
        env.Set(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, (GlobalToolLog.MaxLogSizeBytes + 1).ToString(CultureInfo.InvariantCulture));

        var options = GlobalToolLog.LogOptions.FromEnvironment();

        Assert.Equal(50L * 1024L * 1024L, options.MaxSizeBytes);
    }

    [Fact]
    public void ResolveLogDirectoryForStatus_SkipsUnwritableCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_log_probe_{Guid.NewGuid():N}");
        var fileCandidate = Path.Combine(root, "not-a-directory");
        var stateHome = Path.Combine(root, "state");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(fileCandidate, "occupied");
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_GLOBAL_TOOL_LOG_DIR",
                "XDG_STATE_HOME",
                "XDG_CACHE_HOME",
                "XDG_RUNTIME_DIR");
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", fileCandidate);
            env.Set("XDG_STATE_HOME", stateHome);
            env.Set("XDG_CACHE_HOME", null);
            env.Set("XDG_RUNTIME_DIR", null);

            var resolved = GlobalToolLog.ResolveLogDirectoryForStatus();

            Assert.Equal(Path.Combine(stateHome, "cdidx", "logs"), resolved);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryNormalizeLogDirectoryCandidate_ReturnsFalseForInvalidPath()
    {
        var invalid = "bad" + '\0' + "path";

        var ok = GlobalToolLog.TryNormalizeLogDirectoryCandidate(invalid, out var fullPath);

        Assert.False(ok);
        Assert.Equal(string.Empty, fullPath);
    }

    [Fact]
    public void TryStart_WritesInvariantUtcTimestampAndStackTrace()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_{Guid.NewGuid():N}");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["search", "Needle"],
                appVersion: "test",
                beforeDispatchForTesting: ThrowForGlobalToolLogTest));

            Assert.Equal(CommandExitCodes.UnhandledException, exitCode);
            Assert.Contains("Run `cdidx report`", stderr);
            var logPath = Assert.Single(Directory.GetFiles(logRoot, "stderr-*.log"));
            Assert.Matches(
                $@"^stderr-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-p\d+-\d{{6}}\.log$",
                Path.GetFileName(logPath));
            var log = File.ReadAllText(logPath);
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[INFO\] session_start", RegexOptions.Multiline), log);
            Assert.Contains("unhandled_exception", log);
            Assert.Contains(nameof(ThrowForGlobalToolLogTest), log);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public void TryStart_ErrorMirrorIgnoresDisposedOriginalConsoleWriter()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_disposed_{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            using var session = GlobalToolLog.TryStartForTesting(
                ["status"],
                "test",
                afterWriterCreated: () => Console.SetError(new ThrowingTextWriter()));

            var exception = Record.Exception(() => Console.Error.WriteLine("mirrored error"));

            Assert.NotNull(session);
            Assert.Null(exception);
        }
        finally
        {
            Console.SetError(originalError);
            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
        }
    }

    private static void ThrowForGlobalToolLogTest() =>
        throw new InvalidOperationException("global log stack trace test");

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Flush() => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void Write(char value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void Write(string? value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void WriteLine(string? value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));
    }
}
