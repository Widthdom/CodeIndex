using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class GlobalToolLogTests
{
    private const UnixFileMode PermissionBits =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

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
    public void PrivateLogFile_OpenAppend_OnUnixRejectsSymlinkTargets_Issue3824()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_symlink_{Guid.NewGuid():N}");
        var target = Path.Combine(directory, "target.log");
        var link = Path.Combine(directory, "link.log");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(target, "target");
            File.CreateSymbolicLink(link, target);

            var ex = Assert.Throws<IOException>(() =>
            {
                using var _ = PrivateLogFile.OpenAppend(link);
            });

            Assert.Contains("symbolic link or reparse point", ex.Message, StringComparison.Ordinal);
            Assert.Equal("target", File.ReadAllText(target));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateLogFile_HardenExisting_CapsBestEffortWork_Issue3027()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_harden_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var fileCount = PrivateLogFile.MaxExistingFilesToHarden + 2;
        var diagnostics = new List<PrivateLogFileDiagnostic>();
        try
        {
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Combine(directory, $"stderr-{i:D4}.log");
                File.WriteAllText(path, "x");
                File.SetUnixFileMode(path, PermissionBits);
            }

            PrivateLogFile.HardenExisting(directory, "stderr-*.log", diagnostics.Add);

            var privateCount = 0;
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Combine(directory, $"stderr-{i:D4}.log");
                if ((File.GetUnixFileMode(path) & PermissionBits) == PrivateLogFile.PrivateFileMode)
                    privateCount++;
            }

            Assert.Equal(PrivateLogFile.MaxExistingFilesToHarden, privateCount);
            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(Path.Combine(directory, "stderr-0000.log")) & PermissionBits);
            Assert.NotEqual(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(Path.Combine(directory, $"stderr-{fileCount - 1:D4}.log")) & PermissionBits);
            var diagnostic = Assert.Single(diagnostics, item => item.Operation == "harden_existing_cap");
            Assert.Equal("cap_exceeded", diagnostic.Reason);
            Assert.DoesNotContain(directory, diagnostic.Target, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateLogFile_TrySetPrivatePermissions_WhenPathMissing_ReportsDiagnostic_Issue3475()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cdidx_missing_private_log_{Guid.NewGuid():N}.log");
        var diagnostics = new List<PrivateLogFileDiagnostic>();

        PrivateLogFile.TrySetPrivatePermissions(path, diagnostics.Add);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("set_private_permissions", diagnostic.Operation);
        Assert.Equal("not_found", diagnostic.Reason);
        Assert.Equal(Path.GetFileName(path), diagnostic.Target);
        Assert.DoesNotContain(Path.GetTempPath(), diagnostic.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateLogFile_PruneOldFiles_KeepsNewestFilesWithoutMaterializingAll_Issue3028()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        const int retainedFileCount = 5;
        const int fileCount = retainedFileCount + 17;
        var timestamp = DateTime.UtcNow.AddHours(-1);
        try
        {
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Combine(directory, $"stderr-{i:D4}.log");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, timestamp);
            }

            PrivateLogFile.PruneOldFiles(directory, "stderr-*.log", retainedFileCount);

            var remaining = Directory.GetFiles(directory, "stderr-*.log")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var expected = Enumerable.Range(fileCount - retainedFileCount, retainedFileCount)
                .Select(i => $"stderr-{i:D4}.log")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, remaining);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateLogFile_PruneOldFiles_DeleteFailureReportsDiagnosticAndContinues_Issue3962()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_prune_diag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var diagnostics = new List<PrivateLogFileDiagnostic>();
        try
        {
            var timestamp = DateTime.UtcNow.AddHours(-1);
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(directory, $"stderr-{i:D4}.log");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, timestamp);
            }

            PrivateLogFile.PruneOldFiles(
                directory,
                "stderr-*.log",
                retainedFileCount: 1,
                diagnostics.Add,
                path =>
                {
                    if (string.Equals(Path.GetFileName(path), "stderr-0000.log", StringComparison.Ordinal))
                        throw new IOException("delete denied");
                    File.Delete(path);
                });

            Assert.True(File.Exists(Path.Combine(directory, "stderr-0000.log")));
            Assert.False(File.Exists(Path.Combine(directory, "stderr-0001.log")));
            Assert.True(File.Exists(Path.Combine(directory, "stderr-0002.log")));
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("prune_old_file_delete", diagnostic.Operation);
            Assert.Equal("io_error", diagnostic.Reason);
            Assert.Equal("stderr-0000.log", diagnostic.Target);
            Assert.DoesNotContain(directory, diagnostic.Target, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateLogFile_TryRotateSlots_ReplacesExistingSlotsAtomicallyWherePossible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_rotate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "metrics.jsonl");
        try
        {
            File.WriteAllText(path, "current");
            File.WriteAllText(path + ".1", "previous-1");
            File.WriteAllText(path + ".2", "previous-2");

            Assert.True(PrivateLogFile.TryRotateSlots(path, retainedFileCount: 3));

            Assert.False(File.Exists(path));
            Assert.Equal("current", File.ReadAllText(path + ".1"));
            Assert.Equal("previous-1", File.ReadAllText(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateLogFile_TryRotateSlots_ReportsPostReplaceFlushFailure_Issue3776()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cdidx_private_log_rotate_flush_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "metrics.jsonl");
        var failures = new List<Exception>();
        try
        {
            File.WriteAllText(path, "current");
            AtomicFileWriter.FlushParentDirectoryForTesting = _ => throw new IOException("flush denied");

            var rotated = PrivateLogFile.TryRotateSlots(
                path,
                retainedFileCount: 3,
                onFailure: failures.Add);

            Assert.False(rotated);
            var failure = Assert.Single(failures);
            Assert.Contains("Atomic replace completed", failure.Message, StringComparison.Ordinal);
            Assert.Contains("target file was already replaced", failure.Message, StringComparison.Ordinal);
            Assert.Contains("parent directory could not be flushed", failure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            Assert.Equal("current", File.ReadAllText(path + ".1"));
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryStart_WritesPrivateLogDiagnostics_Issue3475()
    {
        if (OperatingSystem.IsWindows())
            return;

        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_private_diag_{Guid.NewGuid():N}");
        var capturedLogPath = Path.Combine(Path.GetTempPath(), $"cdidx_captured_private_diag_{Guid.NewGuid():N}.log");
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            using (var session = GlobalToolLog.TryStartForTesting(
                ["status"],
                "test",
                createWriter: _ => PrivateLogFile.OpenAppendText(capturedLogPath)))
            {
                Assert.NotNull(session);
            }

            var log = File.ReadAllText(capturedLogPath);
            Assert.Contains("private_log_diagnostic", log);
            Assert.Contains("operation=\"set_private_permissions\"", log);
            Assert.Contains("reason=\"not_found\"", log);
            Assert.DoesNotContain(logRoot, log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
            if (File.Exists(capturedLogPath))
                File.Delete(capturedLogPath);
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
    public void FormatArgs_UsesScopedCdidxEnvironmentRedactionOverride_Issue3690()
    {
        var previous = Environment.GetEnvironmentVariable("CDIDX_LOG_REDACT");
        using var env = CdidxEnvironment.Push(new Dictionary<string, string>
        {
            ["CDIDX_LOG_REDACT"] = "none",
        });

        var formatted = GlobalToolLog.FormatArgs(["--token=abc123"]);

        Assert.Equal(previous, Environment.GetEnvironmentVariable("CDIDX_LOG_REDACT"));
        Assert.Contains("abc123", formatted);
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
    public void FormatArgs_RedactsSharedSensitiveNames_Issue3933()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_LOG_REDACT");
        env.Set("CDIDX_LOG_REDACT", null);

        var formatted = GlobalToolLog.FormatArgs([
            "--github-token",
            "gh-secret",
            "--serviceCredential=credential-secret",
        ]);

        Assert.Contains("--github-token <redacted>", formatted);
        Assert.Contains("--serviceCredential=<redacted>", formatted);
        Assert.DoesNotContain("gh-secret", formatted);
        Assert.DoesNotContain("credential-secret", formatted);
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
    public void FormatExceptionChain_ClassifiesExceptionMessagesWithoutRawDetails_Issue3371()
    {
        var exception = new IOException(
            "failed to read /Users/widthdom/private/project/token.txt token=0123456789abcdef0123456789abcdef query=SELECT * FROM secret");

        var formatted = GlobalToolLog.FormatExceptionChain(exception);

        Assert.Contains("type=System.IO.IOException", formatted);
        Assert.Contains("message=\"io_error\"", formatted);
        Assert.DoesNotContain("/Users/widthdom/private", formatted);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", formatted);
        Assert.DoesNotContain("SELECT * FROM secret", formatted);
    }

    [Fact]
    public void FormatExceptionChain_ClassifiesInnerExceptionMessages_Issue3371()
    {
        var exception = new InvalidOperationException(
            "outer raw path /tmp/private",
            new UnauthorizedAccessException("denied /home/user/secret.txt password=hunter2"));

        var formatted = GlobalToolLog.FormatExceptionChain(exception);

        Assert.Contains("message=\"invalid_operation\"", formatted);
        Assert.Contains("message=\"access_denied\"", formatted);
        Assert.DoesNotContain("/tmp/private", formatted);
        Assert.DoesNotContain("/home/user/secret.txt", formatted);
        Assert.DoesNotContain("hunter2", formatted);
    }

    [Fact]
    public void FormatExceptionChain_BoundsLongNestedMessagesAndRedactsSensitiveValues_Issue3725()
    {
        const string secretPath = "/Users/widthdom/private/project/token.txt";
        const string secretToken = "0123456789abcdef0123456789abcdef";
        Exception exception = new IOException($"failed to read {secretPath} token={secretToken}");
        for (var i = 0; i < 220; i++)
            exception = new InvalidOperationException($"outer {i} raw path {secretPath} token={secretToken}", exception);

        var formatted = GlobalToolLog.FormatExceptionChain(exception);

        Assert.True(formatted.Length <= GlobalToolLog.MaxExceptionChainChars);
        Assert.Contains(GlobalToolLog.ExceptionChainTruncationMarker, formatted);
        Assert.Contains("message=\"invalid_operation\"", formatted);
        Assert.DoesNotContain(secretPath, formatted);
        Assert.DoesNotContain(secretToken, formatted);
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
            if (!OperatingSystem.IsWindows())
                Assert.Equal(
                    DataDirectorySecurity.PrivateDirectoryMode,
                    File.GetUnixFileMode(resolved) & PermissionBits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveTempFallbackLogDirectory_UsesPrivateTempFallback_Issue3675()
    {
        var directory = GlobalToolLog.ResolveTempFallbackLogDirectoryForTesting();

        Assert.Equal(DataDirectorySecurity.ResolveSensitiveTempFallbackDirectory("logs"), directory);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cdidx", "logs")),
            directory);
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

    [Fact]
    public void TryStart_ErrorMirrorTruncatesLargeWrites_Issue3166()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_large_mirror_{Guid.NewGuid():N}");
        var originalError = Console.Error;
        var visibleError = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);
            Console.SetError(visibleError);
            var prefix = new string('e', GlobalToolLog.MirroredStderrWriteMaxChars);
            const string tail = "TAIL_ISSUE_3166";
            var raw = prefix + tail;

            using (var session = GlobalToolLog.TryStartForTesting(["status"], "test"))
            {
                Assert.NotNull(session);
                Console.Error.WriteLine(raw);
            }

            Assert.Contains(tail, visibleError.ToString());
            var logPath = Directory.GetFiles(logRoot, "stderr-*.log", SearchOption.TopDirectoryOnly).Single();
            var log = File.ReadAllText(logPath);
            Assert.Contains($"original length {raw.Length} chars", log);
            Assert.DoesNotContain(tail, log);
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
